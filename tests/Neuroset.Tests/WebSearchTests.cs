using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Neuroset.Application;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class WebSearchTests
{
    [Fact]
    public async Task DuckDuckGo_ProtocolRelative_Redirects_Are_Decoded_And_Deduplicated()
    {
        const string html = """
            <html><body>
              <div class="result results_links web-result">
                <div class="links_main result__body">
                  <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fcats%3Fx%3D1%26y%3D2&amp;rut=ignored">Cats &amp; Breeds</a>
                  <a class="result__snippet">Worldwide breed statistics.</a>
                </div>
              </div>
              <div class="result results_links web-result">
                <a class="result__a" href="/l/?uddg=https%3A%2F%2Fexample.org%2Fpopular%2Dcats&amp;rut=ignored">Popular cats</a>
                <a class="result__snippet">A second result.</a>
              </div>
            </body></html>
            """;
        using var client = new HttpClient(new DelegateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));
        var provider = new DuckDuckGoSearchProvider(client);

        var response = await provider.SearchAsync("popular cat breeds", 5);
        var results = response.Results;

        results.Should().HaveCount(2);
        response.Attempts.Should().ContainSingle(attempt => attempt.Provider == "DuckDuckGo" && attempt.Status == "success");
        results[0].Title.Should().Be("Cats & Breeds");
        results[0].Url.Should().Be("https://example.com/cats?x=1&y=2");
        results[0].Snippet.Should().Be("Worldwide breed statistics.");
        results[1].Url.Should().Be("https://example.org/popular-cats");
    }

    [Fact]
    public async Task DuckDuckGo_Challenge_Automatically_Falls_Back_To_Bing_Rss()
    {
        const string challenge = "<html><body><div class=\"anomaly-modal\">bot verification</div></body></html>";
        const string rss = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel>
              <item><title>Exact fleet enumeration</title><link>https://example.org/enumeration</link><description>Detailed &amp; verified count.</description></item>
            </channel></rss>
            """;
        using var client = new HttpClient(new DelegateHttpHandler(request => request.RequestUri!.Host.Contains("duckduckgo",StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseMessage(HttpStatusCode.Accepted){Content=new StringContent(challenge,Encoding.UTF8,"text/html")}
            : new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(rss,Encoding.UTF8,"application/rss+xml")}));
        var provider = new DuckDuckGoSearchProvider(client);

        var response = await provider.SearchAsync("battleship enumeration", 5);

        response.Results.Should().ContainSingle();
        response.Results[0].Source.Should().Be("Bing");
        response.Results[0].Snippet.Should().Be("Detailed & verified count.");
        response.Attempts.Should().HaveCount(2);
        response.Attempts[0].Status.Should().Be("blocked");
        response.Attempts[0].HttpStatusCode.Should().Be(202);
        response.Attempts[1].Status.Should().Be("success");
    }

    [Fact]
    public async Task Empty_Search_Produces_A_Structured_Report_Instead_Of_A_Raw_Array()
    {
        var tool = new WebSearchTool(new EmptySearchProvider());
        using var arguments = JsonDocument.Parse("""{"query":"an intentionally empty query"}""");

        var result = await tool.ExecuteAsync(arguments.RootElement, new(Guid.NewGuid(), "call", Path.GetTempPath(), false, new Dictionary<string, string>()));

        result.Success.Should().BeTrue();
        result.Output.Should().NotBe("[]");
        using var report = JsonDocument.Parse(result.Output);
        report.RootElement.GetProperty("query").GetString().Should().Be("an intentionally empty query");
        report.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        report.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
        report.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private sealed class EmptySearchProvider : ISearchProvider
    {
        public Task<SearchResponse> SearchAsync(string query,int limit=5,CancellationToken cancellationToken=default)=>Task.FromResult(new SearchResponse([], [new SearchAttempt("test","no_results",0)]));
    }
}
