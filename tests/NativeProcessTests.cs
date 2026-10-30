using Jellyfin.Plugin.AssFonts;
using Xunit;
using Xunit.Abstractions;

namespace AssFonts.Tests;

public sealed class NativeProcessTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-process-" + Guid.NewGuid().ToString("N"));

    private async Task<(bool Success, string Detail)> Run(string mode, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        return await AssfontsNative.ExecuteAsync("dotnet", root,
            new[] { Path.Combine(AppContext.BaseDirectory, "NativeProcessFixture/NativeProcessFixture.dll"), mode, Path.Combine(root, "attempts") }, token);
    }

    [Fact]
    public async Task RetriesOnceWhenNativeProcessStallsAfterWritingOutput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Run("stall-once", timeout.Token);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "attempts")).Length);
    }

    [Fact]
    public async Task RepeatedShutdownStallsDoNotRetryForever()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Run("always-stall", timeout.Token);
        Assert.False(result.Success);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "attempts")).Length);
    }

    [Fact]
    public async Task CompletionMessageDoesNotHideNativeErrors()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.False((await Run("late-error", timeout.Token)).Success);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "attempts")));
    }

    [Fact]
    public async Task SlowProcessingBeforeCompletionIsNotRestarted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Run("slow-success", timeout.Token);
        Assert.True(result.Success, result.Detail);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "attempts")));
    }

    [Fact]
    public async Task CancellationWithoutCompletionDoesNotRetry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = Run("no-completion", timeout.Token);
        while (!File.Exists(Path.Combine(root, "attempts")) || new FileInfo(Path.Combine(root, "attempts")).Length < 8)
            await Task.Delay(10, timeout.Token);
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "attempts")));
    }

    [Fact]
    public async Task RepeatedRealNativeSubsettingCompletes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var database = await TestFontDatabase.CreateAsync(root, () => new(Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts"), ""));
        var binary = await NativeBinary.ExtractAsync(root, timeout.Token);
        var recovered = 0;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var result = await AssfontsNative.ExecuteAsync(binary, root,
                new[] { "-d", Path.Combine(root, "font-index"), "-o", root, "-m", "1", "-i", Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.ass") }, timeout.Token);
            Assert.True(result.Success, result.Detail);
            if (result.Detail.Contains("Retried assfonts")) recovered++;
        }
        output.WriteLine($"200 native invocations completed; recovered shutdown stalls: {recovered}.");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
