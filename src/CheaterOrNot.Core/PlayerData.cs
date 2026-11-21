using System.Globalization;
using System.Text;

namespace CheaterOrNot.Core;

public static class PlayerId
{
    public static string Parse(string input)
    {
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var leetifyProfile = uri.Host == "leetify.com" && parts.Length == 3 &&
                parts[0] is "app" or "public" && parts[1] == "profile";
            var knownProfile = Sources.IsKnownHost(uri.Host) && parts.Length == 2 &&
                parts[0] == Sources.ProfilePrefix(uri.Host);
            if (uri.Scheme != "https" || (!leetifyProfile && !knownProfile) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                throw new FormatException("Use a SteamID64 or a supported HTTPS player-profile URL.");
            value = parts[^1];
        }
        if (value.Length != 17 || !value.All(char.IsAsciiDigit) ||
            !ulong.TryParse(value, out var id) ||
            id <= 76561197960265728UL || id > 76561202255233023UL)
            throw new FormatException("Enter a valid public individual SteamID64 (17 digits). Vanity names are not supported.");
        return value;
    }
}

public static class Sources
{
    public static readonly string[] PageHosts = ["csst.at", "cstracker.gg"];
    private static readonly string[] InputHosts = ["csst.at", "cstracker.gg", "csstats.gg"];
    public static readonly string[] CommunityHosts = ["cs2tracker.gg", "cswatch.gg", "trackbans.com", "csrep.gg", "expose.gg"];
    public static bool IsKnownHost(string host) => InputHosts.Contains(host) || host is "steamcommunity.com" or "cswatch.gg";
    public static string ProfilePrefix(string host) => host switch
    {
        "csst.at" => "profile",
        "cstracker.gg" => "players",
        "csstats.gg" => "player",
        "steamcommunity.com" => "profiles",
        "cswatch.gg" => "player",
        _ => throw new ArgumentException("Unsupported source.")
    };
    public static Uri Profile(string host, string id) =>
        new($"https://{host}/{ProfilePrefix(host)}/{PlayerId.Parse(id)}");

    public static Uri ReviewUrl(string host, string id)
    {
        PlayerId.Parse(id);
        if (IsKnownHost(host)) return Profile(host, id);
        if (!CommunityHosts.Contains(host)) throw new FormatException("Unsupported review source.");
        return new Uri($"https://{host}/");
    }

    public static Uri LeetifyProfile(string id) =>
        new($"https://leetify.com/public/profile/{PlayerId.Parse(id)}");

    public static bool IsSamePlayerPage(Uri requested, Uri candidate)
    {
        if (requested == candidate) return true;
        if (requested.Scheme != "https" || candidate.Scheme != "https" ||
            requested.Host != "leetify.com" || candidate.Host != requested.Host ||
            !requested.IsDefaultPort || !candidate.IsDefaultPort ||
            requested.UserInfo.Length != 0 || candidate.UserInfo.Length != 0 ||
            requested.Query.Length != 0 || candidate.Query.Length != 0) return false;
        var source = requested.AbsolutePath.TrimEnd('/').Split('/');
        var target = candidate.AbsolutePath.TrimEnd('/').Split('/');
        return source.Length == 4 && target.Length == 4 &&
            source[1] is "app" or "public" && target[1] is "app" or "public" &&
            source[2] == "profile" && target[2] == "profile" &&
            source[3].Length == 17 && source[3].All(char.IsAsciiDigit) && source[3] == target[3];
    }

    public static IReadOnlyList<(string Name, Uri Url)> PublicPages(string id)
    {
        PlayerId.Parse(id);
        return [
            ("Leetify", LeetifyProfile(id)),
            .. PageHosts.Select(host => (host, Profile(host, id))),
            ("cswatch.gg", Profile("cswatch.gg", id)),
            ("Steam", Profile("steamcommunity.com", id))
        ];
    }

    public static readonly (string Name, string Url, string Reason)[] UnsupportedSources =
    [
        ("TrackBans", "https://trackbans.com/cs2-ai-cheater-detection/", "No automatic adapter; the checker reported maintenance when reviewed on 2026-11-16.")
    ];
}

public sealed record FetchProgress(int CompletedSources, int TotalSources, string Source,
    string Status, int PerformanceSummaries);

public sealed record KprCounters(long Kills, long Rounds, long? ReportedMatchTotal = null);

public sealed record PlayerStats(int? Matches = null, double? Kd = null,
    double? Headshots = null, double? WinRate = null, double? Kpr = null)
{
    public bool HasMetrics => Kd.HasValue || Headshots.HasValue || WinRate.HasValue || Kpr.HasValue;
    public bool CombatMatchCountUnverified { get; init; }
    public KprCounters? KprCounts { get; init; }
    public double? ContextWinRate { get; init; }
    public double? ContextHeadshots { get; init; }
    public double? ContextKpr { get; init; }

    public void Validate()
    {
        if (Matches is < 0 ||
            new[] { Kd, Headshots, WinRate, Kpr, ContextWinRate, ContextHeadshots, ContextKpr }.Any(x => x.HasValue && (!double.IsFinite(x.Value) || x < 0)) ||
            Headshots is > 100 || WinRate is > 100 || ContextWinRate is > 100 || ContextHeadshots is > 100)
            throw new FormatException("Invalid metric value; percentages must be 0-100 and counts/ratios nonnegative.");
        if (KprCounts is { } counts)
        {
            if (counts.Kills < 0 || counts.Rounds <= 0 || counts.ReportedMatchTotal is < 0)
                throw new FormatException("Invalid reported kills or rounds for K/R.");
            var expected = (double)counts.Kills / counts.Rounds;
            if (new[] { Kpr, ContextKpr }.Any(value => value is { } kpr &&
                Math.Abs(kpr - expected) > 1e-10 * Math.Max(1, expected)))
                throw new FormatException("Reported kills and rounds do not match K/R.");
        }
    }
}

