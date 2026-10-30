namespace Jellyfin.Plugin.AssFonts;

internal sealed record FallbackFontSnapshot(FontFile? File)
{
    public static FallbackFontSnapshot Capture(string setting)
    {
        var configured = setting.Trim();
        FontFile? file = null;
        try
        {
            if (Path.IsPathFullyQualified(configured) && System.IO.File.Exists(configured)
                && new[] { ".ttf", ".ttc", ".otf", ".otc" }.Contains(Path.GetExtension(configured), StringComparer.OrdinalIgnoreCase))
            {
                var source = new FileInfo(configured);
                var target = source.ResolveLinkTarget(true);
                var resolved = target is null ? source : new FileInfo(target.FullName);
                if (resolved.Length <= SubtitleProcessor.MaxInputBytes)
                    file = new(source.FullName, resolved.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return new(file);
    }
}
