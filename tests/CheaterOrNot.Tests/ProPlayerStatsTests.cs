using System.Net;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class ProPlayerStatsTests
{
    [Fact]
    public async Task RefreshMigratesFaceitAndKeepsSeparateLastGoodRows()
    {
        var directory = Directory.CreateTempSubdirectory("CheaterOrNot-pro-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "pro-stats-v1.json");
            var service = new ProPlayerStats(path);
            var player = ProPlayerStats.Roster.Single(row => row.Name == "m0NESY");
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            await File.WriteAllTextAsync(path, $$$"""
                {"LastAttempt":"{{{old:O}}}","Samples":[{"SteamId":"{{{player.SteamId}}}","FetchedAt":"{{{old:O}}}",
                "Stats":{"Matches":400,"Wins":200,"WinRate":50,"Kd":10615.5,"AverageKd":1.1}}]}
                """);
            var before = await service.LoadAsync(CancellationToken.None);
            Assert.True(before.RefreshDue(DateTimeOffset.UtcNow));
            var csst = new Evidence("csst.at public page", new Uri("https://csst.at"), "OK",
                new PlayerStats(10, Kd: 2.78, Kpr: 1.46), Note: "fixture-private-note");
            var steam = new Evidence("Steam Web API (official, keyed)", new Uri("https://api.steampowered.com"), "OK",
                new PlayerStats(700) { ContextKpr = .75, KprCounts = new(75, 100, 700) });
            var tracker = new Evidence("cstracker.gg public page", new Uri("https://cstracker.gg"), "OK",
                new PlayerStats(50, Kd: 1.2), Sections:
                [new PublicSection("Recent analysis: latest 5 matches", "details",
                    new PlayerStats(5, Kd: 3, Headshots: 35),
                    Checks: [new MetricCheck("HLTV rating", 2.36, 1.5)])]);
            var leetify = new Evidence("Leetify API", new Uri("https://api-public.cs-prod.leetify.com"), "Unavailable",
                Note: "HTTP 404: fixture-private-note");
            var requested = new List<string>();
            var (snapshot, _) = await service.RefreshAsync(before, (id, _) =>
            {
                requested.Add(id);
                return Task.FromResult<IReadOnlyList<Evidence>>(id == player.SteamId ? [csst, steam, tracker, leetify] : []);
            }, null, CancellationToken.None);
            Assert.Equal(ProPlayerStats.Roster.Select(row => row.SteamId), requested);
            var cache = await service.LoadAsync(CancellationToken.None);
            var sample = Assert.Single(cache.Samples);
            Assert.Null(sample.Stats);
            var rows = ProPlayerStats.Rows(sample).ToArray();
            Assert.Contains(rows, row => row.Source == "FACEIT Data API" && row.Name == "Average K/D" && row.FetchedAt == old);
            Assert.DoesNotContain(rows, row => row.Source == "FACEIT Data API" && row.Name == "Kills per round");
            Assert.DoesNotContain(rows, row => row.Source == "FACEIT Data API" && row.Name == "K/D");
            Assert.Contains(new ProSourceResult("Leetify API", "No metrics; Unavailable (HTTP 404)"),
                cache.SourceResults[player.SteamId]);
            Assert.Contains(rows, row => row.Source == csst.Source && row.Name == "Kills per round" &&
                row.Matches == 10 && row.Value == 1.46 && row.Kills is null);
            Assert.Contains(rows, row => row.Source == steam.Source && row.Name == "Kills per round" &&
                row.Scope.EndsWith("(context only)") && row.Matches is null && row.Value == .75 && row.Rounds == 100);
            Assert.Contains(rows, row => row.Source == tracker.Source && row.Name == "HLTV rating" && row.Matches == 5);
            Assert.Contains(rows, row => row.Source == tracker.Source && row.Name == "Headshot kills" && row.Matches == 5);
            Assert.Equal(2, rows.Count(row => row.Source == tracker.Source && row.Name == "K/D"));
            Assert.DoesNotContain(Analysis.WebsiteChecks(tracker, new ScreeningRules()), row => row.Check.Name == "Headshot kills");
            Assert.DoesNotContain("fixture-private-note", await File.ReadAllTextAsync(path));
            Assert.False(cache.RefreshDue(snapshot.LastAttempt.AddDays(1).AddTicks(-1)));
            Assert.True(cache.RefreshDue(snapshot.LastAttempt.AddDays(1)));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void TournamentReferenceSuppliesKprWithoutTheAccountCacheAndKeepsRatingVersionsDistinct()
    {
        Assert.Empty(ProStatsSnapshot.Empty.Samples);
        foreach (var player in ProPlayerStats.Roster)
        {
            var sample = Assert.IsType<HltvProSample>(HltvProReference.ForPlayer(player.SteamId));
            Assert.Single(sample.Metrics(), row => row.Name == "Kills per round" && row.Value > 0);
            Assert.Equal("www.hltv.org", new Uri(sample.SourceUrl).Host);
            Assert.DoesNotContain(sample.Metrics(), row => row.Name == "HLTV rating");
        }
    }

    [Fact]
    public async Task ReferenceQuotaDefersOnlyTheAffectedProviderAcrossPlayers()
    {
        var hosts = new List<string>();
        using var client = new TrackerClient(new Handler(request =>
        {
            var host = request.RequestUri!.Host;
            hosts.Add(host);
            return new HttpResponseMessage(host == "open.faceit.com" ? HttpStatusCode.TooManyRequests :
                HttpStatusCode.NotFound) { Content = new StringContent("") };
        })) { ProReferenceRefresh = true };
        foreach (var player in ProPlayerStats.Roster.Take(2))
            await client.FetchAsync(player.SteamId, CancellationToken.None, faceitApiKey: "fixture-only-key");
        Assert.Single(hosts.Where(host => host == "open.faceit.com"));
        Assert.Equal(4, hosts.Count(host => host == "csst.at"));
        Assert.DoesNotContain("steamcommunity.com", hosts);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
