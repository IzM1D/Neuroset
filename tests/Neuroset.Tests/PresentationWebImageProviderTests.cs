using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Neuroset.Application;
using Neuroset.Tools;
using NSubstitute;

namespace Neuroset.Tests;

public sealed class PresentationWebImageProviderTests
{
    [Theory]
    [InlineData("Switzerland flag", "Flag of Switzerland")]
    [InlineData("Coat of arms Switzerland", "Coat of arms of Switzerland")]
    [InlineData("Switzerland coat of arms", "Coat of arms of Switzerland")]
    public async Task Country_Symbols_Use_Exact_Commons_Titles_And_Raster_Thumbnails_With_Attribution(string query, string title)
    {
        using var h = new Harness(uri => uri.Host == "commons.wikimedia.org"
            ? Json(new { query = new { pages = new Dictionary<string, object> { ["123"] = Page(title + ".svg", "https://images.example/symbol.svg", "https://images.example/symbol.png") } } })
            : Content([1, 2, 3], "image/png"));
        var result = await h.Provider.ResolveAsync(new(query));
        Uri.UnescapeDataString(h.Requests[0].Query).Should().Contain("titles=File:" + title + ".svg");
        h.Requests.Should().HaveCount(2);
        result.ImageUrl.Should().Be("https://images.example/symbol.png");
        result.SourcePage.Should().Contain("commons.wikimedia.org/wiki/File:");
        result.Attribution.Should().Contain("Author & Co").And.Contain("CC BY 4.0").And.NotContain("<a");
        result.PngBytes.Should().Equal(Harness.Png);
        h.Decoded.Should().ContainSingle().Which.Should().Equal(1, 2, 3);
        await h.Search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }

    [Fact]
    public async Task Commons_Search_Skips_An_Unrelated_Coat_Of_Arms_And_Decodes_The_Relevant_Photo()
    {
        using var h = new Harness(uri => uri.Host != "commons.wikimedia.org" ? Content([4, 5], "image/jpeg")
            : uri.Query.Contains("titles=", StringComparison.Ordinal) ? Json(new { })
            : Json(new { query = new { pages = new Dictionary<string, object>
            {
                ["1"] = Page("Coat of arms of Benno Walter Gut.svg", "https://images.example/unrelated.svg", "https://images.example/unrelated.png"),
                ["2"] = Page("Switzerland coat of arms photo.jpg", "https://images.example/swiss.jpg")
            } } }));
        var result = await h.Provider.ResolveAsync(new("Coat of arms Switzerland"));
        result.ImageUrl.Should().Be("https://images.example/swiss.jpg");
        h.Requests.Should().NotContain(uri => uri.AbsoluteUri.Contains("unrelated", StringComparison.Ordinal));
        h.Decoded.Should().ContainSingle().Which.Should().Equal(4, 5);
    }

    [Fact]
    public async Task Long_Geneva_Query_Relaxes_Descriptions_To_Find_A_Real_Commons_Photo_Without_Stock_Sites()
    {
        const string query = "Geneva Switzerland Lake Geneva Jet d Eau city panorama";
        using var h = new Harness(uri => uri.Host != "commons.wikimedia.org" ? Content([1], "image/jpeg")
            : !Uri.UnescapeDataString(uri.Query).Contains("gsrsearch=Geneva Switzerland Lake Jet d Eau&", StringComparison.Ordinal) ? Json(new { })
            : Json(new { query = new { pages = new Dictionary<string, object> { ["1"] = Page("Jet dEau, Rainbow.jpg", "https://images.example/geneva.jpg") } } }));
        var result = await h.Provider.ResolveAsync(new(query));
        result.ImageUrl.Should().Be("https://images.example/geneva.jpg");
        h.Requests.Should().HaveCountLessThan(10);
        await h.Search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }

