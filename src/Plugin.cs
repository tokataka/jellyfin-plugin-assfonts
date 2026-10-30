using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AssFonts;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string FontDirectory { get; set; } = string.Empty;
    public string FallbackFont { get; set; } = string.Empty;
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "ASS Fonts";
    public override string Description => "Embed subset fonts into outgoing ASS/SSA subtitles and cache the generated response.";
    public override Guid Id => Guid.Parse("cb963c2d-9d76-48b1-9f90-5c0cd5b25eae");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [new PluginPageInfo { Name = "assfonts", EmbeddedResourcePath = "Jellyfin.Plugin.AssFonts.Configuration.configPage.html" }];
}
