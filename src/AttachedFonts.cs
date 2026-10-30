using System.Text;

namespace Jellyfin.Plugin.AssFonts;

public static class AttachedFonts
{
    private const int MaximumFonts = 256;
    private const long MaximumBytes = SubtitleProcessor.MaxInputBytes / 4 * 3;

    public static async Task<int> ExtractAsync(string input, string directory, CancellationToken token)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        FileStream? output = null;
        string? temporary = null;
        var inFonts = false;
        var fontCount = 0;
        long totalBytes = 0;
        uint bits = 0;
        var bitCount = 0;
        var characters = 0L;
        var buffer = new byte[8192];
        var buffered = 0;

        async Task FlushAsync()
        {
            if (buffered == 0) return;
            totalBytes += buffered;
            if (totalBytes > MaximumBytes) throw new InvalidDataException("Attached fonts exceed the size limit.");
            await output!.WriteAsync(buffer.AsMemory(0, buffered), token);
            buffered = 0;
        }

        async Task FinishAsync()
        {
            if (output is null) return;
            if (characters == 0 || characters % 4 == 1)
                throw new InvalidDataException("Invalid attached font encoding length.");
            await FlushAsync();
            await output.DisposeAsync(); output = null;
            var header = new byte[4];
            using (var font = File.OpenRead(temporary!)) font.ReadExactly(header);
            var extension = header.AsSpan().SequenceEqual("OTTO"u8) ? ".otf"
                : header.AsSpan().SequenceEqual("ttcf"u8) ? ".ttc"
                : header.AsSpan().SequenceEqual("true"u8) || header.AsSpan().SequenceEqual(new byte[] { 0, 1, 0, 0 }) ? ".ttf"
                : throw new InvalidDataException("Attachment is not a supported font file.");
            File.Move(temporary!, Path.ChangeExtension(temporary!, extension));
        }

        try
        {
            while (await reader.ReadLineAsync(token) is { } raw)
            {
                token.ThrowIfCancellationRequested();
                var line = raw.Trim();
                if (line.Equals("[Fonts]", StringComparison.OrdinalIgnoreCase))
                {
                    await FinishAsync(); inFonts = true; continue;
                }
                if (!inFonts) continue;
                if (IsOtherSection(line))
                {
                    await FinishAsync(); inFonts = false; continue;
                }
                if (line.StartsWith("fontname:", StringComparison.OrdinalIgnoreCase))
                {
                    await FinishAsync();
                    if (fontCount >= MaximumFonts) throw new InvalidDataException("Too many attached fonts.");
                    Directory.CreateDirectory(directory);
                    // Attachment names are untrusted labels, never filesystem paths.
                    temporary = Path.Combine(directory, $"font-{fontCount++:D4}.part");
                    output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    bits = 0; bitCount = 0; characters = 0;
                    continue;
                }
                if (output is null || line.Length == 0) continue;
                foreach (var character in line)
                {
                    if (character < 33 || character > 96)
                        throw new InvalidDataException("Invalid attached font encoding character.");
                    bits = (bits << 6) | (uint)(character - 33);
                    bitCount += 6; characters++;
                    if (bitCount >= 8)
                    {
                        bitCount -= 8;
                        buffer[buffered++] = (byte)(bits >> bitCount);
                        if (buffered == buffer.Length) await FlushAsync();
                    }
                }
            }
            await FinishAsync();
            return fontCount;
        }
        finally { if (output is not null) await output.DisposeAsync(); }
    }

    private static bool IsOtherSection(string line) =>
        line.Equals("[Events]", StringComparison.OrdinalIgnoreCase)
        || line.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase)
        || line.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase)
        || line.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
        || line.Equals("[Graphics]", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("[Aegisub", StringComparison.OrdinalIgnoreCase);
}
