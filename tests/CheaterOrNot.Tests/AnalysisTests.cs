using CheaterOrNot.Core;
using System.Net;

namespace CheaterOrNot.Tests;

public class AnalysisTests
{
    [Fact]
    public void FinalResultShowsRollingTrendsOutsideLargestSampleDeduplication()
    {
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test",
            new PlayerStats(333, 0.50), Sections:
            [
                new PublicSection("Recent 7-day CSTracker trend", "", new PlayerStats(20, 0.57)),
                new PublicSection("Recent 14-day CSTracker trend", "", new PlayerStats(58, 0.53)),
                new PublicSection("Recent 63-day CSTracker trend", "", new PlayerStats(120, 0.51))
            ]);

        var report = Analysis.RenderFinal(Id, [evidence]);

        Assert.Contains("RECENT ROLLING TRENDS (overlapping, not merged)", report);
        Assert.Contains("Recent 7-day | K/D 0.57 | 20 matches", report);
        Assert.Contains("Recent 14-day | K/D 0.53 | 58 matches", report);
        Assert.Contains("Recent 63-day | K/D 0.51 | 120 matches", report);
    }

    [Fact]
    public void FinalResultExplainsRepeatedKdSamplesInsteadOfDroppingOneSource()
    {
        var evidence = new[]
        {
            new Evidence("Leetify", Sources.LeetifyProfile(Id), "Test", new PlayerStats(30, 0.55)),
            new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test",
                Sections: [new PublicSection("Detailed combat aggregates: 333 matches", "", new PlayerStats(333, 0.50))])
        };

        var report = Analysis.RenderFinal(Id, evidence);

        Assert.Contains("REPEATED METRICS BY SOURCE (separate samples; not merged)", report);
        Assert.Contains("Leetify: K/D 0.55 did not cross", report);
        Assert.Contains("sample: 30 matches; scope: recent overview (all 5v5 sources)", report);
        Assert.Contains("CSTracker: K/D 0.5 did not cross", report);
        Assert.Contains("sample: 333 matches; scope: Detailed combat aggregates", report);
        Assert.Contains("They are not duplicates to discard and are never averaged.", report);
    }

    [Fact]
    public void EquivalentSamplesPreferNewerWebsiteData()
    {
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = old.AddDays(2);
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", TestPlayerIds.Primary),
            "Test", Sections:
            [
                new PublicSection("Equivalent panel", "", new PlayerStats(90, 3))
                    { DataUpdatedAt = old },
                new PublicSection("Equivalent panel", "", new PlayerStats(90, 1.2))
                    { DataUpdatedAt = newer }
            ]);

        var check = Assert.Single(Analysis.WebsiteChecks(evidence, new ScreeningRules()));

        Assert.Equal(1.2, check.Check.Value);
        Assert.Equal(newer, check.DataUpdatedAt);
    }

    [Fact]
    public void FresherDataDoesNotReplaceLargerNonEquivalentSample()
    {
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", TestPlayerIds.Primary),
            "Test", Sections:
            [
                new PublicSection("Recent panel", "", new PlayerStats(90, 3))
                    { DataUpdatedAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero) },
                new PublicSection("Lifetime panel", "", new PlayerStats(300, 1.2))
                    { DataUpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
            ]);

        var check = Assert.Single(Analysis.WebsiteChecks(evidence, new ScreeningRules()));

        Assert.Equal(300, check.Matches);
        Assert.Equal(1.2, check.Check.Value);
    }

    private static readonly string Id = TestPlayerIds.Primary;

    [Fact]
    public void SmurfReviewContextIsVisibleButCannotChangeScreeningOrBatchPriority()
    {
        var plain = new Evidence("Steam public page", Sources.Profile("steamcommunity.com", Id), "Public account context");
        var context = plain with
        {
            RetrievedAt = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero),
            SmurfReviewContext = ["Steam account age: 2 days.", "Steam level: 0.", "Premier rating: unknown."]
        };
        Assert.Equal(Analysis.Summarize([plain]), Analysis.Summarize([context]));
        var final = Analysis.RenderFinal(Id, [context]);
        Assert.Contains("VERDICT: INCONCLUSIVE", final);
        Assert.Contains("0/4 core metric types; 0 distinct metric types assessed", final);
        Assert.Contains("SMURF-REVIEW CONTEXT (not scored)", final);
        Assert.Contains("Steam public page (retrieved 2026-11-16 12:00:00Z)", final);
        Assert.Contains("Steam account age: 2 days", final);
        Assert.Contains("No automatic smurf assessment is made", final);
        Assert.Contains("Steam level: 0", Analysis.Render(Id, [context]));
        Assert.DoesNotContain("ACCOUNT CAUTIONS", final);
        IReadOnlyList<(string Id, IReadOnlyList<Evidence> Evidence)> before =
            [(Id, [plain]), (TestPlayerIds.Secondary, [plain])];
        IReadOnlyList<(string Id, IReadOnlyList<Evidence> Evidence)> after =
            [(Id, [plain]), (TestPlayerIds.Secondary, [context])];
        Assert.Equal(Analysis.RenderBatch(before).Split("FINAL RESULT")[0],
            Analysis.RenderBatch(after).Split("FINAL RESULT")[0]);
    }

    [Fact]
    public void CustomCoreRulesChangeResultsAndReportsWithoutOverridingWebsiteCutoffs()
    {
        var rules = new ScreeningRules { KdThreshold = 1.4, KillsPerRoundThreshold = 1.2, HeadshotMinimumMatches = 20 };
        var evidence = new[] { new Evidence("Scope", Sources.Profile("csst.at", Id), "Test",
            new PlayerStats(15, 1.5, 90, Kpr: 1.1)) };
        var custom = Analysis.RenderFinal(Id, evidence, rules);
        Assert.Contains("K/D of 1.5 crosses", custom);
        Assert.Contains("Not assessed: Headshot kills", custom);
        Assert.Contains("does not cross app rule >= 1.2", custom);
        Assert.Contains("App rules: custom", custom);
        Assert.Contains("\"KdThreshold\": 1.4", Analysis.Render(Id, evidence, rules: rules));
        Assert.Contains("Headshot kills of 90% crosses", Analysis.RenderFinal(Id, evidence));
        var website = evidence[0] with { Sections = [new PublicSection("Website", "", new PlayerStats(15, 1.5),
            Checks: [new MetricCheck("K/D", 1.5, 3, RuleSource: "website cutoff")])] };
        var onlyWebsite = website with { Stats = null };
        Assert.Contains("K/D 1.5 did not cross its warning line (>= 3", Analysis.RenderFinal(Id, [onlyWebsite], rules));
    }

    [Fact]
    public void CustomThresholdsAreReportedPreciselyEvenWhenValuesRoundSimilarly()
    {
        var rules = new ScreeningRules { KillsPerRoundThreshold = 1.2345 };
        var evidence = new[] { new Evidence("Scope", Sources.Profile("csst.at", Id), "Test",
            new PlayerStats(15, Kpr: 1.2346)) };
        Assert.Contains("1.2346 crosses", Analysis.RenderFinal(Id, evidence, rules));
        Assert.Contains(">= 1.2345", Analysis.RenderFinal(Id, evidence, rules));
        Assert.Contains("Kills per round >= 1.2345", Analysis.Render(Id, evidence, rules: rules));
    }

    [Theory]
    [InlineData("{id}")]
    [InlineData("https://cstracker.gg/players/{id}")]
    [InlineData("https://csst.at/profile/{id}/")]
    [InlineData("https://csstats.gg/player/{id}")]
    [InlineData("https://cswatch.gg/player/{id}")]
    [InlineData("https://leetify.com/app/profile/{id}")]
    [InlineData("https://leetify.com/public/profile/{id}/")]
    public void AcceptsSupportedPlayerInput(string input) => Assert.Equal(Id, PlayerId.Parse(input.Replace("{id}", Id)));

    [Theory]
    [InlineData("https://evil.example/players/{id}")]
    [InlineData("https://csst.at/players/{id}")]
    [InlineData("https://csst.at:8443/profile/{id}")]
    [InlineData("http://csst.at/profile/{id}")]
    [InlineData("https://leetify.com/other/profile/{id}")]
    [InlineData("https://leetify.com/app/profile/{id}/extra")]
    [InlineData("https://leetify.com.evil.example/app/profile/{id}")]
    public void RejectsInvalidPlayerInput(string input) =>
        Assert.Throws<FormatException>(() => PlayerId.Parse(input.Replace("{id}", Id)));

    [Fact]
    public void RejectsNonIndividualOrOutOfRangeIds()
    {
        Assert.Throws<FormatException>(() => PlayerId.Parse(TestPlayerIds.PublicIndividualBase.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Throws<FormatException>(() => PlayerId.Parse(new string('9', 17)));
    }

    [Fact]
    public void ParsesLabelsNextLineAndLeavesMissingUnknown()
    {
        var stats = PageParser.Parse("Matches Played\n1,234\nK/D Ratio\n2.01\nHS%: 76%\nWin Rate: 84%");
        Assert.Equal(1234, stats.Matches);
        Assert.Equal(2.01, stats.Kd);
        Assert.Equal(76, stats.Headshots);
        Assert.Null(stats.Kpr);
    }

    [Theory]
    [InlineData("HS%: 101%")]
    [InlineData("Matches: 12.5\nK/D: 2")]
    [InlineData("K/D: 2\nK/D: 1")]
    [InlineData("K/D: 2%")]
    [InlineData("K/D: -2\nHS%: 80%")]
    [InlineData("K/D: garbage\nHS%: 80%")]
    [InlineData("Cloudflare verification required")]
    public void RejectsInvalidAmbiguousOrMissingSummary(string text) =>
        Assert.Throws<FormatException>(() => PageParser.Parse(text));

    [Fact]
    public void HtmlDoesNotUseScriptStats()
    {
        var text = PageParser.VisibleText("<script>K/D: 9</script><div>Matches: 150</div><div>K/D: 1.2</div><div hidden>HS%: 100%</div>");
        var stats = PageParser.Parse(text);
        Assert.Equal(1.2, stats.Kd);
        Assert.Null(stats.Headshots);
    }

    [Fact]
    public void DiscoveryDoesNotAcceptOtherPlayersOrUnsafeHosts()
    {
        var html = $"""<a href="https://cstracker.gg/players/{Id}?x=1">yes</a><a href="https://evil.example/players/{Id}">no</a><a href="https://csstats.gg/player/{TestPlayerIds.Secondary}">no</a>""";
        var links = PageParser.DiscoverLinks(html, Sources.Profile("csst.at", Id), Id);
        Assert.Single(links);
        Assert.Equal(Sources.Profile("cstracker.gg", Id), links[0]);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    public void SmallSamplesAreAssessedTentativelyRatherThanDiscarded(int count)
    {
        var text = Analysis.Render(Id, [new Evidence("Test", Sources.Profile("csst.at", Id),
            "Test", new PlayerStats(count, 2, 75, 80, 1))]);
        Assert.Contains("Indicators: K/D >= 2.00; Overall headshot percentage >= 75%; Win rate >= 80%; Kills per round >= 1.00.", text);
        Assert.Contains("uncalibrated", text);
        Assert.Contains("assessed as provided, including small ones", text);
        Assert.DoesNotContain("Blatant", text);
    }

    [Theory]
    [InlineData(5, "NO STATISTICAL RED FLAGS FOUND")]
    [InlineData(10, "POSSIBLE CHEATING INDICATOR FOUND")]
    public void TinySampleHeadshotPopOffsCannotTripACoreIndicatorAlone(int matches, string expected)
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("Recent", Sources.LeetifyProfile(Id), "Test", new PlayerStats(matches, 0.9, 90))
        ]);
        Assert.Contains(expected, result);
        if (matches < 10) Assert.Contains("Not assessed: Headshot kills", result);
        else Assert.Contains("Headshot kills of 90% crosses", result);
    }

    [Fact]
    public void BatchResultRanksPlayersByFlaggedTypesThenCautionsWithoutProbabilities()
    {
        var flagged = (TestPlayerIds.Primary, (IReadOnlyList<Evidence>)[
            new Evidence("A", Sources.Profile("cstracker.gg", TestPlayerIds.Primary), "Test", new PlayerStats(100, 3))]);
        var cautioned = (TestPlayerIds.Secondary, (IReadOnlyList<Evidence>)[
            new Evidence("A", Sources.Profile("cstracker.gg", TestPlayerIds.Secondary), "Test", new PlayerStats(100, 0.9)),
            new Evidence("B", Sources.Profile("cswatch.gg", TestPlayerIds.Secondary), "Public account context",
                Cautions: ["CSWatch reports Steam bans: 1 on record."])]);
        var clean = (TestPlayerIds.Third, (IReadOnlyList<Evidence>)[
            new Evidence("A", Sources.Profile("cstracker.gg", TestPlayerIds.Third), "Test", new PlayerStats(100, 0.9))]);
        var report = Analysis.RenderBatch([clean, flagged, cautioned]);
        var first = report.IndexOf(TestPlayerIds.Primary, StringComparison.Ordinal);
        var second = report.IndexOf(TestPlayerIds.Secondary, StringComparison.Ordinal);
        var third = report.IndexOf(TestPlayerIds.Third, StringComparison.Ordinal);
        Assert.True(first < second && second < third);
        Assert.Contains("BATCH RESULT | 3 players checked", report);
        Assert.Contains($"1. {TestPlayerIds.Primary} | Player: unavailable | POSSIBLE CHEATING INDICATOR | flagged types: 1", report);
        Assert.Contains("| NO STATISTICAL RED FLAGS | flagged types: 0 | cautions: 1", report);
        Assert.Contains("not cheating probability", report);
        Assert.Contains($"FINAL RESULT | SteamID64 {TestPlayerIds.Third}", report);
        Assert.DoesNotContain("%", report.Split('\n')[0]);
    }

    [Fact]
    public void BatchAndFinalShowOnlyTheNameAssociatedWithEachPlayersEvidence()
    {
        var first = new Evidence("Steam", Sources.Profile("steamcommunity.com", TestPlayerIds.Primary), "Public account context")
            { DisplayName = "Fixture A" };
        var second = new Evidence("Steam", Sources.Profile("steamcommunity.com", TestPlayerIds.Secondary), "Public account context")
            { DisplayName = "Fixture B" };
        var batch = Analysis.RenderBatch([
            (TestPlayerIds.Primary, [first]),
            (TestPlayerIds.Secondary, [second])
        ]);
        Assert.Contains($"{TestPlayerIds.Primary} | Player: \"Fixture A\"", batch);
        Assert.Contains($"{TestPlayerIds.Secondary} | Player: \"Fixture B\"", batch);
        Assert.Contains($"FINAL RESULT | SteamID64 {TestPlayerIds.Primary} | Player: \"Fixture A\"", batch);
        Assert.Contains($"FINAL RESULT | SteamID64 {TestPlayerIds.Secondary} | Player: \"Fixture B\"", batch);
        Assert.Contains("Player: unavailable", Analysis.RenderFinal(TestPlayerIds.Third, []));
    }

    [Fact]
    public void ConflictingDisplayNamesAreNotGuessed()
    {
        var url = Sources.Profile("steamcommunity.com", Id);
        Evidence[] sources = [
            new Evidence("First", url, "Test") { DisplayName = "Fixture A" },
            new Evidence("Second", url, "Test") { DisplayName = "Fixture B" }
        ];
        Assert.Contains("Player: unavailable", Analysis.RenderFinal(Id, sources));
    }

    [Fact]
    public void SteamNameIsPreferredOverAConflictingWebsiteName()
    {
        Evidence[] sources = [
            new Evidence("CSWatch", Sources.Profile("cswatch.gg", Id), "Test") { DisplayName = "Old name" },
            new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test") { DisplayName = "Current name" }
        ];
        Assert.Contains("Player: \"Current name\"", Analysis.RenderFinal(Id, sources));
    }

    [Fact]
    public void CsWatchNameIsClearlyLabeledWhenSteamNameIsUnavailable()
    {
        Evidence[] sources = [
            new Evidence("CSWatch", Sources.Profile("cswatch.gg", Id), "Test") { DisplayName = "Fixture Player" },
            new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Unavailable")
        ];
        var result = Analysis.RenderFinal(Id, sources);
        Assert.Contains("Player: \"Fixture Player\" (CSWatch; may be outdated)", result);
        Assert.Contains("Player: \"Fixture Player\" (CSWatch; may be outdated)",
            Analysis.RenderBatch([(Id, sources)]));
    }

    [Fact]
    public void BatchSummaryMarksAccountOnlyPlayersInconclusive()
    {
        var summary = Analysis.Summarize([
            new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Public account context")]);
        Assert.Equal("INCONCLUSIVE", summary.Verdict);
        Assert.False(summary.HasData);
    }

    [Fact]
    public void OverlappingSmallSamplesAreNotAdded()
    {
        var stats = new PlayerStats(60, 3, 90);
        var report = Analysis.Render(Id, [new Evidence("A", Sources.Profile("csst.at", Id), "Test", stats),
            new Evidence("B", Sources.Profile("cstracker.gg", Id), "Test", stats)]);
        Assert.Contains("Indicators: K/D >= 2.00; Overall headshot percentage >= 75%.", report);
        Assert.Contains("Matches: 60", report);
        Assert.DoesNotContain("Matches: 120", report);
        Assert.Contains("assessed as provided, including small ones", report);
    }

    [Fact]
    public void MissingDataDoesNotProduceCleanVerdict()
    {
        var report = Analysis.Render(Id, [new Evidence("A", Sources.Profile("csst.at", Id), "Blocked")]);
        Assert.Contains("INSUFFICIENT DATA", report);
        Assert.DoesNotContain("NO SELECTED INDICATORS", report);
    }

    [Fact]
    public void EvidenceExplainsMissingMetricsAndDisplaysSeparateSampleValues()
    {
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Public data",
            new PlayerStats(494, 1.53), Sections: [
                new PublicSection("Detailed combat aggregates", "Separate lifetime sample", new PlayerStats(600, 1.4, 47)),
                new PublicSection("Recent analysis unavailable", "Parsing failed: invalid metric.")
            ]);
        var report = Analysis.Render(Id, [evidence]);
        Assert.Contains("Matches: 494 | K/D: 1.53 | HS: not available in this sample", report);
        Assert.Contains("Matches: 600 | K/D: 1.4 | HS: 47%", report);
        Assert.Contains("not zero or a passed check", report);
        Assert.DoesNotContain("no demo, recoil, or longitudinal analysis", report);
        Assert.Equal(1, report.Split("Parsing failed:").Length - 1);
        var final = Analysis.RenderFinal(Id, [evidence]);
        Assert.Contains("Partial retrieval: CSTracker: Recent analysis unavailable", final);
        Assert.Contains("not completeness for every source", final);
    }

    [Theory]
    [InlineData(0.99, false)]
    [InlineData(0.999, false)]
    [InlineData(1.0, true)]
    [InlineData(1.001, true)]
    [InlineData(1.1, true)]
    public void KprThresholdKeepsItsValueAndIdentifiesItsSource(double kpr, bool flagged)
    {
        Evidence[] sources = [
            new Evidence("Scope", Sources.Profile("csst.at", Id), "Public data", new PlayerStats(15, Kd: 1.1, Kpr: kpr))
        ];
        var result = Analysis.RenderFinal(Id, sources);
        Assert.Equal(flagged, Analysis.Summarize(sources).FlaggedTypes > 0);
        if (flagged)
            Assert.Contains("source: Scope; sample: 15 matches", result);
        else
            Assert.Contains($"Kills per round {kpr.ToString("G", System.Globalization.CultureInfo.InvariantCulture)} did not cross", result);
        if (kpr is 0.999 or 1.001)
            Assert.Contains($"K/R: {kpr.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}", Analysis.Render(Id, sources));
        else
            Assert.Contains("K/R: " + kpr.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Analysis.Render(Id, sources));
    }

    [Theory]
    [InlineData(0.7, false)]
    [InlineData(1.1, true)]
    public void UnverifiedKprMatchCountIsNotBorrowedFromOtherMetrics(double kpr, bool flagged)
    {
        var stats = new PlayerStats(Matches: 400, Kd: 1.2, Kpr: kpr) { CombatMatchCountUnverified = true };
        Evidence[] sources = [new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test", stats)];
        var final = Analysis.RenderFinal(Id, sources);
        Assert.Equal(flagged ? 1 : 0, Analysis.Summarize(sources).FlaggedTypes);
        Assert.Contains("sample: match count not verified", final);
        if (!flagged)
            Assert.Contains("K/D 1.2 did not cross its warning line (>= 2; sample: unknown size)", final);
        Assert.Contains("Combat metric match counts are unverified", Analysis.Render(Id, sources));
    }

    [Fact]
    public void UnverifiedCombatMatchCountDoesNotSuppressHeadshotAssessment()
    {
        var stats = new PlayerStats(Matches: 5, Headshots: 80, WinRate: 60)
            { CombatMatchCountUnverified = true };
        var evidence = new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test", stats);
        var final = Analysis.RenderFinal(Id, [evidence]);
        Assert.Contains("Headshot kills of 80% crosses", final);
        Assert.Contains("sample: unknown size", final);
        Assert.Contains("Win rate", final);
    }

    [Fact]
    public void FinalResultShowsSteamKillsAndRoundsForContextOnlyKpr()
    {
        var stats = new PlayerStats(Matches: 15, Kd: 1.5, Headshots: 40, WinRate: 60)
        {
            ContextKpr = 0.5,
            CombatMatchCountUnverified = true,
            KprCounts = new KprCounters(1500, 3000)
        };
        var evidence = new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test", stats);
        var final = Analysis.RenderFinal(Id, [evidence]);
        Assert.Contains("1,500 reported kills / 3,000 reported rounds; match count not verified", final);
        Assert.Contains("context only, not assessed; no K/R warning line applied", final);
        Assert.DoesNotContain("Kills per round 0.5 did not cross its warning line (>= 1; sample: unknown size)", final);
    }

    [Theory]
    [InlineData(0.68, 0)]
    [InlineData(1.1, 1)]
    public void FinalResultSeparatesSteamContextKprWithoutSuppressingScopeScreening(double scopeKpr, int expectedFlags)
    {
        var steam = new PlayerStats(Matches: 400)
        {
            ContextKpr = 1.12,
            CombatMatchCountUnverified = true,
            KprCounts = new KprCounters(1120, 1000)
        };
        Evidence[] sources = [
            new Evidence("Scope", Sources.Profile("csst.at", Id), "Test", new PlayerStats(15, Kpr: scopeKpr)),
            new Evidence("Steam", new Uri("https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/"), "Test", steam)
        ];
        var final = Analysis.RenderFinal(Id, sources);
        Assert.Contains("K/R BY SOURCE (separate samples)", final);
        Assert.Contains($"Scope: {scopeKpr.ToString(System.Globalization.CultureInfo.InvariantCulture)} | 15 matches | Scope website-displayed value", final);
        Assert.Contains("Steam: 1.12 | 1,120 reported kills / 1,000 reported rounds; match count not verified | Steam counter ratio", final);
        Assert.Contains("not directly comparable", final);
        Assert.DoesNotContain("Kills per round of 1.12 crosses", final);
        Assert.Equal(expectedFlags, Analysis.Summarize(sources).FlaggedTypes);
        Assert.Equal(1, Analysis.Summarize(sources).MetricTypes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportRejectsKprThatDisagreesWithItsReportedCounters(bool contextOnly)
    {
        var stats = new PlayerStats(15, Kpr: contextOnly ? null : 0.6)
        {
            ContextKpr = contextOnly ? 0.6 : null,
            CombatMatchCountUnverified = true,
            KprCounts = new KprCounters(1500, 3000)
        };
        Assert.Throws<FormatException>(() => Analysis.RenderFinal(Id,
            [new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test", stats)]));
        Assert.Throws<FormatException>(() => Analysis.Render(Id,
            [new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Test", stats)]));
    }

    [Fact]
    public void PatternWarningsDoNotClaimToDetectToggledAssistance()
    {
        var evidence = new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Public data",
            Sections: [new PublicSection("Historical blocks", "", Checks: [
                new MetricCheck("K/D oscillation blocks", 2, 2)])]);
        var report = Analysis.RenderFinal(Id, [evidence]);
        Assert.Contains("POSSIBLE CHEATING INDICATOR FOUND", report);
        Assert.Contains("Their order is not tested", report);
        Assert.Contains("does not establish toggled assistance", report);
        Assert.DoesNotContain("on/off signature", report);
    }

    [Theory]
    [InlineData(99, 3, 90, "MULTIPLE POSSIBLE CHEATING INDICATORS FOUND")]
    [InlineData(100, 0.53, 30, "NO STATISTICAL RED FLAGS FOUND")]
    [InlineData(100, 2, 30, "POSSIBLE CHEATING INDICATOR FOUND")]
    [InlineData(100, 2, 75, "MULTIPLE POSSIBLE CHEATING INDICATORS FOUND")]
    public void FinalResultExplainsTheScreeningOutcomeWithoutClaimingACheatingVerdict(int matches, double kd, double hs, string expected)
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("Returned matches", Sources.Profile("cstracker.gg", Id), "Public data", new PlayerStats(matches, kd, hs)),
            new Evidence("Missing source", Sources.Profile("csstats.gg", Id), "Unavailable", Note: "HTTP 403")
        ]);
        Assert.Contains(expected, result);
        Assert.Contains("1 of 2 checked sources", result);
        Assert.Contains("Missing source: Unavailable", result);
        Assert.DoesNotContain("HTTP 403", result);
        Assert.Contains("HTTP 403", Analysis.Render(Id, [
            new Evidence("Missing source", Sources.Profile("csstats.gg", Id), "Unavailable", Note: "HTTP 403")
        ]));
        Assert.Contains("cannot confirm either", result);
    }

    [Fact]
    public void FinalResultShowsWhichChecksActuallyRanForTheReportedPlayer()
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("Leetify", Sources.LeetifyProfile(Id), "Public data", new PlayerStats(30, 0.51, 32.91, 46.67)),
            new Evidence("Scope", Sources.Profile("csst.at", Id), "Public data", new PlayerStats(Kd: 0.44, Kpr: 0.35)),
            new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Public data", new PlayerStats(100, 0.53))
        ]);
        Assert.Contains("NO STATISTICAL RED FLAGS FOUND", result);
        Assert.Contains("SCREENING COVERAGE: 4/4", result);
        Assert.Contains("K/D 0.53 did not cross", result);
        Assert.Contains("Headshot kills 32.91%", result);
        Assert.Contains("Win rate 46.67%", result);
        Assert.Contains("Kills per round 0.35", result);
        Assert.Contains("not proof of clean play", result);
        Assert.Contains("treated as tentative, not ignored", result);
    }

    [Fact]
    public void SmallSampleWarningsAreNotHiddenByAnUnflaggedLargerSample()
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("Recent", Sources.LeetifyProfile(Id), "Test", new PlayerStats(30, 3, 90)),
            new Evidence("History", Sources.Profile("cstracker.gg", Id), "Test", new PlayerStats(100, 0.53))
        ]);
        Assert.Contains("MULTIPLE POSSIBLE CHEATING INDICATORS FOUND", result);
        Assert.Contains("K/D of 3 crosses", result);
        Assert.Contains("Headshot kills of 90% crosses", result);
        Assert.DoesNotContain("NO STATISTICAL RED FLAGS", result);
    }

    [Fact]
    public void EmptyAndAccountOnlyResultsCannotProduceANegativeScreeningResult()
    {
        foreach (Evidence[] evidence in new Evidence[][] {
            [],
            [new Evidence("Steam", Sources.Profile("steamcommunity.com", Id), "Public account context")]
        })
        {
            var result = Analysis.RenderFinal(Id, evidence);
            Assert.Contains("INCONCLUSIVE - NO ANALYZABLE PERFORMANCE DATA", result);
            Assert.Contains("SCREENING COVERAGE: 0/4", result);
            Assert.DoesNotContain("NO STATISTICAL RED FLAGS FOUND", result);
        }
    }

    [Fact]
    public void FinalResultDoesNotAddOverlappingSamplesOrTreatAccountContextAsGameplay()
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("A", Sources.Profile("cstracker.gg", Id), "Test", new PlayerStats(60, 3, 90)),
            new Evidence("B", Sources.Profile("csst.at", Id), "Test", new PlayerStats(60, 3, 90)),
            new Evidence("Account", Sources.Profile("steamcommunity.com", Id), "Public account context", Note: "Private account")
        ]);
        Assert.Contains("MULTIPLE POSSIBLE CHEATING INDICATORS FOUND", result);
        Assert.Contains("2 of 3 checked sources", result);
        Assert.Contains("sample: 60 matches", result);
        Assert.DoesNotContain("sample: 120 matches", result);
    }

    [Theory]
    [InlineData(450, true)]
    [InlineData(600, false)]
    [InlineData(757, false)]
    public void WebsiteReactionCutoffsActuallyAffectTheAssessmentForNinetyMatches(double ttd, bool expectedFlag)
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Public data", new PlayerStats(100, 0.53),
                Sections: [new PublicSection("Recent analysis", "", new PlayerStats(Matches: 90),
                    Checks: [new MetricCheck("TTD", ttd, 600, "ms", MetricComparison.Below, "website cutoff")])])
        ]);
        Assert.Contains(expectedFlag ? "VERDICT: POSSIBLE CHEATING INDICATOR FOUND" : "VERDICT: NO STATISTICAL RED FLAGS FOUND", result);
        Assert.Contains("sample: 90 matches", result);
        Assert.Contains("< 600ms", result);
        Assert.DoesNotContain("INCONCLUSIVE", result);
    }

    [Fact]
    public void RepeatedFlagsForTheSameMetricAreNotPresentedAsIndependentIndicators()
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("A", Sources.Profile("cstracker.gg", Id), "Test", new PlayerStats(30, 3)),
            new Evidence("B", Sources.Profile("csst.at", Id), "Test", new PlayerStats(Kd: 3))
        ]);
        Assert.Contains("1 screening indicator type(s)", result);
        Assert.Contains("POSSIBLE CHEATING INDICATOR FOUND", result);
        Assert.DoesNotContain("MULTIPLE POSSIBLE", result);
    }

    [Fact]
    public void FlaggedMetricsShowProfessionalScaleContextWithoutAffectingTheVerdict()
    {
        var result = Analysis.RenderFinal(Id, [
            new Evidence("Scope", Sources.Profile("csst.at", Id), "Test", new PlayerStats(Matches: 120, Kpr: 1.1))
        ]);
        Assert.Contains("no same-source, same-panel example with known sample sizes available", result);
        Assert.DoesNotContain("Pro scale", result);
        Assert.Contains("POSSIBLE CHEATING INDICATOR FOUND", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteamContextKprNeverFlagsOrAddsScreeningCoverage(bool otherMetrics)
    {
        var stats = otherMetrics ? new PlayerStats(1000, Kd: 1.05, Headshots: 31, WinRate: 49) : new PlayerStats();
        stats = stats with
        {
            ContextKpr = 1.25,
            CombatMatchCountUnverified = true,
            KprCounts = new KprCounters(1000, 800)
        };
        var steam = new Evidence("Steam Web API (official, keyed)", Sources.Profile("steamcommunity.com", Id), "Test", stats);
        var rules = new ScreeningRules { KillsPerRoundThreshold = 0.01 };
        var result = Analysis.RenderFinal(Id, [steam], rules);
        Assert.Contains("1,000 reported kills / 800 reported rounds; match count not verified", result);
        Assert.Contains("context only, not assessed; no K/R warning line applied", result);
        Assert.DoesNotContain("hot streak", result);
        Assert.DoesNotContain("Pro scale", result);
        Assert.DoesNotContain("PRO-SCALE SOURCE", result);
        Assert.DoesNotContain("Kills per round of", result);
        Assert.Equal(0, Analysis.Summarize([steam], rules).FlaggedTypes);
        Assert.Equal(otherMetrics ? 3 : 0, Analysis.Summarize([steam], rules).MetricTypes);
        Assert.Contains(otherMetrics ? "3/4 core metric types" : "0/4 core metric types", result);
        Assert.Contains(otherMetrics ? "VERDICT: NO STATISTICAL RED FLAGS FOUND" : "VERDICT: INCONCLUSIVE", result);
        var evidence = Analysis.Render(Id, [steam], rules: rules);
        Assert.Contains("K/R: 1.25 (lifetime counter ratio; context only, not assessed)", evidence);
        Assert.DoesNotContain("Kills per round >=", evidence);
    }

    [Fact]
    public void LiveReferenceDiagnosticsStayInEvidenceAndNeverReplaceTheNumericSnapshotSource()
    {
        var evidence = new Evidence("Scope", Sources.Profile("csst.at", Id), "Test", new PlayerStats(Matches: 120, Kpr: 1.1));
        var note = "live HLTV top-rated player list (retrieved 2026-12-01 00:00:00Z); top-listed professional: example";
        var result = Analysis.RenderFinal(Id, [evidence]);
        Assert.DoesNotContain("PRO-SCALE SOURCE", result);
        Assert.DoesNotContain(note, result);
        Assert.Contains(note, Analysis.Render(Id, [evidence], note));
        Assert.DoesNotContain(note, Analysis.RenderBatch([(Id, [evidence])]));
        var failure = "HLTV live reference list unavailable: HTTP 403: Forbidden. Reference bands were not refreshed.";
        Assert.Contains(failure, Analysis.Render(Id, [evidence], failure));
        Assert.DoesNotContain("HTTP 403", result);
        Assert.DoesNotContain("bot verification", result);
    }

    [Fact]
    public void ProScaleContextOnlyAppearsForFlaggedMetricsWithAKnownProfessionalBand()
    {
        var clean = Analysis.RenderFinal(Id, [
            new Evidence("Scope", Sources.Profile("csst.at", Id), "Test", new PlayerStats(Matches: 120, Kpr: 0.7))
        ]);
        Assert.DoesNotContain("Pro scale", clean);
        Assert.DoesNotContain("PRO-SCALE SOURCE", clean);
        var ttdOnly = Analysis.RenderFinal(Id, [
            new Evidence("CSTracker", Sources.Profile("cstracker.gg", Id), "Test", new PlayerStats(100, 0.53),
                Sections: [new PublicSection("Recent analysis", "", new PlayerStats(Matches: 90),
                    Checks: [new MetricCheck("TTD", 300, 600, "ms", MetricComparison.Below, "website cutoff")])])
        ]);
        Assert.Contains("POSSIBLE CHEATING INDICATOR FOUND", ttdOnly);
        Assert.DoesNotContain("PRO-SCALE SOURCE", ttdOnly);
        Assert.DoesNotContain("Pro scale", ttdOnly);
    }

    [Fact]
    public void ProExamplesPreserveVerdictAndRequireMatchingProviderPanelMetricAndKnownSample()
    {
        const string source = "csst.at public page";
        const string scope = "website Scope panel; K/R as displayed";
        var pro = ProPlayerStats.Roster[0];
        var date = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        Evidence[] evidence = [new(source, Sources.Profile("csst.at", Id), "Test", new PlayerStats(15, Kpr: 1.1))];
        var matching = new ProMetric(source, scope, "Kills per round", 1.39, "", 15, date);
        var snapshot = new ProStatsSnapshot(date, [
            new(pro.SteamId, date) { Metrics = [
                matching, matching with { Scope = "Other panel", Value = 6.31 },
                matching with { Source = "HLTV", Value = 6.32 }] },
            new(ProPlayerStats.Roster[1].SteamId, date) { Metrics = [matching with { Matches = null, Value = 6.33 }] },
            new(ProPlayerStats.Roster[2].SteamId, date) { Metrics = [matching with { Unit = "%", Value = 6.34 }] }
        ]);
        var result = Analysis.RenderFinal(Id, evidence, proStats: snapshot);
        Assert.Contains("ZywOo: 1.39 | 15 matches | fetched 2025-01-02 00:00:00Z; over 24 hours old", result);
        Assert.DoesNotContain("6.3", result);
        Assert.Contains(">= 1, app rule", result);
        var batch = Analysis.RenderBatch([(Id, evidence)], proStats: snapshot);
        Assert.Contains("ZywOo: 1.39", batch);
        Assert.Equal(Analysis.RenderBatch([(Id, evidence)]).Split("Full per-player results")[0],
            batch.Split("Full per-player results")[0]);
        Assert.DoesNotContain("ZywOo: 1.39", Analysis.RenderFinal(pro.SteamId, evidence, proStats: snapshot));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidWebsiteMeasurementsCannotTurnIntoUnflaggedResults(double value) =>
        Assert.Throws<FormatException>(() => Analysis.RenderFinal(Id, [
            new Evidence("Website", Sources.Profile("cstracker.gg", Id), "Test",
                Sections: [new PublicSection("Recent", "", new PlayerStats(Matches: 90),
                    Checks: [new MetricCheck("TTD", value, 600, "ms", MetricComparison.Below)])])
        ]));

    [Fact]
    public void ZeroMatchPlaceholdersAreRejectedRatherThanTentativelyAssessed() =>
        Assert.Throws<FormatException>(() => Analysis.RenderFinal(Id, [
            new Evidence("Placeholder", Sources.Profile("cstracker.gg", Id), "Test", new PlayerStats(0, 3))
        ]));
    [Theory]
    [InlineData("User-agent: *\nDisallow: /", false)]
    [InlineData("User-agent: *\nDisallow: /profile/*", false)]
    [InlineData("User-agent: *\nDisallow: /\nAllow: /profile/", true)]
    [InlineData("User-agent: *\nDisallow: /\nUser-agent: CheaterOrNot\nAllow: /", true)]
    [InlineData("User-agent: *\nCrawl-delay: 5", true)]
    [InlineData("User-agent: *\nCrawl-delay: 31", false)]
    [InlineData("User-agent: *\nCrawl-delay: invalid", false)]
    [InlineData("User-agent: *\nDisallow:", true)]
    public void HonorsRobotsRules(string rules, bool expected) =>
        Assert.Equal(expected, RobotsPolicy.Allows(rules, "/profile/" + Id));

    [Fact]
    public async Task RobotsBlockPreventsPageRequests()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("/robots.txt", request.RequestUri!.AbsolutePath);
            return Text("User-agent: *\nDisallow: /");
        });
        using var client = new TrackerClient(handler);
        var result = await client.FetchAsync(Id, CancellationToken.None);
        var supported = Sources.PublicPages(Id).Count;
        Assert.Equal(supported, handler.Requests);
        Assert.All(result.Take(supported), e => Assert.Equal("Not fetched", e.Status));
        Assert.All(result.Skip(supported), e => Assert.Equal("Unsupported automatic source", e.Status));
        Assert.Equal(Sources.UnsupportedSources.Length, result.Count - supported);
        var final = Analysis.RenderFinal(Id, result);
        Assert.Contains("Not checked: TrackBans", final);
        Assert.Contains("reported maintenance", final);
        Assert.DoesNotContain("13 catalogued sites", final);
    }

    [Theory]
    [InlineData("<dl><dt>Matches</dt><dd>150</dd><dt>K/D</dt><dd>1.2</dd></dl>")]
    [InlineData("<div><span>Matches</span><strong>150</strong></div><div><span>K/D</span><strong>1.2</strong></div>")]
    [InlineData("<div><strong>150</strong><span>Matches</span></div><div><strong>1.2</strong><span>K/D</span></div>")]
    [InlineData("<table><tr><th>Matches</th><th>K/D</th></tr><tr><td>150</td><td>1.2</td></tr></table>")]
    public void AutomaticHtmlParsingSupportsCommonAggregateLayouts(string html) =>
        Assert.Equal(new PlayerStats(150, 1.2), PageParser.Parse(PageParser.VisibleText(html)));

    [Fact]
    public async Task AutomaticHtmlCheckFetchesOnlyVerifiedProfilesAndDoesNotRetainRawPages()
    {
        var urls = new List<Uri>();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!;
            urls.Add(url);
            Assert.Null(request.Headers.Authorization);
            Assert.True(request.Headers.CacheControl!.NoStore);
            if (url.AbsolutePath == "/robots.txt") return Text("User-agent: *\nAllow: /");
            Assert.True(Sources.PublicPages(Id).Any(s => s.Url == url) ||
                url.Host == "steamcommunity.com" && url.Query == "?xml=1");
            if (url.Host == "cstracker.gg") return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (url.Host == "csst.at") return Text(PublicProfileFixtures.Scope(Id));
            if (url.Host == "cswatch.gg") return Text(PublicProfileFixtures.CsWatch(Id));
            if (url.Host == "steamcommunity.com") return Text(PublicProfileFixtures.Steam(Id));
            return Text("<html><script>RAW-HTML-SHOULD-NOT-BE-RETAINED</script><dl><dt>Matches</dt><dd>150</dd><dt>K/D</dt><dd>1.2</dd></dl></html>");
        });
        using var client = new TrackerClient(handler);
        var result = await client.FetchAsync(Id, CancellationToken.None);
        // Every source does robots + one page, except Leetify which stops at attribution setup (robots only).
        Assert.Equal(Sources.PublicPages(Id).Count * 2 - 1, urls.Count);
        Assert.Equal(1, result.Count(e => e.Stats is not null));
        Assert.Equal(2, result.Count(e => e.Status == "Public account context"));
        Assert.Contains(result, e => e.Source == "cstracker.gg public page" && e.Note!.Contains("403"));
        Assert.Equal(Sources.UnsupportedSources.Length, result.Count(e => e.Status == "Unsupported automatic source"));
        var report = Analysis.Render(Id, result);
        Assert.DoesNotContain("RAW-HTML-SHOULD-NOT-BE-RETAINED", report);
        Assert.DoesNotContain("Low risk 92/100", report);
    }

    [Fact]
    public async Task AutomaticHtmlCheckDoesNotReusePagesOnNextCheck()
    {
        var block = false;
        var pages = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt")
                return Text(block ? "User-agent: *\nDisallow: /" : "User-agent: *\nAllow: /");
            pages++;
            return Text(request.RequestUri.Host switch
            {
                "csst.at" => PublicProfileFixtures.Scope(Id),
                "cstracker.gg" => PublicProfileFixtures.CsTracker(Id),
                "csstats.gg" when !request.RequestUri.AbsolutePath.EndsWith("/stats") => PublicProfileFixtures.CsStats(Id),
                "cswatch.gg" => PublicProfileFixtures.CsWatch(Id),
                "steamcommunity.com" => PublicProfileFixtures.Steam(Id),
                _ => "<div>Matches: 150</div><div>K/D: 1.2</div>"
            });
        });
        using var client = new TrackerClient(handler);
        var first = await client.FetchAsync(Id, CancellationToken.None);
        Assert.Equal(Sources.PublicPages(Id).Count - 3, first.Count(e => e.Stats is not null));
        block = true;
        var second = await client.FetchAsync(Id, CancellationToken.None);
        Assert.All(second, e => Assert.Null(e.Stats));
        Assert.Equal(Sources.PublicPages(Id).Count - 1, pages);
    }

    [Fact]
    public async Task RedirectsAndOversizedBodiesAreExplicitFailures()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? Text("User-agent: *\nAllow: /")
            : request.RequestUri.Host == "cstracker.gg"
                ? new HttpResponseMessage(HttpStatusCode.Found)
                : Text(new string('x', 2_000_001)));
        using var client = new TrackerClient(handler);
        var result = await client.FetchAsync(Id, CancellationToken.None);
        Assert.Contains(result, e => e.Source == "cstracker.gg public page" && e.Note!.Contains("Redirect declined"));
        Assert.Contains(result, e => e.Source == "csst.at public page" && e.Note!.Contains("2 MB limit"));
        Assert.All(result, e => Assert.Null(e.Stats));
    }

    [Fact]
    public async Task RequestsAreCancelable()
    {
        using var client = new TrackerClient(new FakeHandler(_ => throw new InvalidOperationException("Should not request.")));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(Id, cancel.Token));
    }

    [Fact]
    public void CommunitySourcesUseOnlyVerifiedUrlsAndDoNotGuessProfileRoutes()
    {
        Assert.Equal(new Uri($"https://cswatch.gg/player/{Id}"), Sources.ReviewUrl("cswatch.gg", Id));
        Assert.Equal(new Uri("https://trackbans.com/"), Sources.ReviewUrl("trackbans.com", Id));
        Assert.Throws<FormatException>(() => Sources.ReviewUrl("evil.example", Id));
        Assert.Throws<FormatException>(() => PlayerId.Parse($"https://csrep.gg/player/{Id}"));
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(response(request));
        }
    }
}