public enum MetricComparison { AtLeast, Above, Below }

public sealed record MetricCheck(string Name, double Value, double Threshold, string Unit = "",
    MetricComparison Comparison = MetricComparison.AtLeast, string RuleSource = "app rule", int? BetterThanPercent = null)
{
    public bool IsFlagged
    {
        get
        {
            if (!double.IsFinite(Value) || !double.IsFinite(Threshold) || Value < 0 || Threshold < 0 ||
                BetterThanPercent is < 0 or > 100 || Unit == "%" && (Value > 100 || Threshold > 100))
                throw new FormatException("Invalid performance measurement or screening threshold.");
            return Comparison switch
            {
                MetricComparison.AtLeast => Value >= Threshold,
                MetricComparison.Above => Value > Threshold,
                MetricComparison.Below => Value < Threshold,
                _ => throw new FormatException("Unknown screening comparison.")
            };
        }
    }
}

public sealed record PublicSection(string Title, string Text, PlayerStats? Stats = null, bool DetailedOnly = false,
    IReadOnlyList<MetricCheck>? Checks = null)
{
    public DateTimeOffset? DataUpdatedAt { get; init; }
}

public sealed record AccountContext(string Note, IReadOnlyList<string> Cautions)
{
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> SmurfReviewContext { get; init; } = [];
}

