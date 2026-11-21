using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

public sealed class GeminiWebsiteReview
{
    private sealed record Row(int Number, string Source, string Metric, double Value, string Unit,
        int? Matches, string Comparison, double Threshold, string Rule, bool Crossed);
    private sealed record Sample(string Alias, Row[] Rows, string[] Context, ReviewBaseline Baseline);
    private sealed record ReviewFocus(int Row, string Reason);
    private sealed record ReviewBaseline(int AssessedRows, int DistinctMetrics, int CrossedMetrics, bool HasShortSamples);

    internal static readonly IReadOnlySet<string> Metrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "K/D", "Headshot kills", "Win rate", "Kills per round", "Time to damage (TTD)", "Preaim",
        "Aim offset", "HLTV rating", "KAST", "Average damage per round (ADR)", "Accuracy (not headshot percentage)",
        "Utility damage per match", "Recent-vs-lifetime K/D jump", "Weapon accuracy spread", "Map K/D outlier ratio",
        "K/D oscillation blocks", "Opening duel win rate"
    };

    private static readonly Dictionary<string, string> FollowUps = new(StringComparer.Ordinal)
    {
        ["verify_scope"] = "Verify metric definition, sample window, mode and available counts on the original source.",
        ["compare_samples"] = "Compare with a larger sample from the same source, mode and comparable opposition.",
        ["inspect_demo"] = "If there are independent gameplay concerns, watch full demos in context rather than relying on these statistics.",
        ["no_action"] = "No additional action is indicated by these statistics alone; investigate only if separate gameplay concerns exist."
    };

    private readonly Sample[] _samples;
    public string JsonBody { get; }
    public Uri Endpoint { get; }
    public IReadOnlyList<int> ReviewedPlayerNumbers { get; }

    internal string RetryJsonBody(string rejectionReason)
    {
        var root = JsonNode.Parse(JsonBody)?.AsObject() ??
            throw new FormatException("Could not prepare a corrected Gemini request.");
        var parts = root["systemInstruction"]?["parts"]?.AsArray() ??
            throw new FormatException("Could not locate Gemini's system instruction.");
        if (parts.Count != 1 || parts[0]?["text"] is not JsonValue instruction)
            throw new FormatException("Could not update Gemini's system instruction.");
        var original = instruction.GetValue<string>();
        parts[0]!["text"] = original +
            "\n\nCORRECTION REQUIRED: The previous candidate was rejected: " +
            rejectionReason.Replace('\r', ' ').Replace('\n', ' ') +
            " Re-evaluate from the supplied rows and context. Remove unsupported skill, normal-play, matchmaking-quality, " +
            "precision, external-assistance and absence-of-anomaly interpretations. Use only the literal source values, " +
            "their samples, and the permitted review-priority analysis. If the statistics do not establish something, say " +
            "they cannot establish it. Do not use the words normal, standard, typical, expected or commensurate anywhere " +
            "in the report. Do not describe a rank or skill bracket as explaining, predicting or matching the performance.";
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private GeminiWebsiteReview(string model, string json, Sample[] samples, int[] numbers)
    {
        Endpoint = new($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        JsonBody = json;
        _samples = samples;
        ReviewedPlayerNumbers = Array.AsReadOnly(numbers);
    }

    public static bool HasRows(IReadOnlyList<Evidence> evidence, ScreeningRules rules) =>
        evidence.Any(item => Eligible(item) && SourceLabel(item.Url.Host) is not null &&
            Analysis.WebsiteChecks(item, rules).Any(sample => Metrics.Contains(sample.Check.Name)));

    public static GeminiWebsiteReview Create(IReadOnlyList<IReadOnlyList<Evidence>> players, ScreeningRules rules, string model)
    {
        model = GeminiReviewRequest.ValidateModel(model);
        rules.Validate();
        var samples = new List<Sample>();
        var numbers = new List<int>();
        for (var index = 0; index < players.Count; index++)
        {
            var rows = new List<Row>();
            var context = new List<string>();
            foreach (var item in players[index])
            {
                if (!Eligible(item) || SourceLabel(item.Url.Host) is not { } source) continue;
                foreach (var sample in Analysis.WebsiteChecks(item, rules))
                {
                    var check = sample.Check;
                    if (!Metrics.Contains(check.Name)) continue;
                    rows.Add(new(rows.Count + 1, source, check.Name, check.Value,
                        check.Unit switch { "%" => "%", "ms" => "ms", " deg" => "degrees", _ => "" },
                        sample.Matches, check.Comparison switch
                        {
                            MetricComparison.AtLeast => ">=", MetricComparison.Above => ">",
                            MetricComparison.Below => "<", _ => throw new FormatException("Unknown screening comparison.")
                        },
                        check.Threshold, check.RuleSource == "CSTracker website cutoff" ? "website" : "app", check.IsFlagged));
                }
                foreach (var section in item.Sections ?? [])
                {
                    if (section.Title.StartsWith("Recent ", StringComparison.Ordinal) &&
                        section.Title.EndsWith("-day CSTracker trend", StringComparison.Ordinal) &&
                        section.Stats?.Kd is { } kd)
                        context.Add($"{source}: {section.Title.Replace(" CSTracker trend", "", StringComparison.Ordinal)} " +
                            $"K/D {kd:0.##}, {section.Stats.Matches?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} matches.");
                }
                foreach (var rankContext in item.SmurfReviewContext)
                {
                    if (rankContext.StartsWith("CS2 matchmaking rank", StringComparison.Ordinal) ||
                        rankContext.StartsWith("CS2 profile level", StringComparison.Ordinal) ||
                        rankContext.StartsWith("Competitive ", StringComparison.Ordinal) ||
                        rankContext.StartsWith("Premier rating (CSTracker)", StringComparison.Ordinal) ||
                        rankContext.StartsWith("Displayed CS2 service medal", StringComparison.Ordinal))
                        context.Add(source + ": " + rankContext);
                }
            }
            if (rows.Count == 0) continue;
            var baseline = new ReviewBaseline(rows.Count,
                rows.Select(row => row.Metric).Distinct(StringComparer.Ordinal).Count(),
                rows.Where(row => row.Crossed).Select(row => row.Metric).Distinct(StringComparer.Ordinal).Count(),
                rows.Any(row => row.Matches is > 0 and < 100 || row.Matches is null));
            samples.Add(new(string.Create(CultureInfo.InvariantCulture, $"Player{index + 1:00}"),
                rows.ToArray(), context.Distinct(StringComparer.Ordinal).Take(24).ToArray(), baseline));
            numbers.Add(index + 1);
        }
        if (samples.Count == 0) throw new FormatException("The displayed check has no eligible statistical evidence to review.");
        var schema = new
        {
            type = "object", required = new[] { "players" }, properties = new
            {
                players = new { type = "array", items = new
                {
                    type = "object", required = new[] { "alias", "reviewLevel", "headline", "overview",
                        "keySignals", "legitimateExplanations", "focus", "limits", "followUp" }, properties = new
                    {
                        alias = new { type = "string", @enum = samples.Select(sample => sample.Alias).ToArray() },
                        reviewLevel = new { type = "string", @enum = new[] { "Inconclusive", "Low", "Moderate", "High" } },
                        headline = new { type = "string", minLength = 20, maxLength = 180 },
                        overview = new { type = "string", minLength = 40, maxLength = 900 },
                        keySignals = new { type = "array", minItems = 1, maxItems = 6, items = new
                        {
                            type = "string", minLength = 20, maxLength = 350
                        } },
                        legitimateExplanations = new { type = "array", minItems = 1, maxItems = 5, items = new
                        {
                            type = "string", minLength = 20, maxLength = 300
                        } },
                        focus = new
                        {
                            type = "array", minItems = 1, maxItems = 3, items = new
                            {
                                type = "object", required = new[] { "row", "reason" }, properties = new
                                {
                                    row = new { type = "integer" },
                                    reason = new { type = "string", minLength = 20, maxLength = 500 }
                                }
                            }
                        },
                        limits = new { type = "string", minLength = 20, maxLength = 700 },
                        followUp = new { type = "string", @enum = FollowUps.Keys.ToArray() }
                    }
                } }
            }
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = """
                You are a CS2 statistics analyst writing an evidence-review report for a community administrator.
                Produce a tailored report for each alias; do not reproduce the local report's layout or merely restate every row.
                Use reviewLevel only as a manual-review priority, not a cheating probability: Inconclusive for insufficient
                or highly uncertain data, Low for no crossings without material conflicting context, Moderate for one or a
                few crossings/strong uncertainties worth reviewing, and High only when multiple distinct warning-line metrics
                cross with adequate samples or independently concerning context. Never use Blatant or a percentage probability.
                Explain the actual pattern in headline and overview. In keySignals, cite specific values, sample sizes, windows
                and source scopes. In legitimateExplanations, give concrete alternatives such as role, weapon choice, maps,
                opposition, modes, short samples, rank mismatch or normal performance variance. In focus, cite 1-3 row numbers
                and explain exactly why each matters. Explicitly discuss limitations and choose one followUp action.
                Use only supplied rows/context/baseline. Never invent data, combine non-comparable samples, infer intent,
                identify a cheat, accuse a person, or say someone cheated or is clean. Distinguish provider-reported values
                from app threshold comparisons. Historical bans, rank and service-medal data are context, not statistical proof.
                Never call a value "normal", "expected", "baseline", "human-like", or "lower/intermediate skill" unless an
                explicitly supplied calibrated population range supports that exact statement. A value merely below a warning
                cutoff means only "did not cross this heuristic cutoff"; it is not evidence of natural play, absence of
                assistance, precision limits, or a person's true skill bracket. Never use "suggests no evidence of assistance"
                or equivalent conclusions from aggregate statistics. Prefer "the supplied data does not flag..." and state that
                these aggregates cannot evaluate individual-round mechanics, visibility, timing, intent, or demonstrations.
                Because no calibrated population range is supplied, do not use the words normal, standard, typical, expected
                or commensurate anywhere in the report, and do not claim that a rank explains or matches the measured performance.
                Treat "Low" review priority as a recommendation for no statistical escalation, not as a behavioral finding.
                """ } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(samples, options) } } } },
            generationConfig = new { responseMimeType = "application/json", responseJsonSchema = schema,
                maxOutputTokens = (int)Math.Min(16000L, 3072L + samples.Count * 1000L) }
        }, options);
        if (Encoding.UTF8.GetByteCount(json) > 64_000)
            throw new FormatException("The displayed batch is too large for one Gemini stats review. Check fewer players and try again; nothing was sent.");
        return new(model, json, samples.ToArray(), numbers.ToArray());
    }

    internal string Render(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        RequireFields(root, "players");
        var players = root.GetProperty("players");
        if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() != _samples.Length)
            throw new FormatException("Gemini did not return one stats review per supplied alias.");
        var selected = new Dictionary<string, (string ReviewLevel, string Headline, string Overview, string[] KeySignals,
            string[] LegitimateExplanations, ReviewFocus[] Focus, string Limits, string FollowUp)>(StringComparer.Ordinal);
        foreach (var player in players.EnumerateArray())
        {
            RequireFields(player, "alias", "reviewLevel", "headline", "overview", "keySignals",
                "legitimateExplanations", "focus", "limits", "followUp");
            var alias = player.GetProperty("alias");
            var reviewLevel = player.GetProperty("reviewLevel");
            var headline = player.GetProperty("headline");
            var overview = player.GetProperty("overview");
            var keySignals = player.GetProperty("keySignals");
            var legitimateExplanations = player.GetProperty("legitimateExplanations");
            var limits = player.GetProperty("limits");
            var followUp = player.GetProperty("followUp");
            var focus = player.GetProperty("focus");
            var sample = alias.ValueKind == JsonValueKind.String ? _samples.FirstOrDefault(x => x.Alias == alias.GetString()) : null;
            if (sample is null ||
                reviewLevel.ValueKind != JsonValueKind.String ||
                reviewLevel.GetString() is not ("Inconclusive" or "Low" or "Moderate" or "High") ||
                headline.ValueKind != JsonValueKind.String ||
                headline.GetString() is not { Length: >= 20 and <= 180 } ||
                overview.ValueKind != JsonValueKind.String ||
                overview.GetString() is not { Length: >= 40 and <= 900 } ||
                !TryStrings(keySignals, 1, 6, 20, 350, out var signals) ||
                !TryStrings(legitimateExplanations, 1, 5, 20, 300, out var explanations) ||
                limits.ValueKind != JsonValueKind.String || limits.GetString() is not { Length: >= 20 and <= 700 } ||
                followUp.ValueKind != JsonValueKind.String || !FollowUps.ContainsKey(followUp.GetString()!) ||
                focus.ValueKind != JsonValueKind.Array || focus.GetArrayLength() is < 1 or > 3)
                throw new FormatException("Gemini returned an invalid stats review selection.");
            var focusRows = new List<ReviewFocus>();
            foreach (var item in focus.EnumerateArray())
            {
                RequireFields(item, "row", "reason");
                var row = item.GetProperty("row");
                var reason = item.GetProperty("reason");
                if (row.ValueKind != JsonValueKind.Number || !row.TryGetInt32(out var n) ||
                    n < 1 || n > sample.Rows.Length || focusRows.Any(existing => existing.Row == n) ||
                    reason.ValueKind != JsonValueKind.String || reason.GetString() is not { Length: >= 20 and <= 500 })
                    throw new FormatException("Gemini cited an unknown, duplicate or malformed stats row.");
                focusRows.Add(new(n, reason.GetString()!));
            }
            if (!selected.TryAdd(sample.Alias, (reviewLevel.GetString()!, headline.GetString()!,
                overview.GetString()!, signals, explanations, focusRows.ToArray(),
                limits.GetString()!, followUp.GetString()!)))
                throw new FormatException("Gemini repeated a stats review alias.");
        }
        var text = new StringBuilder("AI-ASSISTED STATISTICAL REVIEW | Not a cheating probability or verdict\n");
        text.AppendLine("The model authors this interpretation from the completed local samples. Review levels describe manual-review priority, not guilt, innocence, skill classification or detector confidence.");
        foreach (var sample in _samples)
        {
            var choice = selected[sample.Alias];
            text.AppendLine($"\n{sample.Alias} | {choice.Headline.Trim()}");
            text.AppendLine($"REVIEW LEVEL: {choice.ReviewLevel.ToUpperInvariant()}");
            text.AppendLine(choice.Overview.Trim());
            text.AppendLine("\nKey signals:");
            foreach (var signal in choice.KeySignals) text.AppendLine("- " + signal.Trim());
            text.AppendLine("\nLegitimate explanations:");
            foreach (var explanation in choice.LegitimateExplanations) text.AppendLine("- " + explanation.Trim());
            text.AppendLine("\nHighlighted evidence:");
            foreach (var selectedFocus in choice.Focus)
            {
                var row = sample.Rows[selectedFocus.Row - 1];
                var size = row.Matches is null ? "unknown match count" : $"{row.Matches} matches" + (row.Matches < 100 ? " (short sample)" : "");
                text.AppendLine(FormattableString.Invariant(
                    $"- {row.Source} | {row.Metric}: {Analysis.NumberNearThreshold(row.Value, row.Threshold, row.Unit)} | {size} | {(row.Crossed ? "crossed" : "did not cross")} {row.Rule} rule {row.Comparison} {row.Threshold:G}{row.Unit}."));
                text.AppendLine("  Interpretation: " + selectedFocus.Reason.Trim());
            }
            text.AppendLine("Limits: " + choice.Limits.Trim());
            text.AppendLine("Recommendation: " + FollowUps[choice.FollowUp]);
            text.AppendLine($"Local screening baseline: {sample.Baseline.CrossedMetrics} of " +
                $"{sample.Baseline.DistinctMetrics} distinct assessed metric types crossed app/website warning lines across " +
                $"{sample.Baseline.AssessedRows} source rows. Baseline calculations remain separate from this AI interpretation.");
        }
        var narrative = text.ToString();
        ValidateNarrativeClaims(narrative);
        return narrative;
    }

    private static bool TryStrings(JsonElement element, int minItems, int maxItems, int minLength,
        int maxLength, out string[] values)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < minItems ||
            element.GetArrayLength() > maxItems) return false;
        var items = new List<string>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
                text.Length < minLength || text.Length > maxLength) return false;
            items.Add(text);
        }
        values = items.ToArray();
        return true;
    }

    private static void ValidateNarrativeClaims(string text)
    {
        var bannedTerms = new[]
        {
            @"\b(?:normal|standard|typical|expected|commensurate)\b",
            @"\b(?:lower|higher|entry|intermediate|elite)[-\s]+(?:tier|skill)(?:[-\s]+bracket)?\b",
            @"\bcurrent skill (?:level|bracket)\b|\bobserved (?:rank|skill) bracket\b",
            @"\baligned with (?:the player's )?(?:recorded )?(?:skill bracket|rank)\b",
            @"\b(?:mechanical|precision|performance) anomal(?:y|ies)\b",
            @"\bno sudden,? unexplained spikes\b|\black of .* oscillation.*indicat",
            @"\b(?:no|lack of) (?:evidence|signs) of (?:assistance|mechanical assistance|anomalous (?:behavior|performance)|abnormal behavior)\b",
            @"\bsuggests? (?:effective matchmaking|a lack of statistical distortion|no external assistance)\b"
        };
        if (bannedTerms.Any(pattern => Regex.IsMatch(text, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
            throw new FormatException(
                "Gemini generated an unsupported normal-play, skill-bracket, matchmaking-quality or absence-of-assistance claim. " +
                "That AI report was not accepted.");
    }

    private static void AppendLimits(StringBuilder text, IEnumerable<Row> focus, Row[] all)
    {
        var prioritized = focus.Concat(all).ToArray();
        var unknown = prioritized.FirstOrDefault(row => row.Matches is null);
        var shortSample = prioritized.FirstOrDefault(row => row.Matches is > 0 and < 100);
        var limits = new List<string>();
        if (unknown is not null)
            limits.Add($"{unknown.Source}'s {unknown.Metric} has no verified match count, so its sample size cannot be judged");
        if (shortSample is not null)
            limits.Add($"{shortSample.Source}'s {shortSample.Metric} covers {shortSample.Matches} matches and may vary in a larger sample");
        if (limits.Count == 0)
            limits.Add("the reviewed statistics do not show what happened in individual rounds");
        text.AppendLine("Limits: " + string.Join("; ", limits) + ". Panels may cover different modes, opponents or periods.");
    }

    private static void RequireFields(JsonElement item, params string[] expected)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new FormatException("Gemini returned unexpected, duplicate or missing stats review fields.");
    }

    private static bool Eligible(Evidence item) => item.Status != "Unsupported automatic source";

    private static string? SourceLabel(string host) => host switch
    {
        "leetify.com" or "api.cs-prod.leetify.com" => "Leetify public website",
        "api-public.cs-prod.leetify.com" => "Leetify API",
        "csst.at" => "csst.at public page",
        "cstracker.gg" => "cstracker.gg public page",
        "api.steampowered.com" => "Steam Web API",
        "open.faceit.com" => "FACEIT Data API",
        _ => null
    };
}
