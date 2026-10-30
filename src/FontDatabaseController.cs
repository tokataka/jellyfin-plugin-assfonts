using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

[ApiController]
[Route("AssFonts/FontDatabase")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class FontDatabaseController(FontDatabase database, ILogger<FontDatabaseController> logger) : ControllerBase
{
    [HttpGet]
    public object Status() => new { LastBuiltUtc = database.Current?.BuiltAtUtc, database.IsRebuilding,
        FontCount = database.Current?.FontCount ?? 0, database.LastError };

    [HttpPost("Rebuild")]
    public async Task<ActionResult> Rebuild(CancellationToken token)
    {
        try
        {
            await database.RebuildAsync(token);
            return Ok(Status());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Manual font database rebuild failed.");
            return Problem("Unable to rebuild the font database. Check the server log for details.");
        }
    }
}
