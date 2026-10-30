using System.Text;
using Jellyfin.Plugin.AssFonts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;

namespace AssFonts.Tests;

public sealed class FallbackTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-fallback-" + Guid.NewGuid().ToString("N"));
    private static readonly string Subtitle = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.ass"));
    private static readonly string Font = Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts/test.ttf");
    private static readonly string LocalFont = Path.Combine(AppContext.BaseDirectory, "Fixtures/local-font.ttf");

    private FontDatabase? database;
    private async Task<SubtitleProcessor> Create(string directory, Func<string> fallback, string cacheName = "cache")
    {
        database = await TestFontDatabase.CreateAsync(Path.Combine(root, cacheName), () => new(directory, fallback()));
        return new(Path.Combine(root, cacheName), database, new AssfontsRunner(Path.Combine(root, cacheName)), new TestLogger(output));
    }

    private static async Task<string?> Process(SubtitleProcessor processor, string text)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await using var result = await processor.TransformAsync(source, "ass", default);
        Assert.Equal(text, Encoding.UTF8.GetString(source.ToArray()));
        Assert.Equal(0, source.Position);
        return result is null ? null : await new StreamReader(result).ReadToEndAsync();
    }

    [Fact]
    public async Task FallbackBindsStylesAndInlineOverridesToTheEmbeddedFont()
    {
        using var processor = await Create("", () => Font);
        var input = Subtitle.Replace("AssFonts Test", "Missing Style Font").Replace("가나다 ABC", @"가나다 {\fnMissing Inline Font}ABC");
        var result = await Process(processor, input);
        Assert.NotNull(result);
        Assert.Contains("fontname:", result);
        Assert.DoesNotContain("Style: Default,Missing Style Font,", result);
        Assert.DoesNotContain(@"\fnMissing Inline Font}", result);
        // Reprocess using only the generated attachments: font names in the subtitle must resolve.
        using var verify = await Create("", () => "", "verify");
        Assert.NotNull(await Process(verify, result));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache", "jobs")));
    }

    [Fact]
    public async Task FallbackNeverReplacesAnAvailableAttachedFont()
    {
        var fonts = Path.Combine(root, "fonts"); Directory.CreateDirectory(fonts);
        File.Copy(Font, Path.Combine(fonts, "external.ttf"));
        var input = AttachmentTests.WithFont(Subtitle.Replace("가나다 ABC", @"가나다 ABC{\fnMissing Font}ABC"), File.ReadAllBytes(Font), "attached.ttf");
        using var processor = await Create(fonts, () => LocalFont);
        var result = await Process(processor, input);
        Assert.NotNull(result);
        Assert.Contains("fontname: font-0000[", result);
        Assert.DoesNotContain("fontname: external[", result);
        Assert.Equal(2, result.Split("fontname:").Length - 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("relative.ttf")]
    [InlineData("directory")]
    public async Task UnavailableFallbackLeavesTheOriginalSubtitle(string setting)
    {
        Directory.CreateDirectory(root);
        var fallback = setting == "missing" ? Path.Combine(root, "missing.ttf") : setting == "directory" ? root : setting;
        using var processor = await Create("", () => fallback);
        Assert.Null(await Process(processor, Subtitle.Replace("AssFonts Test", "Missing Font")));
    }

    [Fact]
    public async Task FallbackWithoutRequiredGlyphsLeavesTheOriginalSubtitle()
    {
        using var processor = await Create("", () => LocalFont);
        Assert.Null(await Process(processor, Subtitle.Replace("AssFonts Test", "Missing Font").Replace("ABC", "🦄")));
    }

    [Fact]
    public async Task FallbackSettingChangeInvalidatesTheSubtitleCache()
    {
        var fallback = Font;
        using var processor = await Create("", () => fallback);
        var input = Subtitle.Replace("AssFonts Test", "Missing Font");
        Assert.NotNull(await Process(processor, input));
        fallback = "";
        await database!.RebuildAsync();
        Assert.Null(await Process(processor, input));
        fallback = Font;
        await database!.RebuildAsync();
        Assert.NotNull(await Process(processor, input));
    }

    [Fact]
    public async Task RebuildingAfterFallbackReplacementInvalidatesTheSubtitleCache()
    {
        Directory.CreateDirectory(root);
        var fallback = Path.Combine(root, "fallback.ttf");
        File.Copy(Font, fallback);
        using var processor = await Create("", () => fallback);
        var input = Subtitle.Replace("AssFonts Test", "Missing Font");
        var original = await Process(processor, input);
        Assert.NotNull(original);
        File.Copy(LocalFont, fallback, true);
        await database!.RebuildAsync();
        var replaced = await Process(processor, input);
        Assert.NotNull(replaced);
        Assert.NotEqual(original, replaced);
    }

    private sealed class TestLogger(ITestOutputHelper output) : ILogger<SubtitleProcessor>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => output.WriteLine(formatter(state, exception) + "\n" + exception);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
