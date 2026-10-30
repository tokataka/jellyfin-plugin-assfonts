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
        var args = arguments.ToArray();
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var result = await ExecuteAttemptAsync(binary, work, args, token);
            if (result.ShutdownStalled && attempt == 0) continue;
            var detail = attempt > 0 ? "Retried assfonts after a shutdown stall.\n" + result.Detail : result.Detail;
            return (result.Success, detail);
        }
    }

    private static async Task<(bool Success, string Detail, bool ShutdownStalled)> ExecuteAttemptAsync(
        string binary, string work, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(binary)
        {
            WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start assfonts.");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = CaptureAsync(process.StandardOutput, completed);
        var stderr = CaptureAsync(process.StandardError, completed);
        var stalled = false;
        async Task StopAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        try
        {
            var exit = process.WaitForExitAsync(token);
            if (await Task.WhenAny(exit, completed.Task) == completed.Task)
            {
                // assfonts 0.7.3 can lose the log queue's final wake-up. Only watch for
                // shutdown after a terminal message, never while normal subsetting runs.
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(1), token)) != exit)
                {
                    token.ThrowIfCancellationRequested();
                    stalled = true;
                    await StopAsync();
                }
            }
            await exit;
        }
        catch (OperationCanceledException) { await StopAsync(); throw; }
        var logs = await Task.WhenAll(stdout, stderr);
        return (!stalled && process.ExitCode == 0 && !logs.Any(l => l.Bad),
            $"assfonts exit code: {process.ExitCode}\n" + (stalled ? "assfonts did not exit after its terminal log message.\n" : "")
            + string.Join("\n", logs.Select(l => l.Text)), stalled);
    }

    private static async Task<(bool Bad, string Text)> CaptureAsync(StreamReader reader, TaskCompletionSource completed)
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
            if (chunk.Contains("[INFO] Create font-embeded subtitle:", StringComparison.Ordinal)
                || chunk.Contains("[INFO] Fonts database has been saved in", StringComparison.Ordinal)
                || chunk.Contains("[ERROR]", StringComparison.Ordinal)) completed.TrySetResult();
            tail = chunk[^Math.Min(128, chunk.Length)..];
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        }
        return (bad, text.ToString());
    }
}
