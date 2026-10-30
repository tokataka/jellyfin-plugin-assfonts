using System.Reflection;
using Jellyfin.Plugin.AssFonts;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssFonts.Tests;

public sealed class FontDatabaseControllerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "assfonts-controller-" + Guid.NewGuid().ToString("N"));
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "Fixtures/fonts");

    [Fact]
    public async Task ManualRebuildUpdatesTimestampEvenWithoutFontChanges()
    {
        using var database = await TestFontDatabase.CreateAsync(root, () => new(Fonts, ""));
        var original = database.Current!;
        var controller = new FontDatabaseController(database, NullLogger<FontDatabaseController>.Instance);
        Assert.IsType<OkObjectResult>(await controller.Rebuild(default));
        Assert.True(database.Current!.BuiltAtUtc > original.BuiltAtUtc);
        Assert.Equal(original.Fonts, database.Current.Fonts);
        Assert.False(File.Exists(Path.Combine(root, "font-index", "state.json")));
    }

    [Fact]
    public void DatabaseEndpointsRequireAdministratorAuthorization()
    {
        var controller = typeof(FontDatabaseController);
        Assert.Equal(Policies.RequiresElevation, controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.All(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
