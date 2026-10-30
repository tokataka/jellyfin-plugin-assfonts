namespace Jellyfin.Plugin.AssFonts;

internal sealed record FontFile(string Path, string ResolvedPath);

internal sealed record FontDirectorySnapshot(string Directory, IReadOnlyList<FontFile> Files)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".ttc", ".otf", ".otc" };

    private static IEnumerable<string> EnumerateFonts(string directory)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.Directory) == 0) yield return item.FullName;
            else if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var file in EnumerateFonts(item.FullName)) yield return file;
        }
    }

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
                foreach (var path in EnumerateFonts(directory)
                    .Where(path => Extensions.Contains(Path.GetExtension(path))).Order(StringComparer.Ordinal))
                {
                    var source = new FileInfo(path);
                    var resolved = source.ResolveLinkTarget(true);
                    var file = resolved is null ? source : new FileInfo(resolved.FullName);
                    files.Add(new FontFile(source.FullName, file.FullName));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            directory = string.Empty;
            files.Clear();
        }
        return new(directory, files);
    }
}
