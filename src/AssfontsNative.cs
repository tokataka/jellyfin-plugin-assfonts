using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AssFonts;

internal static class AssfontsNative
{
    internal static async Task<JsonArray> BuildDatabaseAsync(string binary, string build,
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

    internal static string PathKey(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? WindowsPathKey(full) : full;
    }

    private static string WindowsPathKey(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static async Task<(bool Success, string Detail)> ExecuteAsync(string binary, string work,
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
