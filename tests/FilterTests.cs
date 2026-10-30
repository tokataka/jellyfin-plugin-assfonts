using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using System.Reflection;
using System.Text;
using System.Security.Claims;
using Jellyfin.Plugin.AssFonts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssFonts.Tests;

public sealed class FilterTests
{
    private static ResultExecutingContext Context(IActionResult result,string action="GetSubtitle",string format="ass",string controller="Subtitle")
    {
        var http=new DefaultHttpContext(); http.Request.Method="GET";
        http.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"test")],"Test"));
        http.Request.RouteValues["routeFormat"]=format;
        var descriptor=new ControllerActionDescriptor { ControllerName=controller, ActionName=action,
            ControllerTypeInfo=typeof(Jellyfin.Api.Controllers.SubtitleController).GetTypeInfo() };
        return new ResultExecutingContext(new ActionContext(http,new RouteData(),descriptor,new ModelStateDictionary()),
            [],result,new object());
    }

    [Theory]
    [InlineData("GetSubtitle")]
    [InlineData("GetSubtitleWithTicks")]
    public async Task ReplacesOnlySuccessfulSubtitleStreamAndPreservesContentType(string action)
    {
        using var source=new MemoryStream([1,2,3]);
        var context=Context(new FileStreamResult(source,"text/x-ssa"),action);
        var engine=new StubProcessor();
        var filter=new SubtitleResultFilter(engine,NullLogger<SubtitleResultFilter>.Instance, Proxy<ILibraryManager>(), Proxy<IMediaSourceManager>());
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(context,[],context.Result,new object())));
        var result=Assert.IsType<FileStreamResult>(context.Result);
        Assert.Equal("text/x-ssa",result.ContentType);
        Assert.False(source.CanRead);
        using var output=result.FileStream;
        Assert.Equal("packed",await new StreamReader(output).ReadToEndAsync());
        Assert.Equal(1,engine.Calls);
    }

    [Theory]
    [InlineData("GetSubtitle","vtt","Subtitle")]
    [InlineData("GetRemoteSubtitles","ass","Subtitle")]
    [InlineData("GetSubtitle","ass","Other")]
    public async Task UnrelatedResponsesAreUntouched(string action,string format,string controller)
    {
        using var source=new MemoryStream([1,2,3]);
        var original=new FileStreamResult(source,"text/plain"); var context=Context(original,action,format,controller);
        var engine=new StubProcessor(); var filter=new SubtitleResultFilter(engine,NullLogger<SubtitleResultFilter>.Instance, Proxy<ILibraryManager>(), Proxy<IMediaSourceManager>());
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(context,[],context.Result,new object())));
        Assert.Same(original,context.Result); Assert.True(source.CanRead); Assert.Equal(0,engine.Calls);
    }

    [Fact]
    public async Task QueryFormatOverrideIsRespected()
    {
        using var source=new MemoryStream([1]);var original=new FileStreamResult(source,"text/vtt");var context=Context(original);
        context.HttpContext.Request.QueryString=new QueryString("?format=vtt");
        var engine=new StubProcessor();await new SubtitleResultFilter(engine,NullLogger<SubtitleResultFilter>.Instance, Proxy<ILibraryManager>(), Proxy<IMediaSourceManager>())
            .OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(context,[],context.Result,new object())));
        Assert.Same(original,context.Result);Assert.Equal(0,engine.Calls);
    }

    [Fact]
    public async Task FailedConversionLeavesOriginalStreamAndHeaders()
    {
        using var source=new MemoryStream([1,2,3]);var original=new FileStreamResult(source,"text/x-ssa");var context=Context(original);
        context.HttpContext.Response.Headers.ETag="old";
        var engine=new StubProcessor { Fail=true };await new SubtitleResultFilter(engine,NullLogger<SubtitleResultFilter>.Instance, Proxy<ILibraryManager>(), Proxy<IMediaSourceManager>())
            .OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(context,[],context.Result,new object())));
        Assert.Same(original,context.Result);Assert.True(source.CanRead);Assert.Equal(0,source.Position);
        Assert.Equal("old",context.HttpContext.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task AnonymousRequestDoesNotReceiveAdditionalServerFonts()
    {
        using var source=new MemoryStream([1,2,3]);var original=new FileStreamResult(source,"text/x-ssa");var context=Context(original);
        context.HttpContext.User=new ClaimsPrincipal(new ClaimsIdentity());
        var engine=new StubProcessor();await new SubtitleResultFilter(engine,NullLogger<SubtitleResultFilter>.Instance, Proxy<ILibraryManager>(), Proxy<IMediaSourceManager>())
            .OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(context,[],context.Result,new object())));
        Assert.Same(original,context.Result);Assert.Equal(0,engine.Calls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task LogsSelectedSubtitleRatherThanTemporaryResponseFile(bool queryOverride, bool embedded, bool emptyQuery)
    {
        using var original = new MemoryStream([1, 2, 3]);
        var context = Context(new FileStreamResult(original, "text/x-ssa"));
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        context.HttpContext.Request.RouteValues["routeItemId"] = queryOverride ? Guid.Empty.ToString() : itemId.ToString();
        context.HttpContext.Request.RouteValues["routeMediaSourceId"] = queryOverride ? "wrong-version" : "selected-version";
        context.HttpContext.Request.RouteValues["routeIndex"] = queryOverride ? "1" : "4";
        if (queryOverride)
            context.HttpContext.Request.QueryString = new QueryString($"?itemId={itemId}&mediaSourceId=selected-version&index=4");
        if (emptyQuery)
            context.HttpContext.Request.QueryString = new QueryString("?itemId=&mediaSourceId=%20&index=");
        var item = new Video { Id = itemId, Path = Path.Combine("library", "default.mkv"), Name = "Episode title" };
        var library = Proxy<ILibraryManager>((method, args) =>
        {
            Assert.Equal("GetItemById", method!.Name);
            Assert.Equal(itemId, args![0]);
            return item;
        });
        var media = Proxy<IMediaSourceManager>((method, args) =>
        {
            Assert.Equal("GetMediaSource", method!.Name);
            Assert.Same(item, args![0]);
            Assert.Equal("selected-version", args[1]);
            return Task.FromResult(new MediaSourceInfo
            {
                Path = Path.Combine("library", "selected.mkv"),
                MediaStreams = [new MediaStream { Index = 1, Type = MediaStreamType.Subtitle,
                    IsExternal = true, Path = Path.Combine("library", "wrong.ass") },
                    new MediaStream { Index = 4, Type = MediaStreamType.Subtitle,
                    IsExternal = !embedded, Path = embedded ? null : Path.Combine("library", "selected.ko.ass") }]
            });
        });
        var processor = new StubProcessor();
        await new SubtitleResultFilter(processor, NullLogger<SubtitleResultFilter>.Instance, library, media)
            .OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, new object())));
        using var result = Assert.IsType<FileStreamResult>(context.Result).FileStream;
        Assert.Equal(embedded ? "embedded subtitle #4" : "selected.ko.ass", processor.Context!.SubtitleName);
    }

    [Fact]
    public async Task MetadataLookupFailureDoesNotPreventSubtitleProcessing()
    {
        using var source = new MemoryStream([1, 2, 3]);
        var context = Context(new FileStreamResult(source, "text/x-ssa"));
        context.HttpContext.Request.RouteValues["routeItemId"] = Guid.NewGuid().ToString();
        context.HttpContext.Request.RouteValues["routeIndex"] = "1";
        var library = Proxy<ILibraryManager>((_, _) => throw new IOException("metadata unavailable"));
        var processor = new StubProcessor();
        await new SubtitleResultFilter(processor, NullLogger<SubtitleResultFilter>.Instance, library, Proxy<IMediaSourceManager>())
            .OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, new object())));
        using var result = Assert.IsType<FileStreamResult>(context.Result).FileStream;
        Assert.Equal("packed", await new StreamReader(result).ReadToEndAsync());
        Assert.Equal(new SubtitleLogContext("unknown"), processor.Context);
    }

    private sealed class StubProcessor : ISubtitleProcessor
    {
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public SubtitleLogContext? Context { get; private set; }
        public Task<Stream?> TransformAsync(Stream input,string extension,CancellationToken token, SubtitleLogContext? context = null)
        { Calls++; Context = context; return Task.FromResult<Stream?>(Fail ? null : new MemoryStream(Encoding.UTF8.GetBytes("packed"))); }
    }
    private static T Proxy<T>(Func<MethodInfo?, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, EmptyProxy>();
        ((EmptyProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public class EmptyProxy : DispatchProxy
    {
        public Func<MethodInfo?, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler?.Invoke(targetMethod, args);
    }

}
