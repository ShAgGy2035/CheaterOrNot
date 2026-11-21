using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

public enum GeminiPlayerReviewStatus { ReviewSuggested, NoConcernIdentified, InsufficientData }

public sealed record GeminiReviewMoment(int Round, int Tick, int? ClipNumber = null, double? ClipSeconds = null, string? Reason = null);
public enum GeminiVideoEvidenceBasis { Visible, Audible, NotDiscernible }
public sealed record GeminiVideoObservation(int ClipNumber, double Seconds, GeminiVideoEvidenceBasis Basis, string Description);

public sealed record GeminiPlayerReview(string Alias, GeminiPlayerReviewStatus Status, string Reason, string? Observation,
    IReadOnlyList<GeminiReviewMoment> Moments, IReadOnlyList<GeminiVideoObservation>? VideoEvidence = null);

internal sealed record GeminiReviewPlayerContext(string Alias, string SteamId, int AssessedShots, string MeasurementSummary,
    bool HasDetailedMeasurements)
{
    public HashSet<GeminiReviewMoment> Moments { get; } = [];
}

public sealed class GeminiReviewResult
{
    private readonly IReadOnlyList<GeminiReviewPlayerContext> _context;
    private readonly bool _video;
    public string Commentary { get; }
    public IReadOnlyList<GeminiPlayerReview> Players { get; }
    internal string RawJson { get; private init; } = "";

    private GeminiReviewResult(string commentary, IReadOnlyList<GeminiPlayerReview> players,
        IReadOnlyList<GeminiReviewPlayerContext> context, bool video) =>
        (Commentary, Players, _context, _video) = (commentary, players, context, video);

