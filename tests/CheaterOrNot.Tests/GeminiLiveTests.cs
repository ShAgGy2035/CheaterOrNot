using System.Text;
using System.Text.Json;
using CheaterOrNot.Core;
using Xunit.Abstractions;

namespace CheaterOrNot.Tests;

public class GeminiLiveTests(ITestOutputHelper output)
{
    [LiveGeminiFact]
    [Trait("Category", "LiveGemini")]
    public async Task ImportsLocalDemoAndGeneratesCommentaryFromGemini()
    {
        var path = Environment.GetEnvironmentVariable("CHEATERORNOT_GEMINI_DEMO");
        Assert.True(!string.IsNullOrWhiteSpace(path) && File.Exists(path),
            "Set CHEATERORNOT_GEMINI_DEMO to an authorized local CS2 .dem recording.");
        var key = SettingsStore.LoadGeminiApiKey();
        Assert.True(key is not null, "Save a readable Gemini key in the app before running the live test.");
        var model = Environment.GetEnvironmentVariable("CHEATERORNOT_GEMINI_MODEL")
            ?? GeminiReviewRequest.DefaultModel;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await using var stream = File.OpenRead(path!);
        var demo = await DemoImporter.ImportAsync(stream, Path.GetFileName(path!), cancellation.Token);
        Assert.NotEmpty(demo.Players);
        var gameplay = Assert.IsType<DemoGameplayReport>(demo.Gameplay);
        var assessed = gameplay.Players.Sum(player => player.AssessedShots);
        Assert.True(assessed > 0, "The supplied demo did not produce any eligible assessed shots.");
        Assert.NotEmpty(gameplay.RoundTelemetry);
        Assert.Equal(assessed, gameplay.RoundTelemetry.Sum(round => round.AssessedShots));
        Assert.NotEmpty(gameplay.ShotWindows);
        Assert.NotEmpty(gameplay.EngagementCoverage);
        Assert.NotEmpty(gameplay.Engagements);
        Assert.Equal(gameplay.TotalEngagements, gameplay.EngagementCoverage.Sum(row => row.Engagements));
        Assert.Equal(gameplay.RoundTelemetry.Sum(row => row.EligibleShots),
            gameplay.EngagementCoverage.Sum(row => row.EligibleShots));
        var preview = GeminiReviewRequest.Create(gameplay, demo.IsIncomplete, model);
        Assert.InRange(Encoding.UTF8.GetByteCount(preview.JsonBody), 1, 64_000);
        using var body = JsonDocument.Parse(preview.JsonBody);
        using var summary = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        var root = summary.RootElement;
        Assert.Equal("cs2-gameplay-telemetry-review-v4", root.GetProperty("schema").GetString());
        Assert.Equal(gameplay.Players.Count, root.GetProperty("playerCoverage").GetProperty("rows").GetArrayLength());
        Assert.NotEmpty(root.GetProperty("engagements").GetProperty("examples").EnumerateArray());
        foreach (var player in demo.Players)
            Assert.False(preview.JsonBody.Contains(player.SteamId, StringComparison.Ordinal),
                "The Gemini preview must not contain roster SteamIDs; nothing was sent.");
        Assert.False(preview.JsonBody.Contains(key!, StringComparison.Ordinal),
            "The Gemini preview must not contain the API key; nothing was sent.");
        output.WriteLine("Local import succeeded: {0} players, {1} assessed shots, {2} selected moments; partial recording: {3}.",
            demo.Players.Count, assessed, gameplay.TotalFindings, demo.IsIncomplete);
        output.WriteLine("Identity-stripped preview prepared: model {0}, {1} UTF-8 bytes.",
            preview.Model, Encoding.UTF8.GetByteCount(preview.JsonBody));
        output.WriteLine("Richer context available: {0} round rows, {1} retained combat events of {2} eligible events.",
            gameplay.RoundTelemetry.Count, gameplay.CombatEvents.Count, gameplay.TotalCombatEvents);
        output.WriteLine("Multi-tick context: {0}/{1} retained/eligible shot windows; {2}/{3} retained/eligible damage callbacks.",
            gameplay.ShotWindows.Count, gameplay.TotalShotWindows, gameplay.DamageEvents.Count, gameplay.TotalDamageEvents);
        output.WriteLine("Longer context: {0}/{1} retained/local engagement groups; {2} round coverage rows; {3} geometry-assessed shots.",
            gameplay.Engagements.Count, gameplay.TotalEngagements, gameplay.EngagementCoverage.Count,
            gameplay.EngagementCoverage.Sum(row => row.GeometryAssessedShots));

        using var client = new GeminiReviewClient();
        await client.ValidateKeyAsync(key!, preview.Model, cancellation.Token);
        output.WriteLine("Live metadata access succeeded; generation quota is not yet established.");
        var result = await client.ReviewAsync(preview, key!, cancellation.Token);
        var commentary = result.Render(demo.Players).ReplaceLineEndings("\n");
        Assert.True(commentary.StartsWith("UNVERIFIED AI COMMENTARY", StringComparison.Ordinal),
            "Gemini generation did not return separately labeled commentary.");
        Assert.True(commentary.Contains("\n\n", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(commentary[(commentary.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]),
            "Gemini generation did not return usable commentary.");
        Assert.Equal(demo.Players.Count, result.Players.Count);
        Assert.All(result.Players, player => Assert.False(string.IsNullOrWhiteSpace(player.Observation)));
        foreach (var heading in new[] { "\nCoverage\n", "\nPlayer observations\n", "\nReview moments with reasons\n",
            "\nAlternative explanations\n", "\nMissing evidence\n" })
            Assert.Contains(heading, commentary);
        Assert.Contains("AI DEMO FINAL RESULT", commentary);
        Assert.True(demo.Players.All(player => commentary.Contains(player.SteamId, StringComparison.Ordinal)),
            "The local final table must include every imported player.");
        output.WriteLine("Live generateContent succeeded: {0} commentary characters. Payload, identities, key and response text were not logged.",
            commentary.Length);
    }
}

public sealed class LiveGeminiFactAttribute : FactAttribute
{
    public LiveGeminiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CHEATERORNOT_LIVE_GEMINI") != "1")
            Skip = "Opt in with scripts\\Test-LiveGemini.cmd: imports a local demo and sends its identity-stripped summary to Google; quota/charges may apply.";
    }
}
