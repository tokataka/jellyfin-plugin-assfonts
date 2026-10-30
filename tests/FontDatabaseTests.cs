using System.Reflection;
using Jellyfin.Plugin.AssFonts;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssFonts.Tests;

public sealed class FontDatabaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-database-" + Guid.NewGuid().ToString("N"));
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts");

    [Fact]
    public async Task HostedServiceBuildsOnStartupAndConfigurationSave()
    {
        Directory.CreateDirectory(root);
        var initial = new PluginConfiguration { FontDirectory = Fonts };
        var paths = DispatchProxy.Create<IApplicationPaths, TestProxy>();
        ((TestProxy)(object)paths).Handler = (_, _) => root;
        var serializer = DispatchProxy.Create<IXmlSerializer, TestProxy>();
        ((TestProxy)(object)serializer).Handler = (method, _) => method.Name == "DeserializeFromFile" ? initial : null;
        var plugin = new Plugin(paths, serializer);
        using var database = new FontDatabase(root, () => new(plugin.Configuration.FontDirectory, plugin.Configuration.FallbackFont), NullLogger<FontDatabase>.Instance);
        using var service = new FontDatabaseService(database, NullLogger<FontDatabaseService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(timeout.Token);
        try
        {
            var started = await database.GetSnapshotAsync(timeout.Token);
            Assert.Equal(1, started.FontCount);
            plugin.UpdateConfiguration(new PluginConfiguration());
            while (database.Current!.Settings.Directory != "" || database.IsRebuilding)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(0, database.Current.FontCount);
            Assert.True(database.Current.BuiltAtUtc > started.BuiltAtUtc);
            Assert.Equal("[]", File.ReadAllText(Path.Combine(root, "font-index", "fonts.json")));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnreadableOptionalFallbackDoesNotBlockDirectoryDatabase()
    {
        Directory.CreateDirectory(root);
        var fallback = Path.Combine(root, "locked.ttf");
        File.Copy(Path.Combine(Fonts, "test.ttf"), fallback);
        using var locked = new FileStream(fallback, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var database = new FontDatabase(root, () => new(Fonts, fallback), NullLogger<FontDatabase>.Instance);
        var result = await database.RebuildAsync();
        Assert.Equal(1, result.FontCount);
        Assert.Null(result.Fallback);
        Assert.True(database.IsCurrent(result));
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