    internal static GeminiReviewResult Parse(string text, GeminiReviewRequest request)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException)
        {
            throw new FormatException("Gemini did not return valid structured review JSON. No player results were accepted.");
        }
        using (document)
        {
            var root = document.RootElement;
            var video = request.VideoClips.Count > 0;
            Fields(root, video ? ["players"] : ["commentary", "players"]);
            string? coverage = null, alternatives = null, missing = null;
            if (!video)
            {
                var commentary = root.GetProperty("commentary");
                Fields(commentary, "coverage", "alternativeExplanations", "missingEvidence");
                coverage = Text(commentary.GetProperty("coverage"), 2000, true);
                alternatives = Text(commentary.GetProperty("alternativeExplanations"), 2000, true);
                missing = Text(commentary.GetProperty("missingEvidence"), 2000, true);
            }
            var players = root.GetProperty("players");
            var requiredContexts = video
                ? request.PlayerContext.Where(context =>
                    request.VideoClips.Any(clip => clip.Alias == context.Alias)).ToArray()
                : request.PlayerContext;
            var validPlayerCounts = new HashSet<int> { requiredContexts.Count, request.PlayerContext.Count };
            if (players.ValueKind != JsonValueKind.Array ||
                !validPlayerCounts.Contains(players.GetArrayLength()))
                throw new FormatException("Gemini did not return exactly one review for every supplied player.");
            var contexts = request.PlayerContext.ToDictionary(p => p.Alias, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reviews = new List<GeminiPlayerReview>();
            foreach (var player in players.EnumerateArray())
            {
                Fields(player, video
                    ? ["alias", "moments", "videoEvidence"]
                    : ["alias", "status", "reason", "observation", "moments"]);
                var alias = Text(player.GetProperty("alias"), 32, false);
                if (!contexts.TryGetValue(alias, out var context) || !seen.Add(alias))
                    throw new FormatException("Gemini returned an unknown or duplicate player alias. No results were accepted.");
                if (video)
                {
                    reviews.Add(ParseVideoPlayer(player, context, request));
                    continue;
                }
                var status = Text(player.GetProperty("status"), 32, false) switch
                {
                    "review_suggested" => GeminiPlayerReviewStatus.ReviewSuggested,
                    "no_concern_identified" => GeminiPlayerReviewStatus.NoConcernIdentified,
                    "insufficient_data" => GeminiPlayerReviewStatus.InsufficientData,
                    _ => throw new FormatException("Gemini returned an unsupported player review status.")
                };
                var reason = Text(player.GetProperty("reason"), 240, false);
                var observation = Text(player.GetProperty("observation"), 600, false);
                var moments = player.GetProperty("moments");
                if (moments.ValueKind != JsonValueKind.Array || moments.GetArrayLength() > 3)
                    throw new FormatException("Gemini returned invalid player review moments.");
                var cited = new HashSet<GeminiReviewMoment>();
                foreach (var moment in moments.EnumerateArray())
                {
                    Fields(moment, "round", "tick");
                    if (moment.GetProperty("round").ValueKind != JsonValueKind.Number ||
                        moment.GetProperty("tick").ValueKind != JsonValueKind.Number ||
                        !moment.GetProperty("round").TryGetInt32(out var round) ||
                        !moment.GetProperty("tick").TryGetInt32(out var tick))
                        throw new FormatException("Gemini cited an unsupplied, mismatched or duplicate review moment.");
                    var anchor = new GeminiReviewMoment(round, tick);
                    if (!context.Moments.Contains(anchor))
                        throw new FormatException("Gemini cited an unsupplied review moment.");
                    if (!cited.Add(anchor)) throw new FormatException("Gemini cited a duplicate review moment.");
                }
                if ((status == GeminiPlayerReviewStatus.ReviewSuggested) != (cited.Count > 0) ||
                    (status == GeminiPlayerReviewStatus.NoConcernIdentified &&
                        (context.AssessedShots == 0 || !context.HasDetailedMeasurements)))
                    throw new FormatException("Gemini player status lacks the required supporting coverage or review moments.");
                reviews.Add(new(alias, status, reason, observation, Array.AsReadOnly(cited.ToArray())));
            }
            if (requiredContexts.Any(context => !seen.Contains(context.Alias)))
                throw new FormatException("Gemini did not return exactly one review for every supplied player.");
            var formatted = new StringBuilder(
                (request.VideoClips.Count > 0 ? "UNVERIFIED AI VIDEO COMMENTARY" : "UNVERIFIED AI COMMENTARY") +
                " | Not a cheating verdict. May contain errors; verify every claim against the local measurements and demo. " +
                "No website flags, coverage or rankings were changed.\n\nCoverage\n");
            formatted.AppendLine("Measured coverage: " + request.CoverageSummary);
            if (video)
            {
                var audioTracks = request.VideoClips.Count(clip => clip.HasAudio);
                var audibleObservations = reviews.Sum(player => player.VideoEvidence?.Count(
                    item => item.Basis == GeminiVideoEvidenceBasis.Audible) ?? 0);
                formatted.AppendLine($"Audio: {audioTracks}/{request.VideoClips.Count} supplied clips contained a verified audio track. Gemini reported {audibleObservations} audible observation(s), not independently verified. An audio track does not establish that teammate comms were recorded or that a player heard a cue.");
                formatted.AppendLine("Local event context, when supplied: retained eligible firearm shots and human-vs-human kills at nominal 64-tick offsets, not verified frame timestamps. Missing events do not establish survival or no firing.");
            }
            if (!video) formatted.AppendLine("AI coverage notes: " + coverage);
            formatted.AppendLine("\nPlayer observations");
            var byAlias = reviews.ToDictionary(p => p.Alias, StringComparer.Ordinal);
            foreach (var context in requiredContexts)
            {
                formatted.AppendLine($"- {context.Alias} | Measured basis: {context.MeasurementSummary}");
                foreach (var clip in request.VideoClips.Where(c => c.Alias == context.Alias))
                    foreach (var death in clip.EventTimeline?.RetainedDeaths ?? [])
                        formatted.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"  LOCAL CAMERA-ATTRIBUTION CAUTION: clip {clip.Number}, recorded firearm death at tick {death.Tick}, nominal +{death.NominalSeconds:0.######}s. Later footage may be a death/third-person/spectator camera, not this player's continuing aim. The transition frame is not verified."));
                if (byAlias[context.Alias].Observation is { } observation)
                    formatted.AppendLine("  AI observation: " + observation);
                foreach (var item in byAlias[context.Alias].VideoEvidence ?? [])
                    formatted.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"  Video observation ({item.Basis}): clip {item.ClipNumber}, +{item.Seconds:0.###}s (approximate) | {item.Description}"));
            }
            formatted.AppendLine("\nReview moments with reasons");
            var suggestions = requiredContexts.Select(p => byAlias[p.Alias])
                .Where(p => p.Status == GeminiPlayerReviewStatus.ReviewSuggested).ToArray();
            if (suggestions.Length == 0)
                formatted.AppendLine("The AI suggested no specific moments for closer review in this supplied sample. This does not establish absence of concerning behavior. Bookmarks are not the criteria for whether a review is warranted.");
            if (video && suggestions.Length > 0)
                formatted.AppendLine("AI-PROPOSED BOOKMARKS: reasons are unverified interpretations, not local findings. Recorded firing does not establish an absent target, abnormal behavior, intent or a useful reason for review. Independent usefulness has not been validated.");
            foreach (var suggestion in suggestions)
            {
                formatted.AppendLine("- " + suggestion.Alias + ": " + suggestion.Reason);
                foreach (var moment in suggestion.Moments)
                    formatted.AppendLine("  " + DescribeMoment(moment));
                foreach (var clip in request.VideoClips.Where(c => c.Alias == suggestion.Alias &&
                    suggestion.Moments.Any(m => m.ClipNumber == c.Number)))
                {
                    formatted.AppendLine($"  LOCAL CLIP EVENT EVIDENCE: clip {clip.Number}, requested POV {clip.Alias}; clip-wide context, not matched to AI timestamps.");
                    if (clip.EventTimeline is not { } timeline)
                    {
                        formatted.AppendLine("    Event timeline was not supplied to this review. This is not a zero-event result.");
                        continue;
                    }
                    var shots = DemoReviewSampling.Across(timeline.RetainedShots, 12).ToArray();
                    var kills = DemoReviewSampling.Across(timeline.RetainedKills, 12).ToArray();
                    var deaths = DemoReviewSampling.Across(timeline.RetainedDeaths, 12).ToArray();
                    formatted.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"    Retained own-player firearm shots: {timeline.RetainedShots.Count}; showing {shots.Length}. Retained own-player kills: {timeline.RetainedKills.Count}; showing {kills.Length}. Recorded deaths of this POV player: {timeline.RetainedDeaths.Count}; showing {deaths.Length}."));
                    foreach (var shot in shots)
                        formatted.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"    Recorded shot: tick {shot.Tick}, nominal +{shot.NominalSeconds:0.######}s, weapon {shot.Weapon}."));
                    foreach (var kill in kills)
                        formatted.AppendLine(FormattableString.Invariant(
                            $"    Recorded kill: tick {kill.Tick}, nominal +{kill.NominalSeconds:0.######}s, victim {kill.OpponentAlias ?? "unknown alias"}, weapon {kill.Weapon}{(kill.Headshot == true ? ", headshot." : ".")}"));
                    foreach (var death in deaths)
                        formatted.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"    Recorded death: tick {death.Tick}, nominal +{death.NominalSeconds:0.######}s, weapon {death.Weapon}."));
                    formatted.AppendLine("    At most 12 evenly distributed retained events of each type are shown. Positive context only, not exhaustive counts or verified video timestamps. Zero retained events do not prove no firing or survival. No visible/audible claim, target absence, intent or AI rationale is verified by these event rows.");
                }
            }
            formatted.AppendLine("\nAlternative explanations");
            if (video)
            {
                formatted.AppendLine("Local context: ordinary movement, target switching, recoil, occlusion and demo/encoding/sampling artifacts are possible explanations, not established causes. No validated normality/cheating baseline is supplied.");
            }
            else formatted.AppendLine("Possibilities only, not observed causes: " + alternatives);
            formatted.AppendLine("\nMissing evidence");
            if (video)
            {
                formatted.Append("LOCAL LIMITS ALWAYS APPLY: selected short reconstructed POV clips are not exhaustive match/fight coverage or original player video. Lossy 960x540/30-fps copies and requested 10-fps model sampling can omit fine/fast details. Raw mouse input, missing subtick commands, the player's complete knowledge and precise reaction time are not recovered. Audio-stream presence does not establish what the player heard. Clip timestamps and clip-start bookmarks do not establish exact frame-to-demo/geometry alignment. Cheating and clean play cannot be established by this review.");
            }
            else formatted.Append(missing);
            return new GeminiReviewResult(formatted.ToString(), reviews.AsReadOnly(), requiredContexts,
                request.VideoClips.Count > 0) { RawJson = text };
        }
    }

    public string Render(IReadOnlyList<DemoPlayer> roster)
    {
        var local = new Dictionary<string, DemoPlayer>(StringComparer.Ordinal);
        foreach (var player in roster)
            if (!local.TryAdd(player.SteamId, player))
                throw new FormatException("The local roster contains duplicate identities. Prepare a new review.");
        var reviews = Players.ToDictionary(p => p.Alias, StringComparer.Ordinal);
        var text = new StringBuilder("AI DEMO FINAL RESULT | Unverified observations, not cheating or clean-play findings\n");
        text.AppendLine(_video
            ? "Names are recorded demo names, mapped locally. Uploaded footage was NOT anonymized and may visibly contain names, avatars or IDs."
            : "Names are recorded demo names, not verified current Steam names. Names and SteamIDs were mapped locally, not sent to Gemini.");
        text.AppendLine(_video
            ? "No AI review moment suggested means only that Gemini did not suggest a moment in the supplied clips; it is not a clean-play finding or clearance. Clip-start ticks are nominal bookmarks, not exact event alignment."
            : "No review concern identified means only within the supplied telemetry; it does not clear a player.");
        if (_video) text.AppendLine("Video statuses describe supplied POV/declared discernibility and optional review moments; they are not AI player classifications. Review suggested means only AI-proposed bookmarks, not locally corroborated suspicion or independently validated usefulness.");
        foreach (var context in _context)
        {
            if (!local.TryGetValue(context.SteamId, out var player))
                throw new FormatException("The local roster does not match this review. Import and prepare the review again.");
            var review = reviews[context.Alias];
            var status = review.Status switch
            {
                GeminiPlayerReviewStatus.ReviewSuggested => "Review suggested",
                GeminiPlayerReviewStatus.NoConcernIdentified when _video => "No AI moment suggested",
                GeminiPlayerReviewStatus.NoConcernIdentified => "No review concern identified",
                GeminiPlayerReviewStatus.InsufficientData => "Insufficient data",
                _ => throw new FormatException("Unsupported review status.")
            };
            var name = WebsiteDataParser.ValidDisplayName(player.Name) ?? "Name unavailable";
            text.AppendLine();
            text.AppendLine($"{name} ({context.Alias})");
            text.AppendLine($"- SteamID64: {context.SteamId}");
            text.AppendLine($"- AI review status: {status}");
            text.AppendLine($"- Summary: {review.Reason}");
            var observations = review.VideoEvidence ?? [];
            var displayedObservations = observations.Count <= 3
                ? observations
                : DemoReviewSampling.Across(observations, 3).ToArray();
            text.AppendLine(observations.Count > 3
                ? $"- Supplied clips: {observations.Count} (showing {displayedObservations.Count} representative observations; all are in Evidence)"
                : $"- Supplied clips: {observations.Count}");
            foreach (var observation in displayedObservations)
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  - Clip {observation.ClipNumber}, +{observation.Seconds:0.###}s ({observation.Basis}): {observation.Description}"));
            foreach (var moment in review.Moments)
                text.AppendLine("- AI-proposed review moment: " + DescribeMoment(moment));
            if (observations.Count == 0 && review.Moments.Count == 0)
                text.AppendLine("- No review moment or video observation was supplied for this player in the accepted result.");
            text.AppendLine("- Limits: this short-sample AI result does not establish cheating, clean play, intent, visibility, raw input or precise reaction time.");
        }
        text.AppendLine("\nDETAILED AI REVIEW EVIDENCE");
        text.Append(Commentary);
        return text.ToString();
    }

    internal static GeminiReviewResult Combine(IReadOnlyList<GeminiReviewResult> results)
    {
        if (results.Count == 0 || results.Any(result => !result._video))
            throw new FormatException("Only video batch results can be combined.");
        var contexts = results.SelectMany(result => result._context)
            .GroupBy(context => context.Alias, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(context => context.SteamId != first.SteamId))
                    throw new FormatException("The video batch results contain mismatched player identities.");
                return first;
            })
            .ToArray();
        var reviews = contexts.Select(context =>
        {
            var batches = results.SelectMany(result => result.Players)
                .Where(player => player.Alias == context.Alias).ToArray();
            var observations = batches.SelectMany(player => player.VideoEvidence ?? []).OrderBy(item => item.ClipNumber).ToArray();
            var moments = batches.SelectMany(player => player.Moments)
                .GroupBy(moment => (moment.ClipNumber, moment.ClipSeconds))
                .Select(group => group.First())
                .OrderBy(moment => moment.ClipNumber)
                .Take(3)
                .ToArray();
            var status = moments.Length > 0
                ? GeminiPlayerReviewStatus.ReviewSuggested
                : observations.Any(item => item.Basis != GeminiVideoEvidenceBasis.NotDiscernible)
                    ? GeminiPlayerReviewStatus.NoConcernIdentified
                    : GeminiPlayerReviewStatus.InsufficientData;
            var reason = status == GeminiPlayerReviewStatus.ReviewSuggested
                ? moments[0].Reason!
                : status == GeminiPlayerReviewStatus.NoConcernIdentified
                    ? "No specific video review moment was suggested across the supplied POV clips; this is not a clean-play finding."
                    : "No supplied POV observation was declared discernible.";
            return new GeminiPlayerReview(context.Alias, status, reason, null,
                moments, observations);
        }).ToArray();
        var commentary = new StringBuilder("COMBINED BATCH EVIDENCE\n");
        for (var index = 0; index < results.Count; index++)
        {
            commentary.AppendLine($"\n=== Batch {index + 1}/{results.Count} ===");
            commentary.Append(results[index].Commentary);
        }
        return new(commentary.ToString(), reviews, contexts, true);
    }

    public static string FinalSection(string rendered)
    {
        var final = rendered.IndexOf("AI DEMO FINAL RESULT", StringComparison.Ordinal);
        var start = final < 0 ? 0 : final;
        var detail = rendered.IndexOf("\n\nDETAILED AI REVIEW EVIDENCE", start, StringComparison.Ordinal);
        return detail < 0 ? rendered[start..] : rendered[start..detail];
    }

    public static string DetailedSection(string rendered)
    {
        var final = rendered.IndexOf("AI DEMO FINAL RESULT", StringComparison.Ordinal);
        var detail = rendered.IndexOf("DETAILED AI REVIEW EVIDENCE", StringComparison.Ordinal);
        if (detail < 0) return final > 0 ? rendered[..final].TrimEnd() : rendered;
        return final > 0
            ? rendered[..final].TrimEnd() + "\n\n" + rendered[detail..]
            : rendered[detail..];
    }

    private static string DescribeMoment(GeminiReviewMoment moment) => moment.ClipNumber is { } number
        ? string.Create(CultureInfo.InvariantCulture, $"clip {number}, +{moment.ClipSeconds:0.###}s (approximate); round {moment.Round}, nominal clip-start tick {moment.Tick}; {moment.Reason?.Replace('|', '/')}")
        : string.Create(CultureInfo.InvariantCulture, $"round {moment.Round}, tick {moment.Tick}");

    private static GeminiPlayerReview ParseVideoPlayer(JsonElement player, GeminiReviewPlayerContext context, GeminiReviewRequest request)
    {
        var evidence = player.GetProperty("videoEvidence");
        var moments = player.GetProperty("moments");
        var ownClips = request.VideoClips.Where(clip => clip.Alias == context.Alias)
            .Select(clip => clip.Number).ToHashSet();
        if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() != ownClips.Count ||
            (evidence.GetArrayLength() > 0) != context.HasDetailedMeasurements ||
            moments.ValueKind != JsonValueKind.Array || moments.GetArrayLength() > 3)
            throw new FormatException("Video review requires exactly one timestamped observation for every supplied clip and at most three review moments.");
        var observed = new List<GeminiVideoObservation>();
        var seenEvidence = new HashSet<(int, double, GeminiVideoEvidenceBasis)>();
        foreach (var item in evidence.EnumerateArray())
        {
            Fields(item, "clipNumber", "seconds", "basis", "description");
            var (clip, seconds) = VideoPoint(item, request, context.Alias, "video observation");
            var basis = Text(item.GetProperty("basis"), 32, false) switch
            {
                "visible" => GeminiVideoEvidenceBasis.Visible,
                "audible" when clip.HasAudio => GeminiVideoEvidenceBasis.Audible,
                "not_discernible" => GeminiVideoEvidenceBasis.NotDiscernible,
                _ => throw new FormatException("Video evidence uses an unsupported or unavailable observation basis.")
            };
            var description = Text(item.GetProperty("description"), 400, false);
            ValidateVideoLanguage(description);
            if (!seenEvidence.Add((clip.Number, seconds, basis)))
                throw new FormatException("Gemini repeated a video observation timestamp and basis.");
            observed.Add(new(clip.Number, seconds, basis, description));
        }
        if (!ownClips.SetEquals(observed.Select(item => item.ClipNumber)))
            throw new FormatException("Gemini omitted a supplied clip from its video observations. No batch review was accepted.");
        var cited = new List<GeminiReviewMoment>();
        foreach (var item in moments.EnumerateArray())
        {
            Fields(item, "round", "tick", "clipNumber", "seconds", "reason");
            var (clip, seconds) = VideoPoint(item, request, context.Alias, "review moment");
            if (item.GetProperty("round").ValueKind != JsonValueKind.Number ||
                item.GetProperty("tick").ValueKind != JsonValueKind.Number ||
                !item.GetProperty("round").TryGetInt32(out var round) ||
                !item.GetProperty("tick").TryGetInt32(out var tick) || clip.Round != round || clip.StartTick != tick ||
                cited.Any(m => m.ClipNumber == clip.Number && m.ClipSeconds == seconds))
                throw new FormatException("Gemini cited a wrong round/bookmark or duplicate video review moment.");
            var reason = Text(item.GetProperty("reason"), 240, false);
            ValidateVideoLanguage(reason);
            cited.Add(new(round, tick, clip.Number, seconds, reason));
        }
        var discernible = observed.Any(e => e.Basis != GeminiVideoEvidenceBasis.NotDiscernible);
        if (!discernible && cited.Count > 0)
            throw new FormatException("Undiscernible or absent footage cannot support suggested video review moments.");
        var status = !discernible ? GeminiPlayerReviewStatus.InsufficientData
            : cited.Count > 0 ? GeminiPlayerReviewStatus.ReviewSuggested : GeminiPlayerReviewStatus.NoConcernIdentified;
        var explanation = status switch
        {
            GeminiPlayerReviewStatus.ReviewSuggested => cited[0].Reason!,
            GeminiPlayerReviewStatus.NoConcernIdentified => "No specific video review moment was suggested within the short supplied POV; this is not a clean-play finding.",
            _ => "No own POV was supplied, or all supplied observations were declared not discernible."
        };
        return new(context.Alias, status, explanation, null, cited.AsReadOnly(), observed.AsReadOnly());
    }

    private static (GeminiVideoContext Clip, double Seconds) VideoPoint(JsonElement item, GeminiReviewRequest request,
        string alias, string citationKind)
    {
        if (item.GetProperty("clipNumber").ValueKind != JsonValueKind.Number ||
            item.GetProperty("seconds").ValueKind != JsonValueKind.Number ||
            !item.GetProperty("clipNumber").TryGetInt32(out var number) ||
            !item.GetProperty("seconds").TryGetDouble(out var seconds) || !double.IsFinite(seconds))
            throw new FormatException("Gemini returned an invalid video timestamp.");
        var clip = request.VideoClips.SingleOrDefault(c => c.Number == number);
        if (clip is null)
            throw new FormatException($"Gemini's {citationKind} for {alias} cited unsupplied clip {number}. No review was accepted.");
        if (clip.Alias != alias)
            throw new FormatException($"Gemini's {citationKind} for {alias} cited clip {number}, which belongs to {clip.Alias}'s POV. No review was accepted.");
        if (seconds < 0 || seconds > clip.DurationSeconds)
            throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                $"Gemini's {citationKind} for {alias} cited {seconds:0.###} seconds in clip {number}; valid range is 0-{clip.DurationSeconds:0.###} seconds. No review was accepted."));
        return (clip, seconds);
    }

    private static void ValidateVideoLanguage(string text)
    {
        if (Regex.IsMatch(text,
            @"\b(?:consistent with (?:standard|normal|typical)(?: player)? (?:gameplay|play|behavior)|within normal (?:gameplay )?parameters|appears? normal|no (?:signs|evidence) of (?:anomalous|abnormal|suspicious) behavio[u]?r|no suspicious behavio[u]?r (?:was )?(?:observed|identified)|no anomalous patterns were identified)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new FormatException("Gemini returned an unsupported normal-play/absence-of-anomalies claim. No video review was accepted; inspect the footage manually. No automatic retry.");
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException("Gemini returned an invalid structured review object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
            throw new FormatException("Gemini returned missing, duplicate or unexpected structured review fields.");
    }

    private static string Text(JsonElement value, int maximum, bool multiline)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("Gemini returned a non-text review field.");
        var text = value.GetString()!.Trim();
        if (text.Length == 0 || text.Length > maximum || text.Any(c =>
            (char.IsControl(c) && !(multiline && (c is '\r' or '\n' or '\t'))) ||
            c is '\u2028' or '\u2029' or '\u061c' or '\u200e' or '\u200f' ||
            c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u206F'))
            throw new FormatException("Gemini returned empty, oversized or unsafe review text.");
        return text;
    }
}
