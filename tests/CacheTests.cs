using System.Text;
using Jellyfin.Plugin.AssFonts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AssFonts.Tests;

public sealed class CacheTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string fonts;
    private readonly FontDatabase database;
    private readonly CountingRunner runner;
    private readonly SubtitleProcessor cache;
    private readonly TestLogger logger;
    private readonly byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.ass"));

    public CacheTests(ITestOutputHelper output)
    {
        fonts = Path.Combine(root, "fonts");
        Directory.CreateDirectory(fonts);
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts")))
            File.Copy(path, Path.Combine(fonts, Path.GetFileName(path)));
        runner = new CountingRunner(new AssfontsRunner(Path.Combine(root, "cache")));
        logger = new TestLogger(output);
        database = new FontDatabase(Path.Combine(root, "cache"), () => new(fonts, ""), NullLogger<FontDatabase>.Instance);
        cache = new SubtitleProcessor(Path.Combine(root, "cache"), database, runner, logger);
    }

    public async Task InitializeAsync() => await database.RebuildAsync();
    public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task EmbedsRealSubsetAndLeavesSourceUntouched()
    {
        using var source = new MemoryStream(input);
        await using var result = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        var text = await new StreamReader(result!).ReadToEndAsync();
        Assert.Contains("[Fonts]", text);
        Assert.Contains("fontname:", text);
        Assert.Contains("가나다 ABC", text);
        Assert.Equal(0, source.Position);
        Assert.Equal(input, source.ToArray());
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache", "jobs")));
    }

    [Fact]
    public async Task SimultaneousRequestsShareOneGenerationAndReuseDiskCache()
    {
        var outputs = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => {
            using var source = new MemoryStream(input);
            await using var result = await cache.TransformAsync(source, "ass", default);
            Assert.NotNull(result);
            return await new StreamReader(result!).ReadToEndAsync();
        }));
        Assert.All(outputs, text => Assert.Equal(outputs[0], text));
        Assert.Equal(1, runner.Calls);
        using var again = new SubtitleProcessor(Path.Combine(root,"cache"),database,runner,NullLogger<SubtitleProcessor>.Instance);
        using var stream = new MemoryStream(input);
        await using var cached = await again.TransformAsync(stream,"ass",default);
        Assert.NotNull(cached);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task LogsGenerationAndCacheHitWithActualSizesAndElapsedTime()
    {
        using var source = new MemoryStream(input);
        await using var generated = await cache.TransformAsync(source, "ass", default, new("episode-01.ko.ass"));
        Assert.NotNull(generated);
        await using var cached = await cache.TransformAsync(source, "ass", default, new("episode-02.ko.ass"));
        Assert.NotNull(cached);
        var entries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.DoesNotContain("(Cached)", entries[0].Message);
        Assert.Contains("(Cached)", entries[1].Message);
        Assert.Equal("episode-01.ko.ass", entries[0].Values["SubtitleName"]);
        Assert.Equal("episode-02.ko.ass", entries[1].Values["SubtitleName"]);
        foreach (var entry in entries)
        {
            Assert.Equal(generated.Length / 1024d, entry.Values["OutputKiB"]);
            Assert.True(Assert.IsType<double>(entry.Values["ElapsedMs"]) >= 0);
        }
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task FontChangesDoNotInvalidateSubtitlesUntilDatabaseRebuild()
    {
        using var source = new MemoryStream(input);
        await using var original = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(original);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/local-font.ttf"), Path.Combine(fonts, "added.ttf"));
        await using var again = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(again);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task DirectoryDatabaseChangesOnlyAfterExplicitRebuild()
    {
        var file = Path.Combine(root, "cache", "font-index", "fonts.json");
        var original = File.ReadAllBytes(file);
        var modified = File.GetLastWriteTimeUtc(file);
        using var source = new MemoryStream(input);
        await using var first = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(first);
        var added = Path.Combine(fonts, "local.ttf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/local-font.ttf"), added);
        using var other = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("ABC", "AB")));
        await using var second = await cache.TransformAsync(other, "ass", default);
        Assert.NotNull(second);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
        await database.RebuildAsync();
        Assert.Contains(added.Replace("\\", "\\\\"), File.ReadAllText(file));
        File.Delete(added);
        await database.RebuildAsync();
        Assert.DoesNotContain("local.ttf", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root, "cache", "font-index", "state.json")));
    }

    [Fact]
    public async Task MissingDirectoryDiscardsFontDatabaseButStillProcessesAttachments()
    {
        var font = File.ReadAllBytes(Directory.GetFiles(fonts)[0]);
        using var first = new MemoryStream(input);
        await using var initial = await cache.TransformAsync(first, "ass", default);
        Assert.NotNull(initial);
        var databaseFile = Path.Combine(root, "cache", "font-index", "fonts.json");
        Assert.True(File.Exists(databaseFile));
        Directory.Delete(fonts, true);
        await database.RebuildAsync();
        var attached = AttachmentTests.WithFont(Encoding.UTF8.GetString(input), font, "attached.ttf");
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(attached));
        await using var result = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        Assert.Equal("[]", File.ReadAllText(databaseFile));
    }

    [Fact]
    public async Task QueuedRequestUsesTheLatestConfiguredDirectory()
    {
        var location = Path.Combine(root, "switching");
        var configured = fonts;
        var blocking = new BlockingRunner(new AssfontsRunner(location));
        using var changing = await TestFontDatabase.CreateAsync(location, () => new(configured, ""));
        using var processor = new SubtitleProcessor(location, changing, blocking, logger);
        using var firstSource = new MemoryStream(input);
        using var secondSource = new MemoryStream(input);
        var first = processor.TransformAsync(firstSource, "ass", default);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = processor.TransformAsync(secondSource, "ass", default);
        configured = Path.Combine(root, "new-fonts"); Directory.CreateDirectory(configured);
        File.Copy(Directory.GetFiles(fonts)[0], Path.Combine(configured, "test.ttf"));
        await changing.RebuildAsync();
        blocking.Release.TrySetResult();
        await using var old = await first;
        await using var current = await second;
        Assert.Null(old);
        Assert.NotNull(current);
    }

    [Fact]
    public async Task CacheHitDoesNotReturnFontsFromAnOutdatedConfiguration()
    {
        var configured = fonts;
        using var changing = await TestFontDatabase.CreateAsync(Path.Combine(root, "cache"), () => new(configured, ""));
        using var processor = new SubtitleProcessor(Path.Combine(root, "cache"), changing, runner, logger);
        using var original = new MemoryStream(input);
        await using var seeded = await processor.TransformAsync(original, "ass", default);
        Assert.NotNull(seeded);
        using var source = new BlockingReadStream(input);
        var pending = processor.TransformAsync(source, "ass", default);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        configured = "";
        source.Release.TrySetResult();
        await using var result = await pending;
        Assert.Null(result);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData("fonts.json")]
    public async Task RebuildsDamagedFontDatabase(string file)
    {
        using var source = new MemoryStream(input);
        await using var initial = await cache.TransformAsync(source, "ass", default);
        Assert.NotNull(initial);
        File.WriteAllText(Path.Combine(root, "cache", "font-index", file), "damaged");
        await database.RebuildAsync();
        using var changed = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("ABC", "AC")));
        await using var result = await cache.TransformAsync(changed, "ass", default);
        Assert.NotNull(result);
        var entries = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "cache", "font-index", "fonts.json")))!.AsArray();
        Assert.All(entries, entry => Assert.Equal(Directory.GetFiles(fonts)[0], entry!["path"]!.GetValue<string>()));
        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task SubtitleChangesAndDatabaseRebuildInvalidateCache()
    {
        async Task Generate(byte[] data) { using var s=new MemoryStream(data); await using var r=await cache.TransformAsync(s,"ass",default); Assert.NotNull(r); }
        await Generate(input);
        await Generate(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("ABC", "AB")));
        var file=Directory.GetFiles(fonts)[0];
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        await database.RebuildAsync();
        await Generate(input);
        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public async Task MissingFontWithExitZeroReturnsOriginalAndBacksOff()
    {
        var data=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("AssFonts Test", "Missing Font xyz"));
        for(var i=0;i<2;i++) {
            using var source=new MemoryStream(data);
            Assert.Null(await cache.TransformAsync(source,"ass",default));
            Assert.Equal(0,source.Position);
            Assert.Equal(data,source.ToArray());
        }
        Assert.Equal(1,runner.Calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(root,"cache","subtitles"),"*.ass"));
    }

    [Fact]
    public async Task MissingGlyphDoesNotReplaceOriginalWithIncompleteSubset()
    {
        using var source=new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("ABC","ABC👍")));
        Assert.Null(await cache.TransformAsync(source,"ass",default));
        Assert.Equal(0,source.Position);
    }

    [Fact]
    public async Task CancelledRequestKeepsInputReadable()
    {
        using var token=new CancellationTokenSource(); token.Cancel();
        using var source=new MemoryStream(input);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.TransformAsync(source,"ass",token.Token));
        Assert.Equal(0,source.Position);
    }

    [Fact]
    public async Task OtherFormatsDoNotStartNativeProcess()
    {
        using var source=new MemoryStream(input);
        Assert.Null(await cache.TransformAsync(source,"vtt",default));
        Assert.Equal(0,runner.Calls);
    }

    [Fact]
    public async Task LinkedFontsAreIncludedAndTargetChangesInvalidateCache()
    {
        var file=Directory.GetFiles(fonts)[0];var target=Path.Combine(root,"real.ttf");
        File.Move(file,target);File.CreateSymbolicLink(file,target);
        await database.RebuildAsync();
        using var first=new MemoryStream(input);
        await using var packed=await cache.TransformAsync(first,"ass",default);
        Assert.NotNull(packed);
        File.SetLastWriteTimeUtc(target,DateTime.UtcNow.AddMinutes(2));
        await database.RebuildAsync();
        using var second=new MemoryStream(input);
        await using var regenerated=await cache.TransformAsync(second,"ass",default);
        Assert.NotNull(regenerated);Assert.Equal(2,runner.Calls);
    }

    [Fact]
    public async Task LinkedFontsInLongDirectoriesAreResolved()
    {
        var directory = Path.Combine(root, new string('a', 180), "fonts");
        Directory.CreateDirectory(directory);
        var target = Directory.GetFiles(fonts)[0];
        File.CreateSymbolicLink(Path.Combine(directory, "linked.ttf"), target);
        using var longDatabase = await TestFontDatabase.CreateAsync(Path.Combine(root, "long-cache"), () => new(directory, ""));
        using var longPaths = new SubtitleProcessor(Path.Combine(root, "long-cache"), longDatabase, runner,
            NullLogger<SubtitleProcessor>.Instance);
        using var source = new MemoryStream(input);
        await using var result = await longPaths.TransformAsync(source, "ass", default);
        Assert.NotNull(result);
        Assert.Contains("[Fonts]", await new StreamReader(result!).ReadToEndAsync());
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("invalid")]
    [InlineData("timeout")]
    public async Task AllProcessingFailuresBackOffAndExpireOldFailureMarkers(string failure)
    {
        var failedRunner=new FailedRunner(failure);
        using var failing=new SubtitleProcessor(Path.Combine(root,"failure-cache"),database,failedRunner,NullLogger<SubtitleProcessor>.Instance);
        var directory=Path.Combine(root,"failure-cache","subtitles");Directory.CreateDirectory(directory);
        var old=Path.Combine(directory,"old.failed");File.WriteAllText(old,"");File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-40));
        for(var i=0;i<2;i++) {
            using var source=new MemoryStream(input);
            Assert.Null(await failing.TransformAsync(source,"ass",default));Assert.Equal(0,source.Position);
        }
        Assert.Equal(1,failedRunner.Calls);
        Assert.False(File.Exists(old));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root,"failure-cache","jobs")));
    }

    [Fact]
    public async Task PendingRequestsDoNotCreateUnboundedWorkDirectories()
    {
        var blocking=new BlockingRunner(new AssfontsRunner(Path.Combine(root,"bounded")));
        using var bounded=new SubtitleProcessor(Path.Combine(root,"bounded"),database,blocking,NullLogger<SubtitleProcessor>.Instance);
        async Task Request() { using var s=new MemoryStream(input);await using var output=await bounded.TransformAsync(s,"ass",default);Assert.NotNull(output); }
        var first=Request();await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rest=Enumerable.Range(0,5).Select(_=>Request()).ToArray();
        await Task.Delay(150);
        var directories=Directory.GetDirectories(Path.Combine(root,"bounded","jobs")).Length;
        blocking.Release.TrySetResult();await Task.WhenAll(rest.Append(first));
        Assert.Equal(1,directories);
    }

    private void Dispose() { cache.Dispose(); database.Dispose(); Directory.Delete(root,true); }

    private sealed class TestLogger(ITestOutputHelper output) : ILogger<SubtitleProcessor>
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Values)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Add((level, message, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary()));
            output.WriteLine(message + "\n" + exception);
        }
    }

    private sealed class CountingRunner(IAssfontsRunner inner) : IAssfontsRunner
    {
        private int calls;
        public int Calls=>calls;
        public Task<RunResult> RunAsync(string input,string work,FontDatabaseSnapshot fonts,CancellationToken token)
        { Interlocked.Increment(ref calls); return inner.RunAsync(input,work,fonts,token); }
    }

    private sealed class FailedRunner(string failure) : IAssfontsRunner
    {
        public int Calls { get; private set; }
        public async Task<RunResult> RunAsync(string input,string work,FontDatabaseSnapshot fonts,CancellationToken token)
        {
            Calls++;
            if(failure=="timeout") await Task.Delay(Timeout.Infinite,token);
            if(failure=="throw") throw new IOException("deliberate runner failure");
            var output=Path.Combine(work,"invalid.ass");await File.WriteAllTextAsync(output,"not a subtitle",token);
            return new RunResult(true,output,"");
        }
    }

    private sealed class BlockingReadStream(byte[] data) : MemoryStream(data)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await base.ReadAsync(buffer, token);
        }
    }

    private sealed class BlockingRunner(IAssfontsRunner inner) : IAssfontsRunner
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RunResult> RunAsync(string input,string work,FontDatabaseSnapshot fonts,CancellationToken token)
        { Entered.TrySetResult();await Release.Task.WaitAsync(token);return await inner.RunAsync(input,work,fonts,token); }
    }
}