    [Fact]
    public async Task A_Country_Map_Cannot_Be_Replaced_With_A_Neighbours_Map_Or_An_Unrelated_Country_Photo()
    {
        using var h = new Harness(uri => uri.Host != "commons.wikimedia.org" ? Content([1], "image/png")
            : uri.Query.Contains("titles=", StringComparison.Ordinal) ? Json(new { })
            : Json(new { query = new { pages = new Dictionary<string, object>
            {
                ["1"] = Page("Liechtenstein topographic map-de.png", "https://images.example/neighbour.png"),
                ["2"] = Page("Switzerland red deer.jpg", "https://images.example/deer.jpg"),
                ["3"] = Page("Switzerland location map.png", "https://images.example/map.png")
            } } }));
        var result = await h.Provider.ResolveAsync(new("Switzerland map neighbouring countries Germany France Italy Austria Liechtenstein"));
        result.ImageUrl.Should().Be("https://images.example/map.png");
        h.Requests.Should().NotContain(uri => uri.AbsolutePath.Contains("neighbour", StringComparison.Ordinal) || uri.AbsolutePath.Contains("deer.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Language_Map_Cannot_Be_Replaced_By_A_General_Geographic_Map()
    {
        using var h = new Harness(uri => uri.Host != "commons.wikimedia.org" ? Content([1], "image/png")
            : uri.Query.Contains("titles=", StringComparison.Ordinal) ? Json(new { })
            : Json(new { query = new { pages = new Dictionary<string, object>
            {
                ["1"] = Page("General Map of Switzerland.jpg", "https://images.example/geographic.png"),
                ["2"] = Page("Linguistic map of Switzerland.png", "https://images.example/languages.png")
            } } }));
        var result = await h.Provider.ResolveAsync(new("Switzerland four national languages linguistic regions map German French Italian Romansh"));
        result.ImageUrl.Should().Be("https://images.example/languages.png");
        h.Requests.Should().NotContain(uri => uri.AbsolutePath == "/geographic.png");
    }

    [Fact]
    public async Task Unrelated_Search_Pages_And_Stock_Previews_Are_Never_Downloaded_As_Slide_Photos()
    {
        using var h = new Harness(uri => uri.Host == "commons.wikimedia.org" ? Json(new { }) : Content([1], "image/png"));
        h.Search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse([
            new("Microsoft Bluetooth troubleshooting", "https://learn.microsoft.com/bluetooth", "Windows help", "test"),
            new("Geneva Switzerland photo", "https://www.shutterstock.com/stock-photo/geneva", "Geneva city", "test"),
            new("Geneva city on Lake Geneva", "https://images.example/geneva.png", "Switzerland", "test")], []));
        var result = await h.Provider.ResolveAsync(new("Geneva Switzerland Lake Geneva Jet d Eau city panorama"));
        result.ImageUrl.Should().Be("https://images.example/geneva.png");
        h.Requests.Should().NotContain(uri => uri.Host.Contains("microsoft", StringComparison.Ordinal) || uri.Host.Contains("shutterstock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_Forbidden_Source_Is_Skipped_On_Resume_And_Another_URL_Can_Be_Used()
    {
        using var h = new Harness(uri => uri.Host == "commons.wikimedia.org" ? Json(new { })
            : uri.AbsolutePath == "/forbidden" ? new(HttpStatusCode.Forbidden) : Content([1], "image/png"));
        Func<Task> fail = () => h.Provider.ResolveAsync(new("Bern", "https://images.example/forbidden"));
        await fail.Should().ThrowAsync<InvalidDataException>();
        h.Search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse([new("Bern", "https://images.example/alternative.png", "", "test")], []));
        var result = await h.Provider.ResolveAsync(new("Bern", "https://images.example/forbidden"));
        result.ImageUrl.Should().Be("https://images.example/alternative.png");
        h.Requests.Count(uri => uri.AbsolutePath == "/forbidden").Should().Be(1);
    }

    [Fact]
    public async Task Wikimedia_RetryAfter_Prevents_Repeating_Rate_Limited_API_Requests()
    {
        using var h = new Harness(_ => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } });
        Func<Task> resolve = () => h.Provider.ResolveAsync(new("Geneva Switzerland Lake Geneva Jet d Eau city panorama"));
        await resolve.Should().ThrowAsync<InvalidDataException>();
        await resolve.Should().ThrowAsync<InvalidDataException>();
        h.Requests.Where(uri => uri.Host == "commons.wikimedia.org").Should().ContainSingle();
        h.Decoded.Should().BeEmpty();
    }

    [Fact]
    public async Task Rate_Limited_Commons_Uses_An_Independent_Image_Index_With_Actual_Source_And_License()
    {
        using var h = new Harness(uri => uri.Host == "commons.wikimedia.org" ? new(HttpStatusCode.TooManyRequests)
            : uri.Host == "api.openverse.org" ? Json(new { results = new[] { new { title = "Geneva Switzerland Jet d Eau", url = "https://images.example/geneva.jpg",
                foreign_landing_url = "https://www.flickr.com/photos/photographer/geneva", creator = "Photographer", license = "by", license_version = "4.0", license_url = "https://creativecommons.org/licenses/by/4.0/" } } })
            : Content([1], "image/jpeg"));
        var result = await h.Provider.ResolveAsync(new("Geneva Switzerland Lake Geneva Jet d Eau city panorama"));
        result.ImageUrl.Should().Be("https://images.example/geneva.jpg");
        result.SourcePage.Should().Be("https://www.flickr.com/photos/photographer/geneva");
        result.Attribution.Should().Contain("Photographer").And.Contain("https://creativecommons.org/licenses/by/4.0/");
        await h.Search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }

    [Fact]
    public async Task Alternative_Commons_File_Page_Uses_The_Raster_Asset_And_Real_Attribution()
    {
        using var h = new Harness(uri => uri.Host == "commons.wikimedia.org"
            ? Json(new { query = new { pages = new Dictionary<string, object> { ["123"] = Page("Flag of Switzerland.svg", "https://images.example/flag.svg", "https://images.example/flag.png") } } })
            : Content([1], "image/png"));
        var result = await h.Provider.ResolveAsync(new("Switzerland flag", "https://commons.wikimedia.org/wiki/File:Flag_of_Switzerland.svg"));
        result.ImageUrl.Should().Be("https://images.example/flag.png");
        result.SourcePage.Should().Be("https://commons.wikimedia.org/wiki/File:Flag_of_Switzerland.svg");
        result.Attribution.Should().Contain("CC BY 4.0");
        h.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Public_Page_Follows_Validated_Redirects_And_Resolves_Relative_OpenGraph_Images()
    {
        using var h = new Harness(uri => uri.AbsolutePath switch
        {
            "/start" => Redirect("/articles/city"),
            "/articles/city" => Content(Encoding.UTF8.GetBytes("<html><meta property='og:image' content='../photos/city.jpg?size=large&amp;download=1'></html>"), "text/html"),
            _ => Content([7, 8, 9], "image/jpeg")
        });
        var result = await h.Provider.ResolveAsync(new("Bern", "https://sources.example/start"));
        result.SourcePage.Should().Be("https://sources.example/articles/city");
        result.ImageUrl.Should().Be("https://sources.example/photos/city.jpg?size=large&download=1");
        h.Requests.Should().HaveCount(3).And.Equal(h.Validated);
    }

    [Fact]
    public async Task A_Redirect_To_A_Blocked_Address_Is_Not_Requested_And_Public_Search_Can_Recover()
    {
        using var h = new Harness(uri => uri.AbsolutePath == "/blocked" ? Redirect("http://127.0.0.1/private")
            : uri.Host == "commons.wikimedia.org" ? Json(new { }) : Content([1], "image/png"));
        h.Validate = uri => { if (uri.IsLoopback) throw new InvalidDataException("Private address blocked"); };
        h.Search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse([new("Bern", "https://images.example/public.png", "", "test")], []));
        var result = await h.Provider.ResolveAsync(new("Bern", "https://sources.example/blocked"));
        result.ImageUrl.Should().Be("https://images.example/public.png");
        h.Validated.Should().Contain(uri => uri.IsLoopback);
        h.Requests.Should().NotContain(uri => uri.IsLoopback);
    }

    [Fact]
    public async Task Individual_Http_Timeout_Is_Recoverable_And_Does_Not_Cancel_The_Turn()
    {
        using var h = new Harness(uri => uri.AbsolutePath == "/slow" ? throw new TaskCanceledException("HTTP timeout")
            : uri.Host == "commons.wikimedia.org" ? Json(new { }) : Content([1], "image/png"));
        h.Search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse([new("Bern", "https://images.example/recovered.png", "", "test")], []));
        var result = await h.Provider.ResolveAsync(new("Bern", "https://sources.example/slow"));
        result.ImageUrl.Should().Be("https://images.example/recovered.png");
        h.Decoded.Should().ContainSingle();
    }

    [Fact]
    public async Task User_Cancellation_Stops_Downloading_Without_Fallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var h = new Harness(_ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        Func<Task> resolve = () => h.Provider.ResolveAsync(new("Bern", "https://images.example/slow"), cancellation.Token);
        await resolve.Should().ThrowAsync<OperationCanceledException>();
        h.Requests.Should().ContainSingle();
        h.Decoded.Should().BeEmpty();
        await h.Search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }

    [Theory]
    [InlineData("application/pdf", false)]
    [InlineData("image/png", true)]
    public async Task Invalid_Or_Oversized_Downloads_Fail_Before_Decoding_Without_Inventing_An_Image(string mime, bool oversized)
    {
        using var h = new Harness(uri =>
        {
            if (uri.Host == "commons.wikimedia.org") return Json(new { });
            var response = Content([1, 2, 3], mime);
            if (oversized) response.Content.Headers.ContentLength = 33 * 1024 * 1024;
            return response;
        });
        Func<Task> resolve = () => h.Provider.ResolveAsync(new("Bern", "https://images.example/invalid"));
        var failure = await resolve.Should().ThrowAsync<InvalidDataException>();
        failure.Which.Message.Should().Contain("No usable internet image").And.Contain(oversized ? "32 MB" : mime);
        h.Decoded.Should().BeEmpty();
    }

    private static object Page(string title, string url, string? thumb = null) => new
    {
        title = "File:" + title, index = 1,
        imageinfo = new[] { new { url, thumburl = thumb, descriptionurl = "https://commons.wikimedia.org/wiki/File:" + Uri.EscapeDataString(title),
            extmetadata = new { Artist = new { value = "<a>Author &amp; Co</a>" }, LicenseShortName = new { value = "CC BY 4.0" } } } }
    };

    private static HttpResponseMessage Json(object value) => Content(JsonSerializer.SerializeToUtf8Bytes(value), "application/json");
    private static HttpResponseMessage Content(byte[] bytes, string mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return response;
    }
    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } };

    private sealed class Harness : IDisposable
    {
        public static readonly byte[] Png = [137, 80, 78, 71];
        public List<Uri> Requests { get; } = [];
        public List<Uri> Validated { get; } = [];
        public List<byte[]> Decoded { get; } = [];
        public Action<Uri>? Validate { get; set; }
        public ISearchProvider Search { get; } = Substitute.For<ISearchProvider>();
        public PresentationWebImageProvider Provider { get; }
        private readonly HttpClient _http;

        public Harness(Func<Uri, HttpResponseMessage> response)
        {
            _http = new HttpClient(new Handler(uri => { Requests.Add(uri); return response(uri); }));
            var decoder = Substitute.For<IPresentationImageDecoder>();
            decoder.ToPngAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(call => { Decoded.Add((byte[])call[0]); return Task.FromResult(Png); });
            Search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse([], []));
            Provider = new(_http, decoder, Search, (uri, token) => { token.ThrowIfCancellationRequested(); Validated.Add(uri); Validate?.Invoke(uri); return Task.CompletedTask; }, TimeSpan.Zero);
        }
        public void Dispose() { Provider.Dispose(); _http.Dispose(); }
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request.RequestUri!));
    }
}
