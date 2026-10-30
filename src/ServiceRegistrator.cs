using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<ISubtitleProcessor>(provider =>
        {
            var root = Path.Combine(provider.GetRequiredService<IApplicationPaths>().CachePath, "assfonts");
            return new SubtitleProcessor(root, () => Plugin.Instance?.Configuration.FontDirectory ?? string.Empty,
                new AssfontsRunner(root), provider.GetRequiredService<ILogger<SubtitleProcessor>>());
        });
        services.AddTransient<SubtitleResultFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<SubtitleResultFilter>());
    }
}
