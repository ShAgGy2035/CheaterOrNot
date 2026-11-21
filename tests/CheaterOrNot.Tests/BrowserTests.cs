using System.Net;
using System.Net.Sockets;
using System.Text;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class BrowserTests
{
    private static readonly string Id = TestPlayerIds.Primary;

    [Fact]
    public async Task BrowserChecksRenderOnlySourcesThatNeedItAndHonorRobots()
    {
        using var handler = new RobotsHandler();
        using var client = new TrackerClient(handler);
        await using var renderer = new FakeRenderer();
        var result = await client.FetchAsync(Id, CancellationToken.None, renderer: renderer);
        Assert.Equal(Sources.PublicPages(Id).Count - 3, renderer.Pages.Count);
        Assert.DoesNotContain(renderer.Pages, uri => uri.Host == "cstracker.gg");
        Assert.Contains(result, e => e.Source == "cstracker.gg public page" && e.Status == "Not fetched");
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), result.Single(e => e.Source == "csst.at public page").Stats);
        Assert.Equal(2, result.Count(e => e.Status == "Public account context"));
        Assert.Equal(1, result.Count(e => e.Status == "Browser-rendered page data"));
        Assert.Equal(Sources.UnsupportedSources.Length, result.Count(e => e.Status == "Unsupported automatic source"));
        Assert.Equal(Sources.PublicPages(Id).Count + 2, handler.Requests);
        Assert.DoesNotContain("RAW-BROWSER-MARKER", Analysis.Render(Id, result));
    }

    [Theory]
    [InlineData("This profile is private")]
    [InlineData("Verify you are human")]
    public void ScopeDoesNotImportRestrictedOrVerificationPagesEvenWithStatistics(string notice)
    {
        Assert.Throws<FormatException>(() =>
            WebsiteDataParser.CsstScopeIfPresent($"<h1>{notice}</h1>" + PublicProfileFixtures.Scope(Id), Id));
    }

    [BrowserFact]
    public async Task ChromiumPreservesScopeFromItsOriginalResponseWhenScriptsRemoveThePanel()
    {
        await using var server = new FixtureServer(PublicProfileFixtures.Scope(Id) + """
            <script>
              document.querySelector('.scope-card__body').remove();
              document.body.append('K/D: 9.0');
            </script>
            """);
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), WebsiteDataParser.CsstScope(result.Html, Id));
            Assert.Equal(1, server.Requests);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumWaitsForCompleteScopeBeforeUsingAnInitialPartialPanel()
    {
        var partial = PublicProfileFixtures.Scope(Id).Replace(
            """<div class="scope-stat"><dt>KPR</dt><dd><span class="text-white">0.7</span><span class="scope-delta">0.03</span></dd></div>""", "");
        await using var server = new FixtureServer(partial + """
            <script>setTimeout(() => {
              document.querySelector('.scope-grid').insertAdjacentHTML('beforeend',
                '<div class="scope-stat"><dt>KPR</dt><dd><span class="text-white">0.8</span></dd></div>');
            }, 700);</script>
            """);
        await using var renderer = new ChromiumPageRenderer();
        var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.8), WebsiteDataParser.CsstScope(result.Html, Id));
    }

    [BrowserFact]
    public async Task ChromiumRetainsInitialPartialScopeIfScriptsRemoveIt()
    {
        var partial = PublicProfileFixtures.Scope(Id).Replace(
            """<div class="scope-stat"><dt>KPR</dt><dd><span class="text-white">0.7</span><span class="scope-delta">0.03</span></dd></div>""", "");
        await using var server = new FixtureServer(partial + """
            <script>document.querySelector('.scope-card__body').remove();</script>
            """);
        await using var renderer = new ChromiumPageRenderer();
        var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2), WebsiteDataParser.CsstScope(result.Html, Id));
        Assert.Contains("partial identity-verified Scope", result.Note);
    }

    [BrowserFact]
    public async Task ChromiumProjectsVerifiedMatchRowsWithoutUnrelatedOversizedCharts()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id) +
            $"<section id='player-ranks-section' data-vaclive-steam-id='{Id}'><svg><path d='" +
            new string('M', PageParser.MaxCharacters + 100) + "'></path></svg></section><div>K/D: 1.2</div>";
        await using var server = new FixtureServer(html);
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(2, 2), WebsiteDataParser.CsTracker(result.Html, Id));
            var sections = WebsiteDataParser.CsTrackerSections(result.Html, Id);
            Assert.Contains(sections, section => section.Text.Contains("757ms"));
            Assert.Contains(sections, section => section.Text.Contains("AK-47"));
            Assert.DoesNotContain("CHART-TO-EXCLUDE", result.Html);
            Assert.True(result.Html.Length < 10000);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumWaitsForScopeInsteadOfReadingAnEarlierUnrelatedProviderCard()
    {
        await using var server = new FixtureServer($$"""
            <a href="https://steamcommunity.com/profiles/{{Id}}">Steam</a>
            <div>K/D: 9.0</div><div class="scope-card__body"></div>
            <script>
              setTimeout(() => {
                document.querySelector('.scope-card__body').innerHTML =
                  '<dl class="scope-grid" aria-label="Performance">' +
                  '<div class="scope-stat"><dt>K/D</dt><dd><span class="text-white">0.44</span></dd></div>' +
                  '<div class="scope-stat"><dt>KPR</dt><dd><span class="text-white">0.35</span></dd></div></dl>';
              }, 700);
            </script>
            """);
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(Kd: 0.44, Kpr: 0.35), WebsiteDataParser.CsstScope(result.Html, Id));
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumReadinessRecognizesKillsPerRoundWithoutRequiringAnotherMetric()
    {
        await using var server = new FixtureServer("<div>KPR: 0.35</div>");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(Kpr: 0.35), PageParser.Parse(PageParser.VisibleText(result.Html)));
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumKeepsTheWebsiteStatsRequestInItsAnonymousSessionAndDoesNotRepeatIt()
    {
        await using var server = new FixtureServer(PublicProfileFixtures.CsStats(Id) + """
            <div>K/D: 1.2</div><script>
            document.cookie = 'fixture=anonymous-session; path=/';
            fetch(location.pathname + '/stats');
            </script>
            """, statisticsHtml: "<div>Matches: 150</div><div>K/D: 1.2</div>");
        var statisticsUrl = new Uri(server.Url.AbsoluteUri + "/stats");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url,
                (url, _) => Task.FromResult(url == server.Url || url == statisticsUrl),
                CancellationToken.None, statisticsUrl);
            Assert.Equal(200, result.Statistics!.Status);
            Assert.Equal(new PlayerStats(150, 1.2), PageParser.Parse(PageParser.VisibleText(result.Statistics.Html!)));
            Assert.Equal(1, server.StatisticsRequests);
            Assert.Equal("anonymous-session", server.StatisticsCookie);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumRequestsMissingPublicStatsOnceAndPreservesAForbiddenResponse()
    {
        await using var server = new FixtureServer(PublicProfileFixtures.CsStats(Id) + "<div>K/D: 1.2</div>",
            statisticsHtml: "Access denied", statisticsStatus: 403);
        var statisticsUrl = new Uri(server.Url.AbsoluteUri + "/stats");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url,
                (url, _) => Task.FromResult(url == server.Url || url == statisticsUrl),
                CancellationToken.None, statisticsUrl);
            Assert.Equal(403, result.Statistics!.Status);
            Assert.Null(result.Statistics.Html);
            Assert.Contains("disallowed by robots.txt", result.Statistics.Error);
            Assert.Equal(1, server.StatisticsRequests);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChromiumCompletesTheWebsitesOwnStatisticsRetryCheckpointWithoutInteraction(bool returnsToProfile)
    {
        await using var server = new FixtureServer(PublicProfileFixtures.CsStats(Id) + "<div>K/D: 1.2</div>",
            statisticsHtml: "<div>Matches: 150</div><div>K/D: 1.2</div>",
            statisticsChallenge: true, challengeReturnsToProfile: returnsToProfile);
        var statisticsUrl = new Uri(server.Url.AbsoluteUri + "/stats");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url,
                (url, _) => Task.FromResult(url == server.Url || url.AbsolutePath == statisticsUrl.AbsolutePath),
                CancellationToken.None, statisticsUrl);
            Assert.Equal(200, result.Statistics!.Status);
            Assert.Equal(new PlayerStats(150, 1.2), PageParser.Parse(PageParser.VisibleText(result.Statistics.Html!)));
            Assert.Contains("No CAPTCHA was solved", result.Statistics.Note);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumWaitsForTheFrontPageCheckpointToFinishOnItsOwn()
    {
        await using var server = new FixtureServer("<div>Matches: 150</div><div>K/D: 1.2</div>", profileChallenge: true);
        var renderer = new ChromiumPageRenderer();
        try
        {
            var result = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(150, 1.2), PageParser.Parse(PageParser.VisibleText(result.Html)));
            Assert.Contains("completed on its own", result.Note);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumRejectsADifferentPlayersStatisticsRoute()
    {
        await using var server = new FixtureServer(PublicProfileFixtures.CsStats(Id));
        await using var renderer = new ChromiumPageRenderer();
        var other = new Uri(server.Url.AbsoluteUri.Replace(Id, TestPlayerIds.Secondary) + "/stats");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None, other));
        Assert.Equal(0, server.Requests);
    }

    [BrowserFact]
    public async Task ChromiumDetectsChallengeWhileDeferredResourcesAreStillPending()
    {
        await using var server = new FixtureServer("""
            <html><head><title>Just a moment...</title><script src="/slow.js" defer></script></head>
            <body>Performing security verification</body></html>
            """);
        var renderer = new ChromiumPageRenderer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<bool> Permits(Uri url, CancellationToken token)
        {
            if (url.AbsolutePath == "/slow.js") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return url == server.Url;
        }
        try
        {
            var error = await Assert.ThrowsAsync<FormatException>(() =>
                renderer.ReadAsync(server.Url, Permits, cancellation.Token));
            Assert.Contains("access/verification page", error.Message);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumRecognizesSecurityChallengesInsteadOfMisreportingSummaryTimeouts()
    {
        await using var server = new FixtureServer("<html><title>Just a moment...</title><body>Performing security verification</body></html>");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var error = await Assert.ThrowsAsync<FormatException>(() =>
                renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None));
            Assert.Contains("access/verification page", error.Message);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumExecutesJavaScriptExcludesCssHiddenStatsAndDeletesItsProfile()
    {
        await using var server = new FixtureServer("""
            <html><body>
            <div style="display:none">K/D: 9</div><div>Loading player...</div>
            <script>
            document.cookie = 'fixture=test; path=/';
            localStorage.setItem('fixture', 'test');
            fetch('/private-data').catch(() => {});
            setTimeout(() => {
                const summary = document.createElement('section');
                summary.innerHTML = '<div><span>Matches</span><strong>150</strong></div><div><span>K/D</span><strong>1.2</strong></div>';
                document.body.appendChild(summary);
            }, 200);
            </script></body></html>
            """);
        var renderer = new ChromiumPageRenderer();
        var session = renderer.SessionDirectory;
        try
        {
            var page = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            Assert.Equal(new PlayerStats(150, 1.2), PageParser.Parse(PageParser.VisibleText(page.Html)));
            Assert.DoesNotContain("localStorage", page.Html);
            Assert.DoesNotContain("K/D: 9", page.Html);
            Assert.True(Directory.Exists(session));
            Assert.Equal(1, server.Requests);
            Assert.Contains("blocked", page.Note);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(session));
    }

    [BrowserFact]
    public async Task ChromiumDiscardsStatisticsOnPrivateProfilePages()
    {
        await using var server = new FixtureServer("<html><body>Private profile<div>K/D: 9</div></body></html>");
        var renderer = new ChromiumPageRenderer();
        try
        {
            var error = await Assert.ThrowsAsync<FormatException>(() =>
                renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None));
            Assert.Contains("private/login-restricted", error.Message);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumReportsForbiddenPagesWithoutRetryAndCleansUp()
    {
        await using var server = new FixtureServer("<div>K/D: 9</div>", 403);
        var renderer = new ChromiumPageRenderer();
        try
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Equal(1, server.Requests);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    [BrowserFact]
    public async Task ChromiumCancellationInterruptsLoadingAndCleansUp()
    {
        await using var server = new FixtureServer("<html><body>Loading player...</body></html>");
        var renderer = new ChromiumPageRenderer();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var read = renderer.ReadAsync(server.Url, server.Permits, cancellation.Token);
            await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.False(Directory.Exists(renderer.SessionDirectory));
    }

    private sealed class RobotsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.Host == "steamcommunity.com" && request.RequestUri.Query == "?xml=1")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PublicProfileFixtures.Steam(Id)) });
            Assert.Equal("/robots.txt", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.Host is "cstracker.gg" or "fixture.invalid"
                    ? "User-agent: *\nDisallow: /" : "User-agent: *\nAllow: /")
            });
        }
    }

    [BrowserFact]
    public async Task ChromiumProjectsVerifiedProgressionContextWithMatchHistory()
    {
        var html = PublicProfileFixtures.CsTracker(Id) + $$"""
            <section id="player-ranks-section" data-vaclive-steam-id="{{Id}}">
              <script type="application/json" data-player-json="ranks">
              [{"key":"matchmaking:de_dust2","group":"matchmaking","points":[{"x":1795154022,"y":1}]}]
              </script>
            </section>
            <div class="player-heading">
              <div><h1>Test player</h1></div>
              <div>
                <div data-header-credentials><img alt="Matchmaking rank Silver I"></div>
                <div data-demo-profile><span title="CS2 profile level 25 / 40 · 15 levels to the next service medal">25/40</span></div>
              </div>
            </div>
            <div data-demo-profile><span title="CS2 profile level 23 / 40">match row 23/40</span></div>
            <img data-demo-profile-medal title="Displayed: 2026 Service Medal" alt="2026 Service Medal">
            """;
        await using var server = new FixtureServer(html);
        await using var renderer = new ChromiumPageRenderer();
        try
        {
            var rendered = await renderer.ReadAsync(server.Url, server.Permits, CancellationToken.None);
            var context = WebsiteDataParser.CsTrackerProgressionContext(rendered.Html, Id);

            Assert.Contains(context, text => text.Contains("CS2 matchmaking rank") && text.Contains("Silver I"));
            Assert.Contains(context, text => text.Contains("CS2 profile level 25 / 40"));
            Assert.Contains(context, text => text.Contains("Displayed CS2 service medal: 2026"));
            Assert.Contains(context, text => text.Contains("Competitive dust2 rank"));
        }
        finally { await renderer.DisposeAsync(); }
    }

    private sealed class FakeRenderer : IPageRenderer
    {
        public List<Uri> Pages { get; } = [];
        public async Task<RenderedPage> ReadAsync(Uri url, Func<Uri, CancellationToken, Task<bool>> permits,
            CancellationToken cancellationToken, Uri? statisticsUrl = null)
        {
            Assert.True(await permits(url, cancellationToken));
            Assert.False(await permits(new Uri("https://fixture.invalid/data"), cancellationToken));
            Pages.Add(url);
            var html = url.Host switch
            {
                "csst.at" => PublicProfileFixtures.Scope(Id),
                "cswatch.gg" => PublicProfileFixtures.CsWatch(Id),
                "csstats.gg" => PublicProfileFixtures.CsStats(Id),
                _ => "<div>Matches: 150</div><div>K/D: 1.2</div>"
            };
            return new RenderedPage("<script>RAW-BROWSER-MARKER</script>" + html, "Rendered test fixture.",
                statisticsUrl is null ? null : new PublicHtmlResponse(200, "<div>Matches: 150</div><div>K/D: 1.2</div>"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serve;
        private int _requests;
        private int _statisticsRequests;
        public int Requests => _requests;
        public int StatisticsRequests => _statisticsRequests;
        public string? StatisticsCookie { get; private set; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Url { get; }

        public FixtureServer(string html, int status = 200, string? statisticsHtml = null, int statisticsStatus = 200,
            bool statisticsChallenge = false, bool challengeReturnsToProfile = false, bool profileChallenge = false)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            var origin = $"http://localhost:{number}/";
            Url = new Uri(origin + "profile/" + Id);
            _listener.Prefixes.Add(origin);
            _listener.Start();
            _serve = ServeAsync(html, status, statisticsHtml, statisticsStatus, statisticsChallenge, challengeReturnsToProfile, profileChallenge);
        }

        public Task<bool> Permits(Uri url, CancellationToken token) => Task.FromResult(url == Url);

        private async Task ServeAsync(string html, int status, string? statisticsHtml, int statisticsStatus,
            bool statisticsChallenge, bool challengeReturnsToProfile, bool profileChallenge)
        {
            const string challengePage = "<html><head><title>Just a moment...</title></head><body><script>" +
                "document.cookie='cf_clearance=fixture;path=/';location.reload();</script></body></html>";
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _requests);
                    var statistics = context.Request.Url?.AbsolutePath == Url.AbsolutePath + "/stats";
                    var cleared = context.Request.Cookies["cf_clearance"] is not null;
                    var data = Encoding.UTF8.GetBytes(statistics ? statisticsHtml ?? "" : html);
                    context.Response.StatusCode = statistics ? statisticsStatus : status;
                    if (statistics)
                    {
                        Interlocked.Increment(ref _statisticsRequests);
                        StatisticsCookie = context.Request.Cookies["fixture"]?.Value;
                        if (statisticsChallenge)
                        {
                            var retryRoute = context.Request.QueryString["cf_return"] == "1";
                            if (!cleared)
                            {
                                context.Response.StatusCode = 403;
                                data = Encoding.UTF8.GetBytes(retryRoute ? challengePage : "Access denied");
                            }
                            else if (retryRoute && challengeReturnsToProfile)
                            {
                                context.Response.StatusCode = 302;
                                context.Response.RedirectLocation = Url.AbsoluteUri;
                                data = [];
                            }
                            else
                            {
                                context.Response.StatusCode = 200;
                                data = Encoding.UTF8.GetBytes(statisticsHtml ?? "");
                            }
                        }
                    }
                    else if (profileChallenge && !cleared)
                    {
                        context.Response.StatusCode = 403;
                        context.Response.Headers.Add("cf-mitigated", "challenge");
                        data = Encoding.UTF8.GetBytes(challengePage);
                    }
                    context.Response.ContentType = "text/html";
                    context.Response.ContentLength64 = data.Length;
                    await context.Response.OutputStream.WriteAsync(data);
                    context.Response.Close();
                    Requested.TrySetResult();
                }
            }
            catch (Exception ex) when (!_listener.IsListening && ex is HttpListenerException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _serve;
        }
    }
}

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        var browsers = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(browsers) || !Directory.Exists(browsers))
            Skip = "Set PLAYWRIGHT_BROWSERS_PATH to the bundled Chromium directory to run real-browser tests.";
    }
}

public sealed class BrowserTheoryAttribute : TheoryAttribute
{
    public BrowserTheoryAttribute()
    {
        var browsers = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(browsers) || !Directory.Exists(browsers))
            Skip = "Set PLAYWRIGHT_BROWSERS_PATH to the bundled Chromium directory to run real-browser tests.";
    }
}
