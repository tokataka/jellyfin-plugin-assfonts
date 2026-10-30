using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private sealed record FontIndexState(string Fingerprint, string Checksum, string Directory, IReadOnlyList<FontFile> Files);

    public async Task<RunResult> RunAsync(string input, string work, string fontDirectory, CancellationToken token)
    {
        var binary = await NativeBinary.ExtractAsync(cacheRoot, token);
        var attachedDirectory = Path.Combine(work, "attached-fonts");
        var attachedFonts = await AttachedFonts.ExtractAsync(input, attachedDirectory, token);
        var database = JsonNode.Parse(await GetFontDatabaseAsync(binary, work, fontDirectory, token))!.AsArray();
        if (attachedFonts > 0)
        {
            var attachments = await BuildFontDatabaseAsync(binary, Path.Combine(work, "attachment-index"),
                FontDirectorySnapshot.Capture(attachedDirectory), token);
            PrioritizeAttachments(database, attachments);
        }
        await File.WriteAllTextAsync(Path.Combine(work, "fonts.json"), database.ToJsonString(), token);
        var arguments = new[] { "-d", work, "-o", work, "-m", "1", "-i", input };
        var result = await ExecuteAsync(binary, work, arguments, token);
        var output = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".assfonts" + Path.GetExtension(input));
        return new RunResult(result.Success && File.Exists(output), output, result.Detail);
    }

    private async Task<byte[]> GetFontDatabaseAsync(string binary, string work, string configured, CancellationToken token)
    {
        await indexGate.WaitAsync(token);
        try
        {
            var snapshot = FontDirectorySnapshot.Capture(configured);
            var directory = Path.Combine(cacheRoot, "font-index");
            if (snapshot.Files.Count == 0)
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return "[]"u8.ToArray();
            }
            var fingerprint = BinarySha256 + ":" + snapshot.Fingerprint;
            var database = Path.Combine(directory, "fonts.json");
            var statePath = Path.Combine(directory, "state.json");
            try
            {
                if (File.Exists(database) && File.Exists(statePath))
                {
                    var state = JsonSerializer.Deserialize<FontIndexState>(await File.ReadAllTextAsync(statePath, token));
                    if (state?.Fingerprint == fingerprint)
                    {
                        var saved = await File.ReadAllBytesAsync(database, token);
                        if (Convert.ToHexStringLower(SHA256.HashData(saved)) == state.Checksum) return saved;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }

            // Rebuild from an empty database so removed files cannot remain in the index.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            var build = Path.Combine(work, "font-index");
            var entries = await BuildFontDatabaseAsync(binary, build, snapshot, token);
            if (snapshot.Fingerprint != FontDirectorySnapshot.Capture(configured).Fingerprint)
                throw new IOException("Fonts changed while building the font database.");
            var builtDatabase = Path.Combine(build, "fonts.json");
            var bytes = Encoding.UTF8.GetBytes(entries.ToJsonString());
            await File.WriteAllBytesAsync(builtDatabase, bytes, token);
            var stateFile = Path.Combine(build, "state.json");
            await File.WriteAllTextAsync(stateFile,
                JsonSerializer.Serialize(new FontIndexState(fingerprint, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    snapshot.Directory, snapshot.Files)), token);
            Directory.CreateDirectory(directory);
            File.Move(builtDatabase, database, true);
            File.Move(stateFile, statePath, true);
            return bytes;
        }
        finally { indexGate.Release(); }
    }

    private static async Task<JsonArray> BuildFontDatabaseAsync(string binary, string build,
        FontDirectorySnapshot snapshot, CancellationToken token)
    {
        Directory.CreateDirectory(build);
        var builtDatabase = Path.Combine(build, "fonts.json");
        await File.WriteAllTextAsync(builtDatabase, "[]", token);
        var indexed = await ExecuteAsync(binary, build, new[] { "-b", "-f", snapshot.Directory, "-d", build }, token);
        if (!indexed.Success) throw new IOException("Could not build font database: " + indexed.Detail);
        var entries = JsonNode.Parse(await File.ReadAllTextAsync(builtDatabase, token))!.AsArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var allowed = snapshot.Files.ToDictionary(file => PathKey(file.Path), file => file.ResolvedPath, comparer);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i]!;
            var path = entry["path"]!.GetValue<string>();
            // assfonts -b also scans system fonts; retain only the configured directory.
            if (!allowed.TryGetValue(PathKey(path), out var resolved)) { entries.RemoveAt(i); continue; }
            // assfonts 0.7.3 sizes Windows symlinks instead of their targets during subsetting.
            entry["path"] = resolved;
        }
        return entries;
    }

    private static readonly string[] NameFields = ["families", "fullnames", "psnames"];

    private static void PrioritizeAttachments(JsonArray database, JsonArray attachments)
    {
        var names = attachments.SelectMany(entry => NameFields.SelectMany(field => entry![field]!.AsArray()))
            .Select(name => name!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in database)
            foreach (var field in NameFields)
            {
                var aliases = entry![field]!.AsArray();
                for (var i = aliases.Count - 1; i >= 0; i--)
                    if (names.Contains(aliases[i]!.GetValue<string>())) aliases.RemoveAt(i);
            }
        foreach (var entry in attachments) database.Add(entry!.DeepClone());
    }

    private static string PathKey(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? WindowsPathKey(full) : full;
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
