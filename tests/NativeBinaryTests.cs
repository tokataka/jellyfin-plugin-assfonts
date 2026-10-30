using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Jellyfin.Plugin.AssFonts;
using Xunit;

namespace AssFonts.Tests;

public sealed class NativeBinaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-native-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("linux-x64", "5e2ef6e6f819f7a989ddb64cd9090d75c64b3e5db7b13fc3bb4db5948914b17e")]
    [InlineData("linux-arm64", "fb188fecee148d337c3987c81570134403408a3f5269fb21de740e2d9e981e24")]
    [InlineData("win-x64", "c5d9e4a118924c970b1fa9a2b484181dbb29a30b4117517bb502dc67e0c181e1")]
    [InlineData("win-arm64", "6a01519d45cb6b500cead3afde430229f8b63410e5197273b1574c1816eb11e3")]
    [InlineData("osx-x64", "158576e148b4dae9ab1dc8a23b06b9681cd5315c5f585940af9a978a0ea4fe88")]
    [InlineData("osx-arm64", "9e7d1b6bfff3169e02b610430320327986102b41b46fdcb8981fb9b2bd4728d8")]
    public async Task EmbeddedExecutablesMatchOfficialRelease(string runtimeId, string checksum)
    {
        await using var resource = typeof(AssfontsRunner).Assembly.GetManifestResourceStream("AssFonts.Native." + runtimeId);
        Assert.NotNull(resource);
        Assert.Equal(checksum, Convert.ToHexStringLower(await SHA256.HashDataAsync(resource)));
    }

    [Theory]
    [InlineData("LINUX", Architecture.X64, "linux-x64", "assfonts")]
    [InlineData("LINUX", Architecture.Arm64, "linux-arm64", "assfonts")]
    [InlineData("WINDOWS", Architecture.X64, "win-x64", "assfonts.exe")]
    [InlineData("WINDOWS", Architecture.Arm64, "win-arm64", "assfonts.exe")]
    [InlineData("OSX", Architecture.X64, "osx-x64", "assfonts")]
    [InlineData("OSX", Architecture.Arm64, "osx-arm64", "assfonts")]
    public async Task SelectsExecutableForTheProcessPlatform(string os, Architecture architecture, string runtimeId, string fileName)
    {
        var binary = NativeBinary.Select(OSPlatform.Create(os), architecture);
        Assert.Equal(runtimeId, binary.RuntimeId);
        Assert.Equal(fileName, binary.FileName);
        await using var resource = typeof(NativeBinary).Assembly.GetManifestResourceStream(binary.ResourceName);
        Assert.NotNull(resource);
        Assert.Equal(binary.Checksum, Convert.ToHexStringLower(await SHA256.HashDataAsync(resource)));
    }

    [Theory]
    [InlineData("LINUX", Architecture.X86)]
    [InlineData("LINUX", Architecture.Arm)]
    [InlineData("WINDOWS", Architecture.X86)]
    [InlineData("OSX", Architecture.X86)]
    [InlineData("FREEBSD", Architecture.X64)]
    public void RejectsUnsupportedProcessPlatforms(string os, Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(() => NativeBinary.Select(OSPlatform.Create(os), architecture));
    }

    [Fact]
    public async Task ExtractedExecutableRunsOnTheCurrentHost()
    {
        var path = await NativeBinary.ExtractAsync(root, default);
        Assert.Equal(Path.Combine(root, "bin", NativeBinary.Current.Checksum, NativeBinary.Current.FileName), path);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("assfonts v0.7.3", await output + await error);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task ReusesVerifiedExecutableWithoutRewritingIt()
    {
        var path = await NativeBinary.ExtractAsync(root, default);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var modified = File.GetLastWriteTimeUtc(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(path, await NativeBinary.ExtractAsync(root, default));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task ReplacesCorruptCachedExecutable()
    {
        var path = await NativeBinary.ExtractAsync(root, default);
        await File.WriteAllTextAsync(path, "corrupt executable");
        Assert.Equal(path, await NativeBinary.ExtractAsync(root, default));
        await using var binary = File.OpenRead(path);
        Assert.Equal(NativeBinary.Current.Checksum, Convert.ToHexStringLower(await SHA256.HashDataAsync(binary)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ConcurrentExtractionsLeaveOneVerifiedExecutable()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => NativeBinary.ExtractAsync(root, default)));
        Assert.All(paths, path => Assert.Equal(paths[0], path));
        await using var binary = File.OpenRead(paths[0]);
        Assert.Equal(NativeBinary.Current.Checksum, Convert.ToHexStringLower(await SHA256.HashDataAsync(binary)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(paths[0])!));
    }

    [Fact]
    public async Task CancellationDoesNotCreateAnExecutable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeBinary.ExtractAsync(root, cancellation.Token));
        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
