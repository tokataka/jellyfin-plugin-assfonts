using System.Globalization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssFonts;

public sealed class SubtitleResultFilter(ISubtitleProcessor processor, ILogger<SubtitleResultFilter> logger,
    ILibraryManager library, IMediaSourceManager mediaSources) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (context.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerName == "Subtitle"
            && action.ControllerTypeInfo.FullName == "Jellyfin.Api.Controllers.SubtitleController"
            && action.ActionName is "GetSubtitle" or "GetSubtitleWithTicks"
            && HttpMethods.IsGet(request.Method)
            && context.HttpContext.User.Identity?.IsAuthenticated == true
            && context.HttpContext.Response.StatusCode < 400
            && !context.HttpContext.Response.HasStarted)
        {
            var format = request.Query["format"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(format)) format = request.RouteValues["routeFormat"]?.ToString();
            format = format?.ToLowerInvariant();
            if (format is "ass" or "ssa") await TransformAsync(context, format);
        }
        await next();
    }

    private async Task TransformAsync(ResultExecutingContext context, string format)
    {
        Stream? original = null;
        var ownsOriginal = false;
        if (context.Result is FileStreamResult streamResult) original = streamResult.FileStream;
        else if (context.Result is FileContentResult bytesResult)
        {
            original = new MemoryStream(bytesResult.FileContents, writable: false);
            ownsOriginal = true;
        }
        if (original is null) return;
        try
        {
            var names = await GetLogContextAsync(context.HttpContext.Request);
            var transformed = await processor.TransformAsync(original, format, context.HttpContext.RequestAborted, names);
            if (transformed is null) return;
            var previous = (FileResult)context.Result;
            context.Result = new FileStreamResult(transformed, previous.ContentType)
            {
                FileDownloadName = previous.FileDownloadName,
                EnableRangeProcessing = previous.EnableRangeProcessing
            };
            context.HttpContext.Response.Headers.Remove("Content-Length");
            context.HttpContext.Response.Headers.Remove("ETag");
            context.HttpContext.Response.Headers.Remove("Last-Modified");
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            await original.DisposeAsync();
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "ASS Fonts response conversion failed; retaining original subtitle.");
        }
        finally { if (ownsOriginal) await original.DisposeAsync(); }
    }

    private async Task<SubtitleLogContext> GetLogContextAsync(HttpRequest request)
    {
        string? Parameter(string query, string route)
        {
            var value = request.Query[query].FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? request.RouteValues[route]?.ToString() : value;
        }
        var subtitleName = "unknown";
        try
        {
            if (Guid.TryParse(Parameter("itemId", "routeItemId"), out var itemId)
                && int.TryParse(Parameter("index", "routeIndex"), CultureInfo.InvariantCulture, out var index))
            {
                var item = library.GetItemById(itemId);
                if (item is not null)
                {
                    var source = await mediaSources.GetMediaSource(item,
                        Parameter("mediaSourceId", "routeMediaSourceId"), null!, false, request.HttpContext.RequestAborted);
                    var subtitle = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);
                    if (subtitle is not null)
                        subtitleName = subtitle.IsExternal ? FileName(subtitle.Path, $"external subtitle #{index}")
                            : $"embedded subtitle #{index}";
                }
            }
        }
        catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "ASS Fonts could not resolve request filenames for logging.");
        }
        return new SubtitleLogContext(subtitleName);
    }

    private static string FileName(string? path, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(path) ? fallback : Path.GetFileName(path);
        return new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }
}
