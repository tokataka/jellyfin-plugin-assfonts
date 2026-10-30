using System.Threading.Channels;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

public sealed class FontDatabaseService(FontDatabase database, ILogger<FontDatabaseService> logger) : BackgroundService
{
    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plugin = Plugin.Instance;
        void Changed(object? sender, BasePluginConfiguration configuration) => requests.Writer.TryWrite(true);
        if (plugin is not null) plugin.ConfigurationChanged += Changed;
        requests.Writer.TryWrite(true);
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(stoppingToken))
            {
                try { await database.RebuildAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning(exception, "Could not rebuild the font database."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { if (plugin is not null) plugin.ConfigurationChanged -= Changed; }
    }
}
