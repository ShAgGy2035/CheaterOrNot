using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class WebsiteRetrievalTests
{
    private static readonly string Id = TestPlayerIds.Primary;

    [Fact]
    public async Task PublicOnlyRetrievalUsesAnonymousWebsiteResponsesAndChecksLeetifyFirst()
    {
        var urls = new List<Uri>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            urls.Add(url);
            Assert.Null(request.Headers.Authorization);
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host is "leetify.com" or "api.cs-prod.leetify.com"
                    ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            if (url.AbsolutePath == $"/api/profile/{Id}/meta")
                return Text($$"""{"steam64Id":"{{Id}}","isSensitiveDataVisible":false}""");
            Assert.Equal($"/api/profile/{Id}/recent-games/5v5", url.AbsolutePath);
            return Text("""{"matchesPlayed":30,"kdRatio":0.7257019438444925,"headshotKillPercentage":36.904761904761905,"headshotKillPercentageAvg":0.46,"accuracyHead":11.35,"winRate":0.4666666666666667}""");
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyAttributionReady: true);
        Assert.Equal("leetify.com", urls[0].Host);
        Assert.DoesNotContain(urls, url => url.Host is "api-public.cs-prod.leetify.com" or "open.faceit.com" or "api.steampowered.com" or "api.csst.at");
        var website = Assert.Single(result.Where(e => e.Stats is not null));
        Assert.Equal(Sources.LeetifyProfile(Id), website.Url);
        Assert.Equal("Public website recent 5v5 data", website.Status);
        Assert.Equal(30, website.Stats!.Matches);
        Assert.Equal(0.7257019438444925, website.Stats.Kd);
        Assert.Equal(36.904761904761905, website.Stats.Headshots);
        Assert.Equal(46.66666666666667, website.Stats.WinRate!.Value, 10);
        Assert.Null(website.Stats.Kpr);
        Assert.Contains("assessed as provided, including small ones", Analysis.Render(Id, result));
        Assert.Equal(1, urls.Count(u => u.AbsolutePath.EndsWith("/meta")));
        Assert.Equal(1, urls.Count(u => u.AbsolutePath.EndsWith("/recent-games/5v5")));
    }

    [Fact]
    public async Task WrongWebsiteIdentityPreventsDownloadingAnotherPlayersStats()
    {
        var statsRequests = 0;
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host is "leetify.com" or "api.cs-prod.leetify.com"
                    ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            if (url.AbsolutePath.EndsWith("/meta")) return Text($$"""{"steam64Id":"{{TestPlayerIds.Secondary}}"}""");
            statsRequests++;
            throw new InvalidOperationException("Stats must not be fetched after an identity mismatch.");
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None,
            leetifyAttributionReady: true);
        Assert.Equal(0, statsRequests);
        Assert.All(result, e => Assert.Null(e.Stats));
        Assert.Contains(result, e => e.Source.Contains("Leetify") && e.Status == "Unavailable");
    }

    [Theory]
    [InlineData("""{"matchesPlayed":0,"kdRatio":0}""")]
    [InlineData("""{"matchesPlayed":30.5,"kdRatio":1}""")]
    [InlineData("""{"matchesPlayed":30,"headshotKillPercentage":101}""")]
    [InlineData("""{"kdRatio":1.2}""")]
    public void InvalidOrPlaceholderWebsiteSamplesAreRejected(string data)
    {
        using var metadata = JsonDocument.Parse($$"""{"steam64Id":"{{Id}}"}""");
        using var recent = JsonDocument.Parse(data);
        Assert.Throws<FormatException>(() => WebsiteDataParser.Leetify(metadata.RootElement, recent.RootElement, Id));
    }

    [Theory]
    [InlineData("""{"matchesPlayed":30,"kdRatio":1.2,"headshotKillPercentage":101,"winRate":0.5}""", "headshot kill percentage")]
    [InlineData("""{"matchesPlayed":30,"kdRatio":1.2,"headshotKillPercentage":40,"winRate":47}""", "win rate")]
    public async Task LeetifyRetainsValidMetricsWhenAnotherMetricIsInvalid(string data, string invalid)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host is "leetify.com" or "api.cs-prod.leetify.com"
                    ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            if (url.AbsolutePath.EndsWith("/meta")) return Text($$"""{"steam64Id":"{{Id}}"}""");
            return Text(data);
        }));
        var website = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, leetifyAttributionReady: true),
            item => item.Source == "Data Provided by Leetify (public website)");
        Assert.Equal(1.2, website.Stats!.Kd);
        Assert.Contains(website.Sections!, section => section.Title == $"Recent {invalid} unavailable");
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [website]));
    }

    [Fact]
    public void WebsiteDoesNotMistakeAccuracyOrPopulationBenchmarkForPlayersHeadshots()
    {
        using var metadata = JsonDocument.Parse($$"""{"steam64Id":"{{Id}}"}""");
        using var recent = JsonDocument.Parse("""{"matchesPlayed":7,"kdRatio":1.2,"accuracyHead":99,"headshotKillPercentageAvg":0.99}""");
        Assert.Equal(new PlayerStats(7, 1.2), WebsiteDataParser.Leetify(metadata.RootElement, recent.RootElement, Id));
    }

    [Theory]
    [InlineData("https://leetify.com/app/profile/{id}", "https://leetify.com/public/profile/{id}", true)]
    [InlineData("https://leetify.com/app/profile/{id}", "https://leetify.com/public/profile/{other}", false)]
    [InlineData("https://leetify.com/app/profile/{id}", "https://evil.example/public/profile/{id}", false)]
    [InlineData("https://leetify.com/app/profile/{id}", "https://leetify.com/public/profile/{id}?other=1", false)]
    public void CanonicalRoutesOnlyAllowTheSameProviderAndPlayer(string source, string destination, bool expected) =>
        Assert.Equal(expected, Sources.IsSamePlayerPage(new Uri(source.Replace("{id}", Id)),
            new Uri(destination.Replace("{id}", Id).Replace("{other}", TestPlayerIds.Secondary))));

    [Fact]
    public async Task CrawlerWaitsForDeclaredDelayInsteadOfRejectingPermittedPage()
    {
        var clock = new Stopwatch();
        TimeSpan? actualDelay = null;
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
            {
                if (url.Host == "cstracker.gg")
                {
                    clock.Start();
                    return Text("User-agent: *\nAllow: /\nCrawl-delay: 0.05");
                }
                return Text("User-agent: *\nDisallow: /");
            }
            Assert.Equal("cstracker.gg", url.Host);
            actualDelay = clock.Elapsed;
            return Text(PublicProfileFixtures.CsTracker(Id));
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None);
        Assert.NotNull(actualDelay);
        Assert.True(actualDelay >= TimeSpan.FromMilliseconds(50), $"Observed delay: {actualDelay}");
        Assert.Equal(new PlayerStats(2, 2), Assert.Single(result.Where(e => e.Stats is not null)).Stats);
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task ProgressCountsActualChecksAndUsableDataSeparatelyEvenWhenWebsitesFail()
    {
        var updates = new List<FetchProgress>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host is "leetify.com" or "api.cs-prod.leetify.com" or "cstracker.gg"
                    ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            if (url.Host == "cstracker.gg") return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (url.AbsolutePath.EndsWith("/meta")) return Text($$"""{"steam64Id":"{{Id}}"}""");
            return Text("""{"matchesPlayed":30,"kdRatio":0.73}""");
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyAttributionReady: true,
            progress: new InlineProgress(updates.Add));
        var count = Sources.PublicPages(Id).Count;
        Assert.Equal(count * 2, updates.Count);
        for (var index = 0; index < count; index++)
        {
            var start = updates[index * 2];
            var finish = updates[index * 2 + 1];
            Assert.Equal(index, start.CompletedSources);
            Assert.Equal("Checking", start.Status);
            Assert.Equal(index + 1, finish.CompletedSources);
            Assert.Equal(start.Source, finish.Source);
            Assert.Equal(count, finish.TotalSources);
            Assert.Equal(1, finish.PerformanceSummaries);
        }
        Assert.Equal(0, updates[0].PerformanceSummaries);
        Assert.Contains(updates, update => update.Source.Contains("cstracker") && update.Status == "Unavailable");
        Assert.Equal(result.Count(e => e.Stats is { HasMetrics: true }), updates[^1].PerformanceSummaries);
        Assert.True(result.Count > updates[^1].TotalSources);
    }

    [Fact]
    public async Task CancellationDoesNotReportAnInterruptedCheckAsCompleted()
    {
        var updates = new List<FetchProgress>();
        using var cancellation = new CancellationTokenSource();
        using var client = new TrackerClient(new Handler(_ => Text("User-agent: *\nAllow: /")));
        var progress = new InlineProgress(update =>
        {
            updates.Add(update);
            if (update.Status == "Checking" && update.CompletedSources == 1) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchAsync(Id, cancellation.Token, progress: progress));
        Assert.Equal(3, updates.Count);
        Assert.Equal("Checking", updates[^1].Status);
        Assert.Equal(1, updates[^1].CompletedSources);
        Assert.Equal(0, updates[^1].PerformanceSummaries);
    }

    private sealed class InlineProgress(Action<FetchProgress> report) : IProgress<FetchProgress>
    {
        public void Report(FetchProgress value) => report(value);
    }

    [Fact]
    public void TrackerUsesUniqueReturnedRowsNotProfileTotalsOrUnrelatedRatios()
    {
        var html = "<div>Matches: 999</div><div>K/D: 9</div>" + PublicProfileFixtures.CsTracker(Id);
        Assert.Equal(new PlayerStats(2, 2), WebsiteDataParser.CsTracker(html, Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTracker(html.Replace("/matches/2", "/matches/1"), Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTracker(html, TestPlayerIds.Secondary));
    }

    [Fact]
    public void TrackerRetainsPublicTotalsTelemetryBenchmarksAndGameplayTabsAsSeparateSamples()
    {
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id), Id);
        var totals = Assert.Single(sections.Where(section => section.Title.StartsWith("Profile results:")));
        Assert.Equal(278, totals.Stats!.Matches);
        Assert.Equal(100.0 * 133 / 278, totals.Stats.WinRate);
        Assert.Null(totals.Stats.Kd);
        Assert.Contains("299 total matches", totals.Title);
        Assert.Contains("Ties: 21", totals.Text);
        Assert.Contains("Displayed win rate: 48%", totals.Text);
        var recent = Assert.Single(sections.Where(section => section.Title.StartsWith("Recent analysis:")));
        Assert.Equal(new PlayerStats(90, 0.53), recent.Stats);
        Assert.Contains("Time to damage (TTD): 757ms", recent.Text);
        Assert.Contains("Preaim: 3.3 deg", recent.Text);
        Assert.Contains("Aim offset: 20.1 deg", recent.Text);
        Assert.Contains("HLTV rating: 0.63", recent.Text);
        Assert.Contains("KAST: 61.8%", recent.Text);
        Assert.Contains("Average damage per round (ADR): 52.7", recent.Text);
        Assert.Contains("beats 24%", recent.Text);
        Assert.Contains("Accuracy (not headshot percentage): 23.7%", recent.Text);
        Assert.Null(recent.Stats!.Headshots);
        Assert.Contains(sections, section => section.Text.Contains("99.2%") && section.Stats is null);
        Assert.Contains(sections, section => section.DetailedOnly && section.Text.Contains("AK-47"));
        var detailed = Assert.Single(sections.Where(section => section.Title.StartsWith("Detailed combat aggregates:")));
        Assert.Equal(299, detailed.Stats!.Matches);
        Assert.Equal(2319.0 / 4698, detailed.Stats.Kd);
        Assert.Equal(100.0 * 942 / 2319, detailed.Stats.Headshots);
        Assert.Null(detailed.Stats.Kpr);
        Assert.Equal(8, recent.Checks!.Count);
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id), TestPlayerIds.Secondary));
    }

    [Fact]
    public void TrackerAcceptsAWebsiteHeadshotPercentageTruncatedToItsDisplayedPrecision()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("2,319 / 4,698 / 1,268", "2,620 / 5,215 / 1,408")
            .Replace("942 (40.6%)", "1,052 (40.1%)");

        var section = Assert.Single(WebsiteDataParser.CsTrackerSections(html, Id),
            item => item.Title == "Detailed combat aggregates: 299 matches");

        Assert.Equal(100.0 * 1052 / 2620, section.Stats!.Headshots);
        Assert.DoesNotContain(WebsiteDataParser.CsTrackerSections(html, Id),
            item => item.Title == "Detailed headshot percentage unavailable");
    }

    [Fact]
    public void TrackerStillRejectsAHeadshotPercentageOutsideItsDisplayedPrecision()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id).Replace("942 (40.6%)", "942 (39.0%)");

        var sections = WebsiteDataParser.CsTrackerSections(html, Id);

        Assert.Contains(sections, item => item.Title == "Detailed headshot percentage unavailable" &&
            item.Text.Contains("does not match its detailed kill totals"));
    }

    [Fact]
    public void TrackerReadsOnlyAnExplicitProfileSynchronizationTimestamp()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id) +
            """<div>Last synced <time datetime="2026-11-19T23:15:00Z">moments ago</time></div>""";

        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 15, 0, TimeSpan.Zero),
            WebsiteDataParser.CsTrackerLastSync(html, Id));
        Assert.Null(WebsiteDataParser.CsTrackerLastSync(PublicProfileFixtures.CsTrackerOverview(Id), Id));
    }

    [Fact]
    public void TrackerImportsRankLevelAndDisplayedServiceMedalAsUnscoredContext()
    {
        var html = PublicProfileFixtures.CsTracker(Id) + $$"""
            <div data-header-credentials>
              <img alt="Matchmaking rank Silver I">
            </div>
            <div data-demo-profile>
              <span title="CS2 profile level 25 / 40 · 15 levels to the next service medal">lvl 25/40</span>
            </div>
            <img data-demo-profile-medal title="Displayed: 2026 Service Medal" alt="2026 Service Medal">
            <section id="player-ranks-section" data-vaclive-steam-id="{{Id}}">
              <script type="application/json" data-player-json="ranks">
              [
                {
                  "key": "matchmaking:de_dust2",
                  "group": "matchmaking",
                  "points": [{"x": 1795154022, "y": 1}]
                },
                {
                  "key": "premier",
                  "group": "premier",
                  "points": []
                }
              ]
              </script>
            </section>
            """;

        var context = WebsiteDataParser.CsTrackerProgressionContext(html, Id);

        Assert.Contains(context, text => text.Contains("matchmaking rank") && text.Contains("Silver I"));
        Assert.Contains(context, text => text.Contains("CS2 profile level 25 / 40"));
        Assert.Contains(context, text => text.Contains("Displayed CS2 service medal: 2026") &&
            text.Contains("total medals earned is unavailable"));
        Assert.Contains(context, text => text.Contains("Competitive dust2 rank") &&
            text.Contains("Silver I (1/18)") && text.Contains("last observed 2026-11-20"));
    }

    [Fact]
    public void TrackerComputesOverlappingSevenAndFourteenDayWindowsFromExactMatchTimes()
    {
        var html = PublicProfileFixtures.CsTracker(Id)
            .Replace("<td>20 / 10 / 4<span>", "<td title=\"unused\">20 / 10 / 4<span>")
            .Replace("<td><a href=\"/matches/1\">Map</a></td>",
                "<td><a href=\"/matches/1\">Map</a><span title=\"played 2026-11-19 12:00\">1d ago</span></td>")
            .Replace("<td><a href=\"/matches/2\">Map</a></td>",
                "<td><a href=\"/matches/2\">Map</a><span title=\"played 2026-11-10 12:00\">10d ago</span></td>");
        var retrievedAt = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        var windows = WebsiteDataParser.CsTrackerRecentWindows([html], Id, retrievedAt);

        var seven = Assert.Single(windows, section => section.Title == "Recent 7-day CSTracker trend");
        Assert.Equal(1, seven.Stats!.Matches);
        Assert.Equal(2, seven.Stats.Kd);
        var fourteen = Assert.Single(windows, section => section.Title == "Recent 14-day CSTracker trend");
        Assert.Equal(2, fourteen.Stats!.Matches);
        Assert.Equal(2, fourteen.Stats.Kd);
        Assert.Contains("Pooled 30 kills / 15 deaths", fourteen.Text);
        Assert.Equal(9, windows.Count);
        Assert.Equal(
            ["Recent 7-day CSTracker trend", "Recent 14-day CSTracker trend",
             "Recent 21-day CSTracker trend", "Recent 28-day CSTracker trend",
             "Recent 35-day CSTracker trend", "Recent 42-day CSTracker trend",
             "Recent 49-day CSTracker trend", "Recent 56-day CSTracker trend",
             "Recent 63-day CSTracker trend"],
            windows.Select(section => section.Title));
        Assert.Contains("overlap and must not be added together", fourteen.Text);
    }

    [Fact]
    public void TrackerRejectsAmbiguousRecentMatchTimesInsteadOfGuessingFromRelativeText()
    {
        var html = PublicProfileFixtures.CsTracker(Id)
            .Replace("<td><a href=\"/matches/1\">Map</a></td>",
                "<td><a href=\"/matches/1\">Map</a><span>1d ago</span></td>")
            .Replace("<td><a href=\"/matches/2\">Map</a></td>",
                "<td><a href=\"/matches/2\">Map</a><span title=\"played invalid\">2d ago</span></td>");

        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTrackerRecentWindows(
            [html], Id, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TrackerDoesNotInferServiceMedalCountFromRepeatedDisplayedImages()
    {
        var html = PublicProfileFixtures.CsTracker(Id) +
            """
            <img data-demo-profile-medal title="Displayed: 2026 Service Medal" alt="2026 Service Medal">
            <img data-demo-profile-medal title="Displayed: 2026 Service Medal" alt="2026 Service Medal">
            """;

        var context = WebsiteDataParser.CsTrackerProgressionContext(html, Id);
        var medal = Assert.Single(context, text => text.Contains("service medal", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("total medals earned is unavailable", medal);
        Assert.DoesNotContain("2 medals", medal);
    }

    [Fact]
    public void TrackerRejectsConflictingSynchronizationTimestamps()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id) +
            """
            <div>Last synced <time datetime="2026-11-19T23:15:00Z">one moment</time></div>
            <div>Last updated <time datetime="2026-11-20T01:15:00Z">another moment</time></div>
            """;

        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTrackerLastSync(html, Id));
    }

    [Fact]
    public async Task TrackerAttachesPublishedSyncTimeWithoutTreatingItAsRetrievalTime()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id) +
            """<div>Last synced <time datetime="2026-11-19T23:15:00Z">moments ago</time></div>""";
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/robots.txt"
                ? Text(request.RequestUri.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /")
                : Text(html)));

        var source = Assert.Single((await client.FetchAsync(Id, CancellationToken.None))
            .Where(item => item.Source == "cstracker.gg public page"));

        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 15, 0, TimeSpan.Zero), source.DataUpdatedAt);
        Assert.NotEqual(source.RetrievedAt, source.DataUpdatedAt);
        Assert.Contains("Website data updated 2026-11-19 23:15:00Z", Analysis.Render(Id, [source]));
    }

    [Fact]
    public void TrackerRetainsBehaviorAndUtilityFieldsAsUnscoredEvidence()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id).Replace(
            "<div class=\"items-baseline\"><span>Util dmg / match</span><span>74.5</span></div>",
            """
            <div class="items-baseline"><span>Util dmg / match</span><span>74.5</span></div>
            <div class="items-baseline"><span>Enemies flashed / flash</span><span>0.85</span></div>
            <div class="items-baseline"><span>Avg flash duration</span><span>2.32s</span></div>
            <div class="items-baseline"><span>Team damage / match</span><span>9.1</span></div>
            <div class="items-baseline"><span>Input automation / 100M</span><span>0.00</span></div>
            """);

        var details = Assert.Single(WebsiteDataParser.CsTrackerSections(html, Id),
            section => section.Title == "Public tab: details (context; sample as stated by website)");

        Assert.Contains("Enemies flashed / flash: 0.85", details.Text);
        Assert.Contains("Avg flash duration: 2.32s", details.Text);
        Assert.Contains("Team damage / match: 9.1", details.Text);
        Assert.Contains("Input automation / 100M: 0.00", details.Text);
        Assert.True(details.DetailedOnly);
        Assert.Null(details.Stats);
        Assert.Null(details.Checks);
    }

    [Theory]
    [InlineData("90.0%", 90.0)]
    [InlineData("48.1%", 48.1)]
    public void DetailedWinRateIsShownFromItsOwnPanelWithoutChangingTheAssessedProfileRate(string rate, double expected)
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id).Replace(
            "<div class=\"items-baseline\"><span>HS kills</span>",
            $"<div class=\"items-baseline\"><span>Win rate</span><span>{rate}</span></div>" +
            "<div class=\"items-baseline\"><span>HS kills</span>");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        var detailed = Assert.Single(sections, section => section.Title.StartsWith("Detailed combat aggregates:"));
        Assert.Equal(expected, detailed.Stats!.ContextWinRate);
        Assert.Null(detailed.Stats.WinRate);
        Assert.Null(detailed.Stats.Kpr);
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test",
            Sections: sections.Where(section => section.Title.StartsWith("Profile results:") ||
                section.Title.StartsWith("Detailed combat aggregates:")).ToArray());
        Assert.Contains($"Win rate: {expected.ToString("0.##", CultureInfo.InvariantCulture)}% (website displayed; context only)", Analysis.Render(Id, [evidence]));
        var result = Analysis.RenderFinal(Id, [evidence]);
        Assert.Contains("Win rate 47.84% did not cross", result);
        Assert.DoesNotContain("Win rate of 90", result);
    }

    [Fact]
    public void InvalidDetailedWinRateDoesNotDiscardOtherMetrics()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id).Replace(
            "<div class=\"items-baseline\"><span>HS kills</span>",
            "<div class=\"items-baseline\"><span>Win rate</span><span>105%</span></div>" +
            "<div class=\"items-baseline\"><span>HS kills</span>");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        Assert.Contains(sections, section => section.Title == "Detailed win rate unavailable");
        var details = Assert.Single(sections, section => section.Title.StartsWith("Detailed combat aggregates:"));
        Assert.Equal(2319.0 / 4698, details.Stats!.Kd);
        Assert.Null(details.Stats.ContextWinRate);
    }

    [Fact]
    public void MissingDetailedKillsKeepsDisplayedHeadshotsAsUnscoredContext()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("<div class=\"items-baseline\"><span>K/D/A</span><span>2,319 / 4,698 / 1,268</span></div>", "");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        Assert.Contains(sections, section => section.Title == "Detailed K/D unavailable");
        var details = Assert.Single(sections, section => section.Title.StartsWith("Detailed combat aggregates:"));
        Assert.Null(details.Stats!.Kd);
        Assert.Null(details.Stats.Headshots);
        Assert.Equal(40.6, details.Stats.ContextHeadshots);
        Assert.Contains("HS: 40.6% (website displayed; context only)",
            Analysis.Render(Id, [new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", Sections: [details])]));
    }

    [Fact]
    public void InvalidDetailedUtilityDoesNotDiscardCombatMetrics()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("<span>Util dmg / match</span><span>74.5</span>", "<span>Util dmg / match</span><span>bad</span>");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        Assert.Contains(sections, section => section.Title == "Detailed utility damage unavailable");
        var details = Assert.Single(sections, section => section.Title.StartsWith("Detailed combat aggregates:"));
        Assert.Equal(2319.0 / 4698, details.Stats!.Kd);
        Assert.Equal(100.0 * 942 / 2319, details.Stats.Headshots);
    }

    [Fact]
    public void PublicTabFormattingSeparatesLabelsCardsAndTableCellsWithoutChangingMetrics()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("<header><span>299 matches</span></header>",
                "<header><span>04</span><span>details</span><h2>Detailed stats</h2><span>299 matches</span></header>")
            .Replace("Chest 43%", "<span>Chest</span><span>43%</span><span>Head</span><span>17%</span>") +
            """
            <section id="player-kills-section"><div>wallbangs: 1% | 10 / 2,339 kills</div></section>
            """;
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        var kills = sections.Single(section => section.DetailedOnly && section.Title.StartsWith("Public tab: kills")).Text;
        Assert.Contains("different eligible-event scope or denominator", kills);
        Assert.Contains("not reconciled, added, or substituted", kills);
        var details = sections.Single(section => section.DetailedOnly && section.Title.StartsWith("Public tab: details")).Text;
        Assert.Contains("details\nDetailed stats\n299 matches", details);
        Assert.DoesNotContain("04", details);
        Assert.Contains("K/D/A: 2,319 / 4,698 / 1,268", details);
        Assert.Contains("HS kills: 942 (40.6%)", details);
        var maps = sections.Single(section => section.DetailedOnly && section.Title.StartsWith("Public tab: maps")).Text;
        Assert.Contains("Dust2 123 matches | K/D: 0.50 | ADR: 50", maps);
        Assert.Contains("may differ from other CSTracker panels", maps);
        Assert.Contains("ADR: 50\nMirage 46 matches", maps);
        var weapons = sections.Single(section => section.DetailedOnly && section.Title.StartsWith("Public tab: weapons")).Text;
        Assert.Contains("Weapon: M4A1-S | Kills: 632 | HS%: 35% | Shots: 10,578 | Accuracy: 26%", weapons);
        Assert.Contains("Hitgroups: Chest 43% Head 17%", weapons);
        Assert.Contains("\nWeapon: AK-47 |", weapons);
        Assert.DoesNotContain("\n\nWeapon", weapons);
        Assert.Contains("Weapon: HE Grenade | Kills: 28 | HS%: 0% | Shots: 0 | Accuracy: -", weapons);
        Assert.Equal(new PlayerStats(90, 0.53), sections.Single(section => section.Title.StartsWith("Recent analysis:")).Stats);
        Assert.Equal(100.0 * 942 / 2319,
            sections.Single(section => section.Title.StartsWith("Detailed combat aggregates:")).Stats!.Headshots);
    }

    [Fact]
    public void PublicTabFormattingKeepsInlineUnitsAndSeparatesGrenadeLinks()
    {
        var text = PageParser.PublicTabText("""
            <section><h2>Grenades</h2><span>14570 throws</span>
            <div><a><span>Anubis</span><span>1083 grenades</span></a><a><span>Dust2</span><span>6301 grenades</span></a></div>
            <div class="items-baseline"><span>Preaim</span><span>2.8&deg;</span></div>
            <div class="items-baseline"><span>Spot to damage</span><span>643ms</span></div>
            <div class="items-baseline"><span>Unused util on death</span><span>$233</span></div>
            <script>Fake 999</script><div hidden>Hidden value</div><span aria-hidden="true">Decorative</span></section>
            """);
        Assert.Contains("Anubis 1083 grenades\nDust2 6301 grenades", text);
        Assert.Contains("Preaim: 2.8\u00b0", text);
        Assert.Contains("Spot to damage: 643ms", text);
        Assert.Contains("Unused util on death: $233", text);
        Assert.DoesNotContain("Fake", text);
        Assert.DoesNotContain("Hidden", text);
        Assert.DoesNotContain("Decorative", text);
    }

    [Fact]
    public void PublicTabFormattingPreservesKillAndClutchCountsAndReversedMetricLabels()
    {
        var kills = PageParser.PublicTabText("""
            <section id="player-kills-section"><header><div><span>03</span><span>kill profile</span></div><h2>Kill breakdown</h2></header>
            <div><div>3%</div><div>wallbangs</div><p><span>293</span> / <span>9,912</span> kills</p></div>
            <div><div>4%</div><div>through smokes</div><p>393 / 9,912 kills</p></div>
            <div><div>0.2%</div><div>in-air</div><p>22 / 9,912 kills</p></div>
            <div><div>1.9%</div><div>noscope</div><p>24 / 1,246 kills</p></div>
            <div><div>46%</div><div>headshots</div><p>4,528 / 9,862 kills</p></div></section>
            """);
        Assert.Equal("kill profile\nKill breakdown\nwallbangs: 3% | 293 / 9,912 kills\n" +
            "through smokes: 4% | 393 / 9,912 kills\nin-air: 0.2% | 22 / 9,912 kills\n" +
            "noscope: 1.9% | 24 / 1,246 kills\nheadshots: 46% | 4,528 / 9,862 kills", kills);
        var clutches = PageParser.PublicTabText("""
            <section id="player-clutches-section"><header><span>05</span><span>clutches</span><h2>Clutch performance</h2></header>
            <p>313 / 1399 &middot; 22%</p><div class="grid">
            <div><div>1v1</div><div>168 &middot; 88</div><div>66%</div></div>
            <div><div>1v2</div><div>110 &middot; 237</div><div>32%</div></div></div></section>
            """);
        Assert.Equal("clutches\nClutch performance\n313 / 1399 \u00b7 22%\n" +
            "1v1: 168 \u00b7 88 | 66%\n1v2: 110 \u00b7 237 | 32%", clutches);
    }

    [Fact]
    public void MapContextKeepsEveryMetricOnItsMapsSingleRecordLine()
    {
        var text = PageParser.PublicTabText("""
            <section id="player-maps-section"><header><div><div>09</div><span>maps</span></div><h2>Map performance</h2></header>
            <div class="grid"><div><span class="font-medium">Ancient</span><span>37 matches</span>
            <div><span>21W</span><span>15L</span><span>1T</span><span>58% WR</span></div>
            <div class="grid">
            <div><div class="tabular-nums">1.57</div><div>K/D</div></div>
            <div><div class="tabular-nums">107</div><div>ADR</div></div>
            <div><div class="tabular-nums">1.58</div><div>Rating</div></div>
            <div><div class="tabular-nums">557ms</div><div>TTD</div></div>
            <div><div class="tabular-nums">80.1%</div><div>KAST</div></div>
            <div><div class="tabular-nums">3.6&deg;</div><div>Preaim</div></div></div></div>
            <div><span class="font-medium">Warden</span><span>1 matches</span>
            <div><span>1W</span><span>0L</span><span>100% WR</span></div>
            <div><div class="tabular-nums">2.00</div><div>K/D</div></div></div></div></section>
            """);
        Assert.Equal("maps\nMap performance\n" +
            "Ancient 37 matches | 21W 15L 1T 58% WR | K/D: 1.57 | ADR: 107 | Rating: 1.58 | TTD: 557ms | KAST: 80.1% | Preaim: 3.6\u00b0\n" +
            "Warden 1 matches | 1W 0L 100% WR | K/D: 2.00", text);
    }

    [Theory]
    [InlineData("133 wins", "134 wins")]
    [InlineData("757ms", "757%")]
    [InlineData("&quot;valueKey&quot;:&quot;ttd&quot;", "&quot;missingKey&quot;:&quot;ttd&quot;")]
    [InlineData("942 (40.6%)", "2,400 (40.6%)")]
    [InlineData("942 (40.6%)", "942 (90%)")]
    public void TrackerRejectsOnlyTheInvalidOverviewSection(string before, string after)
    {
        var sections = WebsiteDataParser.CsTrackerSections(
            PublicProfileFixtures.CsTrackerOverview(Id).Replace(before, after), Id);
        Assert.Contains(sections, section => section.Title.EndsWith(" unavailable") &&
            section.Text.Contains("Parsing failed:"));
        Assert.Contains(sections, section => section.Title.StartsWith("Website-generated trust rating"));
        Assert.Contains(sections, section => section.Title.StartsWith("Weapon accuracy variance"));
        if (before.Contains("wins"))
            Assert.DoesNotContain(sections, section => section.Title.StartsWith("Profile results:"));
        else
            Assert.Contains(sections, section => section.Title.StartsWith("Profile results:"));
        var detailed = Assert.Single(sections, section => section.Title.StartsWith("Detailed combat aggregates:"));
        Assert.NotNull(detailed.Stats!.Kd);
        if (before.Contains("942")) Assert.Null(detailed.Stats.Headshots);
        var recent = Assert.Single(sections, section => section.Title.StartsWith("Recent analysis:"));
        Assert.Equal(0.53, recent.Stats!.Kd);
        if (before is "757ms" || before.Contains("valueKey"))
            Assert.DoesNotContain("Time to damage (TTD)", recent.Text);
    }

    [Theory]
    [InlineData("Better than 124% of tracked players over their latest 90 matches")]
    [InlineData("Better than 24% of tracked players over their latest ninety matches")]
    [InlineData("Better than 24% of tracked players over their latest 0 matches")]
    public void InvalidBenchmarkDoesNotDiscardVerifiedMetricsOrOtherSamples(string title)
    {
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("Better than 24% of tracked players over their latest 90 matches", title), Id);
        var recent = Assert.Single(sections.Where(section => section.Title.StartsWith("Recent analysis:")));
        Assert.Contains("Time to damage (TTD): 757ms", recent.Text);
        Assert.Contains("benchmark unavailable", recent.Text);
        Assert.Equal(8, recent.Checks!.Count);
        Assert.Null(recent.Checks.Single(check => check.Name == "Time to damage (TTD)").BetterThanPercent);
        Assert.Contains(sections, section => section.Stats?.Headshots is not null);
        Assert.Contains(sections, section => section.Stats?.WinRate is not null);
        Assert.DoesNotContain(sections, section => section.Title.EndsWith(" unavailable"));
    }

    [Theory]
    [InlineData("Better than 24% over unknown games", "unrecognized benchmark title format")]
    [InlineData("Better than 124% of tracked players over their latest 90 matches", "invalid percentile or benchmark sample")]
    public void BenchmarkDiagnosticsExplainWhyOnlyTheBenchmarkWasRejected(string title, string reason)
    {
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("Better than 24% of tracked players over their latest 90 matches", title), Id);
        var recent = Assert.Single(sections, section => section.Title.StartsWith("Recent analysis:"));
        Assert.Contains(reason, recent.Text);
        Assert.Contains("Time to damage (TTD): 757ms", recent.Text);
        Assert.Contains(recent.Checks!, check => check.Name == "Time to damage (TTD)");
    }

    [Fact]
    public void DuplicateBenchmarkLabelsAreNotGuessed()
    {
        const string label = """<span title="Better than 24% of tracked players over their latest 90 matches">beats 24%</span>""";
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace(label, label + label), Id);
        var recent = Assert.Single(sections, section => section.Title.StartsWith("Recent analysis:"));
        Assert.Contains("multiple benchmark labels", recent.Text);
        Assert.Null(recent.Checks!.Single(check => check.Name == "Time to damage (TTD)").BetterThanPercent);
    }

    [Fact]
    public void DuplicateRecentMetricRejectsEveryInstanceWithoutAffectingOtherMetrics()
    {
        const string card = """<div data-histogram-config="{&quot;format&quot;:&quot;ratio&quot;,&quot;valueKey&quot;:&quot;kd&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:1.8}">""";
        var duplicate = card + """<div class="items-baseline"><div class="tabular-nums">2.5</div></div></div>""";
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace(card, duplicate + card), Id);
        var recent = Assert.Single(sections, section => section.Title.StartsWith("Recent analysis:"));
        Assert.Null(recent.Stats!.Kd);
        Assert.DoesNotContain("K/D:", recent.Text);
        Assert.DoesNotContain(recent.Checks!, check => check.Name == "K/D");
        Assert.Contains("Time to damage (TTD)", recent.Text);
        Assert.Contains(sections, section => section.Title == "Recent analysis metric unavailable" &&
            section.Text.Contains("Duplicate CSTracker analysis metric: K/D"));
        Assert.DoesNotContain(sections, section => section.Title.StartsWith("Recent-vs-lifetime"));
    }

    [Fact]
    public void InvalidRecentCutoffKeepsTheVerifiedMeasurementUnscored()
    {
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("&quot;suspiciousCutoff&quot;:600", "&quot;suspiciousCutoff&quot;:&quot;invalid&quot;"), Id);
        var recent = Assert.Single(sections, section => section.Title.StartsWith("Recent analysis:"));
        Assert.Contains("Time to damage (TTD): 757ms", recent.Text);
        Assert.DoesNotContain(recent.Checks!, check => check.Name == "Time to damage (TTD)");
        Assert.Contains(sections, section => section.Title == "Recent analysis Time to damage (TTD) cutoff unavailable");
    }

    [Fact]
    public void BenchmarkComputedOverADifferentWindowIsKeptWithItsOwnStatedSample()
    {
        var sections = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("over their latest 90 matches", "over their latest 88 matches"), Id);
        var recent = Assert.Single(sections.Where(section => section.Title.StartsWith("Recent analysis:")));
        Assert.Contains("beats 24% of tracked players (benchmark window: latest 88 matches)", recent.Text);
        Assert.Equal(90, recent.Stats!.Matches);
    }

    [Fact]
    public async Task TrackerImportsAdditionalPublicDataWithoutInflatingHistoryOrSourceCounts()
    {
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/robots.txt"
                ? Text(request.RequestUri.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /")
                : Text(PublicProfileFixtures.CsTrackerOverview(Id))));
        var result = await client.FetchAsync(Id, CancellationToken.None);
        var source = Assert.Single(result.Where(e => e.Stats is not null));
        Assert.Equal(new PlayerStats(2, 2), source.Stats);
        Assert.Contains(source.Sections!, section => section.Title == "Optional CSTracker checks not assessed");
        var final = Analysis.RenderFinal(Id, result);
        Assert.Contains("1 of 5 checked sources", final);
        Assert.Contains("SCREENING COVERAGE: 3/4", final);
        Assert.Contains("Time to damage (TTD) 757ms", final);
        Assert.DoesNotContain("AK-47 521", final);
        Assert.Contains("AK-47", Analysis.Render(Id, result));
        Assert.Contains("NO STATISTICAL RED FLAGS FOUND", final);
        Assert.Contains("Headshot kills 40.62%", final);
        Assert.Contains("K/D 0.49", final);
        Assert.Contains("Weapon accuracy spread 6%", final);
        Assert.Contains("Utility-to-frag screen not triggered", Analysis.Render(Id, result));
        Assert.DoesNotContain("Utility damage per match", final);
    }

    [Fact]
    public async Task ShortHistoryExplainsWhyOscillationWasNotAssessed()
    {
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/robots.txt"
                ? Text(request.RequestUri.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /")
                : Text(PublicProfileFixtures.CsTrackerOverview(Id))));
        var source = Assert.Single(await client.FetchAsync(Id, CancellationToken.None),
            item => item.Source == "cstracker.gg public page");
        var section = Assert.Single(source.Sections!, item => item.Title == "Performance oscillation (not assessed)");
        Assert.Contains("2 matches", section.Text);
        Assert.Contains("At least 50 matches", section.Text);
        Assert.Null(section.Checks);
    }

    [Theory]
    [InlineData("26%", "20%", "20%", false)]
    [InlineData("22%", "21%", "20.5%", true)]
    public void WeaponAccuracyHomogenizationIsFlaggedOnlyForAHyperNarrowSpread(
        string first, string second, string third, bool expectFlag)
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("\"tabular-nums\">26%", $"\"tabular-nums\">{first}")
            .Replace("\"tabular-nums\">20%</div></td><td><div class=\"tabular-nums\">60,510",
                $"\"tabular-nums\">{second}</div></td><td><div class=\"tabular-nums\">60,510")
            .Replace("\"tabular-nums\">20%</div></td><td><div class=\"tabular-nums\">32,943",
                $"\"tabular-nums\">{third}</div></td><td><div class=\"tabular-nums\">32,943");
        var section = WebsiteDataParser.CsTrackerSections(html, Id)
            .Single(s => s.Title.StartsWith("Weapon accuracy variance"));
        var check = Assert.Single(section.Checks!);
        Assert.Equal("Weapon accuracy spread", check.Name);
        Assert.Equal(expectFlag, check.IsFlagged);
        Assert.Contains("3 weapons with at least 100 kills and 1,000 shots", section.Title);
    }

    [Fact]
    public void WeaponVarianceRequiresThreeQualifyingWeaponsAndRejectsInvalidAccuracy()
    {
        var two = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace(">297</div>", ">97</div>");
        Assert.Contains(WebsiteDataParser.CsTrackerSections(two, Id),
            s => s.Title == "Optional CSTracker checks not assessed" &&
                 s.Text.Contains("2 qualifying weapons") && s.Text.Contains("At least 3"));
        var invalid = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("\"tabular-nums\">26%", "\"tabular-nums\">999%");
        var sections = WebsiteDataParser.CsTrackerSections(invalid, Id);
        Assert.Contains(sections, s => s.Title == "Weapon accuracy variance unavailable");
        Assert.DoesNotContain(sections, s => s.Title.StartsWith("Weapon accuracy variance:"));
        Assert.Contains(sections, s => s.Stats?.Headshots is not null);
    }

    [Theory]
    [InlineData("4,698 / 2,319 / 1,268", "942 (20%)", "5", true, true)]
    [InlineData("4,698 / 2,319 / 1,268", "942 (20%)", "74.5", true, false)]
    [InlineData("2,319 / 4,698 / 1,268", "942 (40.6%)", "5", false, false)]
    public void UtilityToFragScreenAppliesOnlyWithElevatedKd(
        string kda, string hs, string utility, bool expectCheck, bool expectFlag)
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("2,319 / 4,698 / 1,268", kda).Replace("942 (40.6%)", hs)
            .Replace("<span>Util dmg / match</span><span>74.5</span>", $"<span>Util dmg / match</span><span>{utility}</span>");
        var section = WebsiteDataParser.CsTrackerSections(html, Id)
            .Single(s => s.Title.StartsWith("Detailed combat aggregates"));
        if (!expectCheck)
        {
            Assert.True(section.Checks is null or { Count: 0 });
            Assert.Contains("not triggered", section.Text);
            return;
        }
        var check = Assert.Single(section.Checks!);
        Assert.Equal("Utility damage per match", check.Name);
        Assert.Equal(expectFlag, check.IsFlagged);
        if (expectFlag)
            Assert.Contains("role, modes and play style",
                Analysis.RenderFinal(Id, [new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", Sections: [section])]));
    }

    [Fact]
    public void OptionalChecksExplainTheirOwnSampleRequirements()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("<span>46 matches</span>", "<span>13 matches</span>")
            .Replace("<span>25 matches</span>", "<span>11 matches</span>");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id);
        var note = Assert.Single(sections, section => section.Title == "Optional CSTracker checks not assessed");
        Assert.Contains("1 maps have at least 20 matches; at least 3 are required", note.Text);
        Assert.Contains("recent K/D 0.53 (90 matches)", note.Text);
        Assert.Contains("detailed K/D 0.49 (299 matches)", note.Text);
        Assert.Contains(sections, section => section.Title.StartsWith("Detailed combat aggregates") &&
            section.Text.Contains("utility damage 74.5 per match kept as context"));
    }

    [Fact]
    public void WeaponSpreadScreenRequiresAtLeastTwoWeaponCategories()
    {
        // Three qualifying rifles legitimately cluster tightly; the screen must not activate.
        var allRifles = PublicProfileFixtures.CsTrackerOverview(Id).Replace("MAC-10", "FAMAS");
        Assert.Contains(WebsiteDataParser.CsTrackerSections(allRifles, Id),
            section => section.Title == "Optional CSTracker checks not assessed" &&
                section.Text.Contains("3 qualifying weapons across 1 recognized categories"));
        var mixed = WebsiteDataParser.CsTrackerSections(PublicProfileFixtures.CsTrackerOverview(Id), Id)
            .Single(section => section.Title.StartsWith("Weapon accuracy variance"));
        Assert.Contains("Sniper (AWP/SSG 08/SCAR-20/G3SG1) kill share: 0%", mixed.Text);
        Assert.Contains("displayed percentages, which may be rounded", mixed.Text);
    }

    [Theory]
    [InlineData("2.5", "2,319 / 4,698 / 1,268", "942 (40.6%)", true, true)]
    [InlineData("2.5", "4,698 / 2,319 / 1,268", "942 (20%)", true, false)]
    [InlineData("0.53", "2,319 / 4,698 / 1,268", "942 (40.6%)", false, false)]
    public void RecentVsLifetimeKdJumpScreenActivatesOnlyWithElevatedRecentKd(
        string recentKd, string kda, string hs, bool expectSection, bool expectFlag)
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("\"tabular-nums\">0.53</div>", $"\"tabular-nums\">{recentKd}</div>")
            .Replace("2,319 / 4,698 / 1,268", kda).Replace("942 (40.6%)", hs);
        var sections = WebsiteDataParser.CsTrackerSections(html, Id)
            .Where(section => section.Title.StartsWith("Recent-vs-lifetime K/D jump")).ToArray();
        if (!expectSection) { Assert.Empty(sections); return; }
        var check = Assert.Single(Assert.Single(sections).Checks!);
        Assert.Equal("Recent-vs-lifetime K/D jump", check.Name);
        Assert.Equal(expectFlag, check.IsFlagged);
        if (expectFlag)
            Assert.Contains("does not establish an activation date",
                Analysis.RenderFinal(Id, [new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", Sections: sections)]));
    }

    [Theory]
    [InlineData("14-48", "23%", false, false)]
    [InlineData("80-20", "80%", true, true)]
    [InlineData("63-37", "63%", true, false)]
    public void OpeningDuelRateIsScoredOnlyWithAtLeastOneHundredDuels(
        string counts, string displayed, bool expectCheck, bool expectFlag)
    {
        var html = PublicProfileFixtures.Scope(Id)
            .Replace("First duels of the round won: 14-48", "First duels of the round won: " + counts)
            .Replace("\"text-white\">23%", $"\"text-white\">{displayed}");
        var section = Assert.Single(WebsiteDataParser.CsstScopeSections(html, Id));
        Assert.StartsWith("Opening duels", section.Title);
        if (!expectCheck)
        {
            Assert.True(section.Checks is null or { Count: 0 });
            Assert.Contains("context only, not scored", section.Text);
            return;
        }
        var check = Assert.Single(section.Checks!);
        Assert.Equal("Opening duel win rate", check.Name);
        Assert.Equal(expectFlag, check.IsFlagged);
    }

    [Theory]
    [InlineData("H,L,H,L,H,L", 3, true)]
    [InlineData("H,H,L,M,M,M", 1, false)]
    [InlineData("M,M,M,M,M,M", 0, false)]
    public void OscillationScreenFlagsRepeatedExtremesNotSingleStreaks(string pattern, int expectedValue, bool expectFlag)
    {
        var section = WebsiteDataParser.CsTrackerOscillation([OscillationPage(pattern)], Id);
        Assert.NotNull(section);
        Assert.StartsWith($"Performance oscillation: {pattern.Split(',').Length} consecutive blocks", section!.Title);
        var check = Assert.Single(section.Checks!);
        Assert.Equal("K/D oscillation blocks", check.Name);
        Assert.Equal(expectedValue, check.Value);
        Assert.Equal(expectFlag, check.IsFlagged);
        if (expectFlag)
            Assert.Contains("toggled assistance",
                Analysis.RenderFinal(Id, [new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", Sections: [section])]));
    }

    [Fact]
    public void OscillationScreenNeedsAtLeastFiftyMatchesInCompleteBlocks()
    {
        Assert.Null(WebsiteDataParser.CsTrackerOscillation([OscillationPage("H,L,H,L")], Id));
    }

    [Fact]
    public void CustomPatternRulesControlEligibilityAndComparisons()
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id);
        var rules = new ScreeningRules
        {
            UtilityMinimumKd = 0.4, UtilityDamageThreshold = 80,
            RecentMinimumKd = 0.5, RecentKdJumpThreshold = 0.01,
            WeaponMinimumKills = 10000, MapMinimumMatches = 10, MapMinimumCount = 2,
            MapMinimumBestKd = 0.4, MapOutlierRatioThreshold = 1,
            OpeningMinimumDuels = 60, OpeningWinPercentThreshold = 20,
            OscillationBlockSize = 10, OscillationMinimumBlocks = 4, OscillationExtremeBlocks = 1,
            OscillationHighKd = 2.5, OscillationLowKd = 0.4
        };
        var sections = WebsiteDataParser.CsTrackerSections(html, Id, rules);
        var utility = Assert.Single(sections.Single(s => s.Title.StartsWith("Detailed combat aggregates")).Checks!);
        Assert.True(utility.IsFlagged);
        Assert.True(Assert.Single(sections.Single(s => s.Title.StartsWith("Recent-vs-lifetime K/D jump")).Checks!).IsFlagged);
        Assert.True(Assert.Single(sections.Single(s => s.Title.StartsWith("Map K/D consistency")).Checks!).IsFlagged);
        Assert.Contains(sections, s => s.Title == "Optional CSTracker checks not assessed" && s.Text.Contains("10,000+ kills"));
        var opening = Assert.Single(WebsiteDataParser.CsstScopeSections(PublicProfileFixtures.Scope(Id), Id, rules));
        Assert.True(Assert.Single(opening.Checks!).IsFlagged);
        var oscillation = WebsiteDataParser.CsTrackerOscillation([OscillationPage("H,L,H,L")], Id, rules);
        Assert.NotNull(oscillation);
        Assert.True(Assert.Single(oscillation!.Checks!).IsFlagged);
        Assert.Contains("K/D >= 2.5", oscillation.Text);
    }

    [Theory]
    [InlineData("1.60", "0.50", "0.46", true, true)]
    [InlineData("1.60", "0.90", "0.80", true, false)]
    [InlineData("0.50", "0.46", "0.53", false, false)]
    public void MapOutlierScreenActivatesOnlyWithAnElevatedBestMap(
        string dust, string mirage, string nuke, bool expectSection, bool expectFlag)
    {
        var html = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("\"tabular-nums\">0.50</div><div>K/D", $"\"tabular-nums\">{dust}</div><div>K/D")
            .Replace("\"tabular-nums\">0.46</div><div>K/D", $"\"tabular-nums\">{mirage}</div><div>K/D")
            .Replace("\"tabular-nums\">0.53</div><div>K/D", $"\"tabular-nums\">{nuke}</div><div>K/D");
        var sections = WebsiteDataParser.CsTrackerSections(html, Id)
            .Where(section => section.Title.StartsWith("Map K/D consistency")).ToArray();
        if (!expectSection) { Assert.Empty(sections); return; }
        var section = Assert.Single(sections);
        Assert.Contains("3 maps with at least 20 matches", section.Title);
        Assert.DoesNotContain("Italy", section.Text);
        var check = Assert.Single(section.Checks!);
        Assert.Equal("Map K/D outlier ratio", check.Name);
        Assert.Equal(expectFlag, check.IsFlagged);
        if (expectFlag)
            Assert.Contains("comfort-map skill",
                Analysis.RenderFinal(Id, [new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", Sections: [section])]));
    }

    [Fact]
    public void MalformedMapCardsAreRejected()
    {
        var invalid = PublicProfileFixtures.CsTrackerOverview(Id)
            .Replace("<span>123 matches</span>", "<span>123 matches</span><span>99 matches</span>");
        var sections = WebsiteDataParser.CsTrackerSections(invalid, Id);
        Assert.Contains(sections, s => s.Title == "Map K/D consistency unavailable");
        Assert.DoesNotContain(sections, s => s.Title.StartsWith("Map K/D consistency:"));
        Assert.Contains(sections, s => s.Title.StartsWith("Recent analysis:"));
    }

    private static string OscillationPage(string pattern)
    {
        var rows = new System.Text.StringBuilder();
        var match = 0;
        foreach (var block in pattern.Split(','))
        {
            var kda = block switch { "H" => "30 / 10 / 2", "L" => "5 / 15 / 1", _ => "10 / 10 / 1" };
            for (var i = 0; i < 10; i++)
                rows.Append($"<tr><td><a href=\"/matches/{++match}\">Map</a></td><td>{kda}</td></tr>");
        }
        return $"<a href=\"https://steamcommunity.com/profiles/{Id}\">Steam</a>" +
            "<section id=\"player-match-history-section\"><table><thead><tr><th>Match</th><th>K/D/A</th></tr></thead>" +
            $"<tbody>{rows}</tbody></table></section>";
    }

    [Fact]
    public void OpeningDuelDisplayMismatchAndWrongIdentityAreRejected()
    {
        var mismatch = PublicProfileFixtures.Scope(Id).Replace("\"text-white\">23%", "\"text-white\">50%");
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsstScopeSections(mismatch, Id));
        Assert.Throws<FormatException>(() =>
            WebsiteDataParser.CsstScopeSections(PublicProfileFixtures.Scope(Id), TestPlayerIds.Secondary));
    }

    [Fact]
    public void LimitedAccountStatusIsContextOnlyAndValidated()
    {
        var limited = PublicProfileFixtures.Steam(Id).Replace("<isLimitedAccount>0", "<isLimitedAccount>1");
        var context = WebsiteDataParser.SteamContext(limited, Id);
        Assert.Contains("Limited (free) account: yes", context.Note);
        Assert.Empty(context.Cautions);
        Assert.DoesNotContain("Limited", WebsiteDataParser.SteamContext(PublicProfileFixtures.Steam(Id), Id).Note);
        Assert.Throws<FormatException>(() => WebsiteDataParser.SteamContext(
            PublicProfileFixtures.Steam(Id).Replace("<isLimitedAccount>0", "<isLimitedAccount>9"), Id));
    }

    [Fact]
    public async Task InvalidOverviewDoesNotDiscardValidHistoryAndItsErrorIsVisible()
    {
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/robots.txt"
                ? Text(request.RequestUri.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /")
                : Text(PublicProfileFixtures.CsTrackerOverview(Id).Replace("757ms", "757%"))));
        var source = Assert.Single((await client.FetchAsync(Id, CancellationToken.None)).Where(e => e.Stats is not null));
        Assert.Equal(new PlayerStats(2, 2), source.Stats);
        var report = Analysis.Render(Id, [source]);
        Assert.Contains("Recent analysis metric unavailable", report);
        Assert.Contains("Parsing failed: Unexpected CSTracker analysis metric unit.", report);
        Assert.Equal(1, report.Split("Parsing failed:").Length - 1);
        Assert.Contains(source.Sections!, section => section.Stats?.Headshots is not null);
        Assert.Contains(source.Sections!, section => section.Stats?.WinRate is not null);
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [source]));
    }

    [Fact]
    public void ScopeUsesOnlyPerformanceValuesWithoutDeltasOrAnotherProvidersMatchCount()
    {
        var html = "<div>Matches: 999</div><div>HS%: 99%</div>" + PublicProfileFixtures.Scope(Id);
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), WebsiteDataParser.CsstScope(html, Id));
        var flagged = html.Replace(">0.7</span>", ">1.1</span>");
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 1.1), WebsiteDataParser.CsstScope(flagged, Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsstScope(html, TestPlayerIds.Secondary));
    }

    [Theory]
    [InlineData("K/D", 1.2, 0.7)]
    [InlineData("KPR", 0.7, 1.2)]
    public void InvalidScopeMetricDoesNotDiscardAnotherMetric(string label, double invalidValue, double validValue)
    {
        var html = PublicProfileFixtures.Scope(Id).Replace(
            $"<dt>{label}</dt><dd><span class=\"text-white\">{invalidValue.ToString("0.#", CultureInfo.InvariantCulture)}",
            $"<dt>{label}</dt><dd><span class=\"text-white\">invalid");
        var sample = WebsiteDataParser.CsstScopeSample(html, Id);
        Assert.Equal(validValue, label == "K/D" ? sample.Stats!.Kpr : sample.Stats!.Kd);
        Assert.Null(label == "K/D" ? sample.Stats!.Kd : sample.Stats!.Kpr);
        Assert.Contains(sample.Sections, section => section.Title == $"Scope {label} unavailable");
    }

    [Fact]
    public async Task CsstScopeIsRecoveredOnOneSameSessionRetryWhenTheFirstHitHasNoPanel()
    {
        var attempts = 0;
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.Host == "steamcommunity.com" && request.RequestUri.Query == "?xml=1"
                ? Text(PublicProfileFixtures.Steam(Id))
                : Text("User-agent: *\nAllow: /")));
        await using var renderer = new SequenceRenderer(url => url.Host == "csst.at"
            ? (attempts++ == 0 ? "<div>Just a moment...</div>" : PublicProfileFixtures.Scope(Id))
            : PublicProfileFixtures.CsWatch(Id));
        var result = await client.FetchAsync(Id, CancellationToken.None, renderer: renderer);
        var scope = result.Single(e => e.Source == "csst.at public page");
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), scope.Stats);
        Assert.Equal(2, attempts);
        Assert.Contains("same-session retry", scope.Note);
    }

    [Fact]
    public async Task CsstScopeIsNotRetriedWhenTheFirstHitAlreadyHasThePanel()
    {
        var attempts = 0;
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.Host == "steamcommunity.com" && request.RequestUri.Query == "?xml=1"
                ? Text(PublicProfileFixtures.Steam(Id))
                : Text("User-agent: *\nAllow: /")));
        await using var renderer = new SequenceRenderer(url => url.Host == "csst.at"
            ? (attempts++, PublicProfileFixtures.Scope(Id)).Item2
            : PublicProfileFixtures.CsWatch(Id));
        var scope = (await client.FetchAsync(Id, CancellationToken.None, renderer: renderer))
            .Single(e => e.Source == "csst.at public page");
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), scope.Stats);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task InvalidOpeningDuelsDoNotDiscardVerifiedScopePerformance()
    {
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.Host == "steamcommunity.com" && request.RequestUri.Query == "?xml=1"
                ? Text(PublicProfileFixtures.Steam(Id))
                : Text("User-agent: *\nAllow: /")));
        await using var renderer = new SequenceRenderer(url => url.Host == "csst.at"
            ? PublicProfileFixtures.Scope(Id).Replace("\"text-white\">23%", "\"text-white\">50%")
            : PublicProfileFixtures.CsWatch(Id));
        var scope = (await client.FetchAsync(Id, CancellationToken.None, renderer: renderer))
            .Single(e => e.Source == "csst.at public page");
        Assert.Equal(new PlayerStats(Matches: 15, Kd: 1.2, Kpr: 0.7), scope.Stats);
        Assert.Contains(scope.Sections!, section => section.Title == "Opening duels unavailable" &&
            section.Text.Contains("does not match its duel counts"));
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [scope]));
    }

    private sealed class SequenceRenderer(Func<Uri, string> html) : IPageRenderer
    {
        public Task<RenderedPage> ReadAsync(Uri url, Func<Uri, CancellationToken, Task<bool>> permits,
            CancellationToken cancellationToken, Uri? statisticsUrl = null) =>
            Task.FromResult(new RenderedPage(html(url), "Rendered in an isolated Chromium session."));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TrackerFollowsVerifiedOlderLinksAndPoolsRawCountsAcrossPages()
    {
        var first = PublicProfileFixtures.CsTracker(Id).Replace("</section>",
            $"<nav><a href='/players/{Id}?page=2#player-match-history-section'>older</a></nav></section>");
        var second = PublicProfileFixtures.CsTracker(Id).Replace("/matches/1", "/matches/3")
            .Replace("/matches/2", "/matches/4").Replace("20 / 10 / 4", "1 / 20 / 4").Replace("10 / 5 / 2", "1 / 20 / 2");
        var requests = new List<Uri>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            requests.Add(url);
            return Text(url.Query.Length == 0 ? first : second);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None);
        Assert.Equal(2, requests.Count);
        Assert.Equal("?page=2", requests[1].Query);
        var stats = Assert.Single(result.Where(e => e.Stats is not null)).Stats!;
        Assert.Equal(4, stats.Matches);
        Assert.Equal(32.0 / 55, stats.Kd);
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTracker([first, first], Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTrackerOlderPage(first.Replace(Id + "?page", TestPlayerIds.Secondary + "?page"), Id, 1));
    }

    [Fact]
    public void TrackerCanVerifyItsExplicitProfileOwnerWhenNoSteamLinkIsRendered()
    {
        var html = PublicProfileFixtures.CsTracker(Id)
            .Replace($"<a href=\"https://steamcommunity.com/profiles/{Id}\">Steam</a>", "") +
            $"<section id='player-ranks-section' data-vaclive-steam-id='{Id}'></section>";
        Assert.Equal(new PlayerStats(2, 2), WebsiteDataParser.CsTracker(html, Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTracker(html, TestPlayerIds.Secondary));
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsTracker(html +
            $"<section id='player-ranks-section' data-vaclive-steam-id='{Id}'></section>", Id));
    }

    [Fact]
    public async Task TrackerPaginationHasATwentyFivePageRequestLimit()
    {
        var pages = 0;
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            var number = ++pages;
            return Text(PublicProfileFixtures.CsTracker(Id)
                .Replace("/matches/1", $"/matches/{number * 2 + 1}")
                .Replace("/matches/2", $"/matches/{number * 2 + 2}")
                .Replace("</section>", $"<nav><a href='/players/{Id}?page={number + 1}'>older</a></nav></section>"));
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None);
        Assert.Equal(25, pages);
        Assert.Equal(50, Assert.Single(result.Where(e => e.Stats is not null)).Stats!.Matches);
        Assert.Contains(result, e => e.Note?.Contains("25-page request limit") == true);
    }

    [Fact]
    public async Task OlderPageFailureKeepsTheVerifiedSampleAndExplicitlyMarksItPartial()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            if (url.Query.Length != 0) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return Text(PublicProfileFixtures.CsTracker(Id)
                .Replace("<td><a href=\"/matches/1\">Map</a></td>",
                    "<td><a href=\"/matches/1\">Map</a><span title=\"played 2026-11-20 05:00\">today</span></td>")
                .Replace("<td><a href=\"/matches/2\">Map</a></td>",
                    "<td><a href=\"/matches/2\">Map</a><span title=\"played 2026-11-19 05:00\">yesterday</span></td>")
                .Replace("</section>", $"<nav><a href='/players/{Id}?page=2'>older</a></nav></section>"));
        }));
        var source = (await client.FetchAsync(Id, CancellationToken.None)).Single(e => e.Stats is not null);
        Assert.Equal(2, source.Stats!.Matches);
        Assert.Equal("Public match history (partial retrieval)", source.Status);
        Assert.Contains("HTTP 429", source.Note);
        Assert.Contains(source.Sections!, section => section.Title == "Recent 7-day CSTracker trend");
        Assert.Contains(source.Sections!, section => section.Title == "Recent 63-day CSTracker trend");
        Assert.Contains(source.Sections!, section => section.Title == "Performance oscillation (not assessed)");
        var final = Analysis.RenderFinal(Id, [source]);
        Assert.Contains("RECENT ROLLING TRENDS", final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMatchHistoryRetainsIndependentVerifiedData(bool olderPage)
    {
        var first = PublicProfileFixtures.CsTrackerOverview(Id);
        if (olderPage)
            first = first.Replace("</section>", $"<nav><a href='/players/{Id}?page=2'>older</a></nav></section>");
        else
            first = first.Replace("20 / 10 / 4", "invalid");
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host == "cstracker.gg" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            return Text(url.Query.Length == 0 ? first : PublicProfileFixtures.CsTracker(Id));
        }));
        var source = Assert.Single(await client.FetchAsync(Id, CancellationToken.None),
            item => item.Source == "cstracker.gg public page");
        if (olderPage) Assert.Equal(new PlayerStats(2, 2), source.Stats);
        else Assert.Null(source.Stats);
        Assert.Contains(source.Sections!, section => section.Title == "Match history unavailable");
        Assert.Contains(source.Sections!, section => section.Title.StartsWith("Detailed combat aggregates:") && section.Stats!.Kd is not null);
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [source]));
    }

    [Fact]
    public void ReportSeparatesUnimplementedCatalogFromRealPlayerRetrievalFailures()
    {
        var report = Analysis.Render(Id, [
            new Evidence("Checked source", Sources.Profile("csstats.gg", Id), "Unavailable", Note: "HTTP 403"),
            new Evidence("Manual source", new Uri("https://faceitfinder.com/"), "Unsupported automatic source", Note: "No adapter")
        ]);
        Assert.Contains("Checked source | Unavailable", report);
        Assert.Contains("HTTP 403", report);
        Assert.DoesNotContain("NOT AUTOMATICALLY CHECKED", report);
        Assert.DoesNotContain("Manual source", report);
        Assert.DoesNotContain("https://faceitfinder.com/", report);
    }

    [Fact]
    public void AccountContextExcludesRiskScoresAndDoesNotInventGameplayMetrics()
    {
        var account = WebsiteDataParser.CsWatchContext(PublicProfileFixtures.CsWatch(Id), Id);
        Assert.Contains("Account age: 7.4 years", account.Note);
        Assert.Contains("Steam bans: None", account.Note);
        Assert.DoesNotContain("92/100", account.Note);
        Assert.Empty(account.Cautions);
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsWatchContext(PublicProfileFixtures.CsWatch(Id), TestPlayerIds.Secondary));
    }

    [Fact]
    public void CsWatchNameRequiresVerifiedIdentityAndASingleSafeHeading()
    {
        var html = PublicProfileFixtures.CsWatch(Id);
        Assert.Equal("Test player", WebsiteDataParser.CsWatchContext(html, Id).DisplayName);
        var invalid = html.Replace("<h1>Test player</h1>", "<h1>Player&#10;Name</h1>");
        var context = WebsiteDataParser.CsWatchContext(invalid, Id);
        Assert.Null(context.DisplayName);
        Assert.Contains("CSWatch player name unavailable", context.Note);
        Assert.Contains("Steam bans: None", context.Note);
        Assert.Null(WebsiteDataParser.CsWatchContext(html.Replace("<h1>Test player</h1>",
            "<h1>First</h1><h1>Second</h1>"), Id).DisplayName);
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsWatchContext(html, TestPlayerIds.Secondary));
    }

    [Fact]
    public async Task VerifiedCsWatchNameFlowsToFinalWhenSteamIsUnavailable()
    {
        using var client = new TrackerClient(new Handler(request =>
            Text(request.RequestUri!.Host == "cswatch.gg"
                ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /")));
        await using var renderer = new SequenceRenderer(_ => PublicProfileFixtures.CsWatch(Id));
        var sources = await client.FetchAsync(Id, CancellationToken.None, renderer: renderer);
        Assert.Equal("Test player", Assert.Single(sources, item => item.Source == "cswatch.gg public page").DisplayName);
        Assert.Contains("Player: \"Test player\" (CSWatch; may be outdated)", Analysis.RenderFinal(Id, sources));
    }

    [Fact]
    public void BanHistoryBecomesAVisibleCautionWithoutChangingTheStatisticalVerdict()
    {
        var banned = WebsiteDataParser.CsWatchContext(
            PublicProfileFixtures.CsWatch(Id).Replace("<b>None</b>", "<b>1 on record</b>"), Id);
        var caution = Assert.Single(banned.Cautions);
        Assert.Contains("Steam bans: 1 on record", caution);
        var evidence = new Evidence[]
        {
            new("Leetify", Sources.LeetifyProfile(Id), "Public data", new PlayerStats(30, 0.67, 22.85, 23.33)),
            new("CSWatch", Sources.Profile("cswatch.gg", Id), "Public account context",
                Note: banned.Note, Cautions: banned.Cautions)
        };
        var final = Analysis.RenderFinal(Id, evidence);
        Assert.Contains("VERDICT: NO STATISTICAL RED FLAGS FOUND", final);
        Assert.Contains("ACCOUNT CAUTIONS", final);
        Assert.Contains("CSWatch: CSWatch reports Steam bans: 1 on record", final);
        Assert.Contains("ban history", final);
        Assert.Contains("not statistical indicators or proof of current CS2 cheating", final);
        Assert.Contains("CAUTION: CSWatch reports Steam bans: 1 on record", Analysis.Render(Id, evidence));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void SteamBanFlagIsReadEvenWhenTheProfileIsPrivate(string vac, bool expectCaution)
    {
        var xml = PublicProfileFixtures.Steam(Id)
            .Replace("<privacyState>public", "<privacyState>friendsonly")
            .Replace("<vacBanned>0", $"<vacBanned>{vac}");
        var context = WebsiteDataParser.SteamContext(xml, Id);
        Assert.Contains("marks this account friendsonly", context.Note);
        Assert.Contains("ban status is public regardless of privacy", context.Note);
        Assert.Contains($"VAC-ban flag: {(vac == "1" ? "set" : "not set")}", context.Note);
        Assert.DoesNotContain("Member since:", context.Note);
        if (expectCaution) Assert.Contains("VAC-ban flag is set", Assert.Single(context.Cautions));
        else Assert.Empty(context.Cautions);
    }

    [Fact]
    public void StatsProfileContextDoesNotTurnRankWinsIntoAUsableMatchSample()
    {
        var html = PublicProfileFixtures.CsStats(Id);
        var note = WebsiteDataParser.CsStatsContext(html, Id);
        Assert.Contains("tracking is not enabled", note);
        Assert.Contains("not used as a match sample", note);
        Assert.Throws<FormatException>(() => WebsiteDataParser.CsStatsContext(html, TestPlayerIds.Secondary));
    }

    [Fact]
    public void SteamPrivateExportProvidesPrivacyContextWithoutInventingAccountDetails()
    {
        var xml = PublicProfileFixtures.Steam(Id).Replace("<privacyState>public", "<privacyState>private");
        var result = WebsiteDataParser.SteamContext(xml, Id);
        Assert.Contains("marks this account private", result.Note);
        Assert.Contains("VAC-ban flag: not set", result.Note);
        Assert.DoesNotContain("Member since:", result.Note);
        Assert.Equal("Fixture Player", result.DisplayName);
        Assert.Contains("Steam display name publicly returned", result.Note);
        Assert.Empty(result.Cautions);
    }

    [Fact]
    public void InvalidSteamMembershipDateDoesNotDiscardNameOrBanContext()
    {
        var xml = PublicProfileFixtures.Steam(Id)
            .Replace("June 8, 2019", "unrecognized date").Replace("<vacBanned>0", "<vacBanned>1");
        var context = WebsiteDataParser.SteamContext(xml, Id);
        Assert.Equal("Fixture Player", context.DisplayName);
        Assert.Contains("Steam membership date unavailable", context.Note);
        Assert.Contains("VAC-ban flag: set", context.Note);
        Assert.Single(context.Cautions);
    }

    [Fact]
    public void RestrictedSteamProfileDoesNotInventAMissingName()
    {
        var xml = PublicProfileFixtures.Steam(Id)
            .Replace("<privacyState>public", "<privacyState>friendsonly")
            .Replace("<steamID><![CDATA[Fixture Player]]></steamID>", "");
        var context = WebsiteDataParser.SteamContext(xml, Id);
        Assert.Null(context.DisplayName);
        Assert.Contains("Steam display name unavailable", context.Note);
        Assert.DoesNotContain("Member since:", context.Note);
    }

    [Theory]
    [InlineData("<steamID><![CDATA[Fixture Player]]></steamID>", "Fixture Player")]
    [InlineData("<steamID>Fixture &amp; Player</steamID>", "Fixture & Player")]
    [InlineData("<steamID>&#x2067;&#x2067;Fixture</steamID>", "Fixture")]
    [InlineData("<steamID>&#x2066;Fixture&#x2068; Player&#x2069;</steamID>", "Fixture Player")]
    [InlineData("<steamID>&#x2067;&#x2069;</steamID>", null)]
    [InlineData("", null)]
    [InlineData("<steamID>First</steamID><steamID>Second</steamID>", null)]
    [InlineData("<steamID>First&#10;Second</steamID>", null)]
    [InlineData("<steamID>First&#x202e;Second</steamID>", null)]
    [InlineData("<steamID><b>Nested</b></steamID>", null)]
    public void SteamDisplayNameRequiresAnUnambiguousSafeVerifiedProfile(string field, string? expected)
    {
        var xml = PublicProfileFixtures.Steam(Id)
            .Replace("<steamID><![CDATA[Fixture Player]]></steamID>", field);
        var result = WebsiteDataParser.SteamContext(xml, Id);
        Assert.Equal(expected, result.DisplayName);
        if (expected is null) Assert.Contains("Steam display name unavailable", result.Note);
        else Assert.Contains("Steam display name: " + expected, result.Note);
        Assert.Throws<FormatException>(() => WebsiteDataParser.SteamContext(xml, TestPlayerIds.Secondary));
    }

    [Fact]
    public void SteamAccountAgeUsesVerifiedMembershipDateAndDoesNotInventPrivateOrInvalidAge()
    {
        var xml = PublicProfileFixtures.Steam(Id);
        var asOf = new DateTimeOffset(2019, 6, 10, 0, 30, 0, TimeSpan.FromHours(2));
        var context = WebsiteDataParser.SteamContext(xml, Id, asOf);
        Assert.Contains("1 day (member since 2019-06-08; as of 2019-06-09 UTC)", Assert.Single(context.SmurfReviewContext));
        Assert.Empty(context.Cautions);
        foreach (var unavailable in new[]
        {
            xml.Replace("<privacyState>public", "<privacyState>private"),
            xml.Replace("June 8, 2019", "January 1, 2099"),
            xml.Replace("June 8, 2019", "unrecognized date"),
            xml.Replace("<memberSince>June 8, 2019</memberSince>", ""),
            xml.Replace("</profile>", "<memberSince>June 8, 2019</memberSince></profile>")
        })
        {
            var unknown = WebsiteDataParser.SteamContext(unavailable, Id, asOf);
            Assert.StartsWith("Steam account age: unknown", Assert.Single(unknown.SmurfReviewContext));
            Assert.DoesNotContain("member since 2019", string.Join(" ", unknown.SmurfReviewContext));
            Assert.Equal("Fixture Player", unknown.DisplayName);
            Assert.Empty(unknown.Cautions);
        }
    }

    [Fact]
    public async Task VerifiedSteamDisplayNameFlowsIntoTheFinalReport()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt")
                return Text(url.Host == "steamcommunity.com" ? "User-agent: *\nAllow: /" : "User-agent: *\nDisallow: /");
            return Text(PublicProfileFixtures.Steam(Id).Replace("Fixture Player", "\u2067\u2067Fixture Player"));
        }));
        var sources = await client.FetchAsync(Id, CancellationToken.None);
        var steam = Assert.Single(sources, item => item.Source == "Steam public page");
        Assert.Equal("Fixture Player", steam.DisplayName);
        Assert.Contains($"FINAL RESULT | SteamID64 {Id} | Player: \"Fixture Player\"", Analysis.RenderFinal(Id, sources));
        Assert.Contains("member since 2019-06-08", Analysis.RenderFinal(Id, sources));
    }

    [Fact]
    public void SteamPublicXmlRequiresExactIdentityAndRejectsExternalEntities()
    {
        var xml = PublicProfileFixtures.Steam(Id);
        var note = WebsiteDataParser.SteamContext(xml, Id).Note;
        Assert.Contains("VAC-ban flag: not set", note);
        Assert.Contains("Member since: 2019-06-08", note);
        Assert.Throws<FormatException>(() => WebsiteDataParser.SteamContext(xml, TestPlayerIds.Secondary));
        Assert.Throws<FormatException>(() => WebsiteDataParser.SteamContext(
            "<!DOCTYPE profile [<!ENTITY probe SYSTEM 'file:///does-not-exist'>]>" + xml, Id));
        Assert.Throws<FormatException>(() => WebsiteDataParser.SteamContext(xml.Replace("<vacBanned>0", "<vacBanned>9"), Id));
    }

    private const string SteamKey = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task FaceitApiUsesVerifiedIdentityAndKeepsLifetimeValuesAsUnscoredContext()
    {
        const string key = "test-faceit-api-key";
        const string player = "12345678-1234-1234-1234-123456789abc";
        var requests = new List<Uri>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            requests.Add(url);
            Assert.Equal("open.faceit.com", url.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(key, request.Headers.Authorization.Parameter);
            if (url.AbsolutePath == "/data/v4/players")
                return Text(System.Text.Json.JsonSerializer.Serialize(new { player_id = player, steam_id_64 = Id,
                    games = new { cs2 = new { game_player_id = Id } } }));
            Assert.Equal($"/data/v4/players/{player}/stats/cs2", url.AbsolutePath);
            return Text(System.Text.Json.JsonSerializer.Serialize(new { player_id = player, game_id = "cs2",
                lifetime = new Dictionary<string, string> { ["Matches"] = "300", ["Wins"] = "180",
                    ["Win Rate %"] = "60", ["K/D Ratio"] = "10634.06", ["Average K/D Ratio"] = "1.45" } }));
        }));
        var progress = new List<FetchProgress>();
        var evidence = await client.FetchAsync(Id, CancellationToken.None, faceitApiKey: key,
            progress: new InlineProgress(progress.Add));
        var faceit = Assert.Single(evidence, e => e.Source == "FACEIT Data API (official, keyed)");
        Assert.Null(faceit.Stats);
        Assert.Contains("Matches: 300", faceit.Note!);
        Assert.Contains("Average K/D Ratio: 1.45", faceit.Note!);
        Assert.DoesNotContain("10634.06", faceit.Note!);
        Assert.Contains("context only", faceit.Note!);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, uri => Assert.DoesNotContain(key, uri.AbsoluteUri));
        Assert.DoesNotContain(key, Analysis.Render(Id, evidence) + Analysis.RenderFinal(Id, evidence));
        Assert.Equal(Sources.PublicPages(Id).Count + 1, progress[^1].TotalSources);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaceitDistinguishesMissingPlayerFromMissingStatistics(bool missingPlayer)
    {
        const string player = "12345678-1234-1234-1234-123456789abc";
        var requests = new List<Uri>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            requests.Add(url);
            Assert.Equal("open.faceit.com", url.Host);
            if (url.AbsolutePath == "/data/v4/players")
            {
                Assert.Equal($"?game=cs2&game_player_id={Id}", url.Query);
                return missingPlayer ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Text(JsonSerializer.Serialize(new { player_id = player, steam_id_64 = Id,
                        games = new { cs2 = new { game_player_id = Id } } }));
            }
            Assert.Equal($"/data/v4/players/{player}/stats/cs2", url.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var evidence = await client.FetchAsync(Id, CancellationToken.None, faceitApiKey: "test-faceit-api-key");
        var faceit = Assert.Single(evidence, e => e.Source == "FACEIT Data API (official, keyed)");
        Assert.Null(faceit.Stats);
        Assert.Contains("HTTP 404", faceit.Note!);
        Assert.Equal(missingPlayer ? 1 : 2, requests.Count);
        if (missingPlayer)
        {
            Assert.Equal("Unavailable", faceit.Status);
            Assert.Contains("player lookup by SteamID", faceit.Note!);
            Assert.Contains("not a statistics request", faceit.Note!);
        }
        else
        {
            Assert.Equal("Official FACEIT account context", faceit.Status);
            Assert.Equal(requests[^1], faceit.Url);
            Assert.Contains("account found and linked Steam identity verified", faceit.Note!);
            Assert.Contains("CS2 lifetime statistics endpoint", faceit.Note!);
            Assert.Contains("zero matches", faceit.Note!);
            Assert.Contains("no gameplay metrics were assessed", faceit.Note!);
            var final = Analysis.RenderFinal(Id, evidence);
            Assert.Contains("Official FACEIT account context (context only, no assessed gameplay sample)", final);
            Assert.DoesNotContain("FACEIT Data API (official, keyed): Unavailable", final);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaceitRejectsMismatchedProfileOrStatisticsIdentity(bool mismatchProfile)
    {
        const string player = "12345678-1234-1234-1234-123456789abc";
        var statsRequests = 0;
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath == "/data/v4/players")
                return Text(System.Text.Json.JsonSerializer.Serialize(new { player_id = player,
                    steam_id_64 = mismatchProfile ? TestPlayerIds.Secondary : Id }));
            statsRequests++;
            return Text(System.Text.Json.JsonSerializer.Serialize(new { player_id = player, game_id = "other",
                lifetime = new { Matches = "300" } }));
        }));
        var evidence = await client.FetchAsync(Id, CancellationToken.None, faceitApiKey: "test-faceit-api-key");
        var faceit = Assert.Single(evidence, e => e.Source == "FACEIT Data API (official, keyed)");
        Assert.Equal("Unavailable", faceit.Status);
        Assert.Null(faceit.Stats);
        Assert.Equal(mismatchProfile ? 0 : 1, statsRequests);
        Assert.Contains("match", faceit.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401", 1)]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403", 1)]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429", 3)]
    public async Task FaceitApiRetriesTransientAccessFailuresOnly(
        HttpStatusCode code, string expected, int expectedRequests)
    {
        var faceitRequests = 0;
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            faceitRequests++;
            return new HttpResponseMessage(code);
        }));
        var evidence = await client.FetchAsync(Id, CancellationToken.None, faceitApiKey: "test-faceit-api-key");
        var faceit = Assert.Single(evidence, e => e.Source == "FACEIT Data API (official, keyed)");
        Assert.Contains(expected, faceit.Note!);
        Assert.Equal(expectedRequests, faceitRequests);
        Assert.Null(faceit.Stats);
    }

    private static string SteamBansJson(string id, int vacBans, int gameBans, int daysSince) =>
        $$"""{"players":[{"SteamId":"{{id}}","CommunityBanned":false,"VACBanned":{{(vacBans > 0 ? "true" : "false")}},"NumberOfVACBans":{{vacBans}},"DaysSinceLastBan":{{daysSince}},"NumberOfGameBans":{{gameBans}},"EconomyBan":"none"}]}""";

    private static string SteamStatsJson(string id) =>
        $$$"""
        {"playerstats":{"steamID":"{{{id}}}","gameName":"ValveTestApp260","stats":[
            {"name":"total_kills","value":20000},{"name":"total_deaths","value":10000},
            {"name":"total_rounds_played","value":40000},{"name":"total_kills_headshot","value":8000},
            {"name":"total_matches_won","value":600},{"name":"total_matches_played","value":1000},
            {"name":"total_time_played","value":3600000},
            {"name":"total_kills_ak47","value":5000},{"name":"total_shots_ak47","value":40000},{"name":"total_hits_ak47","value":8800},
            {"name":"total_kills_awp","value":2000},{"name":"total_shots_awp","value":8000},{"name":"total_hits_awp","value":1800},
            {"name":"total_kills_deagle","value":900},{"name":"total_shots_deagle","value":10000},{"name":"total_hits_deagle","value":2100},
            {"name":"total_kills_mag7","value":50},{"name":"total_shots_mag7","value":400},{"name":"total_hits_mag7","value":100}
        ]}}
        """;

    [Theory]
    [InlineData("{\"name\":\"total_matches_played\",\"value\":1000}", "Steam-reported match total: 1,000 (context only; not verified as the K/R sample)")]
    [InlineData("{\"name\":\"total_matches_played\",\"value\":0}", "Steam-reported match total: 0 (context only; not verified as the K/R sample)")]
    [InlineData("{\"name\":\"total_matches_played\",\"value\":3000000000}", "Steam-reported match total: 3,000,000,000 (context only; not verified as the K/R sample)")]
    [InlineData("{\"name\":\"total_matches_played\",\"value\":-1}", "Steam-reported match total unavailable")]
    [InlineData("{\"name\":\"another_counter\",\"value\":1000}", "Steam-reported match total unavailable")]
    [InlineData("{\"name\":\"total_matches_played\",\"value\":1000},{\"name\":\"total_matches_played\",\"value\":1000}", "Steam-reported match total unavailable")]
    public async Task SteamKprDisplaysItsSeparateMatchCounterOnlyAsUnverifiedContext(string matchCounter, string expected)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            return url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/")
                ? Text(SteamBansJson(Id, 0, 0, 0))
                : Text(SteamStatsJson(Id).Replace("{\"name\":\"total_matches_played\",\"value\":1000}", matchCounter));
        }));
        var evidence = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var result = Analysis.RenderFinal(Id, evidence);
        Assert.Contains("20,000 reported kills / 40,000 reported rounds; match count not verified", result);
        Assert.Contains(expected, result);
        Assert.Contains("Steam counter ratio; matching modes and periods are unverified", result);
    }

    [Fact]
    public async Task SteamWeaponScreenUsesTheSameCustomRulesAsWebsiteSamples()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            return url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/")
                ? Text(SteamBansJson(Id, 0, 0, 0)) : Text(SteamStatsJson(Id));
        }));
        var rules = new ScreeningRules { WeaponMinimumKills = 1000, WeaponMinimumCount = 2,
            WeaponSpreadPercentThreshold = 0.4 };
        var evidence = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey, rules: rules);
        var steam = Assert.Single(evidence, e => e.Source == "Steam Web API (official, keyed)");
        var section = Assert.Single(steam.Sections!);
        Assert.Contains("2 weapons", section.Title);
        var check = Assert.Single(section.Checks!);
        Assert.Equal(0.4, check.Threshold);
        Assert.False(check.IsFlagged);
    }

    [Fact]
    public async Task SteamApiKeyAddsOfficialBanRecordAndStatisticsWithoutExposingTheKey()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            Assert.Equal("api.steampowered.com", url.Host);
            Assert.Contains($"key={SteamKey}", url.Query);
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
            {
                Assert.Contains($"steamids={Id}", url.Query);
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 30));
            }

            if (url.AbsolutePath == "/IPlayerService/GetSteamLevel/v1/")
            {
                Assert.Contains($"steamid={Id}", url.Query);
                return Text("""{"response":{"player_level":12}}""");
            }
            if (url.AbsolutePath == "/ISteamUser/GetPlayerSummaries/v2/")
            {
                Assert.Contains($"steamids={Id}", url.Query);
                return Text($$$"""{"response":{"players":[{"steamid":"{{{Id}}}","personaname":"\u2067Fallback Player\u2069"}]}}""");
            }
            Assert.StartsWith("/ISteamUserStats/GetUserStatsForGame/", url.AbsolutePath);
            Assert.Contains("appid=730", url.Query);
            Assert.Contains($"steamid={Id}", url.Query);
            return Text(SteamStatsJson(Id));
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(result, e => e.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Official ban record and lifetime statistics retrieved", official.Status);
        Assert.Contains("Player: \"Fallback Player\"", Analysis.RenderFinal(Id, result));
        Assert.Contains("GetPlayerSummaries; SteamID matched", Analysis.Render(Id, result));
        Assert.Equal(1000, official.Stats!.Matches);
        Assert.Equal(2, official.Stats.Kd);
        Assert.Equal(40, official.Stats.Headshots);
        Assert.Equal(60, official.Stats.WinRate);
        Assert.Contains("Win rate divides wins by total matches", official.Note!);
        Assert.Null(official.Stats.Kpr);
        Assert.Equal(0.5, official.Stats.ContextKpr);
        var caution = Assert.Single(official.Cautions!);
        Assert.Contains("1 game ban(s) and 0 VAC ban(s), most recent 30 days ago", caution);
        var weapons = Assert.Single(official.Sections!);
        Assert.Contains("3 weapons", weapons.Title);
        Assert.DoesNotContain("MAG-7", weapons.Text);
        var check = Assert.Single(weapons.Checks!);
        Assert.Equal("Weapon accuracy spread", check.Name);
        Assert.True(check.IsFlagged);
        Assert.Contains("time played 1000 hours", official.Note!);
        Assert.Contains("Steam level: 12", Assert.Single(official.SmurfReviewContext));
        foreach (var item in result)
        {
            Assert.DoesNotContain(SteamKey, item.Url.AbsoluteUri);
            Assert.DoesNotContain(SteamKey, item.Note ?? "");
            foreach (var section in item.Sections ?? []) Assert.DoesNotContain(SteamKey, section.Title + section.Text);
        }
        Assert.DoesNotContain(SteamKey, Analysis.Render(Id, result) + Analysis.RenderFinal(Id, result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamDisplayNameFallbackRejectsWrongIdentityOrDuplicateName(bool duplicateName)
    {
        using var client = new TrackerClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => Text("User-agent: *\nDisallow: /"),
            "/ISteamUser/GetPlayerBans/v1/" => Text(SteamBansJson(Id, vacBans: 0, gameBans: 0, daysSince: 0)),
            "/ISteamUserStats/GetUserStatsForGame/v2/" => Text(SteamStatsJson(Id)),
            "/IPlayerService/GetSteamLevel/v1/" => Text("""{"response":{"player_level":12}}"""),
            "/ISteamUser/GetPlayerSummaries/v2/" => Text(duplicateName
                ? $$$"""{"response":{"players":[{"steamid":"{{{Id}}}","personaname":"First","personaname":"Second"}]}}"""
                : """{"response":{"players":[{"steamid":"76561197960265729","personaname":"Wrong Player"}]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var steam = Assert.Single(result, item => item.Source == "Steam Web API (official, keyed)");
        Assert.Null(steam.DisplayName);
        Assert.NotNull(steam.Stats);
        Assert.Contains("display name", steam.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Player: unavailable", Analysis.RenderFinal(Id, result));
    }

    [Theory]
    [InlineData(1000, 800, 1.25)]
    [InlineData(1000, 0, null)]
    [InlineData(1000, null, null)]
    public async Task SteamKprIsComputedFromReturnedCountersWithoutAssumingMatchingScopes(int kills, int? rounds, double? expected)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 0, daysSince: 0));
            var json = SteamStatsJson(Id)
                .Replace("\"total_kills\",\"value\":20000", $"\"total_kills\",\"value\":{kills}")
                .Replace("\"total_kills_headshot\",\"value\":8000", "\"total_kills_headshot\",\"value\":400");
            json = rounds.HasValue
                ? json.Replace("\"total_rounds_played\",\"value\":40000", $"\"total_rounds_played\",\"value\":{rounds}")
                : json.Replace("{\"name\":\"total_rounds_played\",\"value\":40000},", "");
            return Text(json);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(result, item => item.Source == "Steam Web API (official, keyed)");
        Assert.Null(official.Stats!.Kpr);
        Assert.Equal(expected, official.Stats.ContextKpr);
        Assert.Contains("does not establish that the counters cover the same game modes or periods", official.Note!);
        if (rounds is not null) Assert.Contains($"rounds {rounds}", official.Note!);
        var final = Analysis.RenderFinal(Id, [official]);
        if (expected is not null)
        {
            Assert.Contains("context only, not assessed; no K/R warning line applied", final);
            Assert.Contains("1,000 reported kills / 800 reported rounds; match count not verified", final);
            Assert.DoesNotContain("source: Steam Web API (official, keyed); sample: 1000 matches", final);
            Assert.DoesNotContain("Kills per round of", final);
            Assert.DoesNotContain("Kills per round >=", Analysis.Render(Id, [official]));
            Assert.Contains("K/R: 1.25 (lifetime counter ratio; context only, not assessed)", Analysis.Render(Id, [official]));
            Assert.Contains("Combat metric match counts are unverified", Analysis.Render(Id, [official]));
            var withoutKpr = official with { Stats = official.Stats with { ContextKpr = null } };
            Assert.Equal(Analysis.Summarize([withoutKpr]), Analysis.Summarize([official]));
        }
        else
        {
            Assert.DoesNotContain("Kills per round of", final);
            Assert.Contains(official.Sections!, section => section.Title == "Lifetime K/R unavailable");
        }
    }

    [Fact]
    public async Task SteamRetainsContextOnlyKprWithoutCountingItAsGameplayOrCoverage()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, 0, 0, 0));
            if (url.AbsolutePath == "/IPlayerService/GetSteamLevel/v1/")
                return Text("""{"response":{"player_level":12}}""");
            return Text($$$"""
                {"playerstats":{"steamID":"{{{Id}}}","stats":[
                    {"name":"total_kills","value":1120},{"name":"total_rounds_played","value":1000}
                ]}}
                """);
        }));
        var progress = new List<FetchProgress>();
        var evidence = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey,
            progress: new InlineProgress(progress.Add));
        var steam = Assert.Single(evidence, item => item.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Official ban record and lifetime context retrieved", steam.Status);
        Assert.Equal(1.12, steam.Stats!.ContextKpr);
        Assert.Null(steam.Stats.Kpr);
        Assert.False(steam.Stats.HasMetrics);
        Assert.Equal(0, progress[^1].PerformanceSummaries);
        Assert.False(Analysis.Summarize(evidence).HasData);
        var final = Analysis.RenderFinal(Id, evidence);
        Assert.Contains("VERDICT: INCONCLUSIVE", final);
        Assert.Contains("0/4 core metric types; 0 distinct metric types assessed", final);
        Assert.Contains("Steam Web API (official, keyed): 1.12", final);
        Assert.Contains("context only, not assessed; no K/R warning line applied", final);
        Assert.Contains("K/R: 1.12 (lifetime counter ratio; context only, not assessed)", Analysis.Render(Id, evidence));
    }

    [Fact]
    public async Task InvalidSteamWeaponAccuracyDoesNotDiscardCombatStatsOrBans()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 30));
            return Text(SteamStatsJson(Id).Replace("\"total_hits_ak47\",\"value\":8800",
                "\"total_hits_ak47\",\"value\":50000"));
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        Assert.Equal(0.5, official.Stats!.ContextKpr);
        Assert.Single(official.Cautions!);
        var unavailable = Assert.Single(official.Sections!);
        Assert.Equal("Weapon accuracy variance unavailable", unavailable.Title);
        Assert.Contains("hits exceed shots", unavailable.Text);
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [official]));
    }

    [Fact]
    public async Task InvalidSteamHeadshotTotalsDoNotDiscardOtherMetricsOrBanRecord()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 30));
            return Text(SteamStatsJson(Id).Replace("\"total_kills_headshot\",\"value\":8000",
                "\"total_kills_headshot\",\"value\":20001"));
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        Assert.Equal(0.5, official.Stats!.ContextKpr);
        Assert.Null(official.Stats.Headshots);
        Assert.Single(official.Cautions!);
        Assert.Contains(official.Sections!, section => section.Title == "Lifetime headshot percentage unavailable" &&
            section.Text.Contains("headshot kills exceed"));
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [official]));
    }

    [Fact]
    public async Task SteamExplainsWhenWeaponSamplesDoNotQualifyForTheSpreadCheck()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 0, daysSince: 0));
            return Text(SteamStatsJson(Id).Replace("\"total_kills_awp\",\"value\":2000", "\"total_kills_awp\",\"value\":5")
                .Replace("\"total_kills_deagle\",\"value\":900", "\"total_kills_deagle\",\"value\":5"));
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        var section = Assert.Single(official.Sections!, item => item.Title == "Weapon accuracy variance (Valve lifetime, not assessed)");
        Assert.Contains("1 qualifying weapons", section.Text);
        Assert.Null(section.Checks);
        Assert.Equal(0.5, official.Stats!.ContextKpr);
    }

    [Theory]
    [InlineData("total_deaths", 10000, 0.5, 40.0)]
    [InlineData("total_kills", 20000, null, null)]
    public async Task SteamRetainsRatiosWhoseRequiredCountersAreAvailable(string missing, int value, double? kpr, double? hs)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 0, daysSince: 0));
            return Text(SteamStatsJson(Id).Replace($"{{\"name\":\"{missing}\",\"value\":{value}}},", ""));
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        Assert.Null(official.Stats!.Kd);
        Assert.Equal(kpr, official.Stats.ContextKpr);
        Assert.Equal(hs, official.Stats.Headshots);
        Assert.Equal(60, official.Stats.WinRate);
        Assert.Contains(official.Sections!, section => section.Title == "Lifetime K/D unavailable");
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [official]));
    }

    [Theory]
    [InlineData("\"total_deaths\",\"value\":-1")]
    [InlineData("\"total_deaths\",\"value\":\"invalid\"")]
    [InlineData("\"total_deaths\",\"value\":10000},{\"name\":\"total_deaths\",\"value\":1")]
    public async Task InvalidOrDuplicateSteamCounterDoesNotDiscardIndependentRatios(string replacement)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 30));
            return Text(SteamStatsJson(Id).Replace("\"total_deaths\",\"value\":10000", replacement));
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        Assert.Null(official.Stats!.Kd);
        Assert.Equal(0.5, official.Stats.ContextKpr);
        Assert.Equal(40, official.Stats.Headshots);
        Assert.Equal(60, official.Stats.WinRate);
        Assert.Contains(official.Sections!, section => section.Title == "Lifetime counter unavailable" &&
            section.Text.Contains("total_deaths"));
        Assert.Single(official.Cautions!);
    }

    [Fact]
    public async Task PrivateGameDetailsStillYieldValveOfficialBanRecord()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 2, gameBans: 0, daysSince: 400));
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(result, e => e.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Official ban record retrieved", official.Status);
        Assert.Null(official.Stats);
        Assert.Contains(official.Sections!, section => section.Title == "Lifetime game statistics unavailable" &&
            section.Text.Contains("HTTP 403") && section.Text.Contains("reason is not established"));
        Assert.Contains("0 game ban(s) and 2 VAC ban(s)", Assert.Single(official.Cautions!));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "rejected the statistics request")]
    [InlineData(HttpStatusCode.Unauthorized, "did not authorize")]
    [InlineData(HttpStatusCode.Forbidden, "refused access")]
    [InlineData(HttpStatusCode.InternalServerError, "internal server error")]
    public async Task SteamStatisticsFailuresAreReportedSeparatelyWithoutClaimingACause(HttpStatusCode status, string message)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (request.RequestUri.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 12));
            return new HttpResponseMessage(status);
        }));
        var official = Assert.Single(await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey),
            item => item.Source == "Steam Web API (official, keyed)");
        Assert.Null(official.Stats);
        Assert.Single(official.Cautions!);
        var section = Assert.Single(official.Sections!);
        Assert.Equal("Lifetime game statistics unavailable", section.Title);
        Assert.Contains($"HTTP {(int)status}", section.Text);
        Assert.Contains(message, section.Text);
        Assert.Contains("reason is not established", section.Text);
        Assert.DoesNotContain(SteamKey, Analysis.Render(Id, [official]));
        Assert.Contains("Partial retrieval", Analysis.RenderFinal(Id, [official]));
    }

    [Fact]
    public async Task MismatchedValveIdentityDiscardsTheKeyedResponse()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            return Text(SteamBansJson(TestPlayerIds.Secondary, vacBans: 0, gameBans: 0, daysSince: 0));
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(result, e => e.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Unavailable", official.Status);
        Assert.Null(official.Stats);
        Assert.Null(official.Cautions);
        Assert.Contains("did not match the requested SteamID", official.Note!);
    }

    [Theory]
    [InlineData("User-agent: *\nAllow: /", true)]
    [InlineData("User-agent: *\nDisallow: /stats", false)]
    public async Task ProReferenceRefreshRespectsHltvRobotsAndParsesTheTopPlayer(string robots, bool expectNote)
    {
        var pageRequested = false;
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            Assert.Equal("www.hltv.org", url.Host);
            if (url.AbsolutePath == "/robots.txt") return Text(robots);
            pageRequested = true;
            Assert.Equal("/stats/players", url.AbsolutePath);
            return Text("""<table><td class="playerCol"><a href="/stats/players/21167/donk" class="a">donk</a></td></table>""");
        }));
        var note = await client.FetchProBenchmarkAsync(CancellationToken.None);
        if (expectNote)
        {
            Assert.NotNull(note);
            Assert.Contains("donk", note);
            Assert.Contains("live HLTV", note);
            Assert.Contains("dated snapshot", note);
        }
        else
        {
            Assert.Contains("disallowed by robots.txt", note);
            Assert.False(pageRequested);
        }
    }

    [Fact]
    public async Task ProReferenceRefreshReportsActualFailureWithoutInventingBotVerification()
    {
        using var client = new TrackerClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var blocked = await client.FetchProBenchmarkAsync(CancellationToken.None);
        Assert.Contains("HTTP 403", blocked);
        Assert.DoesNotContain("bot verification", blocked);
        using var noLink = new TrackerClient(new Handler(request =>
            Text(request.RequestUri!.AbsolutePath == "/robots.txt" ? "User-agent: *\nAllow: /" : "<html>maintenance</html>")));
        Assert.Contains("no recognized top-player entry", await noLink.FetchProBenchmarkAsync(CancellationToken.None));
    }

    private const string LeetifyKey = "lk_0123456789abcdefTESTKEY";

    [Fact]
    public async Task SteamKeyTestUsesKeyedBanEndpointAndRequiresMatchingPlayer()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
            Assert.Equal("/ISteamUser/GetPlayerBans/v1/", request.RequestUri.AbsolutePath);
            Assert.Contains($"key={SteamKey}", request.RequestUri.Query);
            Assert.Contains($"steamids={Id}", request.RequestUri.Query);
            return Text(SteamBansJson(Id, 0, 0, 0));
        }));
        await client.ValidateSteamKeyAsync(SteamKey, Id, CancellationToken.None);

        using var mismatch = new TrackerClient(new Handler(_ => Text("""{"players":[{"SteamId":"wrong"}]}""")));
        await Assert.ThrowsAsync<FormatException>(() => mismatch.ValidateSteamKeyAsync(SteamKey, Id, CancellationToken.None));
        using var rejected = new TrackerClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => rejected.ValidateSteamKeyAsync(SteamKey, Id, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task FaceitKeyTestUsesAuthorizedGameEndpointAndRequiresMatchingGame()
    {
        const string key = "faceit-test-token";
        using var client = new TrackerClient(new Handler(request =>
        {
            Assert.Equal("https://open.faceit.com/data/v4/games/cs2", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(key, request.Headers.Authorization.Parameter);
            return Text("""{"game_id":"cs2"}""");
        }));
        await client.ValidateFaceitKeyAsync(key, CancellationToken.None);

        using var mismatch = new TrackerClient(new Handler(_ => Text("""{"game_id":"other"}""")));
        await Assert.ThrowsAsync<FormatException>(() => mismatch.ValidateFaceitKeyAsync(key, CancellationToken.None));
        using var rejected = new TrackerClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => rejected.ValidateFaceitKeyAsync(key, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task LeetifyKeyValidationUsesOfficialEndpointAndRequiresHttp200(HttpStatusCode status)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            Assert.Equal("https://api-public.cs-prod.leetify.com/api-key/validate", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(LeetifyKey, Assert.Single(request.Headers.GetValues("_leetify_key")));
            return new HttpResponseMessage(status);
        }));
        if (status == HttpStatusCode.OK)
            await client.ValidateLeetifyKeyAsync(LeetifyKey, CancellationToken.None);
        else if (status == HttpStatusCode.Unauthorized)
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ValidateLeetifyKeyAsync(LeetifyKey, CancellationToken.None));
        else
            await Assert.ThrowsAsync<FormatException>(() => client.ValidateLeetifyKeyAsync(LeetifyKey, CancellationToken.None));
    }

    [Fact]
    public void LeetifyRankContextSeparatesModesAndExcludesInvalidOrAmbiguousRecords()
    {
        using var doc = JsonDocument.Parse("""
            {"privacy_mode":"public","ranks":{"premier":12500,"competitive":[
                {"map_name":"de_nuke","rank":9},
                {"map_name":"de_mirage","rank":0},
                {"map_name":"de_ancient","rank":19},
                {"map_name":"de_inferno","rank":8},{"map_name":"de_inferno","rank":9},
                {"map_name":"de_dust2","rank":5,"rank":6},
                {"map_name":"de_anubis","rank":2.5},
                {"map_name":"unsafe\nmap","rank":18}
            ]}}
            """);
        var context = string.Join("\n", WebsiteDataParser.LeetifyRankContext(doc.RootElement));
        Assert.Contains("Premier rating (Leetify-tracked): 12500", context);
        Assert.Contains("Competitive de_nuke rank (Leetify-tracked): 9/18", context);
        Assert.Contains("Rank update time unavailable", context);
        foreach (var map in new[] { "de_mirage", "de_ancient", "de_inferno", "de_dust2", "de_anubis" })
            Assert.Contains($"Competitive {map} rank: unknown/unranked", context);
        Assert.Contains("invalid or ambiguous map record", context);
        Assert.DoesNotContain("unsafe", context);
    }

    [Theory]
    [InlineData("""{"privacy_mode":"private","ranks":{"premier":12500}}""", "not identify this profile as public")]
    [InlineData("""{"privacy_mode":"public"}""", "missing, invalid or duplicate Leetify ranks object")]
    [InlineData("""{"privacy_mode":"public","ranks":{},"ranks":{"premier":12500}}""", "duplicate Leetify ranks object")]
    [InlineData("""{"privacy_mode":"public","ranks":{"premier":12500,"premier":14000}}""", "Premier rating: unknown/unranked")]
    [InlineData("""{"privacy_mode":"public","ranks":{"premier":0,"competitive":[]}}""", "Premier rating: unknown/unranked")]
    public void LeetifyRankContextDoesNotInventMissingPrivateOrDuplicateRanks(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        var context = string.Join("\n", WebsiteDataParser.LeetifyRankContext(doc.RootElement));
        Assert.Contains(expected, context);
        Assert.DoesNotContain("12500", context);
        Assert.DoesNotContain("14000", context);
    }

    [Theory]
    [InlineData("""{"response":{"player_level":0}}""", "Steam level: 0")]
    [InlineData("""{"response":{"player_level":-1}}""", "Steam level: unknown")]
    [InlineData("""{"response":{"player_level":2.5}}""", "Steam level: unknown")]
    [InlineData("""{"response":{"player_level":1,"player_level":2}}""", "Steam level: unknown")]
    [InlineData("""{"response":{}}""", "Steam level: unknown")]
    [InlineData("""{"response":{"player_level":1},"response":{"player_level":2}}""", "Steam level: unknown")]
    [InlineData("invalid JSON", "Steam level: unknown")]
    [InlineData("HTTP403", "Steam level: unknown")]
    public async Task SteamLevelFailuresRetainIndependentBanAndGameplayData(string response, string expected)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 0, daysSince: 0));
            if (url.AbsolutePath == "/IPlayerService/GetSteamLevel/v1/")
                return response == "HTTP403" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Text(response);
            return Text(SteamStatsJson(Id));
        }));
        var evidence = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(evidence, e => e.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Official ban record and lifetime statistics retrieved", official.Status);
        Assert.Equal(2, official.Stats!.Kd);
        Assert.Contains("VAC bans 0, game bans 0", official.Note!);
        var context = Assert.Single(official.SmurfReviewContext);
        Assert.Contains(expected, context);
        if (response == "HTTP403") Assert.Contains("HTTP 403", context);
        Assert.DoesNotContain(SteamKey, Analysis.Render(Id, evidence) + Analysis.RenderFinal(Id, evidence));
    }

    [Fact]
    public async Task LeetifyApiKeyAddsOfficialLifetimeProfileWithBanCautionsWithoutExposingTheKey()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            Assert.Equal("api-public.cs-prod.leetify.com", url.Host);
            Assert.Equal($"?steam64_id={Id}", url.Query);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(LeetifyKey, Assert.Single(request.Headers.GetValues("_leetify_key")));
            if (url.AbsolutePath == "/v3/profile/matches")
                return Text($$"""[{"id":"match-a","finished_at":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}","stats":[{"steam64_id":"{{Id}}","total_kills":20,"total_deaths":10}]}]""");
            Assert.Equal("/v3/profile", url.AbsolutePath);
            return Text($$$"""
                {"privacy_mode":"public","winrate":0.6429,"total_matches":2361,"name":"Tester","steam64_id":"{{{Id}}}",
                 "ranks":{"premier":12500,"competitive":[{"map_name":"de_nuke","rank":9}]},
                 "bans":[{"platform":"faceit","platform_nickname":"someone","banned_since":"2022-10-17T13:26:12.018Z"}],
                 "stats":{"preaim":12.0408,"reaction_time_ms":586.7643,"accuracy_head":18.0232,"spray_accuracy":37.0909,"accuracy_enemy_spotted":34.3927}}
                """);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyApiKey: LeetifyKey);
        var official = Assert.Single(result, e => e.Source == "Leetify API (official, keyed)");
        Assert.Equal("Official API lifetime profile data", official.Status);
        Assert.Equal(2361, official.Stats!.Matches);
        Assert.Equal(64.29, official.Stats.WinRate!.Value, 10);
        Assert.Null(official.Stats.Kd);
        Assert.Contains("Premier rating (Leetify-tracked): 12500", Analysis.RenderFinal(Id, result));
        Assert.Contains("Competitive de_nuke rank (Leetify-tracked): 9/18", Analysis.Render(Id, result));
        var caution = Assert.Single(official.Cautions!);
        Assert.Contains("faceit ban since 2022-10-17", caution);
        Assert.Contains("not proof of current CS2 cheating", caution);
        var telemetry = Assert.Single(official.Sections!, section => section.Title.StartsWith("Aim telemetry"));
        Assert.Contains("preaim 12.04 deg", telemetry.Text);
        Assert.Contains("reaction time 586.76 ms", telemetry.Text);
        Assert.Contains("not scored", telemetry.Text);
        Assert.Null(telemetry.Checks);
        var trend = Assert.Single(official.Sections!, section => section.Title.StartsWith("7-day and 15-day trends"));
        Assert.Contains("1 matches | 20 kills / 10 deaths | K/D 2", trend.Text);
        Assert.Contains(trend.Text, Analysis.Render(Id, result));
        Assert.Null(trend.Checks);
        foreach (var item in result)
        {
            Assert.DoesNotContain(LeetifyKey, item.Url.AbsoluteUri);
            Assert.DoesNotContain(LeetifyKey, item.Note ?? "");
        }
        Assert.DoesNotContain(LeetifyKey, Analysis.Render(Id, result) + Analysis.RenderFinal(Id, result));
        Assert.DoesNotContain("Tester", Analysis.Render(Id, result));
    }

    [Fact]
    public void LeetifyDatedHistoryGroupsUtcDatesAndPoolsKillsAndDeathsWithoutScoring()
    {
        using var doc = JsonDocument.Parse($$"""
            [
              {"id":"a","finished_at":"2026-11-16T08:00:00Z","stats":[{"steam64_id":"{{Id}}","total_kills":20,"total_deaths":10}]},
              {"id":"b","finished_at":"2026-11-02T00:00:00Z","stats":[{"steam64_id":"{{Id}}","total_kills":10,"total_deaths":5}]},
              {"id":"c","finished_at":"2026-11-01T23:59:59Z","stats":[{"steam64_id":"{{Id}}","total_kills":9,"total_deaths":0}]},
              {"id":"d","finished_at":"2026-10-17T23:30:00-01:00","stats":[{"steam64_id":"{{Id}}","total_kills":4,"total_deaths":2}]}
            ]
            """);
        var section = WebsiteDataParser.LeetifyDatedHistory(doc.RootElement, Id,
            new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("2026-11-02 to 2026-11-16 UTC: 2 matches | 30 kills / 15 deaths | K/D 2", section.Text);
        Assert.Contains("2026-10-18 to 2026-11-01 UTC: 2 matches | 13 kills / 2 deaths | K/D 6.5", section.Text);
        Assert.Contains("2026-11-10 to 2026-11-16 UTC: 1 matches | 20 kills / 10 deaths | K/D 2", section.Text);
        Assert.Contains("2026-10-27 to 2026-11-02 UTC: 2 matches | 19 kills / 5 deaths | K/D 3.8", section.Text);
        Assert.Contains("views overlap", section.Text);
        Assert.Contains("Context only, not scored", section.Text);
        Assert.Null(section.Stats);
        Assert.Null(section.Checks);
    }

    [Fact]
    public void LeetifyDatedHistoryRejectsDuplicateMatchesAndMismatchedPlayers()
    {
        var match = $$"""{"id":"a","finished_at":"2026-11-01T00:00:00Z","stats":[{"steam64_id":"{{Id}}","total_kills":10,"total_deaths":2}]}""";
        using var duplicates = JsonDocument.Parse($"[{match},{match}]");
        using var wrongPlayer = JsonDocument.Parse($"[{match.Replace(Id, TestPlayerIds.Secondary)}]");
        using var future = JsonDocument.Parse($"[{match.Replace("2026-11-01", "2026-12-01")}]");
        var now = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<FormatException>(() => WebsiteDataParser.LeetifyDatedHistory(duplicates.RootElement, Id, now));
        Assert.Throws<FormatException>(() => WebsiteDataParser.LeetifyDatedHistory(wrongPlayer.RootElement, Id, now));
        Assert.Throws<FormatException>(() => WebsiteDataParser.LeetifyDatedHistory(future.RootElement, Id, now));
    }

    [Fact]
    public void LeetifyDatedHistoryShowsDeathlessPeriodsWithoutInventingARatio()
    {
        using var doc = JsonDocument.Parse($$"""[{"id":"a","finished_at":"2026-11-01T00:00:00Z","stats":[{"steam64_id":"{{Id}}","total_kills":10,"total_deaths":0}]}]""");
        var section = WebsiteDataParser.LeetifyDatedHistory(doc.RootElement, Id,
            new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("K/D undefined (no deaths)", section.Text);
    }

    [Fact]
    public void LeetifyDatedHistoryUsesEachMetricsOwnValidCountersAndShowsComposition()
    {
        using var doc = JsonDocument.Parse($$"""
            [
              {"id":"a","finished_at":"2026-11-15T00:00:00Z","data_source":"matchmaking","map_name":"de_nuke",
               "stats":[{"steam64_id":"{{Id}}","total_kills":20,"total_deaths":10,"rounds_count":20,"total_hs_kills":8,"total_damage":1600}]},
              {"id":"b","finished_at":"2026-11-14T00:00:00Z","data_source":"faceit","map_name":"de_nuke",
               "stats":[{"steam64_id":"{{Id}}","total_kills":10,"total_deaths":5,"rounds_count":10,"total_hs_kills":11}]},
              {"id":"c","finished_at":"2026-11-13T00:00:00Z","data_source":"faceit","map_name":"de_mirage",
               "stats":[{"steam64_id":"{{Id}}","total_kills":4,"total_deaths":5,"rounds_count":0,"total_hs_kills":2,"total_damage":100}]}
            ]
            """);
        var section = WebsiteDataParser.LeetifyDatedHistory(doc.RootElement, Id,
            new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("K/D 1.7", section.Text);
        Assert.Contains("K/R: 1 (2/3 matches", section.Text);
        Assert.Contains("Headshot kills: 41.67% (2/3 matches", section.Text);
        Assert.Contains("ADR-like): 80 (1/3 matches", section.Text);
        Assert.Contains("Data sources: faceit: 2, matchmaking: 1 | Maps: de_mirage: 1, de_nuke: 2", section.Text);
        Assert.Null(section.Checks);
    }

    [Fact]
    public async Task LeetifyProfileNotFoundDoesNotAssertWhyOrRequestHistory()
    {
        var requestedHistory = false;
        using var client = new TrackerClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (path == "/v3/profile/matches") requestedHistory = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyApiKey: LeetifyKey);
        var official = Assert.Single(result, e => e.Source == "Leetify API (official, keyed)");
        Assert.Contains("cause is not confirmed", official.Note!);
        Assert.Contains("Match history was not requested", official.Note!);
        Assert.False(requestedHistory);
    }

    [Fact]
    public async Task LeetifyHistoryFailureKeepsTheVerifiedProfile()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (path == "/v3/profile/matches") return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return Text($$"""{"privacy_mode":"public","winrate":0.5,"total_matches":100,"steam64_id":"{{Id}}"}""");
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyApiKey: LeetifyKey);
        var official = Assert.Single(result, e => e.Source == "Leetify API (official, keyed)");
        Assert.Equal(100, official.Stats!.Matches);
        var unavailable = Assert.Single(official.Sections!);
        Assert.Contains("HTTP 429", unavailable.Text);
        Assert.Null(unavailable.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeetifyHistoryUsesItsLargerBoundedResponseLimit(bool overLimit)
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (path == "/v3/profile/matches")
            {
                if (overLimit)
                {
                    var response = Text("[]");
                    response.Content.Headers.ContentLength = 16_000_001;
                    return response;
                }
                return Text("[" + new string(' ', 2_000_001) + "]");
            }
            return Text($$"""{"privacy_mode":"public","winrate":0.5,"total_matches":100,"steam64_id":"{{Id}}"}""");
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyApiKey: LeetifyKey);
        var official = Assert.Single(result, e => e.Source == "Leetify API (official, keyed)");
        Assert.Equal(100, official.Stats!.Matches);
        var section = Assert.Single(official.Sections!);
        Assert.Contains(overLimit ? "16 MB limit" : "No match history was returned", section.Text);
    }

    [Fact]
    public async Task MismatchedLeetifyApiIdentityIsDiscarded()
    {
        using var client = new TrackerClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/robots.txt"
                ? Text("User-agent: *\nDisallow: /")
                : Text($$"""{"privacy_mode":"public","winrate":0.5,"total_matches":100,"steam64_id":"{{TestPlayerIds.Secondary}}"}""")));
        var result = await client.FetchAsync(Id, CancellationToken.None, leetifyApiKey: LeetifyKey);
        var official = Assert.Single(result, e => e.Source == "Leetify API (official, keyed)");
        Assert.Equal("Unavailable", official.Status);
        Assert.Null(official.Stats);
        Assert.Contains("did not match the requested SteamID", official.Note!);
    }

    [Fact]
    public async Task UnexpectedValveStatsFailureStillKeepsTheAuthoritativeBanRecord()
    {
        using var client = new TrackerClient(new Handler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nDisallow: /");
            if (url.AbsolutePath.StartsWith("/ISteamUser/GetPlayerBans/"))
                return Text(SteamBansJson(Id, vacBans: 0, gameBans: 1, daysSince: 12));
            if (url.AbsolutePath == "/IPlayerService/GetSteamLevel/v1/")
                return Text("""{"response":{"player_level":7}}""");
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        var result = await client.FetchAsync(Id, CancellationToken.None, steamApiKey: SteamKey);
        var official = Assert.Single(result, e => e.Source == "Steam Web API (official, keyed)");
        Assert.Equal("Official ban record retrieved", official.Status);
        Assert.Null(official.Stats);
        Assert.Contains("HTTP 400", official.Note!);
        Assert.Contains("authoritative regardless of privacy", official.Note!);
        Assert.Contains("1 game ban(s) and 0 VAC ban(s)", Assert.Single(official.Cautions!));
        Assert.Contains("Steam level: 7", Assert.Single(official.SmurfReviewContext));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }

}

internal static class TestPlayerIds
{
    internal const ulong PublicIndividualBase = 76561197960265728UL;
    private static readonly ulong FirstAccount = (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue - 3);

    // SteamID-shaped fixture identities, never submitted to live services.
    internal static readonly string Primary = Create(0);
    internal static readonly string Secondary = Create(1);
    internal static readonly string Third = Create(2);

    private static string Create(uint offset) =>
        (PublicIndividualBase + FirstAccount + offset).ToString(CultureInfo.InvariantCulture);
}

internal static class PublicProfileFixtures
{
    public static string CsTrackerOverview(string id) => CsTracker(id) + """
        <nav><a href="#player-weapons-section">Weapons</a><a href="#player-details-section">Details</a></nav>
        <section id="player-profile-section">
          <div data-header-results="all" title="All 299 matches: 133 wins, 145 losses, 21 ties. Win rate leaves ties out.">
            <div><div>win rate</div><div>48%</div></div><div>133 145 21</div><div>299</div>
          </div>
        </section>
        <section id="player-stats-section">
          <header><h2>Stats analysis</h2><span>latest 90 matches</span></header>
          <div id="player-trust-rating-card"><span class="text-5xl">99.2</span><time datetime="2026-09-30T04:48:50Z">46d ago</time></div>
          <div data-histogram-config="{&quot;format&quot;:&quot;ms&quot;,&quot;valueKey&quot;:&quot;ttd&quot;,&quot;lowerIsSuspicious&quot;:true,&quot;suspiciousCutoff&quot;:600}">
            <span title="Better than 24% of tracked players over their latest 90 matches">beats 24%</span>
            <div class="items-baseline"><div class="tabular-nums">757ms</div></div>
            <div data-histogram-chart>CHART-TO-EXCLUDE</div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;deg&quot;,&quot;valueKey&quot;:&quot;xhair&quot;,&quot;lowerIsSuspicious&quot;:true,&quot;suspiciousCutoff&quot;:2.3}">
            <div class="items-baseline"><div class="tabular-nums">3.3&#176;</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;ratio&quot;,&quot;valueKey&quot;:&quot;kd&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:1.8}">
            <div class="items-baseline"><div class="tabular-nums">0.53</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;deg&quot;,&quot;valueKey&quot;:&quot;aim&quot;,&quot;lowerIsSuspicious&quot;:true,&quot;suspiciousCutoff&quot;:11}">
            <div class="items-baseline"><div class="tabular-nums">20.1&#176;</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;ratio&quot;,&quot;valueKey&quot;:&quot;rating&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:1.5}">
            <div class="items-baseline"><div class="tabular-nums">0.63</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;pct&quot;,&quot;valueKey&quot;:&quot;kast&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:85}">
            <div class="items-baseline"><div class="tabular-nums">61.8%</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;dec1&quot;,&quot;valueKey&quot;:&quot;adr&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:110}">
            <div class="items-baseline"><div class="tabular-nums">52.7</div></div>
          </div>
          <div data-histogram-config="{&quot;format&quot;:&quot;pct&quot;,&quot;valueKey&quot;:&quot;acc&quot;,&quot;lowerIsSuspicious&quot;:false,&quot;suspiciousCutoff&quot;:40}">
            <div class="items-baseline"><div class="tabular-nums">23.7%</div></div>
          </div>
        </section>
        <section id="player-details-section">
          <header><span>299 matches</span></header>
          <div class="items-baseline"><span>K/D/A</span><span>2,319 / 4,698 / 1,268</span></div>
          <div class="items-baseline"><span>HS kills</span><span>942 (40.6%)</span></div>
          <div class="items-baseline"><span>Util dmg / match</span><span>74.5</span></div>
        </section>
        <section id="player-weapons-section"><h2>Weapon breakdown</h2><table>
          <thead><tr><th>Weapon</th><th>Kills</th><th>HS%</th><th>Shots</th><th>Accuracy</th><th>Damage</th><th>Hitgroups</th></tr></thead>
          <tbody>
          <tr><td><div>M4A1-S</div></td><td><div class="tabular-nums">632</div></td><td><div class="tabular-nums">35%</div></td><td><div class="tabular-nums">10,578</div></td><td><div class="tabular-nums">26%</div></td><td><div class="tabular-nums">72,543</div></td><td>Chest 43%</td></tr>
          <tr><td><div>AK-47</div></td><td><div class="tabular-nums">521</div></td><td><div class="tabular-nums">51%</div></td><td><div class="tabular-nums">9,955</div></td><td><div class="tabular-nums">20%</div></td><td><div class="tabular-nums">60,510</div></td><td>Chest 42%</td></tr>
          <tr><td><div>MAC-10</div></td><td><div class="tabular-nums">297</div></td><td><div class="tabular-nums">35%</div></td><td><div class="tabular-nums">6,882</div></td><td><div class="tabular-nums">20%</div></td><td><div class="tabular-nums">32,943</div></td><td>Chest 39%</td></tr>
          <tr><td><div>HE Grenade</div></td><td><div class="tabular-nums">28</div></td><td><div class="tabular-nums">0%</div></td><td><div class="tabular-nums">0</div></td><td><div class="tabular-nums">-</div></td><td><div class="tabular-nums">15,733</div></td><td>-</td></tr>
          </tbody></table></section>
        <section id="player-maps-section"><h2>Map performance</h2><div class="grid">
          <div><span class="font-medium">Dust2</span><span>123 matches</span>
            <div><div class="tabular-nums">0.50</div><div>K/D</div></div><div><div class="tabular-nums">50</div><div>ADR</div></div></div>
          <div><span class="font-medium">Mirage</span><span>46 matches</span>
            <div><div class="tabular-nums">0.46</div><div>K/D</div></div><div><div class="tabular-nums">49</div><div>ADR</div></div></div>
          <div><span class="font-medium">Nuke</span><span>25 matches</span>
            <div><div class="tabular-nums">0.53</div><div>K/D</div></div><div><div class="tabular-nums">48</div><div>ADR</div></div></div>
          <div><span class="font-medium">Italy</span><span>2 matches</span>
            <div><div class="tabular-nums">0.77</div><div>K/D</div></div><div><div class="tabular-nums">67</div><div>ADR</div></div></div>
        </div></section>
        """;

    public static string CsStats(string id) => $$"""
        <h1>Test player</h1><a href="https://steamcommunity.com/profiles/{{id}}">Steam</a>
        <div>Tracking Not Enabled</div><div>Season wins: 999</div><p>Loading stats...</p>
        """;

    public static string Scope(string id) => $$"""
        <a href="https://steamcommunity.com/profiles/{{id}}">Steam</a>
        <div class="scope-card"><p class="scope-card__period"><span class="scope-card__role">Rifler</span><span class="tooltip" data-tip="Arrows compare with the 13 matches before.">Last 15 matches</span></p>
        <div class="scope-card__body"><dl class="scope-grid" aria-label="Performance">
        <div class="scope-stat"><dt>K/D</dt><dd><span class="text-white">1.2</span><span class="scope-delta">0.04</span></dd></div>
        <div class="scope-stat"><dt>KPR</dt><dd><span class="text-white">0.7</span><span class="scope-delta">0.03</span></dd></div>
        <div class="scope-stat"><dt><span data-tip="First duels of the round won: 14-48">Opening duels</span></dt><dd><span class="text-white">23%</span></dd></div>
        </dl><table><tr><th>K/D</th><td>9</td></tr></table></div></div>
        """;

    public static string CsTracker(string id) => $$"""
        <a href="https://steamcommunity.com/profiles/{{id}}">Steam</a>
        <section id="player-match-history-section"><table><thead><tr><th>Match</th><th>K / D / A</th></tr></thead>
        <tbody><tr><td><a href="/matches/1">Map</a></td><td>20 / 10 / 4<span>(1f)</span></td></tr>
        <tr><td><a href="/matches/2">Map</a></td><td>10 / 5 / 2</td></tr></tbody></table></section>
        """;

    public static string CsWatch(string id) => $$"""
        <main><h1>Test player</h1><button aria-label="Copy Steam ID {{id}}">{{id}}</button>
        <div class="v-tile"><span>Steam bans</span><b>None</b></div>
        <div class="v-tile"><span>Account age</span><b>7.4 years</b></div>
        <div>Low risk 92/100</div></main>
        """;

    public static string Steam(string id) => $$"""
        <profile><steamID64>{{id}}</steamID64><steamID><![CDATA[Fixture Player]]></steamID><privacyState>public</privacyState>
        <vacBanned>0</vacBanned><isLimitedAccount>0</isLimitedAccount><memberSince>June 8, 2019</memberSince></profile>
        """;
}
