using System.Text;
using Jellyfin.Plugin.AssFonts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AssFonts.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string fonts;
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
        cache = new SubtitleProcessor(Path.Combine(root, "cache"), () => fonts, runner, logger);
    }

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
        using var again = new SubtitleProcessor(Path.Combine(root,"cache"),()=>fonts,runner,NullLogger<SubtitleProcessor>.Instance);
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
    public async Task SubtitleAndFontChangesInvalidateCache()
    {
        async Task Generate(byte[] data) { using var s=new MemoryStream(data); await using var r=await cache.TransformAsync(s,"ass",default); Assert.NotNull(r); }
        await Generate(input);
        await Generate(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).Replace("ABC", "AB")));
        var file=Directory.GetFiles(fonts)[0];
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
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
    public async Task EmptySettingAndOtherFormatsDoNotStartNativeProcess()
    {
        using var source=new MemoryStream(input);
        Assert.Null(await cache.TransformAsync(source,"vtt",default));
        using var disabled=new SubtitleProcessor(Path.Combine(root,"disabled"),()=>"",runner,NullLogger<SubtitleProcessor>.Instance);
        Assert.Null(await disabled.TransformAsync(source,"ass",default));
        Assert.Equal(0,runner.Calls);
    }

    [Fact]
    public async Task LinkedFontsAreIncludedAndTargetChangesInvalidateCache()
    {
        var file=Directory.GetFiles(fonts)[0];var target=Path.Combine(root,"real.ttf");
        File.Move(file,target);File.CreateSymbolicLink(file,target);
        using var first=new MemoryStream(input);
        await using var packed=await cache.TransformAsync(first,"ass",default);
        Assert.NotNull(packed);
        File.SetLastWriteTimeUtc(target,DateTime.UtcNow.AddMinutes(2));
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
        using var longPaths = new SubtitleProcessor(Path.Combine(root, "long-cache"), () => directory, runner,
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
        using var failing=new SubtitleProcessor(Path.Combine(root,"failure-cache"),()=>fonts,failedRunner,NullLogger<SubtitleProcessor>.Instance);
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
        using var bounded=new SubtitleProcessor(Path.Combine(root,"bounded"),()=>fonts,blocking,NullLogger<SubtitleProcessor>.Instance);
        async Task Request() { using var s=new MemoryStream(input);await using var output=await bounded.TransformAsync(s,"ass",default);Assert.NotNull(output); }
        var first=Request();await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rest=Enumerable.Range(0,5).Select(_=>Request()).ToArray();
        await Task.Delay(150);
        var directories=Directory.GetDirectories(Path.Combine(root,"bounded","jobs")).Length;
        blocking.Release.TrySetResult();await Task.WhenAll(rest.Append(first));
        Assert.Equal(1,directories);
    }

    public void Dispose() { cache.Dispose(); Directory.Delete(root,true); }

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
        public Task<RunResult> RunAsync(string input,string work,string fontDirectory,CancellationToken token)
        { Interlocked.Increment(ref calls); return inner.RunAsync(input,work,fontDirectory,token); }
    }

    private sealed class FailedRunner(string failure) : IAssfontsRunner
    {
        public int Calls { get; private set; }
        public async Task<RunResult> RunAsync(string input,string work,string fontDirectory,CancellationToken token)
        {
            Calls++;
            if(failure=="timeout") await Task.Delay(Timeout.Infinite,token);
            if(failure=="throw") throw new IOException("deliberate runner failure");
            var output=Path.Combine(work,"invalid.ass");await File.WriteAllTextAsync(output,"not a subtitle",token);
            return new RunResult(true,output,"");
        }
    }

    private sealed class BlockingRunner(IAssfontsRunner inner) : IAssfontsRunner
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RunResult> RunAsync(string input,string work,string fontDirectory,CancellationToken token)
        { Entered.TrySetResult();await Release.Task.WaitAsync(token);return await inner.RunAsync(input,work,fontDirectory,token); }
    }
}