public sealed record Evidence(string Source, Uri Url, string Status, PlayerStats? Stats = null,
    string? Note = null, IReadOnlyList<PublicSection>? Sections = null, IReadOnlyList<string>? Cautions = null)
{
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DataUpdatedAt { get; init; }
    internal FaceitLifetimeStats? FaceitLifetime { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> SmurfReviewContext { get; init; } = [];
}

public static class Analysis
{
    private static readonly (string Name, Func<PlayerStats, double?> Read, Func<ScreeningRules, double> Threshold,
        string Suffix, string Indicator)[] CoreRules =
    [
        ("K/D", stats => stats.Kd, rules => rules.KdThreshold, "", "K/D"),
        ("Headshot kills", stats => stats.Headshots, rules => rules.HeadshotPercentThreshold, "%", "Overall headshot percentage"),
        ("Win rate", stats => stats.WinRate, rules => rules.WinPercentThreshold, "%", "Win rate"),
        ("Kills per round", stats => stats.Kpr, rules => rules.KillsPerRoundThreshold, "", "Kills per round")
    ];

    public static string Render(string id, IReadOnlyList<Evidence> evidence, string? proRefresh = null, ScreeningRules? rules = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        var report = new StringBuilder();
        report.AppendLine($"CHEATER OR NOT | SteamID64 {id}");
        report.AppendLine("Review indicators, not a cheating verdict or probability. Sources are evaluated separately (overlapping matches are never added together); thresholds are uncalibrated screening rules, and samples are assessed as provided, including small ones.\n");
        report.AppendLine("Each metric row describes only its stated sample. 'Not available in this sample' means the adapter obtained no verified value for that field, not zero or a passed check. Other panels or websites may supply it separately; privacy, access and parsing failures are explained below.\n");
        var anyUsable = false;
        foreach (var item in evidence.Where(item => item.Status != "Unsupported automatic source"))
        {
            report.AppendLine($"{item.Source} | {item.Status}");
            report.AppendLine($"{item.Url} | Retrieved {item.RetrievedAt:u}" +
                (item.DataUpdatedAt is { } updated ? $" | Website data updated {updated:u}" : ""));
            if (!string.IsNullOrWhiteSpace(item.Note)) report.AppendLine(item.Note);
            if (item.SmurfReviewContext.Count > 0)
            {
                report.AppendLine("Smurf-review context (not scored):");
                foreach (var context in item.SmurfReviewContext) report.AppendLine("- " + context);
                report.AppendLine("These fields do not establish smurfing or cheating and do not affect flags or review priority.");
            }
            foreach (var caution in item.Cautions ?? []) report.AppendLine("CAUTION: " + caution);
            if (item.Stats is { } stats && (stats.HasMetrics || stats.ContextKpr is not null))
            {
                AppendStats(report, stats, rules);
                if (stats.HasMetrics)
                {
                    anyUsable = true;
                    var flags = Indicators(stats, rules);
                    report.AppendLine(flags.Count == 0 ? "Indicators: none." : $"Indicators: {string.Join("; ", flags)}.");
                }
                else report.AppendLine("Displayed values are context only; no metrics from this sample were assessed.");
            }
            AppendSections(report, item, rules);
            anyUsable |= item.Sections?.Any(section => section.Stats is { HasMetrics: true } || section.Checks?.Count > 0) == true;
            report.AppendLine();
        }
        if (!anyUsable)
            report.AppendLine("INSUFFICIENT DATA: no usable performance statistics were obtained.");
        if (proRefresh is not null)
        {
            report.AppendLine("\nPROFESSIONAL REFERENCE CONTEXT (not scored)");
            report.AppendLine(proRefresh);
            report.AppendLine();
        }
        report.AppendLine("Legitimate explanations: high skill, smurfing, weapon selection, weaker opponents, mode/rank differences, selection bias, or a short hot streak.");
        report.AppendLine("Limitations: no demo or recoil analysis. Historical match blocks and recent-vs-lifetime comparisons are limited statistical summaries, not behavioral or cheat-toggle analysis. Missing data and historical bans are not proof of current CS2 cheating.");
        report.AppendLine("Recommendation: review several full demos in context. Do not accuse, harass, or penalize a player based on this report alone.");
        report.AppendLine("\nAPP RULES USED FOR THIS CHECK (website cutoffs are shown with their own measurements)");
        report.AppendLine(rules.ToJson());
        return report.ToString();
    }

    public sealed record BatchSummary(string Verdict, int FlaggedTypes, int Cautions, int MetricTypes, bool HasData);

    public static BatchSummary Summarize(IReadOnlyList<Evidence> evidence, ScreeningRules? rules = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        var sources = evidence.Where(e => e.Status != "Unsupported automatic source").ToArray();
        var allChecks = sources.SelectMany(item => WebsiteChecks(item, rules)).ToArray();
        var flagged = allChecks.Where(sc => sc.Check.IsFlagged).Select(sc => sc.Check.Name).Distinct().Count();
        var hasData = allChecks.Length > 0;
        return new BatchSummary(
            !hasData ? "INCONCLUSIVE"
                : flagged > 1 ? "MULTIPLE POSSIBLE CHEATING INDICATORS"
                : flagged == 1 ? "POSSIBLE CHEATING INDICATOR"
                : "NO STATISTICAL RED FLAGS",
            flagged,
            sources.Sum(item => item.Cautions?.Count ?? 0),
            allChecks.Select(sc => sc.Check.Name).Distinct().Count(),
            hasData);
    }

    public static string RenderBatch(IReadOnlyList<(string Id, IReadOnlyList<Evidence> Evidence)> players, ScreeningRules? rules = null,
        ProStatsSnapshot? proStats = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        if (players.Count == 0) throw new FormatException("No players were checked.");
        var ranked = players.Select(player => (player.Id, player.Evidence, Summary: Summarize(player.Evidence, rules)))
            .OrderByDescending(player => player.Summary.FlaggedTypes)
            .ThenByDescending(player => player.Summary.Cautions)
            .ThenBy(player => player.Id, StringComparer.Ordinal).ToArray();
        var report = new StringBuilder();
        report.AppendLine($"BATCH RESULT | {players.Count} players checked");
        report.AppendLine("Ranked by flagged indicator types, then account cautions. Players are screened independently; ranks compare review priority, not cheating probability.\n");
        var rank = 0;
        foreach (var player in ranked)
            report.AppendLine($"{++rank,2}. {player.Id} | Player: {PlayerName(player.Evidence)} | {player.Summary.Verdict} | flagged types: {player.Summary.FlaggedTypes} | cautions: {player.Summary.Cautions} | metric types assessed: {player.Summary.MetricTypes}");
        report.AppendLine("\nFull per-player results follow.");
        foreach (var player in ranked)
        {
            report.AppendLine("\n" + new string('=', 60));
            report.Append(RenderFinal(player.Id, player.Evidence, rules, proStats));
        }
        return report.ToString();
    }

    public static string RenderFinal(string id, IReadOnlyList<Evidence> evidence, ScreeningRules? rules = null,
        ProStatsSnapshot? proStats = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        var sources = evidence.Where(e => e.Status != "Unsupported automatic source").ToArray();
        var perWebsite = sources.Select(item => (Item: item, Checks: WebsiteChecks(item, rules)))
            .Where(x => x.Checks.Count > 0).ToArray();
        var allChecks = perWebsite.SelectMany(x => x.Checks).ToArray();
        var flagged = perWebsite.SelectMany(x => x.Checks
            .Where(sc => sc.Check.IsFlagged)
            .Select(sc => (Item: x.Item, Sample: sc))).ToArray();
        var flaggedTypes = flagged.Select(sc => sc.Sample.Check.Name).Distinct().ToArray();
        var metricTypes = allChecks.Select(sc => sc.Check.Name).Distinct().ToArray();
        var missing = CoreRules.Where(rule => !metricTypes.Contains(rule.Name)).Select(rule => rule.Name).ToArray();
        var coreCovered = CoreRules.Count(rule => metricTypes.Contains(rule.Name));
        var cautions = sources.SelectMany(item => (item.Cautions ?? [])
            .Select(caution => (item.Source, Caution: caution))).ToArray();

        var report = new StringBuilder();
        report.AppendLine($"FINAL RESULT | SteamID64 {id} | Player: {PlayerName(evidence)}");
        report.AppendLine(perWebsite.Length == 0 ? "VERDICT: INCONCLUSIVE - NO ANALYZABLE PERFORMANCE DATA"
            : flaggedTypes.Length > 1 ? "VERDICT: MULTIPLE POSSIBLE CHEATING INDICATORS FOUND"
            : flaggedTypes.Length == 1 ? "VERDICT: POSSIBLE CHEATING INDICATOR FOUND"
            : "VERDICT: NO STATISTICAL RED FLAGS FOUND");
        report.AppendLine(perWebsite.Length == 0
            ? "No public gameplay statistics could be retrieved, so no statistical assessment is possible."
            : flaggedTypes.Length > 0
                ? $"{flaggedTypes.Length} screening indicator type(s) crossed a heuristic warning line. This warrants a closer look (demo review), not an accusation."
                : "No screening indicator was triggered across the data that was retrieved. This is not proof of clean play." +
                  (cautions.Length > 0 ? " Account ban history still warrants attention; see the cautions below." : ""));

        if (cautions.Length > 0)
        {
            report.AppendLine("\nACCOUNT CAUTIONS (ban/report history; reviewed separately from the statistics)");
            foreach (var (cautionSource, caution) in cautions)
                report.AppendLine($"- {cautionSource}: {caution}");
            report.AppendLine("Historical bans raise review priority but are not statistical indicators or proof of current CS2 cheating.");
        }

        report.AppendLine("\nWHAT WAS CHECKED");
        report.AppendLine(perWebsite.Length > 0
            ? $"- Gameplay data from {perWebsite.Length} of {sources.Length} checked sources: {string.Join(", ", perWebsite.Select(x => x.Item.Source))}."
            : $"- None of the {sources.Length} checked sources returned comparable gameplay statistics.");
        report.AppendLine($"- SCREENING COVERAGE: {coreCovered}/{CoreRules.Length} core metric types; {metricTypes.Length} distinct metric types assessed.");
        report.AppendLine("- Coverage counts metric types assessed somewhere in the retrieved data, not completeness for every source, sample or available website panel.");
        report.AppendLine("- Evidence explains optional-check eligibility and sample requirements; not assessed does not mean passed.");
        report.AppendLine("- App rules: " + (rules == new ScreeningRules() ? "default" : "custom") +
            "; the exact settings used are recorded in Evidence. Website-published cutoffs remain separate.");
        foreach (var item in sources)
            foreach (var section in item.Sections?.Where(section => section.Title.EndsWith(" unavailable", StringComparison.Ordinal)) ?? [])
                report.AppendLine($"- Partial retrieval: {item.Source}: {section.Title}; see Evidence for details.");
        if (metricTypes.Length > 0) report.AppendLine("- Metrics: " + string.Join(", ", metricTypes) + ".");
        if (missing.Length > 0) report.AppendLine("- Not assessed: " + string.Join(", ", missing) + " (not retrieved, or the sample was below that rule's flagging floor; never a passed check).");
        foreach (var item in sources.Where(item => WebsiteChecks(item, rules).Count == 0))
            report.AppendLine($"- {item.Source}: {item.Status} (context only, no assessed gameplay sample).");
        var unsupported = evidence.Count(item => item.Status == "Unsupported automatic source");
        if (unsupported > 0)
            foreach (var item in evidence.Where(item => item.Status == "Unsupported automatic source"))
                report.AppendLine($"- Not checked: {item.Source}. {item.Note}");

        report.AppendLine("\nSMURF-REVIEW CONTEXT (not scored)");
        var contextSources = sources.Where(item => item.SmurfReviewContext.Count > 0).ToArray();
        foreach (var item in contextSources)
        {
            report.AppendLine($"- {item.Source} (retrieved {item.RetrievedAt:u}):");
            foreach (var context in item.SmurfReviewContext) report.AppendLine("  " + context);
        }
        if (contextSources.Length == 0)
            report.AppendLine("No account-age, Steam-level or matchmaking-rank context was retrieved.");
        report.AppendLine("Unreported fields are unknown, not zero or suspicious. Steam level is community activity, not CS skill or CS profile XP level. Account age is not time played.");
        report.AppendLine("Rank freshness and a rank-matched performance baseline are not established. No automatic smurf assessment is made; this context never changes cheating flags or review-priority rankings.");

        report.AppendLine("\nDATA FRESHNESS AND SOURCE CONTEXT");
        foreach (var item in perWebsite.Select(entry => entry.Item))
        {
            report.AppendLine(item.DataUpdatedAt is { } updated
                ? $"- {item.Source}: website data timestamp {updated:u}; retrieved {item.RetrievedAt:u}."
                : $"- {item.Source}: no independently parsed website sync/update timestamp; retrieved {item.RetrievedAt:u}.");
        }
        if (sources.Any(item => (item.Sections ?? []).Any(section => section.DetailedOnly)))
            report.AppendLine("- Additional CSTracker tabs and behavior/utility fields are retained in Evidence as unscored context, not statistical proof.");
        report.AppendLine("A newer timestamp takes precedence only for equivalent samples. Larger or non-comparable samples and different websites remain separate and are never averaged together.");

        var repeatedMetrics = perWebsite.SelectMany(entry => entry.Checks
                .Select(sample => (entry.Item, Sample: sample)))
            .GroupBy(row => row.Sample.Check.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (repeatedMetrics.Length > 0)
        {
            report.AppendLine("\nREPEATED METRICS BY SOURCE (separate samples; not merged)");
            foreach (var group in repeatedMetrics)
            {
                report.AppendLine($"{group.Key}:");
                foreach (var (item, sample) in group)
                    report.AppendLine($"- {item.Source}: {group.Key} {CheckValue(sample.Check)} " +
                        (sample.Check.IsFlagged ? "crossed" : "did not cross") +
                        $" {sample.Check.RuleSource} {Relation(sample.Check)} {ThresholdValue(sample.Check)} " +
                        $"(sample: {Sample(sample)}; scope: {ProPlayerStats.Scope(item, sample.Panel)}).");
            }
            report.AppendLine("Repeated values are independent website samples with potentially different matches, windows and modes. They are not duplicates to discard and are never averaged.");
        }

        var recentWindows = sources.SelectMany(item => (item.Sections ?? [])
            .Where(section => section.Title.StartsWith("Recent ", StringComparison.Ordinal) &&
                section.Title.EndsWith("-day CSTracker trend", StringComparison.Ordinal))
            .Where(section => section.Stats is { HasMetrics: true })
            .Select(section => (item, section))).ToArray();
        if (recentWindows.Length > 0)
        {
            report.AppendLine("\nRECENT ROLLING TRENDS (overlapping, not merged)");
            foreach (var (item, section) in recentWindows)
            {
                report.AppendLine($"- {item.Source}: {section.Title.Replace(" CSTracker trend", "", StringComparison.Ordinal)} | " +
                    $"K/D {Number(section.Stats!.Kd)} | {section.Stats.Matches} matches | " +
                    (item.DataUpdatedAt is { } updated ? $"source data updated {updated:u} | " : "") +
                    $"retrieved {item.RetrievedAt:u}.");
            }
            report.AppendLine("These windows use raw kills/deaths from exact, identity-verified match times. They cover every cumulative 7-day interval from 7 through 63 days and overlap; compare trends by scope, but do not add them.");
        }

        var kprSources = perWebsite.SelectMany(entry => entry.Checks
            .Where(sample => sample.Check.Name == "Kills per round")
            .Select(sample => (entry.Item, Sample: sample))).ToArray();
        var contextKprSources = sources.Where(item => item.Stats?.ContextKpr is not null).ToArray();
        if (kprSources.Length > 0 || contextKprSources.Length > 0)
        {
            report.AppendLine("\nK/R BY SOURCE (separate samples)");
            foreach (var (item, sample) in kprSources)
            {
                var scope = sample.MatchCountUnverified
                    ? (item.Url.Host == "api.steampowered.com"
                        ? "Steam counter ratio; matching modes and periods are unverified"
                        : "counter ratio; scope and match count are unverified")
                    : item.Url.Host == "csst.at"
                        ? "Scope website-displayed value; underlying kills and rounds not independently verified"
                        : "source-reported sample";
                var matchContext = item.Url.Host == "api.steampowered.com" && sample.MatchCountUnverified
                    ? sample.KprCounts?.ReportedMatchTotal is { } reported
                        ? $" | Steam-reported match total: {reported.ToString("N0", CultureInfo.InvariantCulture)} (context only; not verified as the K/R sample)"
                        : " | Steam-reported match total unavailable"
                    : "";
                report.AppendLine($"- {item.Source}: {CheckValue(sample.Check)} | {Sample(sample)} | {scope}{matchContext} | " +
                    (sample.Check.IsFlagged ? "crosses" : "does not cross") +
                    $" {sample.Check.RuleSource} {Relation(sample.Check)} {ThresholdValue(sample.Check)}.");
            }
            foreach (var item in contextKprSources)
            {
                if (item.Stats is not { ContextKpr: { } ratio } stats) continue;
                stats.Validate();
                var sample = stats.KprCounts is { } counts
                    ? KprSample(counts, stats.CombatMatchCountUnverified, stats.Matches)
                    : "counter sample unavailable";
                var matches = stats.KprCounts?.ReportedMatchTotal is { } reported
                    ? $"Steam-reported match total: {reported.ToString("N0", CultureInfo.InvariantCulture)} (context only; not verified as the K/R sample)"
                    : "Steam-reported match total unavailable";
                report.AppendLine($"- {item.Source}: {Number(ratio)} | {sample} | Steam counter ratio; matching modes and periods are unverified | {matches} | context only, not assessed; no K/R warning line applied.");
            }
            report.AppendLine("These values can cover different matches, modes or periods and are not directly comparable.");
        }

        if (flaggedTypes.Length > 0)
        {
            report.AppendLine("\nWHY THERE ARE RED FLAGS");
            var referenced = false;
            foreach (var (item, sc) in flagged)
            {
                var source = item.Source;
                report.AppendLine($"- {sc.Check.Name} of {CheckValue(sc.Check)} crosses the warning line ({Relation(sc.Check)} {ThresholdValue(sc.Check)}, {sc.Check.RuleSource}; source: {source}; sample: {Sample(sc)}). {Meaning(sc.Check.Name)}");
                if (sc.Check.Name == "Weapon accuracy spread" && source == "cstracker.gg public page")
                    report.AppendLine("  This spread uses website-displayed percentages, which may be rounded.");
                var scope = ProPlayerStats.Scope(item, sc.Panel);
                if (!ProPlayerStats.CanCompare(item.Url.Host, scope, sc.Check.Name)) continue;
                var examples = proStats is not null && sc.Matches is > 0
                    ? ProPlayerStats.Examples(proStats, id, source, scope, sc.Check, sc.Matches.Value).ToArray()
                    : [];
                if (examples.Length == 0)
                    report.AppendLine("  Pro context: no same-source, same-panel example with known sample sizes available.");
                else
                {
                    referenced = true;
                    report.AppendLine($"  Top Pro Players examples (context only; {source}; {scope}):");
                    foreach (var (player, row) in examples)
                    {
                        var age = DateTimeOffset.UtcNow - row.FetchedAt >= TimeSpan.FromDays(1) ? "; over 24 hours old" : "";
                        report.AppendLine(FormattableString.Invariant(
                            $"    {player}: {row.Value:G}{row.Unit} | {row.Matches:G} matches | fetched {row.FetchedAt:u}{age}"));
                    }
                }
            }
            if (flagged.Any(sc => sc.Sample.Matches is < 100))
                report.AppendLine("- Some flags come from samples under 100 matches; short samples can be affected by a hot streak.");
            if (flagged.Any(sc => sc.Sample.Matches is null && sc.Sample.KprCounts is null))
                report.AppendLine("- Some flags have no verified match count; their sample size cannot be assessed from the available data.");
            if (referenced)
                report.AppendLine("Pro examples are individual account samples, not a normal range or a validated benchmark. Even in the same panel, modes, rank, opposition and dates played may differ. These examples do not set warning lines or affect the verdict.");
            report.AppendLine("These are heuristic screening lines, not a validated cheat detector. Skill, smurfing, weapon choice, mode or rank can also produce them.");
        }
        else if (perWebsite.Length > 0)
        {
            report.AppendLine("\nWHY THERE ARE NO RED FLAGS");
            report.AppendLine("No assessed metric crossed its heuristic warning line; this does not establish a normal range or clean play:");
            var repeatedMetricNames = repeatedMetrics.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (item, checks) in perWebsite)
                foreach (var sc in checks)
                {
                    if (repeatedMetricNames.Contains(sc.Check.Name)) continue;
                    report.AppendLine($"- {item.Source}: {sc.Check.Name} {CheckValue(sc.Check)} did not cross its warning line ({Relation(sc.Check)} {ThresholdValue(sc.Check)}; sample: {Sample(sc)}).");
                }
            if (allChecks.Any(sc => sc.Matches is null or < 100))
                report.AppendLine("Some values come from samples under 100 matches or with an unknown match count and are treated as tentative, not ignored.");
        }

        report.AppendLine("\nLIMITS: screening lines are heuristic, not validated anti-cheat models, and cannot confirm either cheating or clean play. Website benchmarks and trust ratings are not cheating probabilities. Overlapping panels within a website are shown once (largest sample); different websites are never merged.");
        report.AppendLine("NEXT STEP: " + (flaggedTypes.Length > 0
            ? "Review full demos in context; statistics alone do not identify a specific cheating behavior. Do not accuse or penalize based on these results."
            : perWebsite.Length == 0
                ? "Obtain public match data before drawing a statistical conclusion."
                : cautions.Length > 0
                    ? "The statistical screen found no flags, but this account's ban history makes demo review worthwhile if their play looked suspicious."
                    : "No statistical reason to flag this player was found. If their play looked suspicious, review full demos."));
        report.AppendLine("The Evidence tab contains the full per-metric breakdown, source details, access errors, and account/privacy context.");
        return report.ToString();
    }

    private static string Relation(MetricCheck check) => check.Comparison switch
    {
        MetricComparison.AtLeast => ">=",
        MetricComparison.Above => ">",
        MetricComparison.Below => "<",
        _ => throw new FormatException("Unknown screening comparison.")
    };

    private static string PlayerName(IReadOnlyList<Evidence> evidence)
    {
        string?[] Names(string host) => evidence.Where(item => item.Url.Host == host)
            .Select(item => item.DisplayName).Where(name => name is not null)
            .Distinct(StringComparer.Ordinal).ToArray();
        var steam = Names("steamcommunity.com");
        if (steam.Length == 0) steam = Names("api.steampowered.com");
        if (steam.Length > 1) return "unavailable (conflicting Steam names)";
        var fallback = Names("cswatch.gg");
        if (steam.Length == 0 && fallback.Length > 1) return "unavailable (conflicting CSWatch names)";
        var name = steam.Length == 1 ? steam[0] : fallback.Length == 1 ? fallback[0] : null;
        return name is null ? "unavailable" :
            "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" +
            (steam.Length == 0 ? " (CSWatch; may be outdated)" : "");
    }

    private static string Sample(SampleCheck sample)
    {
        if (sample.Check.Name == "Kills per round" && sample.KprCounts is { } counts)
            return KprSample(counts, sample.MatchCountUnverified, sample.Matches);
        return sample.Matches is { } count
            ? $"{count.ToString(CultureInfo.InvariantCulture)} matches"
            : sample.MatchCountUnverified && sample.Check.Name == "Kills per round"
                ? "match count not verified" : "unknown size";
    }

    private static string KprSample(KprCounters counts, bool matchCountUnverified, int? matches)
    {
        static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        return $"{Count(counts.Kills)} reported kills / {Count(counts.Rounds)} reported rounds" +
            (matchCountUnverified ? "; match count not verified" :
                matches is { } count ? $"; {Count(count)} matches" : "");
    }

    private static string Meaning(string name) => name switch
    {
        "K/D" => "This crosses the app's K/D screening rule; skill gaps, opponents and sample selection can also explain it.",
        "Headshot kills" => "This crosses the app's headshot screening rule; weapon selection and sample size matter.",
        "Win rate" => "This crosses the app's win-rate screening rule; skill gaps, team composition and sample selection can also explain it.",
        "Kills per round" => "Skill gaps, modes and sample selection can also affect this value.",
        "Weapon accuracy spread" => "A narrow spread crosses the app's screening rule; weapon usage and shot selection can also explain it.",
        "Utility damage per match" => "Elevated K/D with low utility damage crosses the app's screening rule; role, modes and play style can also explain it.",
        "Recent-vs-lifetime K/D jump" => "These overlapping sample averages differ; improvement, opponents and modes can explain the gap. It does not establish an activation date or assistance.",
        "Opening duel win rate" => "This crosses the app's opening-duel screening rule; skill, positioning and team support can also explain it.",
        "K/D oscillation blocks" => "Both high and low blocks crossed the app's count rule. Their order is not tested, and this does not establish toggled assistance.",
        "Map K/D outlier ratio" => "A large map gap crosses the app's screening rule; comfort-map skill, opponents and modes can also explain it.",
        "Accuracy (not headshot percentage)" => "This exceeds the website's published cutoff, but sniper-heavy players legitimately reach 45-55% overall accuracy; check the sniper kill share in Evidence before reading this as assistance.",
        _ => "This value is beyond the website's own published suspicious cutoff."
    };

    private static void AppendSections(StringBuilder report, Evidence item, ScreeningRules rules)
    {
        foreach (var section in item.Sections ?? [])
        {
            report.AppendLine($"\n{section.Title}");
            report.AppendLine(section.Text);
            if (section.Stats is { } stats && (stats.HasMetrics || stats.ContextWinRate is not null ||
                stats.ContextHeadshots is not null || stats.ContextKpr is not null))
            {
                AppendStats(report, stats, rules);
                if (stats.HasMetrics)
                {
                    var flags = Indicators(stats, rules);
                    report.AppendLine(flags.Count == 0 ? "Indicators: none."
                        : "Indicators: " + string.Join("; ", flags) + ".");
                }
                else report.AppendLine("Displayed values are context only; no metrics from this section were assessed.");
            }
            if (section.Checks is { Count: > 0 })
            {
                report.AppendLine("Threshold comparisons (tentative):");
                foreach (var check in section.Checks) AppendCheck(report, check);
            }
        }
    }

    internal sealed record SampleCheck(MetricCheck Check, int? Matches, KprCounters? KprCounts = null,
        bool MatchCountUnverified = false)
    {
        internal PublicSection? Panel { get; init; }
        internal DateTimeOffset? DataUpdatedAt { get; init; }
    }

    internal static IReadOnlyList<SampleCheck> WebsiteChecks(Evidence item, ScreeningRules rules)
    {
        var deduped = WebsitePanelChecks(item, rules).Select(row => row.Sample with { Panel = row.Panel }).GroupBy(sample => sample.Check.Name)
            .Select(group => group.Aggregate((best, next) => (next.Matches, best.Matches) switch
            {
                ({ } larger, { } smaller) when larger > smaller => next,
                ({ } smaller, { } larger) when smaller < larger => best,
                ({ }, null) => next,
                ({ } equal, { } previous) when equal == previous &&
                    next.DataUpdatedAt is { } newer &&
                    (best.DataUpdatedAt is null || newer > best.DataUpdatedAt) => next,
                _ when next.DataUpdatedAt is { } newer &&
                    (best.DataUpdatedAt is null || newer > best.DataUpdatedAt) => next,
                _ => best
            }))
            .ToArray();
        foreach (var sample in deduped) _ = sample.Check.IsFlagged;
        return deduped;
    }

    internal static IEnumerable<(PublicSection? Panel, SampleCheck Sample)> WebsitePanelChecks(
        Evidence item, ScreeningRules rules, bool includeShortHeadshots = false)
    {
        if (item.Stats is { HasMetrics: true } stats)
            foreach (var check in CoreChecks(stats, rules, includeShortHeadshots))
                yield return (null, CreateSampleCheck(stats, check, item.DataUpdatedAt));
        foreach (var section in item.Sections ?? [])
        {
            var website = section.Checks ?? [];
            var core = section.Stats is { HasMetrics: true } extra ? CoreChecks(extra, rules, includeShortHeadshots) : [];
            var merged = core.Where(check => !website.Any(other => other.Name == check.Name)).Concat(website).ToArray();
            if (section.Stats?.Matches == 0 && merged.Length > 0)
                throw new FormatException("Cannot assess performance metrics with a zero-match sample.");
            foreach (var check in merged)
                yield return (section, section.Stats is { } sample
                    ? CreateSampleCheck(sample, check, section.DataUpdatedAt ?? item.DataUpdatedAt)
                    : new SampleCheck(check, null) { DataUpdatedAt = section.DataUpdatedAt ?? item.DataUpdatedAt });
        }
    }

    private static int? SampleMatches(PlayerStats stats, MetricCheck check) =>
        stats.CombatMatchCountUnverified && check.Name is ("K/D" or "Headshot kills" or "Kills per round")
            ? null : stats.Matches;

    private static SampleCheck CreateSampleCheck(PlayerStats stats, MetricCheck check, DateTimeOffset? dataUpdatedAt) =>
        new(check, SampleMatches(stats, check), stats.KprCounts,
            stats.CombatMatchCountUnverified && check.Name is ("K/D" or "Headshot kills" or "Kills per round"))
        { DataUpdatedAt = dataUpdatedAt };

    private static IReadOnlyList<MetricCheck> CoreChecks(PlayerStats stats, ScreeningRules rules, bool includeShortHeadshots = false)
    {
        stats.Validate();
        if (stats.Matches == 0) throw new FormatException("Cannot assess performance metrics with a zero-match sample.");
        var checks = new List<MetricCheck>();
        foreach (var rule in CoreRules)
            if (rule.Read(stats) is { } value)
            {
                // Headshot percentage is too volatile on tiny samples; one pop-off game must not
                // trip a core indicator. The value is still displayed, it just cannot flag alone.
                if (!includeShortHeadshots && rule.Name == "Headshot kills" && !stats.CombatMatchCountUnverified && stats.Matches < rules.HeadshotMinimumMatches) continue;
                checks.Add(new MetricCheck(rule.Name, value, rule.Threshold(rules), rule.Suffix));
            }
        return checks;
    }

    private static void AppendCheck(StringBuilder report, MetricCheck check)
    {
        var flagged = check.IsFlagged;
        var relation = check.Comparison switch
        {
            MetricComparison.AtLeast => ">=",
            MetricComparison.Above => ">",
            MetricComparison.Below => "<",
            _ => throw new FormatException("Unknown screening comparison.")
        };
        report.AppendLine($"  {check.Name}: {CheckValue(check)} | {(flagged ? "FLAGGED" : "NO FLAG")} | warning if {relation} {ThresholdValue(check)} ({check.RuleSource})" +
            (check.BetterThanPercent is { } percentile ? $" | website benchmark: beats {percentile}%" : ""));
    }

    private static List<string> Indicators(PlayerStats stats, ScreeningRules rules) =>
        CoreChecks(stats, rules).Where(check => check.IsFlagged)
            .Select(check => CoreRules.First(rule => rule.Name == check.Name).Indicator + " >= " +
                (check.Unit == "%" ? ThresholdValue(check) :
                    Math.Round(check.Threshold, 2) == check.Threshold
                        ? check.Threshold.ToString("0.00", CultureInfo.InvariantCulture)
                        : ThresholdValue(check))).ToList();

    private static string Number(double? value, string suffix = "") =>
        value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + suffix : "unknown";

    private static string CheckValue(MetricCheck check) => NumberNearThreshold(check.Value, check.Threshold, check.Unit);

    private static string ThresholdValue(MetricCheck check) =>
        check.Threshold.ToString("G", CultureInfo.InvariantCulture) + check.Unit;

    internal static string NumberNearThreshold(double value, double threshold, string suffix = "") =>
        value != threshold && Number(value) == Number(threshold)
            ? value.ToString("G", CultureInfo.InvariantCulture) + suffix
            : Number(value, suffix);

    private static void AppendStats(StringBuilder report, PlayerStats stats, ScreeningRules rules)
    {
        stats.Validate();
        string Metric(double? value, string suffix = "") =>
            value.HasValue ? Number(value, suffix) : "not available in this sample";
        var kpr = stats.Kpr is { } value
            ? NumberNearThreshold(value, rules.KillsPerRoundThreshold)
            : stats.ContextKpr is { } context
                ? Number(context) + " (lifetime counter ratio; context only, not assessed)"
                : "not available in this sample";
        var winRate = stats.WinRate is null && stats.ContextWinRate is { } displayed
            ? Number(displayed, "%") + " (website displayed; context only)"
            : Metric(stats.WinRate, "%");
        var headshots = stats.Headshots is null && stats.ContextHeadshots is { } displayedHeadshots
            ? Number(displayedHeadshots, "%") + " (website displayed; context only)"
            : Metric(stats.Headshots, "%");
        report.AppendLine($"Matches: {stats.Matches?.ToString(CultureInfo.InvariantCulture) ?? "sample size not provided"} | K/D: {Metric(stats.Kd)} | HS: {headshots} | Win rate: {winRate} | K/R: {kpr}");
        if (stats.CombatMatchCountUnverified)
            report.AppendLine("Combat metric match counts are unverified; the reported match total is not established as the sample for the kill, death, headshot and round counters.");
    }
}
