using System.Text;
using Jellyfin.Plugin.AssFonts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssFonts.Tests;

public sealed class AttachmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-attachments-" + Guid.NewGuid().ToString("N"));
    private static readonly string Subtitle = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.ass"));
    private static readonly byte[] Font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts/test.ttf"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusesAttachedFontWithoutAFontInTheConfiguredDirectory(bool atEnd)
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        var input = WithFont(Subtitle, Font, "../../outside.ttf", atEnd);
        using var cache = CreateCache(fonts);
        for (var request = 0; request < 2; request++)
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
            await using var result = await cache.TransformAsync(source, "ass", default);
            Assert.NotNull(result);
            var generated = await new StreamReader(result!).ReadToEndAsync();
            Assert.Contains("fontname:", generated);
            Assert.Contains("가나다 ABC", generated);
            Assert.Equal(0, source.Position);
            Assert.Equal(input, Encoding.UTF8.GetString(source.ToArray()));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(root, "cache/subtitles"), "*.ass"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache/jobs")));
        Assert.False(File.Exists(Path.Combine(root, "outside.ttf")));
        Assert.Empty(Directory.GetFiles(fonts));
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("file")]
    public async Task UsesAttachmentsWhenFontDirectoryIsUnavailable(string setting)
    {
        Directory.CreateDirectory(root);
        var directory = setting switch
        {
            "missing" => Path.Combine(root, "missing"),
            "relative" => "relative-font-directory",
            "file" => Path.Combine(root, "not-a-directory"),
            _ => ""
        };
        if (setting == "file") File.WriteAllText(directory, "not a directory");
        using var processor = CreateCache(directory);
        var input = WithFont(Subtitle, Font, "attached.ttf");
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await using var result = await processor.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        Assert.Contains("fontname:", await new StreamReader(result).ReadToEndAsync());
        Assert.Equal(input, Encoding.UTF8.GetString(source.ToArray()));
    }

    [Fact]
    public async Task AttachedFontTakesPriorityOverSameFamilyInConfiguredDirectory()
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        File.WriteAllBytes(Path.Combine(fonts, "external.ttf"), Font);
        using var processor = CreateCache(fonts);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(WithFont(Subtitle, Font, "attached.ttf")));
        await using var result = await processor.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        var text = await new StreamReader(result).ReadToEndAsync();
        Assert.Contains("fontname: font-0000[", text);
        Assert.DoesNotContain("fontname: external[", text);
    }

    [Fact]
    public async Task MalformedAttachmentReturnsOriginalAndRemovesTemporaryFonts()
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        File.WriteAllBytes(Path.Combine(fonts, "test.ttf"), Font);
        var input = Subtitle.Replace("[Events]", "[Fonts]\nfontname: broken.ttf\n~~~~\n[Events]");
        using var cache = CreateCache(fonts);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        Assert.Null(await cache.TransformAsync(source, "ass", default));
        Assert.Equal(0, source.Position);
        Assert.Equal(input, Encoding.UTF8.GetString(source.ToArray()));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache/jobs")));
    }

    private SubtitleProcessor CreateCache(string fonts) => new(Path.Combine(root, "cache"), () => fonts,
        new AssfontsRunner(Path.Combine(root, "cache")), NullLogger<SubtitleProcessor>.Instance);

    [Fact]
    public async Task CombinesAttachedAndConfiguredFonts()
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/local-font.ttf"), Path.Combine(fonts, "local.ttf"));
        var mixed = Subtitle.Replace("가나다 ABC", @"가나다 {\fnLocalFont Test}ABC");
        using var cache = CreateCache(fonts);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(WithFont(mixed, Font, "attached.ttf")));
        await using var result = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        var text = await new StreamReader(result!).ReadToEndAsync();
        Assert.Equal(2, text.Split("fontname:").Length - 1);
        Assert.Contains(@"{\fnLocalFont Test}ABC", text);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache/jobs")));
    }

    [Fact]
    public async Task AttachmentDatabaseIsPerSubtitleAndNeverLeaksIntoDirectoryCache()
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        var local = Path.Combine(fonts, "local.ttf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/local-font.ttf"), local);
        var cacheRoot = Path.Combine(root, "cache");
        var runner = new InspectingRunner(new AssfontsRunner(cacheRoot));
        using var processor = new SubtitleProcessor(cacheRoot, () => fonts, runner, NullLogger<SubtitleProcessor>.Instance);
        var mixed = Subtitle.Replace("가나다 ABC", @"가나다 {\fnLocalFont Test}ABC");
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(WithFont(mixed, Font, "attached.ttf")));
        await using var result = await processor.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        Assert.True(runner.AttachmentIndexExisted);
        Assert.Equal(2, runner.IndexedPaths.Length);
        Assert.Contains(local, runner.IndexedPaths);
        var permanent = File.ReadAllText(Path.Combine(cacheRoot, "font-index", "fonts.json"));
        Assert.DoesNotContain("font-0000", permanent);
        Assert.Empty(Directory.GetDirectories(Path.Combine(cacheRoot, "jobs")));

        using var localSource = new MemoryStream(Encoding.UTF8.GetBytes(Subtitle.Replace("AssFonts Test", "LocalFont Test").Replace("가나다 ABC", "ABC")));
        await using var localResult = await processor.TransformAsync(localSource, "ass", default);
        Assert.NotNull(localResult);
        Assert.False(runner.AttachmentIndexExisted);
        Assert.Equal(new[] { local }, runner.IndexedPaths);
    }

    private sealed class InspectingRunner(IAssfontsRunner inner) : IAssfontsRunner
    {
        public bool AttachmentIndexExisted { get; private set; }
        public string[] IndexedPaths { get; private set; } = [];
        public async Task<RunResult> RunAsync(string input, string work, string fontDirectory, CancellationToken token)
        {
            var result = await inner.RunAsync(input, work, fontDirectory, token);
            AttachmentIndexExisted = File.Exists(Path.Combine(work, "attachment-index", "fonts.json"));
            IndexedPaths = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work, "fonts.json"), token))!.AsArray()
                .Select(entry => entry!["path"]!.GetValue<string>()).Distinct().ToArray();
            return result;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExtractsExactBytesWithPartialEncodingGroups(int remainder)
    {
        Directory.CreateDirectory(root);
        var padding = (remainder - Font.Length % 3 + 3) % 3;
        var bytes = Font.Concat(new byte[padding]).ToArray();
        var input = Path.Combine(root, "input.ass");
        await File.WriteAllTextAsync(input, WithFont(Subtitle, bytes, @"C:\outside\font.ttf"));
        var directory = Path.Combine(root, "extracted");
        Assert.Equal(1, await AttachedFonts.ExtractAsync(input, directory, default));
        var extracted = Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(extracted));
        Assert.Equal("font-0000.ttf", Path.GetFileName(extracted));
    }

    [Fact]
    public async Task MalformedEncodingLengthIsRejected()
    {
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.ass");
        await File.WriteAllTextAsync(input, "[Fonts]\nfontname: font.ttf\n!!!!!\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => AttachedFonts.ExtractAsync(input, Path.Combine(root, "extracted"), default));
    }

    internal static string WithFont(string subtitle, byte[] bytes, string name, bool atEnd = false)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var encoded = new string(Convert.ToBase64String(bytes).TrimEnd('=').Select(c => (char)(alphabet.IndexOf(c) + 33)).ToArray());
        var lines = string.Join("\n", encoded.Chunk(80).Select(chars => new string(chars)));
        var block = "[Fonts]\nfontname: " + name + "\n" + lines + "\n";
        return atEnd ? subtitle + "\n" + block : subtitle.Replace("[Events]", block + "[Events]");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
