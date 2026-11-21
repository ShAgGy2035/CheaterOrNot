using System.Net;
using System.Text;
using System.Text.Json;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class GeminiWebsiteReviewTests
{
    [Fact]
    public async Task WebsiteReviewRetriesOneTransientServiceFailureWithoutChangingTheRequest()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(30, Kd: 1.1));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var calls = 0;
        string? firstRequest = null;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            var body = await request.Content!.ReadAsStringAsync(token);
            if (firstRequest is null) firstRequest = body;
            else Assert.Equal(firstRequest, body);
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Reply("""
                {"players":[{
                  "alias":"Player01",
                  "reviewLevel":"Low",
                  "headline":"The supplied sample does not cross its K/D warning line",
                  "overview":"This player has one available K/D sample and it remains below the configured warning line. The review therefore stays focused on uncertainty rather than accusation.",
                  "keySignals":["The csst.at K/D value was 1.1 across 30 matches and did not cross the app threshold."],
                  "legitimateExplanations":["Opposition, role, mode and a relatively short sample can all affect the displayed ratio."],
                  "focus":[{"row":1,"reason":"This is the only supplied row and it did not cross the configured K/D warning line."}],
                  "limits":"Only one 30-match source sample is available, so the result has limited scope.",
                  "followUp":"verify_scope"
                }]}
                """);
        }));

        var report = await client.ReviewWebsiteStatsAsync(review,
            "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Contains("REVIEW LEVEL: LOW", report);
    }

    [Fact]
    public async Task WebsiteReviewReportsWhenTransientServiceFailureSurvivesOneRetry()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(30, Kd: 1.1));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var calls = 0;
        using var client = new GeminiReviewClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));

        var error = await Assert.ThrowsAsync<IOException>(() => client.ReviewWebsiteStatsAsync(review,
            "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None));

        Assert.Equal(3, calls);
        Assert.Contains("after two bounded retries", error.Message);
    }

    [Fact]
    public async Task WebsiteReviewCorrectsARejectedNarrativeOnTheSingleRetry()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(100, Kd: .5));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var calls = 0;
        string? original = null;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            calls++;
            var body = await request.Content!.ReadAsStringAsync(token);
            if (calls == 1)
            {
                original = body;
                return Reply("""
                    {"players":[{
                      "alias":"Player01",
                      "reviewLevel":"Low",
                      "headline":"The player's rank explains the normal performance range",
                      "overview":"These values fall within normal parameters and are typical for the recorded skill bracket, without any statistical anomalies.",
                      "keySignals":["K/D was 0.5 across 100 matches and did not cross the configured warning line."],
                      "legitimateExplanations":["Performance variance is expected across source samples and game modes."],
                      "focus":[{"row":1,"reason":"The K/D value remained below the configured warning line over 100 matches."}],
                      "limits":"One aggregate sample cannot evaluate individual-round mechanics or intent.",
                      "followUp":"no_action"
                    }]}
                    """);
            }
            Assert.NotEqual(original, body);
            Assert.Contains("CORRECTION REQUIRED", body);
            return Reply("""
                {"players":[{
                  "alias":"Player01",
                  "reviewLevel":"Low",
                  "headline":"The supplied K/D sample did not cross its warning line",
                  "overview":"The available sample reports K/D 0.5 across 100 matches. That value did not cross the configured heuristic threshold; the aggregate cannot establish round-level mechanics or intent.",
                  "keySignals":["K/D was 0.5 across 100 matches and did not cross the app threshold."],
                  "legitimateExplanations":["Mode, map, opponent and role differences can affect a source-reported K/D value."],
                  "focus":[{"row":1,"reason":"This is the only supplied metric row, and its value is below the configured K/D warning line."}],
                  "limits":"Only one 100-match source sample is available, and aggregate statistics cannot establish behavior in individual rounds.",
                  "followUp":"no_action"
                }]}
                """);
        }));

        var report = await client.ReviewWebsiteStatsAsync(review,
            "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Contains("REVIEW LEVEL: LOW", report);
        Assert.DoesNotContain("normal performance", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewsOnlyAssessableRowsFromTheCompletedCheckAndRendersLocalValues()
    {
        var id = TestPlayerIds.Primary;
        var csstat = new Evidence("private-label", new Uri($"https://csst.at/profile/{id}?opaque=private-query"),
            "retrieved", new PlayerStats(10, Kd: 2.78, Kpr: 1.46), Note: "private-note",
            Sections: [new("private-section", "private-raw", new PlayerStats(3, Kd: 9))],
            Cautions: ["private-caution"]) { DisplayName = "private-name" };
        var tracker = new Evidence("private-label", new Uri($"https://cstracker.gg/players/{id}"), "retrieved",
            Sections: [new("private-section", "private-raw", new PlayerStats(5, Kd: 3), Checks:
                [new("HLTV rating", 2.36, 1.5, Comparison: MetricComparison.Above, RuleSource: "CSTracker website cutoff"),
                 new("KAST", 87.8, 85, "%", MetricComparison.Above, "CSTracker website cutoff"),
                 new("Average damage per round (ADR)", 156.4, 110, Comparison: MetricComparison.Above, RuleSource: "CSTracker website cutoff"),
                 new("private-metric", 999, 1)])]);
        var supportedChecks = tracker.Sections![0].Checks!.Take(3).ToArray();
        var cleanTracker = tracker with { Sections = [tracker.Sections[0] with { Checks = supportedChecks }] };
        var rules = new ScreeningRules();
        var original = Analysis.RenderFinal(id, [csstat, cleanTracker], rules);
        Assert.Contains("5 screening indicator type(s)", original);
        var review = GeminiWebsiteReview.Create([[], [csstat, tracker]], rules, GeminiReviewRequest.DefaultModel);
        Assert.Equal([2], review.ReviewedPlayerNumbers);
        var count = 0;
        using var client = new GeminiReviewClient(new Handler(async (request, token) =>
        {
            count++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            Assert.Single(request.Headers.GetValues("x-goog-api-key"));
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal(review.JsonBody, body);
            Assert.DoesNotContain("private-", body);
            Assert.DoesNotContain(id, body);
            Assert.DoesNotContain("/profile/", body);
            Assert.Contains("Never call a value", body);
            using var requestDoc = JsonDocument.Parse(body);
            using var payload = JsonDocument.Parse(requestDoc.RootElement.GetProperty("contents")[0]
                .GetProperty("parts")[0].GetProperty("text").GetString()!);
            var rows = payload.RootElement[0].GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(6, rows.Length);
            Assert.All(rows, row => Assert.True(row.GetProperty("crossed").GetBoolean()));
            Assert.Equal(new[] { 10, 10, 5, 5, 5, 5 }, rows.Select(row => row.GetProperty("matches").GetInt32()));
            Assert.Equal(2, rows.Count(row => row.GetProperty("metric").GetString() == "K/D"));
            Assert.Equal(1.46, Assert.Single(rows.Where(row => row.GetProperty("metric").GetString() == "Kills per round"))
                .GetProperty("value").GetDouble());
            return Reply("""
                {"players":[{
                  "alias":"Player02",
                  "reviewLevel":"High",
                  "headline":"Multiple unusually high results warrant a same-scope comparison",
                  "overview":"Across the supplied samples this player has several high results, but the samples are short and come from different scopes. The result warrants comparison rather than classification.",
                  "keySignals":[
                    "Five distinct warning-line metric types crossed across the supplied source samples.",
                    "The csst.at Kills per round value was 1.46 across 10 matches."
                  ],
                  "legitimateExplanations":[
                    "The cited samples are short, so a hot streak or opponent mix can have a large effect."
                  ],
                  "focus":[
                    {"row":2,"reason":"This value stands out against the app threshold, but the short sample makes a single run unusually influential."},
                    {"row":4,"reason":"This provider-specific rating crossed its cutoff in a tiny panel and should be checked against the original source scope."}
                  ],
                  "limits":"Two of the cited panels contain only a few matches, and the providers may cover different modes and opponents.",
                  "followUp":"compare_samples"
                }]}
                """);
        }));
        var report = await client.ReviewWebsiteStatsAsync(review, "AQ.synthetic_Gemini_Key_1234567890", CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Contains("Player02", report);
        Assert.Contains("REVIEW LEVEL: HIGH", report);
        Assert.Contains("several high results", report);
        Assert.Contains("Legitimate explanations:", report);
        Assert.Contains("Kills per round: 1.46 | 10 matches (short sample) | crossed app rule >= 1", report);
        Assert.Contains("HLTV rating: 2.36 | 5 matches (short sample) | crossed website rule > 1.5", report);
        Assert.Contains("Interpretation: This value stands out", report);
        Assert.Contains("Limits: Two of the cited panels", report);
        Assert.Contains("Recommendation:", report);
        Assert.Equal(original, Analysis.RenderFinal(id, [csstat, cleanTracker], rules));
    }

    [Fact]
    public void NoCrossingsExplainsSampleLimitsWithoutRepeatingStatsOrSuggestingEscalation()
    {
        var tracker = new Evidence("fixture", new Uri("https://cstracker.gg"), "retrieved",
            new PlayerStats(315, Kd: 0.4978723404255319), Sections:
            [new("Recent analysis", "", new PlayerStats(90), Checks:
                [new("Time to damage (TTD)", 739, 600, "ms", MetricComparison.Below, "CSTracker website cutoff")]),
             new("Weapon accuracy variance", "", Checks:
                [new("Weapon accuracy spread", 6, 3, "%", MetricComparison.Below)])]);
        var review = GeminiWebsiteReview.Create([[tracker]], new ScreeningRules(), GeminiReviewRequest.DefaultModel);
        var report = review.Render("""
            {"players":[{
              "alias":"Player01",
              "reviewLevel":"Low",
              "headline":"No warning-line crossing in the supplied samples",
              "overview":"The supplied checks contain no warning-line crossing. The most useful issue is sample uncertainty rather than a suspicious pattern in these statistics.",
              "keySignals":[
                "K/D was below the configured warning line in the largest supplied sample.",
                "The reviewed provider metrics did not cross their stated cutoffs."
              ],
              "legitimateExplanations":[
                "Mode, opponent selection and match-to-match variation can explain the differences between panels."
              ],
              "focus":[
                {"row":1,"reason":"This is the largest assessed K/D sample and remains well below the configured warning line."},
                {"row":2,"reason":"The timing metric comes from a source-defined 90-match panel, which should be interpreted within that source's scope."},
                {"row":3,"reason":"Weapon spread has no independently verified match count, so it should not be compared like the match-based samples."}
              ],
              "limits":"The provider panels may cover different periods, and one cited value has no verified match count.",
              "followUp":"compare_samples"
            }]}
            """);
        Assert.Contains("no warning-line crossing", report);
        Assert.Contains("REVIEW LEVEL: LOW", report);
        Assert.Contains("sample uncertainty", report);
        Assert.Contains("largest assessed K/D sample", report);
        Assert.Contains("The provider panels may cover different periods", report);
        Assert.DoesNotContain("0.497872", report);
    }

    [Fact]
    public void RejectsUnsupportedNormalPlayAndSkillBracketClaims()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(100, Kd: 1.1));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var response = """
            {"players":[{
              "alias":"Player01",
              "reviewLevel":"Low",
              "headline":"The performance appears aligned with the recorded skill bracket",
              "overview":"These values fall within normal parameters and suggest no evidence of anomalous behavior for this player's observed rank.",
              "keySignals":["The supplied K/D value remained below its configured warning line across 100 matches."],
              "legitimateExplanations":["Natural performance variance can explain the values in the supplied source sample."],
              "focus":[{"row":1,"reason":"The K/D value did not cross the configured warning line in this 100-match source sample."}],
              "limits":"Only one source sample is available, so the data cannot describe individual-round mechanics or intent.",
              "followUp":"no_action"
            }]}
            """;

        Assert.Throws<FormatException>(() => review.Render(response));
    }

    [Fact]
    public void RejectsLiveSkillRankAndStandardOutcomePhrasing()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(334, Kd: .5));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var response = """
            {"players":[{
              "alias":"Player01",
              "reviewLevel":"Low",
              "headline":"The performance remains below all supplied warning thresholds",
              "overview":"The metrics are consistent with lower-skill bracket play and show a standard distribution of outcomes without mechanical anomalies.",
              "keySignals":["The K/D value is 0.5 across 334 matches and did not cross the configured warning line."],
              "legitimateExplanations":["The Silver I rank can provide context for interpreting the aggregate source values."],
              "focus":[{"row":1,"reason":"The K/D value remained below the configured warning line over the available 334-match sample."}],
              "limits":"Aggregate provider statistics cannot evaluate individual-round mechanics, mouse movement, intent or visibility.",
              "followUp":"no_action"
            }]}
            """;

        Assert.Throws<FormatException>(() => review.Render(response));
    }

    [Fact]
    public void RejectsTermsFromLatestLiveAiOutput()
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved",
            new PlayerStats(334, Kd: .5));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(),
            GeminiReviewRequest.DefaultModel);
        var response = """
            {"players":[{
              "alias":"Player01",
              "reviewLevel":"Low",
              "headline":"Performance remains below the configured warning lines",
              "overview":"The metrics indicate output commensurate with the reported competitive rank and within a standard engagement range.",
              "keySignals":["The K/D value did not cross the configured threshold over 334 matches."],
              "legitimateExplanations":["Performance variation can occur across maps, modes and opponents."],
              "focus":[{"row":1,"reason":"The observed rank provides standard deviation context for the headshot percentage."}],
              "limits":"Aggregate values cannot establish individual-round mechanics, intent or visibility.",
              "followUp":"no_action"
            }]}
            """;

        Assert.Throws<FormatException>(() => review.Render(response));
    }

    [Theory]
    [InlineData("""{"players":[{"alias":"Player02","reviewLevel":"Low","headline":"A sufficiently long review headline.","overview":"This overview is deliberately long enough for validation.","keySignals":["This key signal is deliberately long enough."],"legitimateExplanations":["This explanation is deliberately long enough."],"focus":[{"row":1,"reason":"This reason is deliberately long enough for validation."}],"limits":"The sample limits are deliberately long enough for validation.","followUp":"verify_scope"}]}""")]
    [InlineData("""{"players":[{"alias":"Player01","reviewLevel":"Low","headline":"A sufficiently long review headline.","overview":"This overview is deliberately long enough for validation.","keySignals":["This key signal is deliberately long enough."],"legitimateExplanations":["This explanation is deliberately long enough."],"focus":[{"row":2,"reason":"This reason is deliberately long enough for validation."}],"limits":"The sample limits are deliberately long enough for validation.","followUp":"verify_scope"}]}""")]
    [InlineData("""{"players":[{"alias":"Player01","reviewLevel":"Low","headline":"A sufficiently long review headline.","overview":"This overview is deliberately long enough for validation.","keySignals":["This key signal is deliberately long enough."],"legitimateExplanations":["This explanation is deliberately long enough."],"focus":[{"row":1,"reason":"This reason is deliberately long enough for validation."},{"row":1,"reason":"This duplicate reason is long enough for validation."}],"limits":"The sample limits are deliberately long enough for validation.","followUp":"verify_scope"}]}""")]
    [InlineData("""{"players":[{"alias":"Player01","reviewLevel":"Low","headline":"A sufficiently long review headline.","overview":"This overview is deliberately long enough for validation.","keySignals":["This key signal is deliberately long enough."],"legitimateExplanations":["This explanation is deliberately long enough."],"focus":[{"row":1,"reason":"This reason is deliberately long enough for validation."}],"limits":"The sample limits are deliberately long enough for validation.","followUp":"verify_scope","verdict":"clean"}]}""")]
    [InlineData("""{"players":[{"alias":"Player01","reviewLevel":"Low","headline":"A sufficiently long review headline.","overview":"This overview is deliberately long enough for validation.","keySignals":["This key signal is deliberately long enough."],"legitimateExplanations":["This explanation is deliberately long enough."],"focus":[{"row":1,"reason":"This reason is deliberately long enough for validation."}],"limits":"The sample limits are deliberately long enough for validation.","followUp":"accuse"}]}""")]
    public void RejectsUnsupportedSelectionsAndVerdicts(string response)
    {
        var evidence = new Evidence("fixture", new Uri("https://csst.at"), "retrieved", new PlayerStats(10, Kd: 1.1));
        var review = GeminiWebsiteReview.Create([[evidence]], new ScreeningRules(), GeminiReviewRequest.DefaultModel);
        Assert.Throws<FormatException>(() => review.Render(response));
    }

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } }
        }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
