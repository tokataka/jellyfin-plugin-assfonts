using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AssFonts;

public sealed record RunResult(bool Success, string OutputPath, string Detail);

public interface IAssfontsRunner
{
    Task<RunResult> RunAsync(string input, string work, string fontDirectory, CancellationToken token);
}

public sealed class AssfontsRunner(string cacheRoot) : IAssfontsRunner
{
    public static string BinarySha256 => NativeBinary.Current.Checksum;

    public async Task<RunResult> RunAsync(string input, string work, string fontDirectory, CancellationToken token)
    {
        var binary = await NativeBinary.ExtractAsync(cacheRoot, token);
        var attachedDirectory = Path.Combine(work, "attached-fonts");
        var attachedFonts = await AttachedFonts.ExtractAsync(input, attachedDirectory, token);
        // Search configured fonts and the subtitle's own attachments.
        var database = Path.Combine(work, "fonts.json");
        await File.WriteAllTextAsync(database, "[]", token);
        var fonts = new List<string> { fontDirectory };
        if (attachedFonts > 0) fonts.Add(attachedDirectory);
        var arguments = new List<string> { "-f" };
        arguments.AddRange(fonts);
        if (OperatingSystem.IsWindows() && SubtitleProcessor.EnumerateFonts(fontDirectory).Any(path => new FileInfo(path).LinkTarget is not null))
        {
            // assfonts 0.7.3 sizes Windows symlinks instead of their targets during subsetting.
            // Build its index first, then supply resolved paths without copying the font pool.
            var indexed = await ExecuteAsync(binary, work, arguments.Concat(new[] { "-b", "-d", work }), token);
            if (!indexed.Success) return new RunResult(false, "", indexed.Detail);
            var allowed = fonts.SelectMany(SubtitleProcessor.EnumerateFonts).Select(path => WindowsPathKey(Path.GetFullPath(path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = JsonNode.Parse(await File.ReadAllTextAsync(database, token))!.AsArray();
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i]!;
                var path = Path.GetFullPath(entry["path"]!.GetValue<string>());
                // The build command also scans system fonts; only retain configured/attached files.
                if (!allowed.Contains(WindowsPathKey(path))) { entries.RemoveAt(i); continue; }
                entry["path"] = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            }
            await File.WriteAllTextAsync(database, entries.ToJsonString(), token);
            arguments.Clear();
        }
        arguments.AddRange(new[] { "-d", work, "-o", work, "-m", "1", "-i", input });
        var result = await ExecuteAsync(binary, work, arguments, token);
        var output = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".assfonts" + Path.GetExtension(input));
        return new RunResult(result.Success && File.Exists(output), output, result.Detail);
    }

    private static string WindowsPathKey(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static async Task<(bool Success, string Detail)> ExecuteAsync(string binary, string work,
        IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(binary)
        {
            WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start assfonts.");
        var stdout = CaptureAsync(process.StandardOutput);
        var stderr = CaptureAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        var logs = await Task.WhenAll(stdout, stderr);
        return (process.ExitCode == 0 && !logs.Any(l => l.Bad),
            $"assfonts exit code: {process.ExitCode}\n" + string.Join("\n", logs.Select(l => l.Text)));
    }

    private static async Task<(bool Bad, string Text)> CaptureAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var tail = string.Empty;
        var bad = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var chunk = tail + new string(buffer, 0, count);
            bad |= chunk.Contains("[ERROR]", StringComparison.Ordinal)
                || chunk.Contains("Missing codepoints", StringComparison.Ordinal);
            tail = chunk[^Math.Min(32, chunk.Length)..];
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        }
        return (bad, text.ToString());
    }
}
