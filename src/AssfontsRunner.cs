using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssFonts;

public sealed record RunResult(bool Success, string OutputPath, string Detail);

public interface IAssfontsRunner
{
    Task<RunResult> RunAsync(string input, string work, FontDatabaseSnapshot fonts, CancellationToken token);
}

public sealed class AssfontsRunner(string cacheRoot) : IAssfontsRunner
{
    public static string BinarySha256 => NativeBinary.Current.Checksum;

    public async Task<RunResult> RunAsync(string input, string work, FontDatabaseSnapshot fonts, CancellationToken token)
    {
        var binary = await NativeBinary.ExtractAsync(cacheRoot, token);
        var attachedDirectory = Path.Combine(work, "attached-fonts");
        var attachedFonts = await AttachedFonts.ExtractAsync(input, attachedDirectory, token);
        var database = JsonNode.Parse(fonts.Fonts)!.AsArray();
        await File.WriteAllTextAsync(Path.Combine(work, "fonts.json"), database.ToJsonString(), token);
        var arguments = new List<string> { "-d", work, "-o", work, "-m", "1" };
        // -f builds an in-memory attachment index without scanning default system directories.
        // Native lookup searches this index before the persistent directory database.
        if (attachedFonts > 0) arguments.AddRange(new[] { "-f", attachedDirectory });
        arguments.AddRange(new[] { "-i", input });
        var result = await AssfontsNative.ExecuteAsync(binary, work, arguments, token);
        var stem = Path.GetFileNameWithoutExtension(input);
        var output = Path.Combine(work, stem + ".assfonts" + Path.GetExtension(input));
        var missing = Regex.Matches(result.Detail, @"(?m)^\[WARN\] Missing the font: ""(.*)"" \(-?\d+,-?\d+\)\r?$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (!result.Success && missing.Length > 0 && fonts.Fallback is not null)
        {
            var replacement = JsonNode.Parse(fonts.Fallback);
            if (replacement is not null)
            {
                replacement["families"] = new JsonArray(missing.Select(name => JsonValue.Create(name.ToLowerInvariant())).ToArray());
                replacement["fullnames"] = new JsonArray();
                replacement["psnames"] = new JsonArray();
                database.Add(replacement);
                await File.WriteAllTextAsync(Path.Combine(work, "fonts.json"), database.ToJsonString(), token);
                // Rename both the subset's internal family and ASS references; a filename alone cannot bind a fallback.
                result = await AssfontsNative.ExecuteAsync(binary, work, arguments.Concat(new[] { "-r" }), token);
                output = Path.Combine(work, stem + ".rename.assfonts" + Path.GetExtension(input));
            }
        }
        return new RunResult(result.Success && File.Exists(output), output, result.Detail);
    }

}
