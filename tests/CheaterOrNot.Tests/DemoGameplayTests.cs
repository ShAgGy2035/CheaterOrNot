using System.Globalization;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class DemoGameplayTests
{
    private static readonly ulong Id = ulong.Parse(TestPlayerIds.Primary, CultureInfo.InvariantCulture);
    private static readonly DemoPlayer[] Roster = [new(TestPlayerIds.Primary, "Local name")];
    private static DemoAimSample Sample(int tick, double yaw = 0) =>
        new(Id, tick, 1, 10, 1, true, 0, yaw, 0, 0, 0);

    [Theory]
    [InlineData(19.99, 0)]
    [InlineData(20, 1)]
    [InlineData(30, 1)]
    public void SelectionUsesActualAngularThresholdAndCountsAssessedShots(double angle, int selected)
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100));
        analyzer.Shot(Sample(101, angle), "weapon_ak47");
        var report = analyzer.Build(Roster);
        Assert.Equal(1, report.Players[0].AssessedShots);
        Assert.Equal(selected, report.TotalFindings);
        if (selected > 0) Assert.Equal(angle, Assert.Single(report.Findings).TurnDegrees, 8);
    }

    [Fact]
    public void WrappedYawAndNearVerticalViewsDoNotInventLargeTurns()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100, 179));
        analyzer.Shot(Sample(101, -179), "ak47");
        analyzer.Observe(Sample(102) with { Pitch = 89 });
        analyzer.Shot(Sample(103, 180) with { Pitch = 89 }, "ak47");
        Assert.Equal(2, analyzer.Build(Roster).Players[0].AssessedShots);
        Assert.Empty(analyzer.Build(Roster).Findings);
    }

    [Fact]
    public void SameTickEntityUpdatesAndDuplicateFireEventsDoNotDistortTheComparison()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100));
        analyzer.Observe(Sample(101, 30));
        analyzer.Shot(Sample(101, 30), "ak47");
        analyzer.Shot(Sample(101, 30), "weapon_ak47");
        var report = analyzer.Build(Roster);
        Assert.Equal(1, report.Players[0].Shots);
        Assert.Equal(1, report.Players[0].AssessedShots);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void GapsLifeChangesRoundChangesTeleportsAndInvalidSnapshotsAreExcludedNotPassed()
    {
        var excluded = new[]
        {
            Sample(102, 30),
            Sample(101, 30) with { Life = 2 },
            Sample(101, 30) with { Pawn = 20 },
            Sample(101, 30) with { Round = 2 },
            Sample(101, 30) with { X = 64.01 },
            Sample(101, 30) with { Eligible = false },
            Sample(101, 30) with { Pitch = double.NaN },
            Sample(101, 30) with { Z = double.PositiveInfinity }
        };
        foreach (var sample in excluded)
        {
            var analyzer = new DemoGameplayAnalyzer();
            analyzer.Observe(Sample(100));
            analyzer.Shot(sample, "ak47");
            var report = analyzer.Build(Roster);
            Assert.Equal(1, report.Players[0].Shots);
            Assert.Equal(0, report.Players[0].AssessedShots);
            Assert.Empty(report.Findings);
        }
    }

    [Fact]
    public void KnifeGrenadeUnknownWeaponAndMissingPreviousSnapshotDoNotGenerateFindings()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Shot(Sample(100, 30), "ak47");
        analyzer.Observe(Sample(100));
        foreach (var weapon in new[] { "knife", "hegrenade", "INJECTED NAME", TestPlayerIds.Primary })
            analyzer.Shot(Sample(101, 30), weapon);
        var report = analyzer.Build(Roster);
        Assert.Equal(1, report.Players[0].Shots);
        Assert.Equal(0, report.Players[0].AssessedShots);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void RepetitionRequiresSeveralShotsAcrossSeveralRounds()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var index = 0; index < 3; index++)
        {
            analyzer.Observe(Sample(100 + index * 2));
            analyzer.Shot(Sample(101 + index * 2, 30), "ak47");
        }
        Assert.False(analyzer.Build(Roster).Players[0].Repeated);
        analyzer.Observe(Sample(200) with { Round = 2 });
        analyzer.Shot(Sample(201, 30) with { Round = 2 }, "ak47");
        var report = analyzer.Build(Roster);
        Assert.True(report.Players[0].Repeated);
        Assert.Equal(4, report.Players[0].LargeTurns);
        Assert.Equal(2, report.Players[0].RoundsWithLargeTurns);
        Assert.Contains("not an accusation", report.Render());
        Assert.Contains("not raw mouse input", report.Render());
    }

    [Fact]
    public void MultiTickWindowsCaptureNinetyDegreeTurnWithoutInventingASingleTickBookmark()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var tick = 100; tick < 108; tick++)
            analyzer.Observe(Sample(tick, (tick - 100) * 11.25));
        analyzer.Shot(Sample(108, 90), "ak47");
        for (var tick = 109; tick <= 116; tick++) analyzer.Observe(Sample(tick, 90));
        var report = analyzer.Build(Roster);
        Assert.Empty(report.Findings);
        var window = Assert.Single(report.ShotWindows);
        Assert.Equal(8, window.BeforeTicks);
        Assert.Equal(8, window.AfterTicks);
        Assert.Equal(90, window.BeforeNetTurnDegrees!.Value, 8);
        Assert.Equal(90, window.BeforePathTurnDegrees!.Value, 8);
        Assert.Equal(0, window.AfterNetTurnDegrees!.Value, 8);
        Assert.Equal(17, window.Samples.Count);
        Assert.Equal(-8, window.Samples[0].OffsetTicks);
        Assert.Equal(-90, window.Samples[0].YawRelativeToShotDegrees);
        Assert.Equal(8, window.Samples[^1].OffsetTicks);
    }

    [Fact]
    public void WindowPathDoesNotConfuseAnOutAndBackTurnWithZeroMotion()
    {
        var analyzer = new DemoGameplayAnalyzer();
        var yaw = new[] { 0, 30, 60, 90, 60, 30, 0, 0 };
        for (var index = 0; index < yaw.Length; index++) analyzer.Observe(Sample(100 + index, yaw[index]));
        analyzer.Shot(Sample(108), "ak47");
        var window = Assert.Single(analyzer.Build(Roster).ShotWindows);
        Assert.Equal(0, window.BeforeNetTurnDegrees!.Value, 8);
        Assert.Equal(180, window.BeforePathTurnDegrees!.Value, 8);
    }

    [Fact]
    public void WindowSidesStopAtGapsAndInvalidTransitionsInsteadOfBridgingMissingData()
    {
        var invalidNextSamples = new[]
        {
            Sample(103, 30), Sample(102, 30) with { Life = 2 },
            Sample(102, 30) with { Pawn = 20 }, Sample(102, 30) with { Round = 2 },
            Sample(102, 30) with { Eligible = false }, Sample(102, 30) with { X = 64.01 },
            Sample(102, 30) with { Pitch = double.NaN }
        };
        foreach (var next in invalidNextSamples)
        {
            var analyzer = new DemoGameplayAnalyzer();
            analyzer.Observe(Sample(99));
            analyzer.Shot(Sample(101, 10), "ak47");
            analyzer.Observe(next);
            analyzer.Observe(Sample(104, 30));
            var window = Assert.Single(analyzer.Build(Roster).ShotWindows);
            Assert.Equal(0, window.BeforeTicks);
            Assert.Equal(0, window.AfterTicks);
            Assert.Null(window.BeforeNetTurnDegrees);
            Assert.Null(window.AfterNetTurnDegrees);
            Assert.Single(window.Samples);
        }
    }

    [Fact]
    public void WindowUsesShotSnapshotAndLatestSameTickPostSnapshotWithWrappedYaw()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100, 179));
        analyzer.Shot(Sample(101, -179), "ak47");
        analyzer.Observe(Sample(101, 20));
        analyzer.Observe(Sample(102, 40));
        analyzer.Observe(Sample(102, -178));
        var window = Assert.Single(analyzer.Build(Roster).ShotWindows);
        Assert.Equal(2, window.BeforeNetTurnDegrees!.Value, 8);
        Assert.Equal(1, window.AfterNetTurnDegrees!.Value, 8);
        Assert.Equal(-2, window.Samples[0].YawRelativeToShotDegrees);
        Assert.Equal(1, window.Samples[^1].YawRelativeToShotDegrees);
    }

    [Fact]
    public void DamageContextKeepsSeparateSameTickCallbacksAndDisclosesRetention()
    {
        var victim = ulong.Parse(TestPlayerIds.Secondary, CultureInfo.InvariantCulture);
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Damage(Id, victim, 1, 100, "knife", 20, true);
        analyzer.Damage(Id, victim, 1, 100, "ak47", 0, true);
        analyzer.Damage(Id, victim, 1, 100, "ak47", 20, false);
        for (var index = 0; index < 2005; index++)
            analyzer.Damage(Id, victim, 1, 100, "weapon_ak47", 1, true);
        var report = analyzer.Build([.. Roster, new(TestPlayerIds.Secondary, "Local victim")]);
        Assert.Equal(2005, report.TotalDamageEvents);
        Assert.Equal(2000, report.DamageEvents.Count);
        Assert.All(report.DamageEvents, e => Assert.Equal("ak47", e.Weapon));
        Assert.Equal(0, report.TotalCombatEvents);
        Assert.All(report.RoundTelemetry, r => Assert.Equal(0, r.FirearmKills));
    }

    [Fact]
    public void RoundTelemetryIncludesSmallTurnsAndDistinguishesUnassessedShotsFromZeroTurn()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100));
        analyzer.Shot(Sample(101, 5), "ak47");
        analyzer.Shot(Sample(101, 5), "weapon_ak47");
        analyzer.Observe(Sample(102));
        analyzer.Shot(Sample(103, 15), "ak47");
        analyzer.Shot(Sample(201, 30) with { Round = 2 }, "ak47");
        var report = analyzer.Build(Roster);
        Assert.Empty(report.Findings);
        var first = report.RoundTelemetry[0];
        Assert.Equal(2, first.EligibleShots);
        Assert.Equal(2, first.AssessedShots);
        Assert.Equal(10, first.MeanTurnDegrees!.Value, 8);
        Assert.Equal(15, first.MaximumTurnDegrees!.Value, 8);
        Assert.Equal(103, first.MaximumTurnTick);
        var second = report.RoundTelemetry[1];
        Assert.Equal(1, second.EligibleShots);
        Assert.Equal(0, second.AssessedShots);
        Assert.Null(second.MeanTurnDegrees);
        Assert.Null(second.MaximumTurnDegrees);
        Assert.Null(second.MaximumTurnTick);
    }

    [Fact]
    public void CombatContextKeepsAllHumanKillsAndSeparatesFirearmTotalsAfterRetentionLimit()
    {
        var victim = ulong.Parse(TestPlayerIds.Secondary, CultureInfo.InvariantCulture);
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Kill(Id, victim, 1, 90, "knife", true, true);
        analyzer.Kill(Id, victim, 1, 100, "ak47", true, false);
        analyzer.Kill(Id, Id, 1, 100, "ak47", true, true);
        analyzer.Kill(Id, victim, 0, 100, "ak47", true, true);
        analyzer.Kill(Id, victim, 1, 100, "weapon_ak47", true, true);
        analyzer.Kill(Id, victim, 1, 100, "ak47", true, true);
        for (var index = 0; index < 2003; index++)
            analyzer.Kill(Id, victim, 2, 200 + index, "ak47", false, true);
        var report = analyzer.Build([.. Roster, new(TestPlayerIds.Secondary, "Other local name")]);
        Assert.Equal(2005, report.TotalCombatEvents);
        Assert.Equal(2000, report.CombatEvents.Count);
        Assert.Equal("knife", report.CombatEvents[0].Weapon);
        Assert.True(report.CombatEvents[0].Headshot);
        var killerRound = Assert.Single(report.RoundTelemetry.Where(r => r.SteamId == TestPlayerIds.Primary && r.Round == 1));
        Assert.Equal(1, killerRound.FirearmKills);
        Assert.Equal(1, killerRound.HeadshotKills);
        Assert.Equal(2, killerRound.TotalKills);
        Assert.Equal(2, killerRound.TotalHeadshotKills);
        Assert.Null(killerRound.MeanTurnDegrees);
        Assert.Equal(2004, report.RoundTelemetry.Sum(r => r.FirearmKills));
        Assert.Equal(2005, report.RoundTelemetry.Sum(r => r.TotalKills));
        Assert.Equal(2005, report.RoundTelemetry.Sum(r => r.TotalDeaths));
        Assert.Equal(2004, report.RoundTelemetry.Sum(r => r.FirearmDeaths));
    }

    [Fact]
    public void EngagementsKeepHalfSecondContextAndGroupNearbyShotsWithMovement()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var tick = 0; tick <= 80; tick++)
        {
            var sample = Sample(tick, tick * 0.1) with { X = tick * 0.5 };
            analyzer.Observe(sample);
            if (tick is 32 or 48) analyzer.Shot(sample, "ak47");
        }
        var report = analyzer.Build(Roster);
        var engagement = Assert.Single(report.Engagements);
        Assert.Equal(2, engagement.Shots);
        Assert.Equal(32, engagement.FirstShotTick);
        Assert.Equal(48, engagement.LastShotTick);
        Assert.Equal(32, engagement.BeforeTicks);
        Assert.Equal(32, engagement.AfterTicks);
        Assert.Equal(81, engagement.Points.Count);
        Assert.Equal(8, engagement.PathTurnDegrees!.Value, 6);
        Assert.Equal(40, engagement.MovementPathUnits);
        Assert.Equal(-16, engagement.Points[0].RelativeX);
        var coverage = Assert.Single(report.EngagementCoverage);
        Assert.Equal(81, coverage.ObservedTicks);
        Assert.Equal(2, coverage.EligibleShots);
        Assert.Equal(1, coverage.Engagements);
        Assert.Equal(2, coverage.IncompleteGeometryShots);
        Assert.Equal(2, report.ShotWindows.Count);
    }

    [Fact]
    public void EngagementRetentionSpansLaterRoundsAndCountsOmittedMiddleGroups()
    {
        var analyzer = new DemoGameplayAnalyzer();
        foreach (var round in new[] { 1, 2 })
            foreach (var tick in new[] { 100, 200, 300, 400 })
                analyzer.Shot(Sample(tick) with { Round = round }, "ak47");
        var report = analyzer.Build(Roster);
        Assert.Equal(8, report.TotalEngagements);
        Assert.Equal([100, 400, 100, 400], report.Engagements.OrderBy(e => e.Round).ThenBy(e => e.FirstShotTick).Select(e => e.FirstShotTick));
        Assert.All(report.EngagementCoverage, row =>
        {
            Assert.Equal(4, row.Engagements);
            Assert.Equal(2, row.RetainedEngagements);
        });
    }

    [Fact]
    public void LongBurstsSplitAtBoundAndSequenceGapsRemainPartial()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var tick = 0; tick <= 400; tick++)
        {
            analyzer.Observe(Sample(tick));
            if (tick >= 32 && tick % 16 == 0) analyzer.Shot(Sample(tick), "ak47");
        }
        var report = analyzer.Build(Roster);
        Assert.Equal(2, report.TotalEngagements);
        Assert.All(report.Engagements, row =>
        {
            Assert.InRange(row.LastShotTick - row.FirstShotTick, 0, 256);
            Assert.InRange(row.Points.Count, 1, 321);
        });
        var gap = new DemoGameplayAnalyzer();
        gap.Shot(Sample(100), "ak47");
        gap.Observe(Sample(102));
        gap.Shot(Sample(103), "ak47");
        Assert.Equal(2, gap.Build(Roster).TotalEngagements);
        Assert.Equal(0, gap.Build(Roster).Engagements[0].AfterTicks);
    }

    [Fact]
    public void EngagementGlobalCapDoesNotLoseCoverageCountsForLaterPairs()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var round = 1; round <= 1000; round++)
        {
            analyzer.Shot(Sample(round * 100) with { Round = round }, "ak47");
            analyzer.Shot(Sample(round * 100 + 50) with { Round = round }, "ak47");
        }
        analyzer.Shot(Sample(100_100) with { SteamId = Id + 1 }, "ak47");
        var report = analyzer.Build([.. Roster, new((Id + 1).ToString(CultureInfo.InvariantCulture), "Later player")]);
        Assert.Equal(2001, report.TotalEngagements);
        Assert.Equal(2000, report.Engagements.Count);
        var later = Assert.Single(report.EngagementCoverage.Where(row => row.SteamId != TestPlayerIds.Primary));
        Assert.Equal(1, later.Engagements);
        Assert.Equal(1, later.EligibleShots);
        Assert.Equal(0, later.RetainedEngagements);
    }

    [Theory]
    [InlineData(0, 0, 100, 0, 0, 0)]
    [InlineData(0, 90, 0, 100, 0, 0)]
    [InlineData(0, 0, -100, 0, 0, 180)]
    [InlineData(-45, 0, 100, 0, 100, 0)]
    public void GeometryUsesThreeDimensionalEyeDirectionNotYawOnly(double pitch, double yaw,
        double x, double y, double z, double expected)
    {
        var shooter = Sample(100, yaw) with { Team = 2, Pitch = pitch, EyeX = 0, EyeY = 0, EyeZ = 64 };
        var opponent = shooter with { SteamId = Id + 1, Team = 3, EyeX = x, EyeY = y, EyeZ = 64 + z };
        var geometry = DemoEngagementCollector.Geometry(shooter, [opponent]);
        Assert.Equal(1, geometry.ObservedOpponents);
        Assert.Equal(1, geometry.AssessedOpponents);
        Assert.Equal(expected, geometry.EyeAngleDegrees!.Value, 5);
        Assert.Equal(x, geometry.RelativeEyeX);
        Assert.Equal(y, geometry.RelativeEyeY);
        Assert.Equal(z, geometry.RelativeEyeZ);
        Assert.Equal((Id + 1).ToString(CultureInfo.InvariantCulture), geometry.ClosestAngularOpponent);
    }

    [Fact]
    public void GeometryExcludesAlliesStaleAndDeadPlayersAndKeepsMissingDataUnknown()
    {
        var shooter = Sample(100) with { Team = 2, EyeX = 0, EyeY = 0, EyeZ = 64 };
        var opponent = shooter with { SteamId = Id + 1, Team = 3, EyeX = 100 };
        var missing = opponent with { SteamId = Id + 2, EyeZ = null };
        var geometry = DemoEngagementCollector.Geometry(shooter,
            [opponent, opponent, missing, opponent with { SteamId = Id + 3, Team = 2 },
                opponent with { SteamId = Id + 4, Tick = 99 }, opponent with { SteamId = Id + 5, Eligible = false }]);
        Assert.Equal(2, geometry.ObservedOpponents);
        Assert.Equal(1, geometry.AssessedOpponents);
        Assert.Null(DemoEngagementCollector.Geometry(shooter with { EyeZ = null }, [opponent]).EyeAngleDegrees);
        Assert.Null(DemoEngagementCollector.Geometry(shooter, []).EyeAngleDegrees);
        Assert.False(DemoEngagementCollector.Geometry(shooter, null).OpponentSnapshotAvailable);
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Shot(shooter, "ak47", null);
        analyzer.Shot(shooter with { Tick = 101 }, "ak47", []);
        analyzer.Shot(shooter with { Tick = 102 }, "ak47",
            [opponent with { Tick = 102 }, missing with { Tick = 102 }], 1);
        var coverage = Assert.Single(analyzer.Build(Roster).EngagementCoverage);
        Assert.Equal(1, coverage.GeometryAssessedShots);
        Assert.Equal(1, coverage.NoOpponentShots);
        Assert.Equal(2, coverage.IncompleteGeometryShots);
    }

    [Fact]
    public void CoverageIncludesNonFiringParticipantsWithoutCountingSameTickCommandsTwice()
    {
        var analyzer = new DemoGameplayAnalyzer();
        analyzer.Observe(Sample(100));
        analyzer.Observe(Sample(100, 5));
        analyzer.Observe(Sample(101));
        var row = Assert.Single(analyzer.Build(Roster).EngagementCoverage);
        Assert.Equal(2, row.ObservedTicks);
        Assert.Equal(0, row.EligibleShots);
        Assert.Equal(0, row.Engagements);
        Assert.Null(row.MinimumEyeAngleDegrees);
    }

    [Fact]
    public void FindingRetentionLimitDoesNotSilentlyCapTotals()
    {
        var analyzer = new DemoGameplayAnalyzer();
        for (var index = 0; index < 2005; index++)
        {
            analyzer.Observe(Sample(100 + index * 2));
            analyzer.Shot(Sample(101 + index * 2, 30), "ak47");
        }
        var report = analyzer.Build(Roster);
        Assert.Equal(2000, report.Findings.Count);
        Assert.Equal(2005, report.TotalFindings);
        Assert.Equal(2005, report.Players[0].LargeTurns);
        Assert.Equal(2005, report.TotalShotWindows);
        Assert.Equal(2000, report.ShotWindows.Count);
    }
}
