using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.AssFonts;

public sealed record NativeBinaryDescriptor(string RuntimeId, string Checksum)
{
    public string FileName => RuntimeId.StartsWith("win-", StringComparison.Ordinal) ? "assfonts.exe" : "assfonts";
    public string ResourceName => "AssFonts.Native." + RuntimeId;
}

public static class NativeBinary
{
    private static readonly SemaphoreSlim ExtractionLock = new(1, 1);

    public static NativeBinaryDescriptor Current => Select(
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsLinux() ? OSPlatform.Linux :
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("UNKNOWN"),
        RuntimeInformation.ProcessArchitecture);

    public static NativeBinaryDescriptor Select(OSPlatform os, Architecture architecture)
    {
        var platform = os == OSPlatform.Linux ? "linux" : os == OSPlatform.Windows ? "win" : os == OSPlatform.OSX ? "osx" : "unsupported";
        var cpu = architecture == Architecture.X64 ? "x64" : architecture == Architecture.Arm64 ? "arm64" : "unsupported";
        var runtimeId = platform + "-" + cpu;
        var checksum = runtimeId switch
        {
            "linux-x64" => "5e2ef6e6f819f7a989ddb64cd9090d75c64b3e5db7b13fc3bb4db5948914b17e",
            "linux-arm64" => "fb188fecee148d337c3987c81570134403408a3f5269fb21de740e2d9e981e24",
            "win-x64" => "c5d9e4a118924c970b1fa9a2b484181dbb29a30b4117517bb502dc67e0c181e1",
            "win-arm64" => "6a01519d45cb6b500cead3afde430229f8b63410e5197273b1574c1816eb11e3",
            "osx-x64" => "158576e148b4dae9ab1dc8a23b06b9681cd5315c5f585940af9a978a0ea4fe88",
            "osx-arm64" => "9e7d1b6bfff3169e02b610430320327986102b41b46fdcb8981fb9b2bd4728d8",
            _ => throw new PlatformNotSupportedException($"Bundled assfonts does not support {os} {architecture}. Supported platforms: Linux, Windows and macOS on x64 or ARM64.")
        };
        return new NativeBinaryDescriptor(runtimeId, checksum);
    }

    public static async Task<string> ExtractAsync(string cacheRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var binary = Current;
        // Serializing extraction also avoids replacing an executable while another request verifies it on Windows.
        await ExtractionLock.WaitAsync(token);
        try
        {
            var directory = Path.Combine(cacheRoot, "bin", binary.Checksum);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, binary.FileName);
            if (File.Exists(path) && await HasExpectedChecksumAsync(path, binary.Checksum, token))
            {
                MakeExecutable(path);
                return path;
            }

            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var resource = typeof(NativeBinary).Assembly.GetManifestResourceStream(binary.ResourceName)
                    ?? throw new InvalidOperationException($"Bundled assfonts resource is missing: {binary.RuntimeId}."))
                await using (var output = File.Create(temporary))
                    await resource.CopyToAsync(output, token);
                if (!await HasExpectedChecksumAsync(temporary, binary.Checksum, token))
                    throw new InvalidDataException($"Bundled assfonts checksum mismatch: {binary.RuntimeId}.");
                MakeExecutable(temporary);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { ExtractionLock.Release(); }
    }

    private static async Task<bool> HasExpectedChecksumAsync(string path, string checksum, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) == checksum;
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
