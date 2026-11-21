using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class GeminiReviewTests
{
    private const string Key = "AQ.synthetic_Gemini_Key_1234567890";
    private static readonly string Success = ReviewResponse("""
        {"commentary":{"coverage":"Review the supplied measurements in context.","alternativeExplanations":"Sampling may affect the recorded values.","missingEvidence":"Visibility and raw input are not supplied."},"players":[
          {"alias":"Player01","status":"no_concern_identified","reason":"No specific review concern in the supplied measurements.","observation":"The supplied turn measurement is context, not a normal-play comparison.","moments":[]}]}
        """);

    private static DemoGameplayReport Report(int events = 1) => new(
        [new DemoGameplayPlayer(TestPlayerIds.Primary, "SENSITIVE_LOCAL_ALIAS", 200, 190, events, 2)],
        Enumerable.Range(0, events).Select(i =>
            new DemoAimFinding(TestPlayerIds.Primary, 1, 100 + i, "ak47", 30, 2)).ToArray(), events);

    private static DemoShotWindow Window(int tick = 100) => new(TestPlayerIds.Primary, 1, tick, "ak47",
        8, 8, 90, 90, 0, 0, Enumerable.Range(-8, 17)
            .Select(offset => new DemoViewPoint(offset, 0, Math.Min(offset, 0) * 11.25)).ToArray());

    private static DemoGameplayReport EngagementReport()
    {
        var shots = Enumerable.Range(0, 10).Select(index => new DemoEngagementShot(100 + index * 2,
            true, 1, index == 9 ? 0 : 1, index == 9 ? null : TestPlayerIds.Secondary,
            index == 9 ? null : index == 8 ? 0.5 : 0.6, index == 9 ? null : 100,
            index == 9 ? null : 100, index == 9 ? null : 0, index == 9 ? null : 0, 2)).ToArray();
        return Report(0) with
        {
            Players = [new(TestPlayerIds.Primary, "LOCAL_ONLY_NAME", 10, 10, 0, 0),
                new(TestPlayerIds.Secondary, "OTHER_LOCAL_ONLY_NAME", 0, 0, 0, 0)],
            EngagementCoverage = [new(TestPlayerIds.Primary, 1, 83, 10, 4, 1, 9, 0, 1, 0.5, 116),
                new(TestPlayerIds.Secondary, 1, 83, 0, 0, 0, 0, 0, 0, null, null)],
            Engagements = [new(TestPlayerIds.Primary, 1, 100, 118, "ak47", 10, 32, 32, 8.2, 8.2, 164,
                Enumerable.Range(-32, 83).Select(offset =>
                    new DemoEngagementPoint(offset, 0, offset * 0.1, offset * 2, 0, 0)).ToArray(), shots)],
            TotalEngagements = 4,
            DamageEvents = [new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 117, "ak47", 10)],
            TotalDamageEvents = 1
        };
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(30, 1)]
    public async Task SyntheticGameplayCanBePreviewedAndReviewedWithoutLiveServices(double turn, int selected)
    {
        var analyzer = new DemoGameplayAnalyzer();
        var before = new DemoAimSample(ulong.Parse(TestPlayerIds.Primary, CultureInfo.InvariantCulture),
            100, 1, 10, 1, true, 0, 0, 0, 0, 0);
        analyzer.Observe(before);
        analyzer.Shot(before with { Tick = 101, Yaw = turn }, "weapon_ak47");
        var report = analyzer.Build([new DemoPlayer(TestPlayerIds.Primary, "SENSITIVE_LOCAL_NAME")]);
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);

        Assert.DoesNotContain(TestPlayerIds.Primary, preview.JsonBody);
        Assert.DoesNotContain("SENSITIVE_LOCAL_NAME", preview.JsonBody);
        Assert.DoesNotContain(Key, preview.JsonBody);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString()!);
        var player = Assert.Single(summary.RootElement.GetProperty("players").EnumerateArray());
        Assert.Equal("Player01", player.GetProperty("alias").GetString());
        Assert.Equal(1, player.GetProperty("decodedFirearmShots").GetInt32());
        Assert.Equal(1, player.GetProperty("assessedShots").GetInt32());
        Assert.Equal(selected, player.GetProperty("selectedShots").GetInt32());
        Assert.Equal(selected, summary.RootElement.GetProperty("totalSelectedEvents").GetInt32());
        var events = summary.RootElement.GetProperty("events");
        Assert.Equal(selected, events.GetArrayLength());
        if (selected > 0)
        {
            var finding = Assert.Single(events.EnumerateArray());
            Assert.Equal("Player01", finding.GetProperty("alias").GetString());
            Assert.Equal(1, finding.GetProperty("round").GetInt32());
            Assert.Equal(101, finding.GetProperty("demoTick").GetInt32());
            Assert.Equal("ak47", finding.GetProperty("weapon").GetString());
            Assert.Equal(turn, finding.GetProperty("recordedTurnDegrees").GetDouble(), 3);
        }

        var methods = new List<HttpMethod>();
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            methods.Add(request.Method);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            if (request.Method == HttpMethod.Get)
            {
                Assert.Null(request.Content);
                return Response(HttpStatusCode.OK,
                    $$"""{"name":"models/{{preview.Model}}","supportedGenerationMethods":["generateContent"]}""");
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(preview.Endpoint, request.RequestUri);
            Assert.Equal(preview.JsonBody, await request.Content!.ReadAsStringAsync(token));
            return Response(HttpStatusCode.OK, Success);
        }));

        await client.ValidateKeyAsync(Key, preview.Model, CancellationToken.None);
        Assert.Equal([HttpMethod.Get], methods);
        var commentary = (await client.ReviewAsync(preview, Key, CancellationToken.None)).Commentary;
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], methods);
        Assert.StartsWith("UNVERIFIED AI COMMENTARY", commentary);
        Assert.Contains("Review the supplied measurements in context.", commentary);
        Assert.Equal(selected, report.TotalFindings);
        Assert.Equal(1, report.Players[0].AssessedShots);
    }

    [Fact]
    public async Task SuccessfulKeyTestDoesNotGuaranteeGenerationQuota()
    {
        var methods = new List<HttpMethod>();
        using var client = new GeminiReviewClient(new Handler((request, _) =>
        {
            methods.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Response(HttpStatusCode.OK,
                    $$"""{"name":"models/{{GeminiReviewRequest.DefaultModel}}","supportedGenerationMethods":["generateContent"]}""")
                : Response(HttpStatusCode.TooManyRequests, """{"error":"quota exhausted"}"""));
        }));

        await client.ValidateKeyAsync(Key, GeminiReviewRequest.DefaultModel, CancellationToken.None);
        Assert.Equal([HttpMethod.Get], methods);
        var error = await Assert.ThrowsAsync<IOException>(() => client.ReviewAsync(
            GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
        Assert.Contains("quota/rate limit", error.Message);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], methods);
    }

    [Fact]
    public async Task KeyTestChecksOnlySelectedModelMetadataWithoutPromptOrDemoData()
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://generativelanguage.googleapis.com/v1beta/models/{GeminiReviewRequest.DefaultModel}",
                request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return Task.FromResult(Response(HttpStatusCode.OK,
                $$"""{"name":"models/{{GeminiReviewRequest.DefaultModel}}","supportedGenerationMethods":["generateContent"]}"""));
        }));
        await client.ValidateKeyAsync(Key, GeminiReviewRequest.DefaultModel, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":"models/something-else","supportedGenerationMethods":["generateContent"]}""")]
    [InlineData("""{"name":"models/gemini-3.1-flash-lite","supportedGenerationMethods":["embedContent"]}""")]
    public async Task KeyTestDoesNotAssumeSuccessFromAnUnrelatedOrUnsupportedModel(string body)
    {
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ValidateKeyAsync(Key, GeminiReviewRequest.DefaultModel, CancellationToken.None));
    }

    [Fact]
    public void SummaryUsesAnAllowlistAndGeneratedAliasesNotLocalIdentityFields()
    {
        var review = GeminiReviewRequest.Create(Report(110), true, GeminiReviewRequest.DefaultModel);
        Assert.DoesNotContain(TestPlayerIds.Primary, review.JsonBody);
        Assert.DoesNotContain("SENSITIVE_LOCAL_ALIAS", review.JsonBody);
        Assert.DoesNotContain(Key, review.JsonBody);
        using var request = JsonDocument.Parse(review.JsonBody);
        using var summary = JsonDocument.Parse(request.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        Assert.True(summary.RootElement.GetProperty("partialRecording").GetBoolean());
        Assert.Equal(100, summary.RootElement.GetProperty("events").GetArrayLength());
        Assert.Equal(110, summary.RootElement.GetProperty("totalSelectedEvents").GetInt32());
        var player = Assert.Single(summary.RootElement.GetProperty("players").EnumerateArray());
        Assert.Equal("Player01", player.GetProperty("alias").GetString());
        Assert.Equal(["alias", "decodedFirearmShots", "assessedShots", "selectedShots", "roundsWithSelections"],
            player.EnumerateObject().Select(p => p.Name));
        Assert.True(Encoding.UTF8.GetByteCount(review.JsonBody) <= 64_000);
    }

    [Fact]
    public void ShotWindowsLinkNearbyMatchingEventsWithoutAssumingBulletAttribution()
    {
        var report = Report(0) with
        {
            Players = [.. Report(0).Players, new DemoGameplayPlayer(TestPlayerIds.Secondary, "LOCAL_VICTIM", 0, 0, 0, 0)],
            ShotWindows = [Window(100), Window(101)],
            TotalShotWindows = 2,
            DamageEvents = [
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 99, "ak47", 1),
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 100, "weapon_ak47", 20),
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 103, "ak47", 30),
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 110, "ak47", 1),
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 102, "awp", 1),
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 2, 102, "ak47", 1)],
            TotalDamageEvents = 6,
            CombatEvents = [new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 104, "ak47", true)],
            TotalCombatEvents = 1
        };
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        Assert.DoesNotContain(TestPlayerIds.Primary, preview.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Secondary, preview.JsonBody);
        Assert.DoesNotContain("LOCAL_VICTIM", preview.JsonBody);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var windows = summary.RootElement.GetProperty("shotWindows").GetProperty("windows");
        Assert.Equal(2, windows.GetArrayLength());
        var first = windows[0];
        Assert.Equal(90, first.GetProperty("beforeNetTurnDegrees").GetDouble());
        Assert.Equal(17, first.GetProperty("samples").GetArrayLength());
        Assert.Equal(3, first.GetProperty("nearbyEventCount").GetInt32());
        var nearby = first.GetProperty("nearbyEvents");
        Assert.Equal("damage", nearby[0][1].GetString());
        Assert.Equal("Player02", nearby[0][2].GetString());
        Assert.Equal(100, nearby[0][3].GetInt32());
        Assert.Equal(20, nearby[0][4].GetInt32());
        Assert.Equal(JsonValueKind.Null, nearby[0][5].ValueKind);
        Assert.Equal("kill", nearby[2][1].GetString());
        Assert.True(nearby[2][5].GetBoolean());
        var secondNearby = windows[1].GetProperty("nearbyEvents");
        Assert.Equal(2, secondNearby.GetArrayLength());
        Assert.Equal(nearby[1][0].GetString(), secondNearby[0][0].GetString());
        Assert.Equal(6, summary.RootElement.GetProperty("damageCoverage").GetProperty("totalEvents").GetInt32());
    }

    [Fact]
    public void NearbyContextBoundsPreserveCallbackCountInsteadOfCollapsingPellets()
    {
        var report = Report(0) with
        {
            Players = [.. Report(0).Players, new DemoGameplayPlayer(TestPlayerIds.Secondary, "Local", 0, 0, 0, 0)],
            ShotWindows = [Window()], TotalShotWindows = 1,
            DamageEvents = Enumerable.Range(0, 12).Select(_ =>
                new DemoDamageEvent(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 100, "ak47", 1)).ToArray(),
            TotalDamageEvents = 12
        };
        using var body = JsonDocument.Parse(GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel).JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var window = summary.RootElement.GetProperty("shotWindows").GetProperty("windows")[0];
        Assert.Equal(12, window.GetProperty("nearbyEventCount").GetInt32());
        Assert.Equal(8, window.GetProperty("nearbyEventsIncluded").GetInt32());
        var events = window.GetProperty("nearbyEvents");
        Assert.Equal(8, events.GetArrayLength());
        Assert.Equal(8, events.EnumerateArray().Select(e => e[0].GetString()).Distinct().Count());
    }

    [Fact]
    public void ReviewIncludesRoundMeasurementsAndCombatContextEvenWithoutSelectedBookmarks()
    {
        var report = Report(0) with
        {
            Players = [.. Report(0).Players, new DemoGameplayPlayer(TestPlayerIds.Secondary, "OTHER_LOCAL_NAME", 0, 0, 0, 0)],
            RoundTelemetry = [
                new(TestPlayerIds.Primary, 1, 10, 9, 2.5, 7.25, 101, 1, 1, 0),
                new(TestPlayerIds.Secondary, 1, 0, 0, null, null, null, 0, 0, 1)],
            CombatEvents = [new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 105, "weapon_ak47", true)],
            TotalCombatEvents = 1
        };
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        Assert.DoesNotContain(TestPlayerIds.Primary, preview.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Secondary, preview.JsonBody);
        Assert.DoesNotContain("OTHER_LOCAL_NAME", preview.JsonBody);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var root = summary.RootElement;
        Assert.Equal(0, root.GetProperty("events").GetArrayLength());
        var rounds = root.GetProperty("roundTelemetry");
        Assert.Equal(2, rounds.GetProperty("totalRows").GetInt32());
        Assert.Equal(["alias", "round", "eligibleShots", "assessedShots", "meanTurnDegrees",
            "maximumTurnDegrees", "maximumTurnTick", "totalKills", "totalHeadshotKills", "totalDeaths",
            "firearmKills", "firearmHeadshotKills", "firearmDeaths"],
            rounds.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        var first = rounds.GetProperty("rows")[0];
        Assert.Equal("Player01", first[0].GetString());
        Assert.Equal(2.5, first[4].GetDouble());
        Assert.Equal(101, first[6].GetInt32());
        Assert.Equal(JsonValueKind.Null, rounds.GetProperty("rows")[1][4].ValueKind);
        var combat = Assert.Single(root.GetProperty("combatTimeline").GetProperty("rows").EnumerateArray());
        Assert.Equal("Player01", combat[0].GetString());
        Assert.Equal("Player02", combat[1].GetString());
        Assert.Equal(105, combat[3].GetInt32());
        Assert.Equal("ak47", combat[4].GetString());
        Assert.True(combat[5].GetBoolean());
    }

    [Fact]
    public void LargeTelemetryReviewsStayWithinRequestBudgetAndDiscloseRepresentativeSampling()
    {
        var ids = Enumerable.Range(0, 128).Select(index =>
            (ulong.Parse(TestPlayerIds.Primary, CultureInfo.InvariantCulture) + (ulong)index)
                .ToString(CultureInfo.InvariantCulture)).ToArray();
        var report = Report(2000) with
        {
            Players = ids.Select(id => new DemoGameplayPlayer(id, "LOCAL_ONLY_NAME", 200, 190, 10, 2)).ToArray(),
            RoundTelemetry = Enumerable.Range(1, 100).SelectMany(round => ids.Select(id =>
                new DemoRoundTelemetry(id, round, 100, 90, 2.5, 35, round * 1000, 2, 1, 1))).ToArray(),
            CombatEvents = Enumerable.Range(0, 2000).Select(index =>
                new DemoCombatEvent(ids[0], ids[1], index / 20 + 1, index * 100, "ak47", true)).ToArray(),
            TotalCombatEvents = 2500,
            ShotWindows = Enumerable.Range(0, 64).Select(_ => Window()).ToArray(),
            TotalShotWindows = 80,
            DamageEvents = Enumerable.Range(0, 2000).Select(_ =>
                new DemoDamageEvent(ids[0], ids[1], 1, 100, "ak47", 1)).ToArray(),
            TotalDamageEvents = 2500
        };
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        Assert.InRange(Encoding.UTF8.GetByteCount(preview.JsonBody), 1, 64_000);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var rounds = summary.RootElement.GetProperty("roundTelemetry");
        Assert.Equal(12800, rounds.GetProperty("totalRows").GetInt32());
        var rows = rounds.GetProperty("rows");
        Assert.Equal(rows.GetArrayLength(), rounds.GetProperty("includedRows").GetInt32());
        Assert.InRange(rows.GetArrayLength(), 2, 400);
        Assert.Equal(1, rows[0][1].GetInt32());
        Assert.Equal(100, rows[rows.GetArrayLength() - 1][1].GetInt32());
        Assert.Equal("Player128", rows[rows.GetArrayLength() - 1][0].GetString());
        var timeline = summary.RootElement.GetProperty("combatTimeline");
        Assert.Equal(2500, timeline.GetProperty("totalEvents").GetInt32());
        Assert.Equal(2000, timeline.GetProperty("retainedEvents").GetInt32());
        var events = timeline.GetProperty("rows");
        Assert.Equal(events.GetArrayLength(), timeline.GetProperty("includedEvents").GetInt32());
        Assert.InRange(events.GetArrayLength(), 2, 200);
        Assert.Equal(0, events[0][3].GetInt32());
        Assert.Equal(199900, events[events.GetArrayLength() - 1][3].GetInt32());
        var windows = summary.RootElement.GetProperty("shotWindows");
        Assert.Equal(80, windows.GetProperty("totalWindows").GetInt32());
        Assert.Equal(64, windows.GetProperty("retainedWindows").GetInt32());
        Assert.InRange(windows.GetProperty("includedWindows").GetInt32(), 1, 32);
        Assert.Equal(windows.GetProperty("includedWindows").GetInt32(), windows.GetProperty("windows").GetArrayLength());
    }

    [Fact]
    public void UnrosteredCombatContextIsRejectedWithoutExposingTheIdentifier()
    {
        var report = Report() with
        {
            CombatEvents = [new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 1, 105, "ak47", true)],
            TotalCombatEvents = 1
        };
        var error = Assert.Throws<FormatException>(() =>
            GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel));
        Assert.DoesNotContain(TestPlayerIds.Secondary, error.Message);
        Assert.Contains("missing from the imported roster", error.Message);
    }

    [Fact]
    public void EngagementPreviewDisclosesCoverageAndBoundsRelativeGeometryWithoutIdentities()
    {
        var preview = GeminiReviewRequest.Create(EngagementReport(), false, GeminiReviewRequest.DefaultModel);
        Assert.DoesNotContain(TestPlayerIds.Primary, preview.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Secondary, preview.JsonBody);
        Assert.DoesNotContain("LOCAL_ONLY_NAME", preview.JsonBody);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var root = summary.RootElement;
        var audit = root.GetProperty("playerCoverage");
        Assert.True(audit.GetProperty("available").GetBoolean());
        Assert.Equal(11, audit.GetProperty("columns").GetArrayLength());
        var auditRow = audit.GetProperty("rows")[0];
        Assert.Equal("Player01", auditRow[0].GetString());
        Assert.Equal([1, 83, 10, 4, 1, 1, 9, 0, 1, 1],
            auditRow.EnumerateArray().Skip(1).Select(value => value.GetInt32()));
        var coverage = root.GetProperty("engagementCoverage").GetProperty("rows");
        Assert.Equal(116, coverage[0][10].GetInt32());
        Assert.Equal(JsonValueKind.Null, coverage[1][9].ValueKind);
        var groups = root.GetProperty("engagements");
        Assert.Equal(4, groups.GetProperty("totalGroups").GetInt32());
        Assert.Equal(1, groups.GetProperty("retainedExamples").GetInt32());
        var example = Assert.Single(groups.GetProperty("examples").EnumerateArray());
        var points = example.GetProperty("points");
        Assert.Equal(17, points.GetArrayLength());
        Assert.Equal(83, example.GetProperty("retainedPoints").GetInt32());
        Assert.Equal(-32, points[0][0].GetInt32());
        Assert.Equal(-64, points[0][3].GetDouble());
        Assert.Equal(50, points[16][0].GetInt32());
        var snapshots = example.GetProperty("shotSnapshots");
        Assert.Equal(8, snapshots.GetArrayLength());
        Assert.Equal("Player02", snapshots[0][4].GetString());
        Assert.Equal(100, snapshots[0][7].GetDouble());
        Assert.Equal(118, snapshots[7][0].GetInt32());
        Assert.Equal(JsonValueKind.Null, snapshots[7][4].ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshots[7][5].ValueKind);
        Assert.Equal(117, Assert.Single(example.GetProperty("nearbyEvents").EnumerateArray())[3].GetInt32());
        Assert.InRange(Encoding.UTF8.GetByteCount(preview.JsonBody), 1, 64_000);
    }

    [Theory]
    [InlineData(104, true)]
    [InlineData(116, true)]
    [InlineData(117, true)]
    [InlineData(108, false)]
    [InlineData(68, false)]
    public async Task EngagementCitationsRequireSuppliedShotCoverageOrEventAnchors(int tick, bool accepted)
    {
        var response = ReviewResponse($$"""
            {"commentary":{"coverage":"Bounded engagement examples.","alternativeExplanations":"Sampling may affect the values.","missingEvidence":"Visibility is unknown."},"players":[
              {"alias":"Player01","status":"review_suggested","reason":"Review this supplied context.","observation":"One supplied sequence.","moments":[{"round":1,"tick":{{tick}}}]},
              {"alias":"Player02","status":"insufficient_data","reason":"No eligible shots.","observation":"Only coverage counts were supplied.","moments":[]}]}
            """);
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, response))));
        var request = GeminiReviewRequest.Create(EngagementReport(), false, GeminiReviewRequest.DefaultModel);
        if (accepted)
        {
            var result = await client.ReviewAsync(request, Key, CancellationToken.None);
            Assert.Equal(tick, Assert.Single(result.Players[0].Moments).Tick);
            Assert.Contains("eye-angle minimum 0.5 deg", result.Commentary);
        }
        else await Assert.ThrowsAsync<FormatException>(() => client.ReviewAsync(request, Key, CancellationToken.None));
    }

    [Fact]
    public async Task CountsAloneCannotProduceANoConcernResult()
    {
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, Success))));
        var request = GeminiReviewRequest.Create(Report(0), false, GeminiReviewRequest.DefaultModel);
        await Assert.ThrowsAsync<FormatException>(() => client.ReviewAsync(request, Key, CancellationToken.None));
    }

    [Theory]
    [InlineData("roundTelemetry")]
    [InlineData("engagementCoverage")]
    [InlineData("shotWindows")]
    public void BalancedSelectionsIncludeSparseLatePlayers(string section)
    {
        var ids = new[] { TestPlayerIds.Primary, TestPlayerIds.Secondary, TestPlayerIds.Third };
        var pairs = Enumerable.Range(1, 1000).Select(round => (Id: ids[0], Round: round))
            .Concat([(Id: ids[1], Round: 1001), (Id: ids[2], Round: 1002)]).ToArray();
        var report = Report(0) with
        {
            Players = ids.Select(id => new DemoGameplayPlayer(id, "LOCAL_ONLY_NAME", 100, 90, 0, 0)).ToArray(),
            RoundTelemetry = section == "roundTelemetry" ? pairs.Select(pair =>
                new DemoRoundTelemetry(pair.Id, pair.Round, 1, 1, 1, 1, pair.Round * 100, 0, 0, 0)).ToArray() : [],
            EngagementCoverage = section == "engagementCoverage" ? pairs.Select(pair =>
                new DemoEngagementCoverage(pair.Id, pair.Round, 10, 1, 1, 0, 0, 0, 1, null, null)).ToArray() : [],
            TotalEngagements = pairs.Length,
            ShotWindows = section == "shotWindows" ? Enumerable.Range(0, 100).Select(index => Window(index * 100))
                .Concat([Window(100100) with { SteamId = ids[1], Round = 1001 },
                    Window(100200) with { SteamId = ids[2], Round = 1002 }]).ToArray() : [],
            TotalShotWindows = 102
        };
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var root = summary.RootElement;
        if (section == "shotWindows")
        {
            var aliases = root.GetProperty(section).GetProperty("windows").EnumerateArray()
                .Select(window => window.GetProperty("alias").GetString()).ToArray();
            Assert.Contains("Player02", aliases);
            Assert.Contains("Player03", aliases);
        }
        else
        {
            var aliases = root.GetProperty(section).GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()).ToArray();
            Assert.Contains("Player02", aliases);
            Assert.Contains("Player03", aliases);
        }
        if (section == "engagementCoverage")
        {
            var audit = root.GetProperty("playerCoverage").GetProperty("rows");
            Assert.Equal(3, audit.GetArrayLength());
            Assert.Equal(1000, audit[0][1].GetInt32());
            Assert.Equal(1, audit[2][1].GetInt32());
        }
        Assert.InRange(Encoding.UTF8.GetByteCount(preview.JsonBody), 1, 64_000);
    }

    [Fact]
    public void LargeEngagementReviewsKeepPlayerExamplesWithinTheActualRequestBudget()
    {
        var ids = Enumerable.Range(0, 10).Select(index =>
            (ulong.Parse(TestPlayerIds.Primary, CultureInfo.InvariantCulture) + (ulong)index)
                .ToString(CultureInfo.InvariantCulture)).ToArray();
        var sample = EngagementReport().Engagements[0];
        var pairs = Enumerable.Range(1, 100).SelectMany(round => ids.Select(id => (Id: id, Round: round))).ToArray();
        var report = Report(2000) with
        {
            Players = ids.Select(id => new DemoGameplayPlayer(id, "LOCAL_ONLY_NAME", 2000, 1900, 0, 0)).ToArray(),
            Engagements = pairs.SelectMany(pair => Enumerable.Range(0, 2).Select(_ => pair)).Select((pair, index) =>
                sample with { SteamId = pair.Id, Round = pair.Round, FirstShotTick = 100 + index * 1000,
                    LastShotTick = 118 + index * 1000, ShotContext = sample.ShotContext.Select(shot =>
                        shot with { Tick = shot.Tick + index * 1000, ClosestAngularOpponent =
                            shot.AssessedOpponents > 0 ? ids[(Array.IndexOf(ids, pair.Id) + 5) % ids.Length] : null }).ToArray() }).ToArray(),
            TotalEngagements = 2000,
            EngagementCoverage = pairs.Select(pair =>
                new DemoEngagementCoverage(pair.Id, pair.Round, 100, 20, 2, 2, 18, 0, 2, 0.5, pair.Round * 1000)).ToArray(),
            RoundTelemetry = pairs.Select(pair =>
                new DemoRoundTelemetry(pair.Id, pair.Round, 20, 19, 1, 2, pair.Round * 1000, 1, 0, 1)).ToArray(),
            CombatEvents = Enumerable.Range(0, 2000).Select(index =>
                new DemoCombatEvent(ids[index % 10], ids[(index + 5) % 10], index / 20 + 1, index * 100, "ak47", true)).ToArray(),
            TotalCombatEvents = 2000,
            ShotWindows = Enumerable.Range(0, 2000).Select(index => Window(index * 100)).ToArray(),
            TotalShotWindows = 2000
        };
        var preview = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        Assert.InRange(Encoding.UTF8.GetByteCount(preview.JsonBody), 1, 64_000);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var root = summary.RootElement;
        var groups = root.GetProperty("engagements");
        Assert.Equal(2000, groups.GetProperty("retainedExamples").GetInt32());
        var examples = groups.GetProperty("examples");
        Assert.Equal(examples.GetArrayLength(), groups.GetProperty("includedExamples").GetInt32());
        Assert.Equal(10, examples.EnumerateArray().Select(example => example.GetProperty("alias").GetString()).Distinct().Count());
        var audit = root.GetProperty("playerCoverage").GetProperty("rows");
        Assert.Equal(10, audit.GetArrayLength());
        Assert.All(audit.EnumerateArray(), row => Assert.Equal(200, row[4].GetInt32()));
        foreach (var section in new[] { "roundTelemetry", "engagementCoverage" })
            Assert.Equal(root.GetProperty(section).GetProperty("rows").GetArrayLength(),
                root.GetProperty(section).GetProperty("includedRows").GetInt32());
    }

    [Fact]
    public void UnsupportedWeaponStringsAndEndpointInjectionAreRejectedBeforeNetworking()
    {
        var report = Report() with
        {
            Findings = [new DemoAimFinding(TestPlayerIds.Primary, 1, 100, TestPlayerIds.Primary, 30, 2)]
        };
        Assert.Throws<FormatException>(() => GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel));
        foreach (var model in new[] { "https://example.com", "gemini-x/../../other", "gemini-x?key=secret", "gemini-x\nheader" })
            Assert.Throws<FormatException>(() => GeminiReviewRequest.Create(Report(), false, model));
        var noCoverage = Report() with { Players = [new DemoGameplayPlayer(TestPlayerIds.Primary, "Local", 10, 0, 0, 0)] };
        Assert.Throws<FormatException>(() => GeminiReviewRequest.Create(noCoverage, false, GeminiReviewRequest.DefaultModel));
    }

    [Fact]
    public async Task ClientSendsExactlyThePreviewOnceToGoogleWithKeyOnlyInHeader()
    {
        var review = GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel);
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.Equal(review.JsonBody, await request.Content!.ReadAsStringAsync(token));
            return Response(HttpStatusCode.OK, Success);
        }));
        var result = (await client.ReviewAsync(review, Key, CancellationToken.None)).Commentary;
        Assert.Equal(1, calls);
        Assert.StartsWith("UNVERIFIED AI COMMENTARY", result);
        Assert.Contains("No website flags, coverage or rankings were changed", result);
    }

    [Theory]
    [InlineData("not_cheating", "[]")]
    [InlineData("review_suggested", "[]")]
    [InlineData("review_suggested", """[{"round":1,"tick":999}]""")]
    [InlineData("review_suggested", """[{"round":"1","tick":100}]""")]
    [InlineData("review_suggested", """[{"round":1,"tick":100},{"round":1,"tick":100}]""")]
    [InlineData("no_concern_identified", """[{"round":1,"tick":100}]""")]
    public async Task PlayerResultsRejectUnsupportedStatusesAndMissingOrUnprovidedEvidence(string status, string moments)
    {
        var response = ReviewResponse($$"""
            {"commentary":{"coverage":"Bounded observations.","alternativeExplanations":"Sampling may affect the measurements.","missingEvidence":"Visibility is unknown."},"players":[
              {"alias":"Player01","status":"{{status}}","reason":"Short reason.","observation":"One supplied measured event.","moments":{{moments}}}]}
            """);
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, response))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task PlayerResultsRequireEveryKnownAliasExactlyOnce(string mode)
    {
        var aliases = mode switch
        {
            "missing" => new[] { "Player01" },
            "duplicate" => ["Player01", "Player01"],
            _ => ["Player01", "Player99"]
        };
        var report = Report() with
        {
            Players = [.. Report().Players, new DemoGameplayPlayer(TestPlayerIds.Secondary, "Local", 10, 9, 0, 0)]
        };
        var body = JsonSerializer.Serialize(new
        {
            commentary = new { coverage = "Bounded observations.", alternativeExplanations = "Sampling may affect the measurements.", missingEvidence = "Visibility is unknown." },
            players = aliases.Select(alias => new { alias, status = "no_concern_identified", reason = "Short reason.", observation = "Individual supplied measurements.", moments = Array.Empty<object>() })
        });
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, ReviewResponse(body)))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Fact]
    public async Task FinalTableMapsAllStatusesToLocalNamesAndIdsWithoutSendingIdentities()
    {
        var report = Report() with
        {
            Players = [.. Report().Players,
                new DemoGameplayPlayer(TestPlayerIds.Secondary, "Local only", 10, 9, 0, 0),
                new DemoGameplayPlayer(TestPlayerIds.Third, "Local only", 0, 0, 0, 0)],
            RoundTelemetry = [new(TestPlayerIds.Secondary, 1, 10, 9, 2, 3, 110, 0, 0, 0)]
        };
        var response = ReviewResponse("""
            {"commentary":{"coverage":"Bounded observations.","alternativeExplanations":"Sampling may affect the measurements.","missingEvidence":"Visibility is unknown."},"players":[
              {"alias":"Player03","status":"insufficient_data","reason":"No assessed shots.","observation":"No assessed angle measurements are supplied.","moments":[]},
              {"alias":"Player01","status":"review_suggested","reason":"Review the supplied measured event, not an accusation.","observation":"The supplied 30 degree turn at round 1, tick 100 merits contextual review.","moments":[{"round":1,"tick":100}]},
              {"alias":"Player02","status":"no_concern_identified","reason":"No specific concern in supplied telemetry; not cleared.","observation":"Nine assessed turns are counted; no detailed rows are supplied.","moments":[]}]}
            """);
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.DoesNotContain(TestPlayerIds.Primary, body);
            Assert.DoesNotContain(TestPlayerIds.Secondary, body);
            Assert.DoesNotContain(TestPlayerIds.Third, body);
            Assert.DoesNotContain("Recorded|One", body);
            Assert.DoesNotContain("Recorded Two", body);
            Assert.DoesNotContain("Recorded Three", body);
            using var json = JsonDocument.Parse(body);
            var config = json.RootElement.GetProperty("generationConfig");
            Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
            var players = config.GetProperty("responseJsonSchema").GetProperty("properties").GetProperty("players");
            Assert.Equal(3, players.GetProperty("minItems").GetInt32());
            Assert.Equal(3, players.GetProperty("maxItems").GetInt32());
            return Response(HttpStatusCode.OK, response);
        }));
        var result = await client.ReviewAsync(
            GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None);
        var rendered = result.Render([
            new(TestPlayerIds.Third, "Recorded Three"), new(TestPlayerIds.Secondary, "Recorded Two"),
            new(TestPlayerIds.Primary, "Recorded|One")]).ReplaceLineEndings("\n");
        Assert.DoesNotContain(TestPlayerIds.Primary, result.Commentary);
        Assert.Contains($"Recorded|One (Player01)\n- SteamID64: {TestPlayerIds.Primary}\n- AI review status: Review suggested", rendered);
        Assert.Contains($"Recorded Two (Player02)\n- SteamID64: {TestPlayerIds.Secondary}\n- AI review status: No review concern identified", rendered);
        Assert.Contains($"Recorded Three (Player03)\n- SteamID64: {TestPlayerIds.Third}\n- AI review status: Insufficient data", rendered);
        Assert.True(rendered.IndexOf("Recorded|One", StringComparison.Ordinal) < rendered.IndexOf("Recorded Two", StringComparison.Ordinal));
        Assert.True(rendered.IndexOf("Recorded Two", StringComparison.Ordinal) < rendered.IndexOf("Recorded Three", StringComparison.Ordinal));
        Assert.Contains("round 1, tick 100", rendered);
        Assert.Contains("does not clear a player", rendered);
        var finalSection = GeminiReviewResult.FinalSection(rendered);
        var details = GeminiReviewResult.DetailedSection(rendered);
        Assert.Contains("AI DEMO FINAL RESULT", finalSection);
        Assert.DoesNotContain("DETAILED AI REVIEW EVIDENCE", finalSection);
        Assert.StartsWith("DETAILED AI REVIEW EVIDENCE", details);
        Assert.Throws<FormatException>(() => result.Render([new(TestPlayerIds.Primary, "Only one")]));
    }

    [Fact]
    public async Task ReviewCannotCiteAWindowOmittedFromTheExactRequest()
    {
        var report = Report(0) with
        {
            ShotWindows = Enumerable.Range(100, 33).Select(tick => Window() with { ShotTick = tick }).ToArray(),
            TotalShotWindows = 33
        };
        var request = GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel);
        using var body = JsonDocument.Parse(request.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var included = summary.RootElement.GetProperty("shotWindows").GetProperty("windows")
            .EnumerateArray().Select(window => window.GetProperty("shotTick").GetInt32()).ToHashSet();
        var omitted = Enumerable.Range(100, 33).First(tick => !included.Contains(tick));
        var response = ReviewResponse($$"""
            {"commentary":{"coverage":"Bounded observations.","alternativeExplanations":"Sampling may affect the measurements.","missingEvidence":"Visibility is unknown."},"players":[
              {"alias":"Player01","status":"review_suggested","reason":"Review this window.","observation":"A measured event is supplied.","moments":[{"round":1,"tick":{{omitted}}}]}]}
            """);
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, response))));
        await Assert.ThrowsAsync<FormatException>(() => client.ReviewAsync(request, Key, CancellationToken.None));
    }

    [Theory]
    [InlineData("before\u202eafter")]
    [InlineData("before\u2028after")]
    [InlineData("before\nafter")]
    public async Task StructuredJsonCannotHideUnsafePlayerTextBehindEscapes(string reason)
    {
        var response = ReviewResponse(JsonSerializer.Serialize(new
        {
            commentary = new { coverage = "Bounded observations.", alternativeExplanations = "Sampling may affect the measurements.", missingEvidence = "Visibility is unknown." },
            players = new[] { new { alias = "Player01", status = "insufficient_data", reason, observation = "Individual supplied measurements.", moments = Array.Empty<object>() } }
        }));
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, response))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Fact]
    public async Task DetailedSectionsAndMeasuredExamplesRemainEvenWithoutSuggestedReview()
    {
        var report = Report(0) with
        {
            RoundTelemetry = [new(TestPlayerIds.Primary, 2, 10, 9, 2.5, 7.757, 300, 1, 0, 0)],
            ShotWindows = [Window(222)], TotalShotWindows = 1
        };
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, Success))));
        var result = await client.ReviewAsync(
            GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None);
        var text = result.Render([new(TestPlayerIds.Primary, "Local name")]).ReplaceLineEndings("\n");
        Assert.StartsWith("AI DEMO FINAL RESULT", text);
        Assert.Contains("\nDETAILED AI REVIEW EVIDENCE\n", text);
        var headings = new[] { "\nDETAILED AI REVIEW EVIDENCE\n", "\nCoverage\n", "\nPlayer observations\n",
            "\nReview moments with reasons\n", "\nAlternative explanations\n", "\nMissing evidence\n" };
        var previous = -1;
        foreach (var heading in headings)
        {
            var position = text.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(position > previous, "Required detailed sections must be present in order after the player cards.");
            previous = position;
        }
        Assert.Contains("shot windows included/retained/eligible 1/1/1", text);
        Assert.Contains("Largest included round maximum 7.757 deg at round 2, tick 300", text);
        Assert.Contains("shot tick 222, coverage -8/+8 ticks; before net/path 90/90 deg", text);
        Assert.Contains("AI observation: The supplied turn measurement", text);
        Assert.Contains("The AI suggested no specific moments", text);
        Assert.Contains("Possibilities only, not observed causes:", text);
        Assert.Contains("Visibility and raw input are not supplied.", text);
        Assert.Contains("No review concern identified", text);
    }

    [Theory]
    [InlineData("paragraph_only")]
    [InlineData("missing_coverage")]
    [InlineData("missing_alternatives")]
    [InlineData("missing_evidence")]
    [InlineData("missing_observation")]
    [InlineData("blank_observation")]
    public async Task GenericOrIncompleteDetailedReviewsAreRejected(string defect)
    {
        using var envelope = JsonDocument.Parse(Success);
        var review = JsonNode.Parse(envelope.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()!)!.AsObject();
        if (defect == "paragraph_only") review["commentary"] = "One generic paragraph without detailed sections.";
        else if (defect == "missing_observation") review["players"]![0]!.AsObject().Remove("observation");
        else if (defect == "blank_observation") review["players"]![0]!["observation"] = " ";
        else review["commentary"]!.AsObject().Remove(defect switch
        {
            "missing_coverage" => "coverage",
            "missing_alternatives" => "alternativeExplanations",
            _ => "missingEvidence"
        });
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, ReviewResponse(review.ToJsonString())))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnassessedPlayerCannotReceiveANoConcernResult()
    {
        var report = Report() with
        {
            Players = [.. Report().Players, new DemoGameplayPlayer(TestPlayerIds.Secondary, "Local", 0, 0, 0, 0)]
        };
        var response = ReviewResponse("""
            {"commentary":{"coverage":"Bounded observations.","alternativeExplanations":"Sampling may affect the measurements.","missingEvidence":"Visibility is unknown."},"players":[
              {"alias":"Player01","status":"no_concern_identified","reason":"Short reason.","observation":"Individual supplied measurements.","moments":[]},
              {"alias":"Player02","status":"no_concern_identified","reason":"Short reason.","observation":"Individual supplied measurements.","moments":[]}]}
            """);
        using var client = new GeminiReviewClient(new Handler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, response))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(report, false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task ApiErrorsDoNotEchoSecretsRetryOrFallBack(int status)
    {
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response((HttpStatusCode)status, """{"error":"echo """ + Key + "\"}"));
        }));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.DoesNotContain(Key, error.Message);
        if (status == 429) Assert.Contains("quota/rate limit", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"candidates":[{"finishReason":"MAX_TOKENS"}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"functionCall":{"name":"run"}}]}}]}""")]
    [InlineData("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"\u202eUnsafe"}]}}]}""")]
    public async Task BlockedIncompleteMalformedAndNonTextResponsesAreNotAccepted(string body)
    {
        using var client = new GeminiReviewClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body))));
        await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
    }

    [Fact]
    public async Task StreamingResponseLimitIsEnforcedWithoutAContentLengthHeader()
    {
        using var client = new GeminiReviewClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[128_001]) })));
        var error = await Assert.ThrowsAsync<FormatException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, CancellationToken.None));
        Assert.Contains("128 KB", error.Message);
    }

    [Fact]
    public async Task CancelingInFlightDoesNotAcceptCommentaryOrRetry()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new GeminiReviewClient(new Handler(async (_, token) =>
        {
            calls++;
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK, Success);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReviewAsync(GeminiReviewRequest.Create(Report(), false, GeminiReviewRequest.DefaultModel), Key, cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ReviewResponse(string review) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = review } } } } }
    });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
