using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class GeminiVideoReviewTests
{
    private const string SourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly byte[] Video = Encoding.UTF8.GetBytes("synthetic transport fixture, not playable video");
    private static readonly GeminiVideoContext Clip = new(1, "Player01", 2, 100, 356, 4, true);

    private static GeminiPreparedVideoReview Prepared(bool secondPlayer = false, bool hasAudio = true,
        DemoVideoClipTimeline? timeline = null, bool videoOnly = false, bool clipEventContextOnly = false)
    {
        var players = new List<DemoGameplayPlayer> { new(TestPlayerIds.Primary, "LOCAL_NAME", 2, 2, 1, 1) };
        if (secondPlayer) players.Add(new(TestPlayerIds.Secondary, "OTHER_LOCAL_NAME", 1, 1, 0, 0));
        var telemetry = GeminiReviewRequest.Create(new(players,
            [new(TestPlayerIds.Primary, 2, 100, "ak47", 25, 0)], 1), false, GeminiReviewRequest.DefaultModel);
        return DemoVideoReview.Build(telemetry, [Clip with { HasAudio = hasAudio, EventTimeline = timeline }], [Video],
            [new(1, @"C:\local-only\clip-1.mp4", Convert.ToHexString(SHA256.HashData(Video)), Video.Length)], SourceHash,
            videoOnly, clipEventContextOnly);
    }

    [Fact]
    public void IsolatedEventContextSendsOnlyOwnClipTimelineAndAliasWithUnchangedObservationSchema()
    {
        var focusKill = new DemoVideoTimelineEvent(164, 1, "ak47")
        { OpponentAlias = "Player02", Headshot = true };
        var timeline = new DemoVideoClipTimeline(1, "Player01", 2, 100, 356,
            [new(164, 1, "ak47")], [new(228, 2, "ak47")])
        { RetainedKills = [focusKill], FocusKill = focusKill };
        var prepared = Prepared(secondPlayer: true, timeline: timeline, clipEventContextOnly: true);
        using var body = JsonDocument.Parse(prepared.Request.JsonBody);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(3, parts.GetArrayLength());
        using var descriptor = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
        Assert.Equal(164, Assert.Single(descriptor.RootElement.GetProperty("EventTimeline")
            .GetProperty("RetainedShots").EnumerateArray()).GetProperty("Tick").GetInt32());
        Assert.Equal(2, Assert.Single(descriptor.RootElement.GetProperty("EventTimeline")
            .GetProperty("RetainedDeaths").EnumerateArray()).GetProperty("NominalSeconds").GetDouble());
        Assert.Equal("Player02", descriptor.RootElement.GetProperty("EventTimeline").GetProperty("FocusKill")
            .GetProperty("OpponentAlias").GetString());
        Assert.Equal(Video, Convert.FromBase64String(parts[1].GetProperty("inlineData").GetProperty("data").GetString()!));
        using var roster = JsonDocument.Parse(parts[2].GetProperty("text").GetString()!);
        Assert.Equal(["mode", "players", "videoCitationMap", "limits"], roster.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("one_clip_with_event_context", roster.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Player01", Assert.Single(roster.RootElement.GetProperty("players").EnumerateArray())
            .GetProperty("alias").GetString());
        Assert.Contains("Player01: clip 1, round 2, duration 4 seconds",
            Assert.Single(roster.RootElement.GetProperty("videoCitationMap").EnumerateArray()).GetString());
        Assert.Contains("focus kill tick 164, victim Player02",
            Assert.Single(roster.RootElement.GetProperty("videoCitationMap").EnumerateArray()).GetString());
        Assert.DoesNotContain("OTHER_LOCAL_NAME", prepared.Request.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Secondary, prepared.Request.JsonBody);
        Assert.DoesNotContain("LOCAL_NAME", prepared.Request.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Primary, prepared.Request.JsonBody);
        Assert.DoesNotContain("local-only", prepared.Request.JsonBody);
        Assert.Equal(2048, body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        using var videoOnly = JsonDocument.Parse(Prepared(secondPlayer: true, videoOnly: true).Request.JsonBody);
        Assert.Equal(videoOnly.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText(),
            body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText());
        Assert.Contains("ONE-CLIP EVENT-CONTEXT DIAGNOSTIC", prepared.PreviewText);
        var result = GeminiReviewResult.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]), prepared.Request);
        Assert.Single(result.Players);
        var rendered = result.Render([new(TestPlayerIds.Primary, "Local player")]);
        Assert.Contains("retained clip-local shots 1, kills 1, deaths 1", rendered);
        Assert.Contains("LOCAL CAMERA-ATTRIBUTION CAUTION", rendered);
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(
            Review([new { alias = "Player01", moments = Array.Empty<object>() }], withEvidence: false), prepared.Request));
    }

    [Fact]
    public void IsolatedEventContextRejectsMissingMismatchedOrConflictingScope()
    {
        var timeline = new DemoVideoClipTimeline(1, "Player01", 2, 100, 356, [], []);
        Assert.Throws<FormatException>(() => Prepared(clipEventContextOnly: true));
        Assert.Throws<FormatException>(() => Prepared(timeline: timeline, videoOnly: true, clipEventContextOnly: true));
        Assert.Throws<FormatException>(() => Prepared(timeline: timeline with { Alias = "Player02" }, clipEventContextOnly: true));
        Assert.Throws<FormatException>(() => Prepared(timeline: timeline with
        { RetainedShots = [new(357, 257 / 64.0, "ak47")] }, clipEventContextOnly: true));
        Assert.Throws<FormatException>(() => Prepared(timeline: timeline with
        { RetainedShots = [new(164, 0, "ak47")] }, clipEventContextOnly: true));
        var telemetry = GeminiReviewRequest.Create(new([new(TestPlayerIds.Primary, "Local", 2, 2, 0, 0)], [], 0),
            false, GeminiReviewRequest.DefaultModel);
        Assert.Throws<FormatException>(() => GeminiReviewRequest.WithVideo(telemetry,
            [Clip with { EventTimeline = timeline }, Clip with { Number = 2 }], [Video, Video], clipEventContextOnly: true));
        Assert.Contains("positive context only", Prepared(timeline: timeline, clipEventContextOnly: true).Request
            .PlayerContext.Single().MeasurementSummary);
    }

    [Fact]
    public void OneClipModeExcludesTelemetryOtherPlayersAndKeepsRealVideoObservationValidation()
    {
        var prepared = Prepared(secondPlayer: true, videoOnly: true);
        using var body = JsonDocument.Parse(prepared.Request.JsonBody);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(3, parts.GetArrayLength());
        Assert.Equal(Video, Convert.FromBase64String(parts[1].GetProperty("inlineData").GetProperty("data").GetString()!));
        using var descriptor = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
        Assert.Equal(JsonValueKind.Null, descriptor.RootElement.GetProperty("EventTimeline").ValueKind);
        using var roster = JsonDocument.Parse(parts[2].GetProperty("text").GetString()!);
        Assert.Equal(["mode", "players", "videoCitationMap", "limits"], roster.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Player01", Assert.Single(roster.RootElement.GetProperty("players").EnumerateArray())
            .GetProperty("alias").GetString());
        Assert.Contains("Player01: clip 1, round 2, duration 4 seconds",
            Assert.Single(roster.RootElement.GetProperty("videoCitationMap").EnumerateArray()).GetString());
        Assert.DoesNotContain("Player02", prepared.Request.JsonBody);
        Assert.DoesNotContain("LOCAL_NAME", prepared.Request.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Primary, prepared.Request.JsonBody);
        Assert.Equal(2048, body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Contains("ONE-CLIP VIDEO-ONLY DIAGNOSTIC", prepared.PreviewText);
        var response = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
        var result = GeminiReviewResult.Parse(response, prepared.Request);
        Assert.Single(result.Players);
        Assert.Contains("no demo telemetry was supplied", result.Render([new(TestPlayerIds.Primary, "Local player")]));
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(
            Review([new { alias = "Player01", moments = Array.Empty<object>() }], withEvidence: false), prepared.Request));
    }

    [Fact]
    public void VideoOnlyDiagnosticCannotIncludeMoreThanOneClip()
    {
        var telemetry = GeminiReviewRequest.Create(new([new(TestPlayerIds.Primary, "Local", 2, 2, 0, 0)], [], 0),
            false, GeminiReviewRequest.DefaultModel);
        Assert.Throws<FormatException>(() => GeminiReviewRequest.WithVideo(telemetry,
            [Clip, Clip with { Number = 2 }], [Video, Video], videoOnly: true));
    }

    [Fact]
    public void FullVideoRequestCarriesPerAliasClipAndDurationCitationMap()
    {
        var players = new[]
        {
            new DemoGameplayPlayer(TestPlayerIds.Primary, "LOCAL_NAME", 2, 2, 1, 1),
            new DemoGameplayPlayer(TestPlayerIds.Secondary, "OTHER_LOCAL_NAME", 1, 1, 0, 0)
        };
        var telemetry = GeminiReviewRequest.Create(new(players, [], 0), false, GeminiReviewRequest.DefaultModel);
        GeminiVideoContext[] clips =
        [
            Clip with { DurationSeconds = 4.25 },
            Clip with { Number = 2, Alias = "Player02", DurationSeconds = 6.5 }
        ];
        var files = clips.Select(clip => new GeminiVideoPreviewFile(clip.Number,
            $@"C:\local-only\clip-{clip.Number}.mp4", Convert.ToHexString(SHA256.HashData(Video)), Video.Length)).ToArray();
        var prepared = DemoVideoReview.Build(telemetry, clips, [Video, Video], files, SourceHash);
        using var request = JsonDocument.Parse(prepared.Request.JsonBody);
        var parts = request.RootElement.GetProperty("contents")[0].GetProperty("parts");
        using var payload = JsonDocument.Parse(parts[parts.GetArrayLength() - 1].GetProperty("text").GetString()!);
        var map = payload.RootElement.GetProperty("videoCitationMap").EnumerateArray()
            .Select(element => element.GetString()).ToArray();
        Assert.Equal("Player01: clip 1, round 2, duration 4.25 seconds", map[0]);
        Assert.Equal("Player02: clip 2, round 2, duration 6.5 seconds", map[1]);
        Assert.DoesNotContain(TestPlayerIds.Primary, map[0]);
        Assert.DoesNotContain(TestPlayerIds.Secondary, map[1]);
    }

    [Fact]
    public void OwnPlayerEventContextIsPreviewedAndDeathCameraWarningsCannotBeOmittedByTheModel()
    {
        var gameplay = new DemoGameplayReport([new(TestPlayerIds.Primary, "Player01", 1, 1, 0, 0)], [], 0)
        {
            ShotWindows = [new(TestPlayerIds.Primary, 2, 164, "ak47", 0, 0, null, null, null, null, [])],
            CombatEvents = [new(TestPlayerIds.Secondary, TestPlayerIds.Primary, 2, 228, "ak47", false)]
        };
        var timeline = DemoVideoContextAudit.Timeline(gameplay,
            new(1, TestPlayerIds.Primary, "Player01", 2, 100, 356));
        var prepared = Prepared(timeline: timeline);
        using var body = JsonDocument.Parse(prepared.Request.JsonBody);
        using var descriptor = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString()!);
        var events = descriptor.RootElement.GetProperty("EventTimeline");
        Assert.Equal(164, events.GetProperty("RetainedShots")[0].GetProperty("Tick").GetInt32());
        Assert.Equal(2, events.GetProperty("RetainedDeaths")[0].GetProperty("NominalSeconds").GetDouble());
        Assert.DoesNotContain(TestPlayerIds.Primary, prepared.Request.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Secondary, prepared.Request.JsonBody);
        Assert.Contains("EventTimeline", prepared.PreviewText);
        var result = GeminiReviewResult.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]),
            prepared.Request);
        var rendered = result.Render([new(TestPlayerIds.Primary, "Local player")]);
        Assert.Contains("LOCAL CAMERA-ATTRIBUTION CAUTION: clip 1", rendered);
        Assert.Contains("tick 228, nominal +2s", rendered);
        Assert.Contains("transition frame is not verified", rendered);
        Assert.Contains("not verified frame timestamps", rendered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BookmarkEvidenceDistinguishesMissingTimelineFromZeroRetainedEvents(bool supplied)
    {
        var timeline = supplied ? new DemoVideoClipTimeline(1, "Player01", 2, 100, 356, [], []) : null;
        var response = Review([new { alias = "Player01",
            moments = new[] { new { round = 2, tick = 100, clipNumber = 1, seconds = 1.5 } } }]);
        var result = GeminiReviewResult.Parse(response, Prepared(timeline: timeline).Request);
        var rendered = result.Render([new(TestPlayerIds.Primary, "Local player")]);
        Assert.Equal(GeminiPlayerReviewStatus.ReviewSuggested, Assert.Single(result.Players).Status);
        if (supplied)
        {
            Assert.Contains("Retained own-player firearm shots: 0; showing 0.", rendered);
            Assert.Contains("Zero retained events do not prove no firing or survival", rendered);
            Assert.DoesNotContain("Event timeline was not supplied", rendered);
        }
        else
        {
            Assert.Contains("Event timeline was not supplied to this review. This is not a zero-event result.", rendered);
            Assert.DoesNotContain("Retained own-player firearm shots: 0", rendered);
        }
        Assert.DoesNotContain("Recorded shot:", rendered);
        Assert.DoesNotContain("Recorded death:", rendered);
    }

    [Fact]
    public void BookmarkEvidenceListsOnlyCitedOwnClipOnceAndDisclosesBoundedEventSelection()
    {
        var telemetry = GeminiReviewRequest.Create(new([
            new(TestPlayerIds.Primary, "Local", 2, 2, 0, 0),
            new(TestPlayerIds.Secondary, "Other", 2, 2, 0, 0)], [], 0), false, GeminiReviewRequest.DefaultModel);
        var timeline = new DemoVideoClipTimeline(1, "Player01", 2, 100, 356,
            Enumerable.Range(0, 20).Select(i => new DemoVideoTimelineEvent(100 + i * 8, i / 8.0, "ak47")).ToArray(),
            [new(300, 3.125, "awp")]);
        var second = Clip with { Number = 2, Alias = "Player02",
            EventTimeline = new(2, "Player02", 2, 100, 356, [new(356, 4, "deagle")], []) };
        var request = GeminiReviewRequest.WithVideo(telemetry,
            [Clip with { EventTimeline = timeline }, second], [Video, Video]);
        var response = JsonNode.Parse(Review([
            new { alias = "Player01", moments = new object[] {
                new { round = 2, tick = 100, clipNumber = 1, seconds = 0.3 },
                new { round = 2, tick = 100, clipNumber = 1, seconds = 3.9 } } },
            new { alias = "Player02", moments = Array.Empty<object>() }]))!;
        response["players"]![1]!["videoEvidence"] = JsonSerializer.SerializeToNode(new[] {
            new { clipNumber = 2, seconds = 1, basis = "visible", description = "A brick wall is visible." } });
        var result = GeminiReviewResult.Parse(response.ToJsonString(), request);
        var rendered = result.Render([new(TestPlayerIds.Primary, "Local"), new(TestPlayerIds.Secondary, "Other")]);
        Assert.Equal(1, rendered.Split("LOCAL CLIP EVENT EVIDENCE:", StringSplitOptions.None).Length - 1);
        Assert.Contains("clip 1, requested POV Player01; clip-wide context, not matched to AI timestamps.", rendered);
        Assert.Contains("firearm shots: 20; showing 12", rendered);
        Assert.Equal(12, rendered.Split("Recorded shot:", StringSplitOptions.None).Length - 1);
        Assert.Contains("Recorded shot: tick 100, nominal +0s, weapon ak47.", rendered);
        Assert.Contains("Recorded shot: tick 252, nominal +2.375s, weapon ak47.", rendered);
        Assert.Contains("Recorded death: tick 300, nominal +3.125s, weapon awp.", rendered);
        Assert.DoesNotContain("weapon deagle", rendered);
        Assert.DoesNotContain("LOCAL CLIP EVENT EVIDENCE: clip 2", rendered);
        Assert.Equal(GeminiPlayerReviewStatus.ReviewSuggested, result.Players[0].Status);
        Assert.Equal(GeminiPlayerReviewStatus.NoConcernIdentified, result.Players[1].Status);
    }

    [Fact]
    public void PreviewBindsInlineBytesAndSamplingWithoutSendingRosterStringsOrPaths()
    {
        var prepared = Prepared();
        using var body = JsonDocument.Parse(prepared.Request.JsonBody);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        var video = Assert.Single(parts.EnumerateArray().Where(p => p.TryGetProperty("inlineData", out _)));
        Assert.Equal("video/mp4", video.GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Video, Convert.FromBase64String(video.GetProperty("inlineData").GetProperty("data").GetString()!));
        Assert.Equal(10, video.GetProperty("videoMetadata").GetProperty("fps").GetInt32());
        Assert.DoesNotContain("LOCAL_NAME", prepared.Request.JsonBody);
        Assert.DoesNotContain(TestPlayerIds.Primary, prepared.Request.JsonBody);
        Assert.DoesNotContain("local-only", prepared.Request.JsonBody);
        Assert.DoesNotContain(SourceHash, prepared.Request.JsonBody);
        Assert.DoesNotContain(Convert.ToBase64String(Video), prepared.PreviewText);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prepared.Request.JsonBody))), prepared.PreviewText);
        Assert.Contains("Audio tracks verified in 1/1 copies", prepared.PreviewText);
        Assert.Contains("NOT anonymized", prepared.PreviewText);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(Video)), prepared.PreviewText);
        Assert.Contains("seconds", body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").ToString());
    }

    [Fact]
    public void RenderSeparatesVerifiedAudioTrackFromUnverifiedAudibleObservation()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        var evidence = response["players"]![0]!["videoEvidence"]![0]!;
        evidence["basis"] = "audible";
        evidence["description"] = "A brief reload-like sound is audible.";
        var result = GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request);
        var text = result.Render([new(TestPlayerIds.Primary, "Local player")]);
        Assert.Contains("1/1 supplied clips contained a verified audio track", text);
        Assert.Contains("1 audible observation(s), not independently verified", text);
        Assert.Contains("Video observation (Audible): clip 1, +1.5s (approximate)", text);
        Assert.Contains("does not establish that teammate comms were recorded", text);
    }

    [Fact]
    public async Task AutomatedSendPublishesThePreviewOfTheExactRequestBeforeTransport()
    {
        var folder = TemporaryDirectory();
        try
        {
            var prepared = Prepared();
            var file = prepared.Files.Single() with { Path = Path.Combine(folder, "clip-1.mp4") };
            await File.WriteAllBytesAsync(file.Path, Video);
            var snapshot = new GeminiPreparedVideoReview(prepared.Request, prepared.PreviewText, [file], SourceHash);
            var progress = new ProgressRecorder();
            var requests = 0;
            using var client = new GeminiReviewClient(new Handler(async (request, token) =>
            {
                requests++;
                var announced = Assert.Single(progress.Updates);
                Assert.Equal(DemoGeminiStage.Sending, announced.Stage);
                var body = await request.Content!.ReadAsStringAsync(token);
                Assert.Equal(prepared.Request.JsonBody, body);
                Assert.Equal(prepared.PreviewText, announced.RequestPreview);
                Assert.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), announced.RequestPreview);
                Assert.DoesNotContain(Convert.ToBase64String(Video), announced.RequestPreview);
                var response = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = response } } } } }
                }), Encoding.UTF8, "application/json") };
            }));
            var saved = await DemoGeminiAnalysis.SubmitAsync(snapshot, [new(TestPlayerIds.Primary, "Local player")],
                "AQ.synthetic_Gemini_Key_1234567890", client, progress, CancellationToken.None);
            Assert.Equal(1, requests);
            Assert.True(File.Exists(saved.Path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task AutomatedVideoSendRetriesATransientProviderFailure()
    {
        var prepared = Prepared();
        var requests = 0;
        using var client = new GeminiReviewClient(new Handler((_, _) =>
        {
            requests++;
            if (requests == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var response = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = response } } } } }
                }), Encoding.UTF8, "application/json")
            });
        }));

        var result = await client.ReviewAsync(prepared.Request, "AQ.synthetic_Gemini_Key_1234567890",
            CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AutomatedVideoSendRetriesRateLimitWithSameExactRequest()
    {
        var prepared = Prepared();
        var calls = 0;
        string? requestBody = null;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            var body = await request.Content!.ReadAsStringAsync(token);
            if (requestBody is null) requestBody = body;
            else Assert.Equal(requestBody, body);
            if (calls == 1)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            var response = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = response } } } } }
                }), Encoding.UTF8, "application/json")
            };
        }));

        var result = await client.ReviewAsync(prepared.Request, "AQ.synthetic_Gemini_Key_1234567890",
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PersistentVideoRateLimitUsesThreeAttemptsAndFinalFailureIsExplicit()
    {
        var prepared = Prepared();
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));

        var error = await Assert.ThrowsAsync<IOException>(() => client.ReviewAsync(prepared.Request,
            "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None));

        Assert.Equal(3, calls);
        Assert.Contains("after two bounded retries", error.Message);
    }

    [Fact]
    public async Task BatchedSendValidatesEachResponseAndSavesOneCombinedReport()
    {
        var folder = TemporaryDirectory();
        try
        {
            var prepared = Prepared();
            var file = prepared.Files.Single() with { Path = Path.Combine(folder, "clip-1.mp4") };
            await File.WriteAllBytesAsync(file.Path, Video);
            GeminiPreparedVideoReview Snapshot() =>
                new(prepared.Request, prepared.PreviewText, [file], SourceHash);
            var requests = 0;
            using var client = new GeminiReviewClient(new Handler((_, _) =>
            {
                requests++;
                var responseText = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
                return Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = responseText } } } } }
                }), Encoding.UTF8, "application/json") });
            }));

            var saved = await DemoGeminiAnalysis.SubmitBatchesAsync([Snapshot(), Snapshot()],
                [new DemoPlayer(TestPlayerIds.Primary, "Local player")], "AQ.synthetic_Gemini_Key_1234567890",
                client, null, CancellationToken.None);

            Assert.Equal(2, requests);
            Assert.Contains("Batched video review: 2 request(s), 2 clip(s).", saved.Text);
            Assert.Contains("=== Batch 1/2", saved.Text);
            Assert.Contains("=== Batch 2/2", saved.Text);
            Assert.Equal(1, saved.Text.Split("AI DEMO FINAL RESULT",
                StringSplitOptions.None).Length - 1);
            Assert.Equal(1, GeminiReviewResult.FinalSection(saved.Text).Split("Local player (Player01)",
                StringSplitOptions.None).Length - 1);
            Assert.True(File.Exists(saved.Path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FailedLaterBatchReportsPriorDeliveryAndDoesNotSaveACombinedReview()
    {
        var folder = TemporaryDirectory();
        try
        {
            var prepared = Prepared();
            var file = prepared.Files.Single() with { Path = Path.Combine(folder, "clip-1.mp4") };
            await File.WriteAllBytesAsync(file.Path, Video);
            GeminiPreparedVideoReview Snapshot() =>
                new(prepared.Request, prepared.PreviewText, [file], SourceHash);
            var requests = 0;
            using var client = new GeminiReviewClient(new Handler((_, _) =>
            {
                requests++;
                if (requests == 2) return Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.BadRequest));
                var responseText = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
                return Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = responseText } } } } }
                }), Encoding.UTF8, "application/json") });
            }));

            var error = await Assert.ThrowsAsync<FormatException>(() => DemoGeminiAnalysis.SubmitBatchesAsync(
                [Snapshot(), Snapshot()], [new DemoPlayer(TestPlayerIds.Primary, "Local player")],
                "AQ.synthetic_Gemini_Key_1234567890", client, null, CancellationToken.None));

            Assert.Contains("request 2 of 2 failed after 1 earlier request(s) succeeded", error.Message);
            Assert.Contains("cannot be recalled", error.Message);
            Assert.Empty(Directory.GetFiles(folder, "video-review-*.txt"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void VideoGrammarRetainsRequiredFieldsAndTypesWithoutNestedProviderLengthConstraints()
    {
        using var body = JsonDocument.Parse(Prepared().Request.JsonBody);
        var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema");
        Assert.False(schema.GetProperty("properties").TryGetProperty("commentary", out _));
        Assert.Equal(["players"], schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()).ToArray());
        var player = schema.GetProperty("properties").GetProperty("players").GetProperty("items");
        Assert.Contains("videoEvidence", player.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.False(player.GetProperty("properties").TryGetProperty("status", out _));
        Assert.False(player.GetProperty("properties").TryGetProperty("reason", out _));
        Assert.False(player.GetProperty("properties").TryGetProperty("observation", out _));
        var evidence = player.GetProperty("properties").GetProperty("videoEvidence").GetProperty("items");
        Assert.Equal("number", evidence.GetProperty("properties").GetProperty("seconds").GetProperty("type").GetString());
        Assert.DoesNotContain("\"maxLength\"", schema.GetRawText());
        Assert.DoesNotContain("\"maxItems\"", schema.GetRawText());
        Assert.DoesNotContain("\"maximum\"", schema.GetRawText());
    }

    [Theory]
    [InlineData(2, 100, 1, 1.5)]
    [InlineData(2, 100, 99, 1.5)]
    [InlineData(3, 100, 1, 1.5)]
    [InlineData(2, 101, 1, 1.5)]
    [InlineData(2, 100, 1, -0.1)]
    [InlineData(2, 100, 1, 4.1)]
    public void VideoCitationsMustMatchOwnClipStartAndActualDuration(int round, int tick, int number, double seconds)
    {
        var request = Prepared().Request;
        var response = Review([new { alias = "Player01",
            moments = new[] { new { round, tick, clipNumber = number, seconds } } }]);
        if (round == 2 && tick == 100 && number == 1 && seconds == 1.5)
        {
            var result = GeminiReviewResult.Parse(response, request);
            var rendered = result.Render([new(TestPlayerIds.Primary, "Local player")]);
            Assert.Contains("clip 1, +1.5s (approximate)", rendered);
            Assert.Contains("nominal clip-start tick 100", rendered);
            Assert.Contains("NOT anonymized", rendered);
            Assert.Contains("Player observations", rendered);
        }
        else
        {
            var error = Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response, request));
            if (number != 1) Assert.Contains("cited unsupplied clip 99", error.Message);
            else if (seconds == 1.5) Assert.Contains("wrong round/bookmark", error.Message);
            else Assert.Contains("valid range is 0-4 seconds", error.Message);
        }
    }

    [Fact]
    public void PlayersWithoutPovFootageAreInsufficientAndCannotGetAnotherPlayersClip()
    {
        var prepared = Prepared(true);
        var first = new { alias = "Player01", moments = Array.Empty<object>() };
        var withoutPov = new { alias = "Player02", moments = Array.Empty<object>() };
        Assert.Equal(GeminiPlayerReviewStatus.InsufficientData,
            GeminiReviewResult.Parse(Review([first, withoutPov]), prepared.Request).Players[1].Status);
        var wrongPov = new { alias = "Player02", moments = new object[] { new { round = 2, tick = 100, clipNumber = 1, seconds = 1 } } };
        var error = Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(Review<object>([first, wrongPov]), prepared.Request));
        Assert.Contains("belongs to Player01's POV", error.Message);
    }

    [Fact]
    public void VideoCitationsIdentifyWhenAClipBelongsToAnotherPlayersPov()
    {
        var players = new[]
        {
            new DemoGameplayPlayer(TestPlayerIds.Primary, "LOCAL_NAME", 2, 2, 1, 1),
            new DemoGameplayPlayer(TestPlayerIds.Secondary, "OTHER_LOCAL_NAME", 1, 1, 0, 0)
        };
        var telemetry = GeminiReviewRequest.Create(new(players, [], 0), false, GeminiReviewRequest.DefaultModel);
        GeminiVideoContext[] clips = [Clip, Clip with { Number = 2, Alias = "Player02" }];
        var files = new[]
        {
            new GeminiVideoPreviewFile(1, @"C:\local-only\clip-1.mp4", Convert.ToHexString(SHA256.HashData(Video)), Video.Length),
            new GeminiVideoPreviewFile(2, @"C:\local-only\clip-2.mp4", Convert.ToHexString(SHA256.HashData(Video)), Video.Length)
        };
        var prepared = DemoVideoReview.Build(telemetry, clips, [Video, Video], files, SourceHash);
        var response = Review<object>(
        [
            new { alias = "Player01", moments = Array.Empty<object>(), videoEvidence = Array.Empty<object>() },
            new { alias = "Player02", moments = Array.Empty<object>(), videoEvidence = Array.Empty<object>() }
        ]);
        var invalid = JsonNode.Parse(response)!;
        invalid["players"]![0]!["videoEvidence"] = JsonSerializer.SerializeToNode(new[]
        {
            new { clipNumber = 2, seconds = 1.5, basis = "visible", description = "A wall appears in view." }
        });
        var error = Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(invalid.ToJsonString(), prepared.Request));
        Assert.Contains("clip 2, which belongs to Player02's POV", error.Message);
    }

    [Fact]
    public void InlineBinaryBudgetsAcceptBoundariesAndRejectOneByteOver()
    {
        var telemetry = GeminiReviewRequest.Create(new([new(TestPlayerIds.Primary, "Local", 2, 2, 0, 0)], [], 0),
            false, GeminiReviewRequest.DefaultModel);
        var clips = Enumerable.Range(1, 7).Select(number => Clip with { Number = number }).ToArray();
        var bytes = new byte[2_000_000];
        var request = GeminiReviewRequest.WithVideo(telemetry, clips[..6], Enumerable.Repeat(bytes, 6).ToArray());
        Assert.InRange(Encoding.UTF8.GetByteCount(request.JsonBody), 16_000_000, 18_000_000);
        Assert.Throws<FormatException>(() => GeminiReviewRequest.WithVideo(telemetry, [Clip], [new byte[2_000_001]]));
        Assert.Throws<FormatException>(() => GeminiReviewRequest.WithVideo(telemetry, clips,
            Enumerable.Repeat(bytes, 6).Append(new byte[1]).ToArray()));
        var elevenClips = Enumerable.Range(1, 11).Select(number => Clip with { Number = number }).ToArray();
        Assert.Throws<FormatException>(() => GeminiReviewRequest.WithVideo(telemetry, elevenClips,
            Enumerable.Repeat(new byte[1], elevenClips.Length).ToArray()));
    }

    [Fact]
    public async Task ChangedPreviewCopiesInvalidateApprovalMaterial()
    {
        var folder = TemporaryDirectory();
        try
        {
            var file = Path.Combine(folder, "clip.mp4");
            await File.WriteAllBytesAsync(file, Video);
            var prepared = Prepared();
            var snapshot = new GeminiPreparedVideoReview(prepared.Request, prepared.PreviewText,
                [new(1, file, Convert.ToHexString(SHA256.HashData(Video)), Video.Length)], SourceHash);
            await snapshot.VerifyPreviewFilesAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(file, Video.Select(b => (byte)(b ^ 1)).ToArray());
            await Assert.ThrowsAsync<IOException>(() => snapshot.VerifyPreviewFilesAsync(CancellationToken.None));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task DeferredBatchLoadsOnlyItsVerifiedClipsAndMaterializesAnExactRequestBeforeSend()
    {
        var folder = TemporaryDirectory();
        try
        {
            var filePath = Path.Combine(folder, "clip-1.mp4");
            await File.WriteAllBytesAsync(filePath, Video);
            var telemetry = GeminiReviewRequest.Create(new(
                [new DemoGameplayPlayer(TestPlayerIds.Primary, "LOCAL_NAME", 2, 2, 1, 1)],
                [new(TestPlayerIds.Primary, 2, 100, "ak47", 25, 0)], 1), false,
                GeminiReviewRequest.DefaultModel);
            var file = new GeminiVideoPreviewFile(1, filePath,
                Convert.ToHexString(SHA256.HashData(Video)), Video.Length);
            var prepared = DemoVideoReview.BuildDeferred(telemetry, [Clip], [file], SourceHash, false, false);

            Assert.Contains("Estimated JSON request:", prepared.PreviewText);
            var request = await prepared.PrepareRequestAsync(CancellationToken.None);

            Assert.Equal(prepared.RequestJsonBytes, Encoding.UTF8.GetByteCount(request.JsonBody));
            Assert.Contains(Convert.ToBase64String(Video), request.JsonBody);
            Assert.Contains("Exact JSON request:", prepared.PreviewText);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.JsonBody))),
                prepared.GetRequestHash());
            prepared.ReleaseRequest();
            Assert.Throws<InvalidOperationException>(() => _ = prepared.Request);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task SharedReportSavingPreservesBindingsAndDoesNotPublishCanceledResults()
    {
        var folder = TemporaryDirectory();
        try
        {
            var prepared = Prepared();
            var file = prepared.Files.Single() with { Path = Path.Combine(folder, "clip-1.mp4") };
            var snapshot = new GeminiPreparedVideoReview(prepared.Request, prepared.PreviewText,
                [file], SourceHash);
            var result = GeminiReviewResult.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]),
                snapshot.Request);
            var roster = new[] { new DemoPlayer(TestPlayerIds.Primary, "Local player") };
            var saved = await snapshot.SaveResultAsync(result, roster, CancellationToken.None);
            var text = await File.ReadAllTextAsync(saved.Path);
            Assert.StartsWith("Request SHA256: " + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(prepared.Request.JsonBody))), text);
            Assert.Contains($"Review clip 1 SHA256: {file.Sha256}; bytes: {file.Bytes}", text);
            Assert.Contains("Source demo SHA256: " + SourceHash, text);
            Assert.EndsWith(saved.Text, text);
            Assert.Contains("Local player (Player01)", saved.Text);
            Assert.Contains("No AI moment suggested", saved.Text);
            Assert.DoesNotContain("No review concern identified", saved.Text);
            Assert.Contains("not a clean-play finding", saved.Text);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                snapshot.SaveResultAsync(result, roster, new CancellationToken(true)));
            Assert.Single(Directory.GetFiles(folder, "video-review-*.txt"));
            Assert.Empty(Directory.GetFiles(folder, "*.pending"));
            var second = await snapshot.SaveResultAsync(result, roster, CancellationToken.None);
            Assert.NotEqual(saved.Path, second.Path);
            Assert.Equal(text, await File.ReadAllTextAsync(saved.Path));
            Assert.Equal(2, Directory.GetFiles(folder, "video-review-*.txt").Length);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MultiBatchSaveCombinesEveryValidatedResponseAndKeepsBatchProvenance()
    {
        var folder = TemporaryDirectory();
        try
        {
            var prepared = Prepared();
            var file = prepared.Files.Single() with { Path = Path.Combine(folder, "clip-1.mp4") };
            var batch = new GeminiPreparedVideoReview(prepared.Request, prepared.PreviewText, [file], SourceHash);
            var result = GeminiReviewResult.Parse(
                Review([new { alias = "Player01", moments = Array.Empty<object>() }]), batch.Request);
            var saved = await GeminiPreparedVideoReview.SaveBatchResultsAsync([batch, batch], [result, result],
                [new DemoPlayer(TestPlayerIds.Primary, "Local player")], CancellationToken.None);
            var text = await File.ReadAllTextAsync(saved.Path);
            Assert.Contains("Batched video review: 2 request(s), 2 clip(s).", text);
            Assert.Contains("=== Batch 1/2", text);
            Assert.Contains("=== Batch 2/2", text);
            Assert.Equal(2, text.Split("Request SHA256: ").Length - 1);
            var final = GeminiReviewResult.FinalSection(text);
            var detailed = GeminiReviewResult.DetailedSection(text);
            Assert.StartsWith("AI DEMO FINAL RESULT", final);
            Assert.DoesNotContain("Batch metadata", final);
            Assert.DoesNotContain("Request SHA256", final);
            Assert.Equal(1, final.Split("Local player (Player01)", StringSplitOptions.None).Length - 1);
            Assert.Contains("Batch metadata 1/2", detailed);
            Assert.Contains("Request SHA256", detailed);
            Assert.Contains("DETAILED AI REVIEW EVIDENCE", detailed);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("Player movements appear consistent with standard gameplay.")]
    [InlineData("Aim and engagement patterns are within normal parameters.")]
    [InlineData("Gameplay shows no signs of anomalous behavior.")]
    [InlineData("No suspicious behavior observed in the provided clips.")]
    public void GenericNormalityClaimsAreRejectedEvenWithValidVideoTimestamps(string claim)
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        response["players"]![0]!["videoEvidence"]![0]!["description"] = claim;
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
    }

    [Fact]
    public void VideoWithoutTimestampedSceneObservationsIsNotAcceptedAsACompletedReview()
    {
        var response = Review([new { alias = "Player01", moments = Array.Empty<object>() }], false);
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response, Prepared().Request));
    }

    [Fact]
    public void ClipsWithoutAudioStreamsCannotSupportAudibleEvidence()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        response["players"]![0]!["videoEvidence"]![0]!["basis"] = "audible";
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared(hasAudio: false).Request));
    }

    [Theory]
    [InlineData("visible", 99, 1)]
    [InlineData("visible", 1, 4.1)]
    [InlineData("not_discernible", 1, 1)]
    public void ObservationsRequireOwnClipAndDiscernibleCoverageForNoConcern(string basis, int number, double seconds)
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        response["players"]![0]!["videoEvidence"]![0]!["basis"] = basis;
        response["players"]![0]!["videoEvidence"]![0]!["clipNumber"] = number;
        response["players"]![0]!["videoEvidence"]![0]!["seconds"] = seconds;
        if (basis == "not_discernible")
        {
            Assert.Equal(GeminiPlayerReviewStatus.InsufficientData,
                GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request).Players[0].Status);
        }
        else
        {
            var error = Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
            if (number == 99) Assert.Contains("cited unsupplied clip 99", error.Message);
            else Assert.Contains("valid range is 0-4 seconds", error.Message);
        }
    }

    [Fact]
    public async Task MockTransportUsesOneOfficialPostAndPreservesStructuredOutput()
    {
        var calls = 0;
        var prepared = Prepared();
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            Assert.Equal(prepared.Request.JsonBody, await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("AQ.synthetic_Gemini_Key_1234567890", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            var review = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = review } } } } } })) };
        }));
        var result = await client.ReviewAsync(prepared.Request, "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.StartsWith("UNVERIFIED AI VIDEO COMMENTARY", result.Commentary);
        Assert.Equal(GeminiPlayerReviewStatus.NoConcernIdentified, result.Players[0].Status);
    }

    [Fact]
    public async Task InvalidVideoStructureRetriesWithExplicitClipRequirements()
    {
        var calls = 0;
        var prepared = Prepared();
        var valid = Review([new { alias = "Player01", moments = Array.Empty<object>() }]);
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            var body = await request.Content!.ReadAsStringAsync(token);
            var response = calls == 1
                ? """{"players":[{"alias":"Player01","moments":[],"videoEvidence":[]}]}"""
                : valid;
            if (calls == 2)
            {
                Assert.Contains("must each appear exactly once in videoEvidence", body);
                Assert.Contains("clip 1", body);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = response } } } } } })) };
        }));

        var result = await client.ReviewAsync(prepared.Request, "AQ.synthetic_Gemini_Key_1234567890",
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Single(result.Players[0].VideoEvidence!);
    }

    [Fact]
    public void VideoPlayerClassificationsAreNotAcceptedFromTheModel()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        response["players"]![0]!["status"] = "no_concern_identified";
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
    }

    [Fact]
    public void VideoReviewAcceptsOmittedPlayersWithoutClipsInThisBatch()
    {
        var prepared = Prepared(secondPlayer: true);
        var result = GeminiReviewResult.Parse(
            Review([new { alias = "Player01", moments = Array.Empty<object>() }]), prepared.Request);

        var player = Assert.Single(result.Players);
        Assert.Equal("Player01", player.Alias);
        Assert.DoesNotContain("Player02", result.Render(
            [new(TestPlayerIds.Primary, "Local"), new(TestPlayerIds.Secondary, "Other")]));
    }

    [Fact]
    public void VideoReviewStillRequiresEveryPlayerWithSuppliedClips()
    {
        var prepared = Prepared(secondPlayer: true);
        var response = JsonNode.Parse(
            Review([new { alias = "Player02", moments = Array.Empty<object>() }]))!;
        response["players"]![0]!["videoEvidence"] = new JsonArray();

        Assert.Throws<FormatException>(() =>
            GeminiReviewResult.Parse(response.ToJsonString(), prepared.Request));
    }

    [Fact]
    public void VideoSectionsAreAppOwnedAndProviderCommentaryIsRejected()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        var result = GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request);
        Assert.Contains("Video: 1 coverage-selected reconstructed clips in recorded rounds 2;", result.Commentary);
        Assert.Contains("LOCAL LIMITS ALWAYS APPLY", result.Commentary);
        Assert.Contains("Raw mouse input, missing subtick commands", result.Commentary);
        Assert.Contains("Alternative explanations\nLocal context:", result.Commentary.Replace("\r\n", "\n"));
        Assert.DoesNotContain("AI coverage note", result.Commentary);
        response["commentary"] = new JsonObject { ["missingEvidence"] = "No additional evidence is required." };
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
    }

    [Fact]
    public void VideoObservationRequiresExactlyOneObservationPerClip()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01", moments = Array.Empty<object>() }]))!;
        var evidence = response["players"]![0]!["videoEvidence"]!.AsArray();
        Assert.Single(GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request).Players[0].VideoEvidence!);
        evidence.Add(evidence[0]!.DeepClone());
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
    }

    [Fact]
    public void UndiscernibleFootageCannotSupportSuggestedReviewMoments()
    {
        var response = JsonNode.Parse(Review([new { alias = "Player01",
            moments = new[] { new { round = 2, tick = 100, clipNumber = 1, seconds = 1 } } }]))!;
        response["players"]![0]!["videoEvidence"]![0]!["basis"] = "not_discernible";
        Assert.Throws<FormatException>(() => GeminiReviewResult.Parse(response.ToJsonString(), Prepared().Request));
    }

    [Theory]
    [InlineData(@"..\private.mp4")]
    [InlineData(@"C:\private.mp4")]
    public void ManifestPathsCannotSelectOutsideMedia(string path)
    {
        Assert.Throws<FormatException>(() => DemoVideoReview.ResolveClip(Path.GetTempPath(), path));
    }

    [Fact]
    public async Task LegacyUnboundCapturesCannotBeSilentlyAttachedToAnImportedDemo()
    {
        var folder = TemporaryDirectory();
        try
        {
            var attempt = Directory.CreateDirectory(Path.Combine(folder, "legacy")).FullName;
            await File.WriteAllTextAsync(Path.Combine(attempt, "review-manifest.json"), """{"clips":[],"videos":[]}""");
            await Assert.ThrowsAsync<FormatException>(() => DemoVideoReview.FindAsync(folder, SourceHash, CancellationToken.None));
            DemoVideoClip[] clips = [new(1, TestPlayerIds.Primary, "Player01", 2, 100, 356)];
            var manifest = new DemoVideoManifest(3, SourceHash, 100, clips,
                [new(1, "clip.mp4", 4, 1280, 720, true, SourceHash, 100)]);
            DemoVideoReview.Validate(manifest, SourceHash, clips);
            Assert.Throws<FormatException>(() => DemoVideoReview.Validate(manifest, new string('B', 64), clips));
            Assert.Throws<FormatException>(() => DemoVideoReview.Validate(manifest, SourceHash,
                [clips[0] with { StartTick = 101 }]));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Review<T>(T[] players, bool withEvidence = true)
    {
        var body = JsonSerializer.SerializeToNode(new
        {
            players
        })!;
        foreach (var player in body["players"]!.AsArray())
        {
            foreach (var moment in player!["moments"]!.AsArray())
                moment!["reason"] = "Inspect the supplied visible context at this clip-relative timestamp.";
            if (withEvidence)
                player!["videoEvidence"] = player["alias"]!.GetValue<string>() == "Player01"
                    ? JsonSerializer.SerializeToNode(new[] { new { clipNumber = 1, seconds = 1.5, basis = "visible",
                        description = "The crosshair is held beside the edge of a wall in this synthetic response fixture." } })
                    : new JsonArray();
        }
        return body.ToJsonString();
    }

    private static string TemporaryDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CheaterOrNot-video-review-test-" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed class ProgressRecorder : IProgress<DemoGeminiProgress>
    {
        public List<DemoGeminiProgress> Updates { get; } = [];
        public void Report(DemoGeminiProgress value) => Updates.Add(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
