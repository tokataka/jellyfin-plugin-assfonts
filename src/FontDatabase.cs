using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

public sealed record FontSettings(string Directory, string Fallback);
public sealed record FontDatabaseSnapshot(DateTime BuiltAtUtc, FontSettings Settings, byte[] Fonts, byte[]? Fallback, int FontCount);

public sealed class FontDatabase(string root, Func<FontSettings> getSettings, ILogger<FontDatabase> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FontDatabaseSnapshot? current;
    private volatile bool rebuilding;
    private string? lastError;

    public FontDatabaseSnapshot? Current => Volatile.Read(ref current);
    public bool IsRebuilding => rebuilding;
    public string? LastError => Volatile.Read(ref lastError);
    public bool IsCurrent(FontDatabaseSnapshot snapshot) => ReferenceEquals(Current, snapshot) && snapshot.Settings == Settings();
    private FontSettings Settings() { var value = getSettings(); return new(value.Directory.Trim(), value.Fallback.Trim()); }

    public async Task<FontDatabaseSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await ready.Task.WaitAsync(token);
        return Current!;
    }

    public async Task<FontDatabaseSnapshot> RebuildAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await gate.WaitAsync(deadline.Token);
        var started = Stopwatch.GetTimestamp();
        string? work = null;
        try
        {
            rebuilding = true;
            var settings = Settings();
            work = Path.Combine(root, "builds", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var binary = await NativeBinary.ExtractAsync(root, deadline.Token);
            var directory = FontDirectorySnapshot.Capture(settings.Directory);
            var fonts = directory.Files.Count == 0 ? new JsonArray() : await AssfontsNative.BuildDatabaseAsync(
                binary, Path.Combine(work, "directory"), directory, deadline.Token);
            JsonNode? fallback = null;
            try
            {
                var fallbackFile = FallbackFontSnapshot.Capture(settings.Fallback).File;
                if (fallbackFile is not null)
                {
                    var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    fallback = fonts.Where(entry => comparer.Equals(AssfontsNative.PathKey(entry!["path"]!.GetValue<string>()),
                        AssfontsNative.PathKey(fallbackFile.ResolvedPath))).OrderBy(entry => entry!["index"]!.GetValue<int>()).FirstOrDefault()?.DeepClone();
                    if (fallback is null)
                    {
                        var staged = Path.Combine(work, "fallback-font");
                        Directory.CreateDirectory(staged);
                        var copy = Path.Combine(staged, "fallback" + Path.GetExtension(fallbackFile.Path));
                        await using (var source = File.OpenRead(fallbackFile.ResolvedPath))
                        await using (var target = File.Create(copy))
                            await source.CopyToAsync(target, deadline.Token);
                        var entries = await AssfontsNative.BuildDatabaseAsync(binary, Path.Combine(work, "fallback-index"),
                            FontDirectorySnapshot.Capture(staged), deadline.Token);
                        fallback = entries.OrderBy(entry => entry!["index"]!.GetValue<int>()).FirstOrDefault()?.DeepClone();
                        if (fallback is not null) fallback["path"] = fallbackFile.ResolvedPath;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                fallback = null;
                logger.LogWarning(exception, "Fallback font is unavailable; using directory and attached fonts.");
            }
            if (settings != Settings()) throw new IOException("Settings changed while building the font database.");
            var data = System.Text.Encoding.UTF8.GetBytes(fonts.ToJsonString());
            var fallbackData = fallback is null ? null : System.Text.Encoding.UTF8.GetBytes(fallback.ToJsonString());
            var destination = Path.Combine(root, "font-index");
            Directory.CreateDirectory(destination);
            var temporary = Path.Combine(work, "fonts.json");
            await File.WriteAllBytesAsync(temporary, data, deadline.Token);
            File.Move(temporary, Path.Combine(destination, "fonts.json"), true);
            // A completed rebuild invalidates subtitle caches, including when the font list is unchanged.
            var builtAt = DateTime.UtcNow;
            if (Current is { } old && builtAt <= old.BuiltAtUtc) builtAt = old.BuiltAtUtc.AddTicks(1);
            File.SetLastWriteTimeUtc(Path.Combine(destination, "fonts.json"), builtAt);
            var snapshot = new FontDatabaseSnapshot(builtAt, settings, data, fallbackData,
                fonts.Select(entry => entry!["path"]!.GetValue<string>()).Distinct().Count());
            Volatile.Write(ref current, snapshot);
            Volatile.Write(ref lastError, null);
            // Remove metadata left by older plugin versions; no file inventory is persisted.
            File.Delete(Path.Combine(destination, "state.json"));
            File.Delete(Path.Combine(root, "font-state.txt"));
            var oldFallback = Path.Combine(root, "fallback-index");
            if (Directory.Exists(oldFallback)) Directory.Delete(oldFallback, true);
            logger.LogInformation("Font database rebuilt in {ElapsedMs:F1} ms, {FontCount} fonts.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, snapshot.FontCount);
            return snapshot;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception.Message);
            throw;
        }
        finally
        {
            if (current is null)
                Volatile.Write(ref current, new FontDatabaseSnapshot(DateTime.UtcNow, Settings(), "[]"u8.ToArray(), null, 0));
            ready.TrySetResult();
            rebuilding = false;
            gate.Release();
            if (work is not null)
            {
                try { Directory.Delete(work, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { logger.LogDebug(exception, "Could not remove font database work directory."); }
            }
        }
    }

    public void Dispose() => gate.Dispose();
}
