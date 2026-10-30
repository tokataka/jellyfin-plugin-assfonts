using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AssFonts;

internal sealed record FontFile(string Path, string ResolvedPath, long Length, long ModifiedTicks);

internal sealed record FontDirectorySnapshot(string Directory, IReadOnlyList<FontFile> Files, string Fingerprint)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".ttc", ".otf", ".otc" };

    public static FontDirectorySnapshot Capture(string setting)
    {
        var configured = setting.Trim();
        var directory = string.Empty;
        var files = new List<FontFile>();
        try
        {
            if (Path.IsPathFullyQualified(configured) && System.IO.Directory.Exists(configured))
            {
                directory = Path.GetFullPath(configured);
                foreach (var path in SubtitleProcessor.EnumerateFonts(directory)
                    .Where(path => Extensions.Contains(Path.GetExtension(path))).Order(StringComparer.Ordinal))
                {
                    var source = new FileInfo(path);
                    var resolved = source.ResolveLinkTarget(true);
                    var file = resolved is null ? source : new FileInfo(resolved.FullName);
                    files.Add(new FontFile(source.FullName, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            directory = string.Empty;
            files.Clear();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(configured + "\0" + directory + "\0"));
        foreach (var file in files)
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path + "\0" + file.ResolvedPath + "\0"
                + file.Length.ToString(CultureInfo.InvariantCulture) + "\0"
                + file.ModifiedTicks.ToString(CultureInfo.InvariantCulture) + "\n"));
        return new(directory, files, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
