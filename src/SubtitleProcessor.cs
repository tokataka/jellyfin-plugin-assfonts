using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

public interface ISubtitleProcessor
{
    Task<Stream?> TransformAsync(Stream source, string extension, CancellationToken token, SubtitleLogContext? context = null);
}

public sealed class SubtitleProcessor(string root, Func<string> getFontDirectory,
    IAssfontsRunner runner, ILogger<SubtitleProcessor> logger) : ISubtitleProcessor, IDisposable
{
    public const long MaxInputBytes = 128L * 1024 * 1024;
    private const long MaxCacheBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<Stream?> TransformAsync(Stream source, string extension, CancellationToken token, SubtitleLogContext? context = null)
    {
        token.ThrowIfCancellationRequested();
        if (extension is not ("ass" or "ssa") || !source.CanSeek || !source.CanRead) return null;
        var fontDirectory = getFontDirectory().Trim();
        if (fontDirectory.Length == 0) return null;
        var started = Stopwatch.GetTimestamp();
        context ??= new SubtitleLogContext("unknown");
        var position = source.Position;
        long inputBytes = 0;
        string? work = null;
        string? failedPath = null;
        string? cacheDirectory = null;
        var entered = false;
        void LogProcessed(Stream result, bool cached)
        {
            logger.LogInformation(
                "'{SubtitleName:l}' processed in {ElapsedMs:F1} ms, size {OutputKiB:F1} KiB{CacheStatus:l}",
                context.SubtitleName, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.Length / 1024d, cached ? " (Cached)" : string.Empty);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (source.Length - position > MaxInputBytes) return null;
            if (!Path.IsPathFullyQualified(fontDirectory) || !Directory.Exists(fontDirectory))
                throw new DirectoryNotFoundException("Font directory must be an existing absolute path.");
            // Bound temporary disk use as well as native-process concurrency.
            await gate.WaitAsync(deadline.Token);
            entered = true;
            fontDirectory = Path.GetFullPath(fontDirectory);
            var fontFingerprint = FontFingerprint(fontDirectory);
            cacheDirectory = Path.Combine(root, "subtitles");
            Directory.CreateDirectory(cacheDirectory);
            work = Path.Combine(root, "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var input = Path.Combine(work, "input." + extension);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = File.Create(input))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    inputBytes += count;
                    if (inputBytes > MaxInputBytes) return null;
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
            }
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "assfonts-cache-v2\0" + extension + "\0" + fontFingerprint + "\0" + AssfontsRunner.BinarySha256
                + "\0" + Convert.ToHexStringLower(hash.GetHashAndReset()))));
            var destination = Path.Combine(cacheDirectory, key + "." + extension);
            var failed = Path.Combine(cacheDirectory, key + ".failed");
            failedPath = failed;
            var cached = OpenCached(destination);
            if (cached is not null)
            {
                LogProcessed(cached, true);
                return cached;
            }
            if (File.Exists(failed) && DateTime.UtcNow - File.GetLastWriteTimeUtc(failed) < TimeSpan.FromMinutes(1)) return null;

            var result = await runner.RunAsync(input, work, fontDirectory, deadline.Token);
            if (!result.Success)
            {
                RememberFailure(failedPath);
                logger.LogWarning("ASS Fonts kept original subtitle ({Key}): {Detail}", key[..12], result.Detail);
                return null;
            }
            var output = new FileInfo(result.OutputPath);
            if (!output.Exists || output.Length == 0 || output.Length > MaxInputBytes)
                throw new InvalidDataException("assfonts did not produce a valid-size output.");
            var generated = await File.ReadAllTextAsync(result.OutputPath, deadline.Token);
            if (!generated.Contains("[Fonts]", StringComparison.Ordinal) || !generated.Contains("fontname:", StringComparison.Ordinal))
                throw new InvalidDataException("assfonts output has no font attachments.");
            if (fontFingerprint != FontFingerprint(fontDirectory))
                throw new IOException("Fonts changed during processing; retaining original subtitle.");
            File.Move(result.OutputPath, destination, true);
            if (File.Exists(failed)) File.Delete(failed);
            var processed = OpenCached(destination);
            if (processed is not null) LogProcessed(processed, false);
            return processed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            RememberFailure(failedPath);
            logger.LogWarning("ASS Fonts processing timed out; retaining original subtitle.");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            RememberFailure(failedPath);
            logger.LogWarning(exception, "ASS Fonts processing failed; retaining original subtitle.");
            return null;
        }
        finally
        {
            try { source.Position = position; }
            finally
            {
                if (entered)
                {
                    try { if (cacheDirectory is not null) Prune(cacheDirectory); }
                    finally { gate.Release(); }
                }
            }
            if (work is not null)
            {
                try { Directory.Delete(work, true); }
                catch (IOException ex) { logger.LogDebug(ex, "Could not remove ASS Fonts work directory."); }
                catch (UnauthorizedAccessException ex) { logger.LogDebug(ex, "Could not remove ASS Fonts work directory."); }
            }
        }
    }

    private static string FontFingerprint(string directory)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ttf", ".ttc", ".otf", ".otc" };
        var files = EnumerateFonts(directory).Where(path => extensions.Contains(Path.GetExtension(path))).Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(directory + "\0"));
        foreach (var path in files)
        {
            var file = new FileInfo(path);
            // assfonts follows font-file symlinks but not directory symlinks.
            var target = file.ResolveLinkTarget(true);
            if (target is not null) file = new FileInfo(target.FullName);
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0" + file.FullName + "\0" + file.Length.ToString(CultureInfo.InvariantCulture)
                + "\0" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static IEnumerable<string> EnumerateFonts(string directory)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.Directory) == 0) yield return item.FullName;
            else if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var file in EnumerateFonts(item.FullName)) yield return file;
        }
    }

    private void RememberFailure(string? path)
    {
        if (path is null) return;
        try { File.WriteAllText(path, string.Empty); }
        catch (IOException ex) { logger.LogDebug(ex, "Could not record ASS Fonts failure backoff."); }
        catch (UnauthorizedAccessException ex) { logger.LogDebug(ex, "Could not record ASS Fonts failure backoff."); }
    }

    private static Stream? OpenCached(string path)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > CacheLifetime) return null;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length == 0) { stream.Dispose(); return null; }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return stream;
        }
        catch (FileNotFoundException) { return null; }
    }

    private void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long kept = 0;
            var entries = 0;
            foreach (var file in files)
            {
                kept += file.Length;
                var lifetime = file.Extension == ".failed" ? TimeSpan.FromMinutes(1) : CacheLifetime;
                if (++entries > 5000 || kept > MaxCacheBytes || DateTime.UtcNow - file.LastWriteTimeUtc > lifetime)
                    file.Delete();
            }
        }
        catch (IOException ex) { logger.LogDebug(ex, "ASS Fonts cache cleanup postponed."); }
        catch (UnauthorizedAccessException ex) { logger.LogDebug(ex, "ASS Fonts cache cleanup postponed."); }
    }

    public void Dispose() => gate.Dispose();
}
