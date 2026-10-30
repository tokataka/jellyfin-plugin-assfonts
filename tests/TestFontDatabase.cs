using Jellyfin.Plugin.AssFonts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssFonts.Tests;

internal static class TestFontDatabase
{
    public static async Task<FontDatabase> CreateAsync(string root, Func<FontSettings> settings)
    {
        var database = new FontDatabase(root, settings, NullLogger<FontDatabase>.Instance);
        await database.RebuildAsync();
        return database;
    }
}
