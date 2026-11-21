using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CheaterOrNot.Core;

public static class WebsiteDataParser
{
    private static readonly int[] CsTrackerTrendDays = [7, 14, 21, 28, 35, 42, 49, 56, 63];
    internal static readonly string[] CsTrackerSectionIds =
    [
        "player-profile-section", "player-stats-section", "player-kills-section",
        "player-details-section", "player-clutches-section", "player-maps-section",
        "player-grenades-section", "player-weapons-section"
    ];

    public static DateTimeOffset? CsTrackerLastSync(string html, string id)
    {
        using var document = ProfileDocument(html);
        VerifyCsTrackerIdentity(document, id);
        var timestamps = new List<DateTimeOffset>();
        foreach (var element in document.QuerySelectorAll("[data-last-sync], [data-last-synced], [data-updated-at], time[datetime]"))
        {
            var raw = element.GetAttribute("data-last-sync") ?? element.GetAttribute("data-last-synced") ??
                element.GetAttribute("data-updated-at") ?? element.GetAttribute("datetime");
            var context = string.Join(" ",
                element.TextContent,
                element.ParentElement?.TextContent ?? "",
                element.ParentElement?.PreviousElementSibling?.TextContent ?? "");
            if (raw is null || !(
                element.HasAttribute("data-last-sync") || element.HasAttribute("data-last-synced") ||
                element.HasAttribute("data-updated-at") ||
                Regex.IsMatch(context, @"\b(?:last\s+)?(?:sync(?:ed)?|updated(?:\s+at)?)\b",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))))
                continue;
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new FormatException("CSTracker published an invalid profile data synchronization timestamp.");
            timestamps.Add(timestamp.ToUniversalTime());
        }
        var distinct = timestamps.Distinct().ToArray();
        if (distinct.Length > 1)
            throw new FormatException("CSTracker published conflicting profile data synchronization timestamps.");
        return distinct.Length == 1 ? distinct[0] : null;
    }

    public static IReadOnlyList<PublicSection> CsTrackerSections(string html, string id, ScreeningRules? rules = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        using var document = ProfileDocument(html);
        VerifyCsTrackerIdentity(document, id);
        var result = new List<PublicSection>();
        var optionalNotes = new List<string>();
        void ReadSection(string title, Action read)
        {
            var start = result.Count;
            try { read(); }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                result.RemoveRange(start, result.Count - start);
                result.Add(new PublicSection(title + " unavailable",
                    $"Parsing failed: {ex.Message} Only this section was discarded; other verified samples are retained."));
            }
        }
        ReadSection("Profile results", () =>
        {
            var totals = document.QuerySelectorAll("#player-profile-section [data-header-results='all']");
            if (totals.Length > 1) throw new FormatException("Ambiguous CSTracker profile totals.");
            if (totals.Length == 1)
            {
                var counts = Regex.Match(totals[0].GetAttribute("title") ?? "",
                    @"^All (\d+) matches: (\d+) wins, (\d+) losses, (\d+) ties\. Win rate leaves ties out\.$",
                    RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!counts.Success || !int.TryParse(counts.Groups[1].Value, out var matches) ||
                    !int.TryParse(counts.Groups[2].Value, out var wins) ||
                    !int.TryParse(counts.Groups[3].Value, out var losses) ||
                    !int.TryParse(counts.Groups[4].Value, out var ties) ||
                    (long)wins + losses + ties != matches || wins + (long)losses == 0)
                    throw new FormatException("Invalid CSTracker all-match win/loss/tie totals.");
                var decisiveMatches = wins + losses;
                var rate = 100.0 * wins / decisiveMatches;
                var rateCells = totals[0].Children.Where(cell => cell.Children.Any(child =>
                    child.TextContent.Trim().Equals("win rate", StringComparison.OrdinalIgnoreCase))).ToArray();
                var display = rateCells.Length == 1
                    ? Regex.Match(rateCells[0].TextContent, @"(\d+(?:\.\d+)?)%", RegexOptions.None, TimeSpan.FromSeconds(1))
                    : Match.Empty;
                if (!display.Success || !double.TryParse(display.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var displayedRate) || Math.Abs(displayedRate - rate) > 0.51)
                    throw new FormatException("CSTracker's displayed win rate does not match its public result totals.");
                result.Add(new PublicSection($"Profile results: {matches} total matches",
                    $"Wins: {wins} | Losses: {losses} | Ties: {ties} | Displayed win rate: {displayedRate.ToString("0.##", CultureInfo.InvariantCulture)}% " +
                    $"(computed: {rate.ToString("0.##", CultureInfo.InvariantCulture)}%).\n" +
                    $"Win rate excludes ties ({decisiveMatches} decided matches). This is separate from the recent analysis panel and retrieved match-history sample.",
                    new PlayerStats(Matches: decisiveMatches, WinRate: rate)));
            }
        });
        double? recentKd = null;
        var recentCount = 0;
        ReadSection("Recent analysis", () =>
        {
            var panels = document.QuerySelectorAll("#player-stats-section");
            if (panels.Length > 1) throw new FormatException("Ambiguous CSTracker analysis panel.");
            if (panels.Length == 1)
            {
                var sampleLabels = panels[0].QuerySelectorAll("header span").Select(span =>
                    Regex.Match(span.TextContent.Trim(), @"^latest\s+(\d+)\s+matches$",
                        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))).Where(match => match.Success).ToArray();
                var cards = panels[0].QuerySelectorAll("[data-histogram-config]");
                if (cards.Length > 0)
                {
                    if (sampleLabels.Length != 1 || !int.TryParse(sampleLabels[0].Groups[1].Value, out var count) || count <= 0)
                        throw new FormatException("CSTracker analysis-panel sample count could not be verified.");
                    var lines = new List<(string Key, string Text)>();
                    var checks = new List<MetricCheck>();
                    var keys = new HashSet<string>();
                    double? kd = null;
                    foreach (var card in cards)
                    {
                        ReadSection("Recent analysis metric", () =>
                        {
                            using var config = JsonDocument.Parse(card.GetAttribute("data-histogram-config")!);
                            string RequiredString(string name) =>
                                config.RootElement.ValueKind == JsonValueKind.Object &&
                                config.RootElement.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
                                property.GetString() is { } text
                                    ? text : throw new FormatException($"Missing CSTracker analysis metadata: {name}.");
                            var key = RequiredString("valueKey");
                            (string Name, string Unit, string Format) expected = key switch
                            {
                                "ttd" => ("Time to damage (TTD)", "ms", "ms"),
                                "xhair" => ("Preaim", " deg", "deg"),
                                "aim" => ("Aim offset", " deg", "deg"),
                                "kd" => ("K/D", "", "ratio"),
                                "rating" => ("HLTV rating", "", "ratio"),
                                "kast" => ("KAST", "%", "pct"),
                                "adr" => ("Average damage per round (ADR)", "", "dec1"),
                                "acc" => ("Accuracy (not headshot percentage)", "%", "pct"),
                                _ => throw new FormatException("Unrecognized CSTracker analysis metric.")
                            };
                            var values = card.QuerySelectorAll(".items-baseline > .tabular-nums");
                            if (!keys.Add(key))
                            {
                                lines.RemoveAll(existing => existing.Key == key);
                                checks.RemoveAll(existing => existing.Name == expected.Name);
                                if (key == "kd") kd = null;
                                throw new FormatException($"Duplicate CSTracker analysis metric: {expected.Name}.");
                            }
                            if (values.Length != 1 || RequiredString("format") != expected.Format)
                                throw new FormatException("Ambiguous CSTracker analysis metric.");
                            var raw = values[0].TextContent.Trim();
                            var suffix = expected.Format == "deg" ? "\u00b0" : expected.Unit.Trim();
                            if (suffix.Length > 0 && !raw.EndsWith(suffix, StringComparison.Ordinal))
                                throw new FormatException("Unexpected CSTracker analysis metric unit.");
                            var numeric = suffix.Length == 0 ? raw : raw[..^suffix.Length].Trim();
                            if (!double.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                                !double.IsFinite(value) || value < 0 || expected.Format == "pct" && value > 100)
                                throw new FormatException("Invalid CSTracker analysis metric value.");
                            var line = $"{expected.Name}: {value.ToString("0.##", CultureInfo.InvariantCulture)}{expected.Unit}";
                            int? percentile = null;
                            var benchmark = card.QuerySelectorAll("[title^='Better than ']");
                            if (benchmark.Length > 0)
                            {
                                var match = benchmark.Length == 1 ? Regex.Match(benchmark[0].GetAttribute("title")!,
                                    @"^Better than (\d{1,3})% of tracked players over their latest (\d+) matches$",
                                    RegexOptions.None, TimeSpan.FromSeconds(1)) : Match.Empty;
                                if (benchmark.Length != 1)
                                {
                                    line += " | website benchmark unavailable: multiple benchmark labels; metric retained";
                                }
                                else if (!match.Success)
                                {
                                    line += " | website benchmark unavailable: unrecognized benchmark title format; metric retained";
                                }
                                else if (!int.TryParse(match.Groups[1].Value, out var percent) || percent > 100 ||
                                    !int.TryParse(match.Groups[2].Value, out var benchmarkSample) || benchmarkSample <= 0)
                                {
                                    line += " | website benchmark unavailable: invalid percentile or benchmark sample; metric retained";
                                }
                                else
                                {
                                    // The website sometimes computes a benchmark over a different window than the
                                    // panel headline; keep the data and state the benchmark's own window honestly.
                                    line += $" | website benchmark: beats {percent}% of tracked players" +
                                        (benchmarkSample == count ? "" : $" (benchmark window: latest {benchmarkSample} matches)");
                                    percentile = percent;
                                }
                            }
                            ReadSection($"Recent analysis {expected.Name} cutoff", () =>
                            {
                                var cutoff = Number(config.RootElement, "suspiciousCutoff");
                                if (cutoff.HasValue)
                                {
                                    if (!double.IsFinite(cutoff.Value) || cutoff < 0 ||
                                        !config.RootElement.TryGetProperty("lowerIsSuspicious", out var direction) ||
                                        direction.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                        throw new FormatException("Invalid CSTracker website screening cutoff.");
                                    line += $" | website warning cutoff: {(direction.GetBoolean() ? "below" : "above")} " +
                                        cutoff.Value.ToString("0.##", CultureInfo.InvariantCulture) + expected.Unit;
                                    checks.Add(new MetricCheck(expected.Name, value, cutoff.Value, expected.Unit,
                                        direction.GetBoolean() ? MetricComparison.Below : MetricComparison.Above,
                                        "CSTracker website cutoff", percentile));
                                }
                            });
                            if (key == "kd") kd = value;
                            lines.Add((key, line));
                        });
                    }
                    result.Add(new PublicSection($"Recent analysis: latest {count} matches",
                        string.Join("\n", lines.Select(line => line.Text)) + "\nWebsite benchmarks describe its tracked-player population, not cheating probabilities. Published cutoffs are assessed tentatively, not treated as validated detections.",
                        new PlayerStats(count, kd), Checks: checks));
                    recentKd = kd;
                    recentCount = count;
                }
            }
        });
        ReadSection("Website trust rating", () =>
        {
            var panels = document.QuerySelectorAll("#player-stats-section");
            if (panels.Length > 1) throw new FormatException("Ambiguous CSTracker analysis panel.");
            if (panels.Length == 0) return;
            var trust = panels[0].QuerySelectorAll("#player-trust-rating-card .text-5xl");
            if (trust.Length > 1) throw new FormatException("Ambiguous CSTracker trust rating.");
            if (trust.Length == 1)
            {
                if (!double.TryParse(trust[0].TextContent.Trim(), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score) || score is < 0 or > 100)
                    throw new FormatException("Invalid CSTracker trust rating.");
                var updated = panels[0].QuerySelector("#player-trust-rating-card time[datetime]")?.GetAttribute("datetime");
                if (updated is not null)
                {
                    if (!DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                        throw new FormatException("Invalid CSTracker trust-rating update timestamp.");
                    updated = timestamp.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
                }
                result.Add(new PublicSection("Website-generated trust rating (not scored)",
                    $"CSTracker trust rating: {score.ToString("0.##", CultureInfo.InvariantCulture)}%." +
                    (updated is null ? "" : $" Website update timestamp: {updated}.") +
                    " This is CSTracker's score, not Valve Trust Factor, proof of clean play, or a cheating probability."));
            }
        });
        double? lifetimeKd = null;
        int? lifetimeMatches = null;
        var detailedSampleAvailable = false;
        ReadSection("Detailed combat aggregates", () =>
        {
            var detailed = document.QuerySelectorAll("#player-details-section");
            if (detailed.Length > 1) throw new FormatException("Ambiguous CSTracker detailed aggregate section.");
            if (detailed.Length == 1)
            {
                var sampleLabels = detailed[0].QuerySelectorAll("header span").Select(span =>
                    Regex.Match(span.TextContent.Trim(), @"^(\d+(?:,\d{3})*) matches$",
                        RegexOptions.None, TimeSpan.FromSeconds(1))).Where(match => match.Success).ToArray();
                if (sampleLabels.Length != 1 || !int.TryParse(sampleLabels[0].Groups[1].Value, NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out var matches) || matches <= 0)
                    throw new FormatException("CSTracker detailed aggregate sample count could not be verified.");
                string Field(string label)
                {
                    var fields = detailed[0].QuerySelectorAll(".items-baseline")
                        .Where(row => row.Children.Length == 2 && row.Children[0].TextContent.Trim() == label).ToArray();
                    return fields.Length == 1 ? fields[0].Children[1].TextContent.Trim()
                        : throw new FormatException($"Missing or ambiguous CSTracker detailed {label}.");
                }
                const string whole = @"(?:\d{1,3}(?:,\d{3})+|\d+)";
                int? kills = null, deaths = null, assists = null, headshots = null;
                double? kd = null, hsRate = null, displayedHs = null, contextHs = null;
                ReadSection("Detailed K/D", () =>
                {
                    var kda = Regex.Match(Field("K/D/A"), $@"^({whole})\s*/\s*({whole})\s*/\s*({whole})$",
                        RegexOptions.None, TimeSpan.FromSeconds(1));
                    if (!kda.Success ||
                        !int.TryParse(kda.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var k) ||
                        !int.TryParse(kda.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d) ||
                        !int.TryParse(kda.Groups[3].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var a))
                        throw new FormatException("Invalid CSTracker detailed kill/death/assist totals.");
                    (kills, deaths, assists) = (k, d, a);
                    kd = d > 0 ? (double)k / d : null;
                });
                ReadSection("Detailed headshot percentage", () =>
                {
                    var hs = Regex.Match(Field("HS kills"), $@"^({whole})\s*\((\d+(?:\.\d+)?)%\)$",
                        RegexOptions.None, TimeSpan.FromSeconds(1));
                    if (!hs.Success ||
                        !int.TryParse(hs.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count) ||
                        !double.TryParse(hs.Groups[2].Value, NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var shown) || shown > 100 || count > kills)
                        throw new FormatException("Invalid CSTracker detailed headshot totals.");
                    var rate = kills > 0 ? 100.0 * count / kills.Value : (double?)null;
                    var decimalPoint = hs.Groups[2].Value.IndexOf('.');
                    var decimals = decimalPoint < 0 ? 0 : hs.Groups[2].Value.Length - decimalPoint - 1;
                    var displayedUnit = Math.Pow(10, -decimals);
                    if (rate.HasValue && Math.Abs(shown - rate.Value) > displayedUnit + 1e-8)
                        throw new FormatException("CSTracker's displayed headshot percentage does not match its detailed kill totals.");
                    headshots = count;
                    displayedHs = shown;
                    hsRate = rate;
                    if (rate is null) contextHs = shown;
                });
                double? displayedWinRate = null;
                ReadSection("Detailed win rate", () =>
                {
                    var rows = detailed[0].QuerySelectorAll(".items-baseline")
                        .Where(row => row.Children.Length == 2 && row.Children[0].TextContent.Trim() == "Win rate").ToArray();
                    if (rows.Length == 0) return;
                    if (rows.Length != 1) throw new FormatException("Ambiguous CSTracker detailed win rate.");
                    var raw = rows[0].Children[1].TextContent.Trim();
                    if (!Regex.IsMatch(raw, @"^\d+(?:\.\d+)?%$", RegexOptions.None, TimeSpan.FromSeconds(1)) ||
                        !double.TryParse(raw[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) ||
                        !double.IsFinite(rate) || rate is < 0 or > 100)
                        throw new FormatException("Invalid CSTracker detailed win rate.");
                    displayedWinRate = rate;
                });
                var stats = new PlayerStats(matches, kd, hsRate) { ContextWinRate = displayedWinRate, ContextHeadshots = contextHs };
                stats.Validate();
                var detailChecks = new List<MetricCheck>();
                var utilityNote = "";
                ReadSection("Detailed utility damage", () =>
                {
                    var utilityRows = detailed[0].QuerySelectorAll(".items-baseline")
                        .Where(row => row.Children.Length == 2 && row.Children[0].TextContent.Trim() == "Util dmg / match").ToArray();
                    if (utilityRows.Length > 1) throw new FormatException("Ambiguous CSTracker detailed utility damage.");
                    if (utilityRows.Length == 0)
                    {
                        utilityNote = "\nUtility-to-frag screen not assessed: this sample did not provide utility damage per match.";
                        return;
                    }
                    if (!double.TryParse(utilityRows[0].Children[1].TextContent.Trim(),
                        NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out var utility) || !double.IsFinite(utility) || utility < 0)
                        throw new FormatException("Invalid CSTracker utility damage per match.");
                    if (!kd.HasValue)
                    {
                        utilityNote = $"\nUtility-to-frag screen not assessed: this sample's K/D could not be verified (utility damage {utility.ToString("0.##", CultureInfo.InvariantCulture)} per match kept as context).";
                        return;
                    }
                    // Gate the utility screen on elevated K/D within this same sample.
                    if (kd >= rules.UtilityMinimumKd)
                    {
                        detailChecks.Add(new MetricCheck("Utility damage per match", utility, rules.UtilityDamageThreshold, "", MetricComparison.Below));
                        utilityNote = $"\nUtility-to-frag screen active because this sample's K/D is at least {rules.UtilityMinimumKd.ToString(CultureInfo.InvariantCulture)}.";
                    }
                    else utilityNote = $"\nUtility-to-frag screen not triggered or assessed: it applies only when this sample's K/D is at least {rules.UtilityMinimumKd.ToString(CultureInfo.InvariantCulture)} (utility damage {utility.ToString("0.##", CultureInfo.InvariantCulture)} per match kept as context).";
                });
                var combatTotals = new List<string>();
                if (kills is not null) combatTotals.Add($"Kills: {kills}");
                if (deaths is not null) combatTotals.Add($"Deaths: {deaths}");
                if (assists is not null) combatTotals.Add($"Assists: {assists}");
                if (headshots is not null) combatTotals.Add($"Headshot kills: {headshots} (website displays {displayedHs!.Value.ToString("0.##", CultureInfo.InvariantCulture)}%)");
                result.Add(new PublicSection($"Detailed combat aggregates: {matches} matches",
                    (combatTotals.Count > 0 ? string.Join(" | ", combatTotals) + "\n" : "") +
                    (kd is not null ? "K/D is computed from this section's own kill and death totals. " : "") +
                    (hsRate is not null ? "Headshot kill percentage is computed from this section's own kill totals. " :
                        contextHs is not null ? "The displayed headshot percentage is context only because its kill denominator could not be verified. " : "") +
                    (displayedWinRate is not null ? "The displayed win rate is context only; any assessed win rate uses a separately verified sample. " : "") +
                    "K/R is not inferred without a round count." + utilityNote,
                    stats, Checks: detailChecks.Count > 0 ? detailChecks : null));
                lifetimeKd = kd;
                lifetimeMatches = matches;
                detailedSampleAvailable = true;
            }
        });
        if (!detailedSampleAvailable)
            optionalNotes.Add("Utility-to-frag screen: not assessed; the detailed combat sample was unavailable or could not be verified.");
        if (recentKd is { } recentValue && lifetimeKd is { } lifetimeValue &&
            recentValue >= rules.RecentMinimumKd && recentValue > lifetimeValue)
        {
            result.Add(new PublicSection(
                $"Recent-vs-lifetime K/D jump: latest {recentCount} vs {lifetimeMatches} lifetime matches",
                $"Recent K/D {recentValue.ToString("0.##", CultureInfo.InvariantCulture)} vs lifetime {lifetimeValue.ToString("0.##", CultureInfo.InvariantCulture)}. " +
                "A recent-vs-lifetime difference can reflect improvement, opponents, modes or sample selection; these overlapping averages do not establish when or why performance changed.",
                new PlayerStats(recentCount),
                Checks: [new MetricCheck("Recent-vs-lifetime K/D jump", recentValue - lifetimeValue, rules.RecentKdJumpThreshold)]));
        }
        else if (recentKd is { } recent && lifetimeKd is { } lifetime)
            optionalNotes.Add($"Recent-vs-lifetime K/D jump: not assessed; recent K/D {recent.ToString("0.##", CultureInfo.InvariantCulture)} ({recentCount} matches) must be at least {rules.RecentMinimumKd.ToString(CultureInfo.InvariantCulture)} and greater than detailed K/D {lifetime.ToString("0.##", CultureInfo.InvariantCulture)} ({lifetimeMatches} matches).");
        else
            optionalNotes.Add("Recent-vs-lifetime K/D jump: not assessed; both the recent and detailed K/D samples are required.");
        ReadSection("Weapon accuracy variance", () =>
        {
            var weaponSections = document.QuerySelectorAll("#player-weapons-section");
            if (weaponSections.Length > 1) throw new FormatException("Ambiguous CSTracker weapons section.");
            if (weaponSections.Length == 1 && weaponSections[0].QuerySelectorAll("table") is { Length: 1 } weaponTables)
            {
                var headers = weaponTables[0].QuerySelectorAll("thead th").Select(cell => cell.TextContent.Trim()).ToArray();
                int Column(string name)
                {
                    var index = Array.IndexOf(headers, name);
                    return index >= 0 ? index : throw new FormatException($"Missing CSTracker weapons column: {name}.");
                }
                if (headers.Length > 0)
                {
                    var (weaponColumn, killsColumn, shotsColumn, accuracyColumn) =
                        (Column("Weapon"), Column("Kills"), Column("Shots"), Column("Accuracy"));
                    var qualifying = new List<(string Name, double Accuracy, string? Category)>();
                    var totalKills = 0L;
                    var sniperKills = 0L;
                    foreach (var row in weaponTables[0].QuerySelectorAll("tbody tr"))
                    {
                        var cells = row.QuerySelectorAll("td");
                        if (cells.Length != headers.Length) throw new FormatException("Malformed CSTracker weapons row.");
                        string Cell(int column) => cells[column].QuerySelector(".tabular-nums")?.TextContent.Trim() ?? "";
                        var name = cells[weaponColumn].TextContent.Trim();
                        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
                            throw new FormatException("Invalid CSTracker weapon name.");
                        if (!int.TryParse(Cell(killsColumn), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var kills) ||
                            kills < 0 || !int.TryParse(Cell(shotsColumn), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var shots) || shots < 0)
                            throw new FormatException($"Invalid CSTracker weapon counts: {name}.");
                        var category = WeaponCategory(name);
                        totalKills += kills;
                        if (category == "sniper") sniperKills += kills;
                        // Qualification keeps tiny samples and shotless utility (grenades/knife) out of the variance screen.
                        if (kills < rules.WeaponMinimumKills || shots < rules.WeaponMinimumShots) continue;
                        var accuracy = Regex.Match(Cell(accuracyColumn), @"^(\d+(?:\.\d+)?)%$", RegexOptions.None, TimeSpan.FromSeconds(1));
                        if (!accuracy.Success || !double.TryParse(accuracy.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var percent) || percent > 100)
                            throw new FormatException($"Invalid CSTracker weapon accuracy: {name}.");
                        qualifying.Add((name, percent, category));
                    }
                    // Same-category weapons (e.g., AK + both M4s) legitimately cluster tightly, so the
                    // homogenization screen requires at least two recognized weapon categories.
                    var categories = qualifying.Select(weapon => weapon.Category)
                        .Where(category => category is not null).Distinct().Count();
                    if (qualifying.Count >= rules.WeaponMinimumCount && categories >= rules.WeaponMinimumCategories)
                    {
                        var spread = qualifying.Max(weapon => weapon.Accuracy) - qualifying.Min(weapon => weapon.Accuracy);
                        var sniperShare = totalKills > 0 ? 100.0 * sniperKills / totalKills : 0;
                        result.Add(new PublicSection(
                            $"Weapon accuracy variance: {qualifying.Count} weapons with at least {rules.WeaponMinimumKills:N0} kills and {rules.WeaponMinimumShots:N0} shots",
                            string.Join(" | ", qualifying.Select(weapon =>
                                $"{weapon.Name} {weapon.Accuracy.ToString("0.##", CultureInfo.InvariantCulture)}%")) +
                            $"\nSniper (AWP/SSG 08/SCAR-20/G3SG1) kill share: {sniperShare.ToString("0.#", CultureInfo.InvariantCulture)}% of {totalKills} lifetime kills. " +
                            "The spread uses the website's displayed percentages, which may be rounded. " +
                            "Weapon usage and shot selection can affect the spread; it does not establish assistance.",
                            Checks: [new MetricCheck("Weapon accuracy spread", spread, rules.WeaponSpreadPercentThreshold, "%", MetricComparison.Below)]));
                    }
                    else
                        optionalNotes.Add($"Weapon accuracy spread: not assessed; {qualifying.Count} qualifying weapons across {categories} recognized categories. At least {rules.WeaponMinimumCount} weapons with {rules.WeaponMinimumKills:N0}+ kills and {rules.WeaponMinimumShots:N0}+ shots across {rules.WeaponMinimumCategories} categories are required.");
                }
                else optionalNotes.Add("Weapon accuracy spread: not assessed; weapon table headers were not available.");
            }
            else if (weaponSections.Length == 0)
                optionalNotes.Add("Weapon accuracy spread: not assessed; the CSTracker weapon table was not available.");
            else throw new FormatException("No unique CSTracker weapons table.");
        });
        ReadSection("Map K/D consistency", () =>
        {
            var mapSections = document.QuerySelectorAll("#player-maps-section");
            if (mapSections.Length > 1) throw new FormatException("Ambiguous CSTracker maps section.");
            if (mapSections.Length == 1 && mapSections[0].QuerySelector("div.grid") is { } mapGrid)
            {
                var maps = new List<(string Name, int Matches, double Kd)>();
                foreach (var card in mapGrid.Children)
                {
                    var name = card.QuerySelector("span.font-medium")?.TextContent.Trim();
                    var matchLabel = card.QuerySelectorAll("span")
                        .Select(span => Regex.Match(span.TextContent.Trim(), @"^(\d+(?:,\d{3})*) match(?:es)?$",
                            RegexOptions.None, TimeSpan.FromSeconds(1)))
                        .Where(match => match.Success).ToArray();
                    var kdLabels = card.QuerySelectorAll("div")
                        .Where(label => label.Children.Length == 0 && label.TextContent.Trim() == "K/D").ToArray();
                    if (name is null && matchLabel.Length == 0 && kdLabels.Length == 0) continue;
                    if (string.IsNullOrEmpty(name) || name.Length > 30 || name.Any(char.IsControl) ||
                        matchLabel.Length != 1 || kdLabels.Length != 1 ||
                        !int.TryParse(matchLabel[0].Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var mapMatches) ||
                        mapMatches <= 0 ||
                        !double.TryParse(kdLabels[0].PreviousElementSibling?.TextContent.Trim(),
                            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mapKd) ||
                        !double.IsFinite(mapKd) || mapKd < 0 ||
                        maps.Any(existing => existing.Name == name))
                        throw new FormatException("Invalid or ambiguous CSTracker map performance card.");
                    maps.Add((name, mapMatches, mapKd));
                }
                // Require three sampled maps and elevated best-map K/D before comparing the gap.
                var qualified = maps.Where(map => map.Matches >= rules.MapMinimumMatches).ToList();
                if (qualified.Count >= rules.MapMinimumCount)
                {
                    var best = qualified.OrderByDescending(map => map.Kd).First();
                    var others = qualified.Where(map => map != best).Select(map => map.Kd).OrderBy(kd => kd).ToArray();
                    var median = (others[(others.Length - 1) / 2] + others[others.Length / 2]) / 2;
                    if (best.Kd >= rules.MapMinimumBestKd && median > 0)
                    {
                        result.Add(new PublicSection(
                            $"Map K/D consistency: {qualified.Count} maps with at least {rules.MapMinimumMatches} matches",
                            string.Join(" | ", qualified.Select(map =>
                                $"{map.Name} {map.Kd.ToString("0.##", CultureInfo.InvariantCulture)} ({map.Matches})")) +
                            $"\nBest map {best.Name} at K/D {best.Kd.ToString("0.##", CultureInfo.InvariantCulture)} vs a {median.ToString("0.##", CultureInfo.InvariantCulture)} median across the rest. " +
                            "Comfort-map skill, opponents and modes can produce map differences; this gap alone does not establish assistance.",
                            Checks: [new MetricCheck("Map K/D outlier ratio", best.Kd / median, rules.MapOutlierRatioThreshold)]));
                    }
                    else
                        optionalNotes.Add($"Map K/D outlier ratio: not assessed; {qualified.Count} maps have at least {rules.MapMinimumMatches} matches, but the best qualified map's K/D must be at least {rules.MapMinimumBestKd.ToString(CultureInfo.InvariantCulture)} and the other-map median must be positive.");
                }
                else optionalNotes.Add($"Map K/D outlier ratio: not assessed; {qualified.Count} maps have at least {rules.MapMinimumMatches} matches; at least {rules.MapMinimumCount} are required.");
            }
            else optionalNotes.Add("Map K/D outlier ratio: not assessed; the CSTracker map table was not available.");
        });
        if (optionalNotes.Count > 0)
            result.Add(new PublicSection("Optional CSTracker checks not assessed", string.Join("\n", optionalNotes)));
        foreach (var sectionId in CsTrackerSectionIds.Skip(2))
        {
            ReadSection("Public tab: " + sectionId, () =>
            {
                var sections = document.QuerySelectorAll("#" + sectionId);
                if (sections.Length > 1) throw new FormatException("Ambiguous CSTracker public tab section.");
                if (sections.Length == 0) return;
                var text = PageParser.PublicTabText(sections[0].OuterHtml);
                if (text.Length > 0)
                {
                    var denominatorNote = sectionId == "player-kills-section"
                        ? "\nProvider note: kill-breakdown totals can use a different eligible-event scope or denominator from the detailed combat total. They are retained as published context and are not reconciled, added, or substituted into scored K/D or headshot calculations."
                        : sectionId is "player-clutches-section" or "player-maps-section" or "player-grenades-section" or "player-weapons-section"
                            ? "\nProvider note: this tab's counts and denominators describe the website's own scope. They may differ from other CSTracker panels and are not combined with them; only named, independently verified app calculations are scored."
                            : "";
                    result.Add(new PublicSection("Public tab: " + sectionId["player-".Length..^"-section".Length] + " (context; sample as stated by website)",
                        text + denominatorNote, DetailedOnly: true));
                }
            });
        }
        return result;
    }

    public static IReadOnlyList<string> CsTrackerProgressionContext(string html, string id)
    {
        using var document = ProfileDocument(html);
        VerifyCsTrackerIdentity(document, id);
        var context = new List<string>();

        var projectedRank = document.QuerySelectorAll("#player-ranks-section[data-matchmaking-rank]")
            .Select(section => section.GetAttribute("data-matchmaking-rank"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var credentials = document.QuerySelectorAll("[data-header-credentials]").ToArray();
        if (credentials.Length == 1 || projectedRank.Length == 1)
        {
            var matchmaking = projectedRank.Length == 1
                ? projectedRank
                : credentials[0].QuerySelectorAll("img[alt^='Matchmaking rank ']")
                    .Select(image => image.GetAttribute("alt"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (matchmaking.Length == 1 && matchmaking[0] is { } rank)
                context.Add($"CS2 matchmaking rank (CSTracker demo-observed): {rank["Matchmaking rank ".Length..]}. " +
                    "This is source-attributed rank context, not a live Valve-certified rank.");
        }

        var profileLevel = document.QuerySelectorAll("#player-ranks-section[data-profile-level]")
            .Select(section => section.GetAttribute("data-profile-level"))
            .Concat(document.QuerySelectorAll("[data-demo-profile] [title^='CS2 profile level ']")
                .Select(element => element.GetAttribute("title")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (profileLevel.Length == 1 && profileLevel[0] is { } level)
            context.Add(level + " (CSTracker demo-observed).");

        var displayedMedals = document.QuerySelectorAll("#player-ranks-section[data-displayed-medal]")
            .Select(section => section.GetAttribute("data-displayed-medal"))
            .Concat(document.QuerySelectorAll(
                "img[data-demo-profile-medal][title^='Displayed: '][alt$=' Service Medal']")
                .Select(image => image.GetAttribute("alt")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (displayedMedals.Length == 1 && displayedMedals[0] is { } medal)
        {
            var year = Regex.Match(medal, @"^(\d{4}) Service Medal$",
                RegexOptions.None, TimeSpan.FromSeconds(1));
            context.Add(year.Success
                ? $"Displayed CS2 service medal: {year.Groups[1].Value}. CSTracker exposes the currently displayed medal, not a verifiable lifetime/yearly-medal count; total medals earned is unavailable."
                : $"Displayed CS2 service medal: {medal}. Prior years and total medals earned are unavailable.");
        }
        else
            context.Add("Displayed CS2 service medal: unavailable on this CSTracker page; the total earned cannot be inferred.");

        var rankScripts = document.QuerySelectorAll("#player-ranks-section script[data-player-json='ranks']").ToArray();
        if (rankScripts.Length == 1)
        {
            try
            {
                using var json = JsonDocument.Parse(rankScripts[0].TextContent);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException("CSTracker's rank history is not an array.");
                var premierFound = false;
                foreach (var series in json.RootElement.EnumerateArray())
                {
                    if (series.ValueKind != JsonValueKind.Object ||
                        !series.TryGetProperty("group", out var group) || group.ValueKind != JsonValueKind.String)
                        throw new FormatException("CSTracker returned an invalid rank series.");
                    if (group.GetString() == "premier")
                    {
                        premierFound = true;
                        if (series.TryGetProperty("points", out var premierPoints) &&
                            premierPoints.ValueKind == JsonValueKind.Array && premierPoints.GetArrayLength() > 0)
                        {
                            var latestPremier = premierPoints.EnumerateArray().LastOrDefault();
                            if (latestPremier.ValueKind == JsonValueKind.Object &&
                                latestPremier.TryGetProperty("y", out var rating) && rating.TryGetInt32(out var value) && value > 0)
                                context.Add($"Premier rating (CSTracker demo-observed): {value.ToString(CultureInfo.InvariantCulture)}. Not live Valve-certified data.");
                        }
                        continue;
                    }
                    if (group.GetString() != "matchmaking") continue;
                    if (!series.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                        !key.GetString()!.StartsWith("matchmaking:", StringComparison.Ordinal) ||
                        !series.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array ||
                        points.GetArrayLength() == 0)
                        continue;
                    JsonElement latest = default;
                    foreach (var point in points.EnumerateArray())
                    {
                        if (point.ValueKind == JsonValueKind.Object &&
                            point.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number)
                            if (latest.ValueKind == JsonValueKind.Undefined ||
                                x.GetDouble() > latest.GetProperty("x").GetDouble())
                                latest = point;
                    }
                    if (latest.ValueKind != JsonValueKind.Object ||
                        !latest.TryGetProperty("y", out var y) || !y.TryGetInt32(out var rankNumber) ||
                        rankNumber is < 1 or > 18)
                        continue;
                    var map = key.GetString()!["matchmaking:".Length..]
                        .Replace("de_", "", StringComparison.Ordinal)
                        .Replace("cs_", "", StringComparison.Ordinal);
                    var observed = latest.TryGetProperty("x", out var timestamp) &&
                        timestamp.TryGetDouble(out var unix) && unix >= 0 &&
                        unix <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                            ? $" (last observed {DateTimeOffset.FromUnixTimeSeconds((long)unix):yyyy-MM-dd})"
                            : "";
                    context.Add($"Competitive {map} rank (CSTracker demo-observed): {SkillGroup(rankNumber)} ({rankNumber}/18){observed}.");
                }
                if (premierFound && !context.Any(text => text.StartsWith("Premier rating (CSTracker", StringComparison.Ordinal)))
                    context.Add("Premier rating (CSTracker): unavailable/unranked from the published rank history.");
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                context.Add("CSTracker rank-history context unavailable: " + ex.Message);
            }
        }
        return context;

        static string SkillGroup(int rank) => rank switch
        {
            1 => "Silver I", 2 => "Silver II", 3 => "Silver III", 4 => "Silver IV",
            5 => "Silver Elite", 6 => "Silver Elite Master",
            7 => "Gold Nova I", 8 => "Gold Nova II", 9 => "Gold Nova III", 10 => "Gold Nova Master",
            11 => "Master Guardian I", 12 => "Master Guardian II", 13 => "Master Guardian Elite",
            14 => "Distinguished Master Guardian", 15 => "Legendary Eagle",
            16 => "Legendary Eagle Master", 17 => "Supreme Master First Class", 18 => "Global Elite",
            _ => throw new ArgumentOutOfRangeException(nameof(rank))
        };
    }

    public static PlayerStats CsTracker(string html, string id) => CsTracker([html], id);

    public static PlayerStats CsTracker(IReadOnlyList<string> pages, string id)
    {
        var rows = CsTrackerMatchRows(pages, id);
        double kills = rows.Sum(row => (double)row.Kills);
        double deaths = rows.Sum(row => (double)row.Deaths);
        if (rows.Count == 0 || deaths == 0) throw new FormatException("No usable CSTracker match sample.");
        var result = new PlayerStats(rows.Count, kills / deaths);
        result.Validate();
        return result;
    }

    public static IReadOnlyList<PublicSection> CsTrackerRecentWindows(
        IReadOnlyList<string> pages, string id, DateTimeOffset retrievedAt)
    {
        var rows = CsTrackerDatedMatchRows(pages, id);
        var result = new List<PublicSection>();
        foreach (var days in CsTrackerTrendDays)
        {
            var since = retrievedAt.UtcDateTime.AddDays(-days);
            var selected = rows.Where(row => row.PlayedAt >= since && row.PlayedAt <= retrievedAt.UtcDateTime)
                .ToArray();
            var title = $"Recent {days}-day CSTracker trend";
            if (selected.Length == 0)
            {
                result.Add(new PublicSection(title + " (not assessed)",
                    $"No identity-verified CSTracker match rows fell in the rolling {days}-day window ending {retrievedAt:u}."));
                continue;
            }
            var kills = selected.Sum(row => (long)row.Kills);
            var deaths = selected.Sum(row => (long)row.Deaths);
            if (deaths == 0)
            {
                result.Add(new PublicSection(title + " (not assessed)",
                    $"The window contains {selected.Length} matches and {kills.ToString(CultureInfo.InvariantCulture)} kills, but no deaths; K/D is undefined."));
                continue;
            }
            var stats = new PlayerStats(selected.Length, (double)kills / deaths);
            stats.Validate();
            result.Add(new PublicSection(title,
                $"Rolling window ending at retrieval time {retrievedAt:u}. Pooled {kills.ToString(CultureInfo.InvariantCulture)} kills / " +
                $"{deaths.ToString(CultureInfo.InvariantCulture)} deaths across {selected.Length} identity-verified unique matches. " +
                $"K/D is computed from the raw counters, not by averaging match K/D values. The {string.Join(", ", CsTrackerTrendDays)}-day windows overlap and must not be added together.",
                stats));
        }
        return result;
    }

    /// Counts high- and low-K/D blocks; it does not test alternation or detect toggling.
    public static PublicSection? CsTrackerOscillation(IReadOnlyList<string> pages, string id, ScreeningRules? rules = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        var blockSize = rules.OscillationBlockSize;
        var rows = CsTrackerMatchRows(pages, id);
        var blocks = new List<(double Kd, bool High, bool Low)>();
        for (var start = 0; start + blockSize <= rows.Count; start += blockSize)
        {
            long kills = 0, deaths = 0;
            for (var i = start; i < start + blockSize; i++)
            {
                kills += rows[i].Kills;
                deaths += rows[i].Deaths;
            }
            if (kills == 0 && deaths == 0) throw new FormatException("Empty CSTracker match block; sample discarded.");
            blocks.Add((deaths > 0 ? (double)kills / deaths : double.MaxValue,
                High: kills > 0 && (deaths == 0 || (double)kills / deaths >= rules.OscillationHighKd),
                Low: deaths > 0 && (double)kills / deaths <= rules.OscillationLowKd));
        }
        if (blocks.Count < rules.OscillationMinimumBlocks) return null;
        var high = blocks.Count(block => block.High);
        var low = blocks.Count(block => block.Low);
        return new PublicSection(
            $"Performance oscillation: {blocks.Count} consecutive blocks of {blockSize} matches",
            "Pooled K/D per block (newest first): " + string.Join(", ", blocks.Select(block =>
                block.Kd == double.MaxValue ? "deathless" : block.Kd.ToString("0.##", CultureInfo.InvariantCulture))) +
            $".\nDominant blocks (K/D >= {rules.OscillationHighKd.ToString(CultureInfo.InvariantCulture)}): {high} | Poor blocks (K/D <= {rules.OscillationLowKd.ToString(CultureInfo.InvariantCulture)}): {low}. " +
            "This screen counts high and low blocks, not their alternation. Changes in form, opponents or modes can produce both extremes; this is not evidence of toggled assistance.",
            new PlayerStats(blocks.Count * blockSize),
            Checks: [new MetricCheck("K/D oscillation blocks", Math.Min(high, low), rules.OscillationExtremeBlocks)]);
    }

    private static List<(int Kills, int Deaths)> CsTrackerMatchRows(IReadOnlyList<string> pages, string id)
        => CsTrackerDatedMatchRows(pages, id, false).Select(row => (row.Kills, row.Deaths)).ToList();

    private static List<(int Kills, int Deaths, DateTime PlayedAt)> CsTrackerDatedMatchRows(
        IReadOnlyList<string> pages, string id, bool requireTimestamp = true)
    {
        var seen = new HashSet<string>();
        var rows = new List<(int Kills, int Deaths, DateTime PlayedAt)>();
        foreach (var html in pages)
        {
            using var document = ProfileDocument(html);
            VerifyCsTrackerIdentity(document, id);
            var sections = document.QuerySelectorAll("#player-match-history-section");
            if (sections.Length != 1) throw new FormatException("No unique CSTracker match-history section.");
            var tables = sections[0].QuerySelectorAll("table");
            if (tables.Length != 1) throw new FormatException("No unique CSTracker match-history table.");
            var headers = tables[0].QuerySelectorAll("thead th")
                .Select(e => string.Concat(e.TextContent.Where(c => !char.IsWhiteSpace(c)))).ToArray();
            if (headers.Count(label => label == "K/D/A") != 1)
                throw new FormatException("CSTracker's kill/death/assist column could not be verified.");
            var column = Array.IndexOf(headers, "K/D/A");
            foreach (var row in tables[0].QuerySelectorAll("tbody tr"))
            {
                var links = row.QuerySelectorAll("a[href]").Where(a =>
                    Regex.IsMatch(a.GetAttribute("href")!, @"^/matches/\d+$", RegexOptions.None, TimeSpan.FromSeconds(1))).ToArray();
                if (links.Length != 1 || !seen.Add(links[0].GetAttribute("href")!))
                    throw new FormatException("Missing or duplicate CSTracker match identity; sample discarded.");
                var cells = row.Children;
                if (column >= cells.Length) throw new FormatException("Incomplete CSTracker match row.");
                var counts = string.Concat(cells[column].ChildNodes.OfType<IText>().Select(node => node.TextContent));
                var match = Regex.Match(counts, @"^\s*(\d+)\s*/\s*(\d+)\s*/\s*(\d+)\s*$",
                    RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!match.Success ||
                    !int.TryParse(match.Groups[1].Value, out var k) ||
                    !int.TryParse(match.Groups[2].Value, out var d) ||
                    !int.TryParse(match.Groups[3].Value, out _))
                    throw new FormatException("Invalid CSTracker match kill/death/assist counts.");
                var timestamps = row.QuerySelectorAll("[title^='played ']")
                    .Select(element => element.GetAttribute("title"))
                    .Where(text => text is not null)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (timestamps.Length == 0 && !requireTimestamp)
                {
                    rows.Add((k, d, default));
                    continue;
                }
                if (timestamps.Length != 1 ||
                    !DateTime.TryParseExact(timestamps[0]!["played ".Length..], "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var playedAt))
                    throw new FormatException("CSTracker match time could not be verified from its When-column tooltip.");
                rows.Add((k, d, playedAt));
            }
        }
        return rows;
    }

    public static Uri? CsTrackerOlderPage(string html, string id, int currentPage)
    {
        using var document = ProfileDocument(html);
        VerifyCsTrackerIdentity(document, id);
        var older = document.QuerySelectorAll("#player-match-history-section nav a[href]")
            .Where(a => a.TextContent.Trim().StartsWith("older", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (older.Length == 0) return null;
        var profile = Sources.Profile("cstracker.gg", id);
        if (older.Length != 1 || !Uri.TryCreate(profile, older[0].GetAttribute("href"), out var next) ||
            next.Scheme != "https" || next.Host != profile.Host || !next.IsDefaultPort ||
            next.UserInfo.Length != 0 || next.AbsolutePath != profile.AbsolutePath ||
            next.Query != $"?page={currentPage + 1}" ||
            next.Fragment is not ("" or "#player-match-history-section"))
            throw new FormatException("CSTracker returned an unverified older-match link; pagination stopped.");
        return new UriBuilder(next) { Fragment = "" }.Uri;
    }

    public static PlayerStats CsstScope(string html, string id) =>
        CsstScopeIfPresent(html, id) ?? throw new FormatException("No csst.at Scope performance panel was returned.");

    public static PlayerStats? CsstScopeIfPresent(string html, string id) => CsstScopeSample(html, id).Stats;

    public static (PlayerStats? Stats, IReadOnlyList<PublicSection> Sections) CsstScopeSample(string html, string id)
    {
        using var document = ProfileDocument(html);
        var text = PageParser.VisibleText(html);
        if (Regex.IsMatch(text,
            @"access denied|verify you are human|captcha|this profile is private|sign in to view (?:stats|statistics)|performing security verification",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new FormatException("csst.at returned an access/verification or restricted-profile page; statistics were discarded.");
        var panels = document.QuerySelectorAll(".scope-card__body .scope-grid[aria-label='Performance']");
        if (panels.Length == 0) return (null, []);
        if (panels.Length != 1) throw new FormatException("No unique csst.at Scope performance panel.");
        VerifySteamLink(document, id);
        // The card header publishes its own sample window ("Last N matches"); use it when present
        // instead of leaving the sample unknown. Never borrowed from another provider's card.
        int? matches = null;
        var card = panels[0].Closest(".scope-card");
        var periods = (card ?? document.DocumentElement).QuerySelectorAll(".scope-card__period");
        if (periods.Length > 1) throw new FormatException("Ambiguous csst.at Scope sample period.");
        if (periods.Length == 1)
        {
            var period = Regex.Match(periods[0].TextContent, @"Last\s+(\d+)\s+matches",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (period.Success && int.TryParse(period.Groups[1].Value, out var window) && window > 0)
                matches = window;
        }
        var sections = new List<PublicSection>();
        double? Read(string label)
        {
            var fields = panels[0].QuerySelectorAll(".scope-stat")
                .Where(e => e.QuerySelector("dt")?.TextContent.Trim() == label).ToArray();
            if (fields.Length == 0)
            {
                sections.Add(new PublicSection($"Scope {label} unavailable",
                    $"The Scope panel did not provide {label}; other valid metrics from this panel are retained."));
                return null;
            }
            var values = fields.Length == 1 ? fields[0].QuerySelectorAll("dd > .text-white").ToArray() : [];
            if (fields.Length != 1 || values.Length != 1 || !double.TryParse(values[0].TextContent.Trim(),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value < 0)
            {
                sections.Add(new PublicSection($"Scope {label} unavailable",
                    $"Parsing failed: Invalid or ambiguous csst.at Scope {label}. Other valid metrics from this panel are retained."));
                return null;
            }
            return value;
        }
        var result = new PlayerStats(Matches: matches, Kd: Read("K/D"), Kpr: Read("KPR"));
        result.Validate();
        return (result.HasMetrics ? result : null, sections);
    }

    public static IReadOnlyList<PublicSection> CsstScopeSections(string html, string id, ScreeningRules? rules = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        using var document = ProfileDocument(html);
        var panels = document.QuerySelectorAll(".scope-card__body .scope-grid[aria-label='Performance']");
        if (panels.Length != 1) return [];
        var duels = panels[0].QuerySelectorAll(".scope-stat")
            .Where(field => field.QuerySelector("dt")?.TextContent.Trim() == "Opening duels").ToArray();
        if (duels.Length == 0)
        {
            VerifySteamLink(document, id);
            return [new PublicSection("Opening duels (not assessed)",
                $"The Scope panel did not provide verified opening-duel counts; at least {rules.OpeningMinimumDuels} duels are required for this check.")];
        }
        if (duels.Length > 1) throw new FormatException("Ambiguous csst.at opening-duel entry.");
        VerifySteamLink(document, id);
        var tip = duels[0].QuerySelector("dt [data-tip]")?.GetAttribute("data-tip") ?? "";
        var counts = Regex.Match(tip, @"^First duels of the round won: (\d+)[\u2013-](\d+)$",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        var values = duels[0].QuerySelectorAll("dd > .text-white");
        if (!counts.Success || values.Length != 1 ||
            !int.TryParse(counts.Groups[1].Value, out var won) || !int.TryParse(counts.Groups[2].Value, out var lost) ||
            won < 0 || lost < 0 || won + lost == 0 ||
            !double.TryParse(values[0].TextContent.Trim().TrimEnd('%'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var displayed) || displayed > 100)
            throw new FormatException("Invalid csst.at opening-duel data.");
        var total = won + lost;
        var rate = 100.0 * won / total;
        if (Math.Abs(displayed - rate) > 0.51)
            throw new FormatException("csst.at's displayed opening-duel rate does not match its duel counts.");
        var text = $"Won {won} of {total} first duels of the round ({rate.ToString("0.#", CultureInfo.InvariantCulture)}%). " +
            "Opening-duel outcomes depend on skill, positioning, opponents and team support; this rate does not establish pre-information or assistance.";
        return [total >= rules.OpeningMinimumDuels
            ? new PublicSection("Opening duels (Scope panel)", text,
                Checks: [new MetricCheck("Opening duel win rate", rate, rules.OpeningWinPercentThreshold, "%")])
            : new PublicSection("Opening duels (Scope panel)",
                text + $" Fewer than {rules.OpeningMinimumDuels} recorded duels: context only, not scored.")];
    }

    private static string? WeaponCategory(string name) => name.ToUpperInvariant() switch
    {
        "AK-47" or "M4A4" or "M4A1-S" or "GALIL AR" or "FAMAS" or "AUG" or "SG 553" => "rifle",
        "AWP" or "SSG 08" or "SCAR-20" or "G3SG1" => "sniper",
        "MAC-10" or "MP9" or "MP7" or "MP5-SD" or "UMP-45" or "P90" or "PP-BIZON" => "SMG",
        "USP-S" or "GLOCK-18" or "P2000" or "P250" or "FIVE-SEVEN" or "TEC-9" or "CZ75-AUTO"
            or "DESERT EAGLE" or "DUAL BERETTAS" or "R8 REVOLVER" => "pistol",
        "NOVA" or "XM1014" or "MAG-7" or "SAWED-OFF" => "shotgun",
        "NEGEV" or "M249" => "machine gun",
        _ => null
    };

    public static AccountContext CsWatchContext(string html, string id)
    {
        using var document = ProfileDocument(html);
        var main = document.QuerySelector("main");
        if (main?.QuerySelector("h1") is null || !main.QuerySelectorAll("button").Any(button =>
            button.TextContent.Trim() == id || button.GetAttribute("aria-label") == $"Copy Steam ID {id}"))
            throw new FormatException("CSWatch's visible player identity could not be verified.");
        var headings = main.QuerySelectorAll("h1");
        var displayName = headings.Length == 1 && headings[0].Children.Length == 0
            ? ValidDisplayName(headings[0].TextContent) : null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tile in main.QuerySelectorAll(".v-tile"))
        {
            var label = tile.QuerySelector("span")?.TextContent.Trim();
            if (label is not ("Steam bans" or "Community reports" or "Account age" or "CS2 hours")) continue;
            var value = tile.QuerySelector("b")?.TextContent.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 80 || value.Any(char.IsControl))
                throw new FormatException($"Invalid CSWatch account field: {label}.");
            if (fields.TryGetValue(label, out var previous) && previous != value)
                throw new FormatException($"Conflicting CSWatch account field: {label}.");
            fields[label] = value;
        }
        if (fields.Count == 0 && displayName is null)
            throw new FormatException("No supported public CSWatch account fields or player name were returned.");
        var cautions = new List<string>();
        if (fields.TryGetValue("Steam bans", out var bans) && bans is not ("None" or "0" or "none"))
            cautions.Add($"CSWatch reports Steam bans: {bans}. These can include game bans across different games, not just CS Overwatch bans.");
        return new AccountContext(
            "Identity verified. " + (fields.Count == 0 ? "No supported public account fields were returned" :
                string.Join("; ", fields.Select(field => $"{field.Key}: {field.Value}"))) +
            (displayName is null ? ". CSWatch player name unavailable (missing, ambiguous or invalid)" :
                ". CSWatch player name: " + displayName + " (website-reported; may not be current)") +
            ". Community reports and risk scores are not verified cheating evidence. " +
            "The gameplay mechanics panel is Leetify-derived and not imported; the Leetify source already represents that data.",
            cautions) { DisplayName = displayName };
    }

    public static string CsStatsContext(string html, string id)
    {
        using var document = ProfileDocument(html);
        VerifySteamLink(document, id);
        var heading = document.QuerySelector("h1")?.TextContent.Trim();
        if (string.IsNullOrEmpty(heading) || heading.Length > 80 || heading.Any(char.IsControl))
            throw new FormatException("CSStats's public player header could not be verified.");
        var trackingDisabled = document.All.Any(element => element.Children.Length == 0 &&
            element.TextContent.Trim().Equals("Tracking Not Enabled", StringComparison.OrdinalIgnoreCase));
        return "Public player header obtained; exact Steam profile link verified. " +
            (trackingDisabled
                ? "The website says tracking is not enabled and statistics may be incomplete. "
                : "No comparable performance summary was returned. ") +
            "Gameplay statistics did not become available in this check. Rank and season win counts are not used as a match sample.";
    }

    public static IReadOnlyList<string> LeetifyRankContext(JsonElement root)
    {
        static JsonElement Unique(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object) return default;
            var fields = parent.EnumerateObject().Where(p => p.Name == name).ToArray();
            return fields.Length == 1 ? fields[0].Value : default;
        }
        var privacy = Unique(root, "privacy_mode");
        if (privacy.ValueKind != JsonValueKind.String || privacy.GetString() != "public")
            return ["Premier rating: unknown; Competitive map ranks: unknown (Leetify did not identify this profile as public)."];
        var ranks = Unique(root, "ranks");
        if (ranks.ValueKind != JsonValueKind.Object)
            return ["Premier rating: unknown; Competitive map ranks: unknown (missing, invalid or duplicate Leetify ranks object)."];
        var result = new List<string>();
        var premier = Unique(ranks, "premier");
        result.Add(premier.ValueKind == JsonValueKind.Number && premier.TryGetInt32(out var rating) && rating > 0
            ? $"Premier rating (Leetify-tracked): {rating.ToString(CultureInfo.InvariantCulture)}. Rank update time unavailable."
            : "Premier rating: unknown/unranked (missing, zero, invalid or duplicate value).");
        var competitive = Unique(ranks, "competitive");
        if (competitive.ValueKind != JsonValueKind.Array || competitive.GetArrayLength() == 0)
            result.Add("Competitive map ranks: unknown (no unambiguous rank records returned).");
        else
        {
            var entries = new List<(string Map, JsonElement Rank)>();
            foreach (var entry in competitive.EnumerateArray())
            {
                var map = Unique(entry, "map_name");
                if (map.ValueKind != JsonValueKind.String || map.GetString() is not { } name ||
                    !Regex.IsMatch(name, "^[a-z0-9_]{1,64}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    result.Add("Competitive map rank: unknown (invalid or ambiguous map record).");
                    continue;
                }
                entries.Add((name, Unique(entry, "rank")));
            }
            foreach (var group in entries.GroupBy(entry => entry.Map, StringComparer.Ordinal))
            {
                var rank = group.First().Rank;
                result.Add(group.Count() == 1 && rank.ValueKind == JsonValueKind.Number &&
                    rank.TryGetInt32(out var value) && value is >= 1 and <= 18
                    ? $"Competitive {group.Key} rank (Leetify-tracked): {value}/18. Rank update time unavailable."
                    : $"Competitive {group.Key} rank: unknown/unranked (missing, zero, invalid or duplicate record).");
            }
        }
        return result;
    }

    public static AccountContext SteamContext(string xml, string id, DateTimeOffset? asOf = null)
    {
        if (xml.Length > PageParser.MaxCharacters) throw new FormatException("Steam public XML exceeds the size limit.");
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = PageParser.MaxCharacters
        });
        XElement root;
        try { root = XDocument.Load(reader).Root ?? throw new FormatException("Missing Steam public XML profile."); }
        catch (XmlException ex) { throw new FormatException("Steam returned invalid or unsupported public profile XML.", ex); }
        if (root.Name != "profile" || root.Elements("steamID64").Count() != 1 || root.Element("steamID64")?.Value != id)
            throw new FormatException("Steam public XML returned a missing or mismatched SteamID.");
        var privacy = root.Element("privacyState")?.Value;
        if (privacy is not ("public" or "private" or "friendsonly"))
            throw new FormatException("Steam returned a missing or unrecognized public-profile privacy state.");
        var fields = new List<string>();
        var cautions = new List<string>();
        // Ban status is public on Steam regardless of the profile's privacy setting, so it is read
        // for private/friends-only accounts too. Game bans (e.g., Overwatch convictions) are not in
        // this XML export; CSWatch's ban count covers those.
        if (root.Element("vacBanned") is { } vac)
        {
            if (vac.Value is not ("0" or "1")) throw new FormatException("Invalid Steam public VAC flag.");
            fields.Add("VAC-ban flag: " + (vac.Value == "1" ? "set" : "not set"));
            if (vac.Value == "1")
                cautions.Add("Steam's VAC-ban flag is set. The flagged game is not identified; this is not proof of current CS2 cheating.");
        }
        if (root.Element("isLimitedAccount") is { } limited)
        {
            if (limited.Value is not ("0" or "1")) throw new FormatException("Invalid Steam limited-account flag.");
            // Context only, never scored: a limited (free) account is a common burner signal but also
            // describes many legitimate new players.
            if (limited.Value == "1") fields.Add("Limited (free) account: yes");
        }
        var names = root.Elements("steamID").ToArray();
        var name = names.Length == 1 && !names[0].HasElements ? ValidDisplayName(names[0].Value) : null;
        if (privacy != "public")
            return new AccountContext($"Steam marks this account {privacy}; private details were not imported" +
                (fields.Count > 0 ? ", but ban status is public regardless of privacy: " + string.Join("; ", fields) : "") +
                (name is null ? ". Steam display name unavailable in the public response" :
                    ". Steam display name publicly returned: " + name) +
                ". Game bans (such as Overwatch convictions) are not in this export. Missing data is not evidence of cheating.", cautions)
                {
                    DisplayName = name,
                    SmurfReviewContext = ["Steam account age: unknown (profile is not public; membership date was not imported)."]
                };
        if (name is not null) fields.Add("Steam display name: " + name);
        var ageContext = "Steam account age: unknown (membership date missing, ambiguous or invalid).";
        var today = (asOf ?? DateTimeOffset.UtcNow).UtcDateTime.Date;
        var members = root.Elements("memberSince").ToArray();
        if (members.Length > 0)
        {
            if (members.Length == 1 && !members[0].HasElements &&
                DateTime.TryParseExact(members[0].Value, "MMMM d, yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created) && created <= today)
            {
                fields.Add("Member since: " + created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var days = (today - created).Days;
                ageContext = $"Steam account age: {days.ToString(CultureInfo.InvariantCulture)} {(days == 1 ? "day" : "days")} " +
                    $"(member since {created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; as of {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} UTC).";
            }
            else fields.Add("Steam membership date unavailable (ambiguous or unrecognized)");
        }
        if (fields.Count == 0) throw new FormatException("Steam's public XML contained no supported account fields.");
        return new AccountContext("Exact SteamID verified. " + string.Join("; ", fields) +
            (name is null ? ". Steam display name unavailable (missing, ambiguous or invalid)" : "") +
            ". A VAC flag does not identify a game or establish current CS2 cheating. Game bans (such as Overwatch convictions) are not in this export.", cautions)
            { DisplayName = name, SmurfReviewContext = [ageContext] };
    }

    internal static string? ValidDisplayName(string? value)
    {
        if (value is null) return null;
        // Steam can include directional isolates in otherwise usable names. Do not copy them into reports.
        var name = value.Replace("\u2066", "").Replace("\u2067", "")
            .Replace("\u2068", "").Replace("\u2069", "").Trim();
        return string.IsNullOrEmpty(name) || name.Length > 80 || name.Any(c => char.IsControl(c) ||
            c is '\u2028' or '\u2029' or '\u061c' or '\u200e' or '\u200f' ||
            c is >= '\u202a' and <= '\u202e' or >= '\u206a' and <= '\u206f') ? null : name;
    }

    private static IDocument ProfileDocument(string html)
    {
        if (html.Length > PageParser.MaxCharacters) throw new FormatException("Public profile HTML exceeds the size limit.");
        return new HtmlParser().ParseDocument(html);
    }

    private static void VerifySteamLink(IDocument document, string id)
    {
        if (!document.QuerySelectorAll("a[href]").Any(link =>
            Uri.TryCreate(link.GetAttribute("href"), UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host == "steamcommunity.com" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath.TrimEnd('/') == $"/profiles/{id}"))
            throw new FormatException("The public website's Steam profile identity could not be verified.");
    }

    private static void VerifyCsTrackerIdentity(IDocument document, string id)
    {
        var ranks = document.QuerySelectorAll("#player-ranks-section[data-vaclive-steam-id]");
        if (ranks.Length == 0) { VerifySteamLink(document, id); return; }
        if (ranks.Length != 1 || ranks[0].GetAttribute("data-vaclive-steam-id") != id)
            throw new FormatException("CSTracker's profile-owner SteamID is missing or mismatched.");
    }

    public static PublicSection LeetifyDatedHistory(JsonElement history, string id, DateTimeOffset retrievedAt)
    {
        if (history.ValueKind != JsonValueKind.Array)
            throw new FormatException("Leetify match history is not an array.");

        static JsonElement Field(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                throw new FormatException("Invalid Leetify match-history object.");
            var fields = obj.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
            if (fields.Length != 1)
                throw new FormatException($"Leetify match history has a missing or duplicate {name} field.");
            return fields[0].Value;
        }

        static int Count(JsonElement player, string name)
        {
            var field = Field(player, name);
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var value) || value < 0)
                throw new FormatException($"Invalid Leetify match {name}.");
            return value;
        }

        static int? OptionalCount(JsonElement player, string name)
        {
            var fields = player.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
            return fields.Length == 1 && fields[0].Value.ValueKind == JsonValueKind.Number &&
                fields[0].Value.TryGetInt32(out var value) && value >= 0 ? value : null;
        }

        static string? OptionalLabel(JsonElement obj, string name)
        {
            var fields = obj.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
            if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String) return null;
            var label = fields[0].Value.GetString()?.Trim();
            return string.IsNullOrEmpty(label) || label.Length > 40 || label.Any(char.IsControl) ? null : label;
        }

        var matches = new List<(DateTime Date, int Kills, int Deaths, int? Rounds, int? Headshots, int? Damage,
            string? Source, string? Map)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var today = retrievedAt.UtcDateTime.Date;
        foreach (var match in history.EnumerateArray())
        {
            var matchId = Field(match, "id");
            if (matchId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(matchId.GetString()) ||
                !ids.Add(matchId.GetString()!))
                throw new FormatException("Leetify match history contains a missing or duplicate match ID.");
            var timestamp = Field(match, "finished_at");
            var text = timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : null;
            if (text is null || !Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var finished) ||
                finished > retrievedAt)
                throw new FormatException("Leetify match history contains an invalid or future match timestamp.");
            var players = Field(match, "stats");
            if (players.ValueKind != JsonValueKind.Array)
                throw new FormatException("Leetify match player statistics are missing.");
            var targetPlayers = new List<JsonElement>();
            foreach (var player in players.EnumerateArray())
            {
                var playerId = Field(player, "steam64_id");
                if (playerId.ValueKind != JsonValueKind.String)
                    throw new FormatException("Leetify match player identity is invalid.");
                if (playerId.GetString() == id) targetPlayers.Add(player);
            }
            if (targetPlayers.Count != 1)
                throw new FormatException("Leetify match does not contain exactly one matching player.");
            var playerStats = targetPlayers[0];
            var kills = Count(playerStats, "total_kills");
            var headshots = OptionalCount(playerStats, "total_hs_kills");
            if (headshots > kills) headshots = null;
            matches.Add((finished.UtcDateTime.Date, kills, Count(playerStats, "total_deaths"),
                OptionalCount(playerStats, "rounds_count"), headshots, OptionalCount(playerStats, "total_damage"),
                OptionalLabel(match, "data_source"), OptionalLabel(match, "map_name")));
        }

        if (matches.Count == 0)
            return new PublicSection("7-day and 15-day trends (Leetify official API)",
                "No match history was returned. No date-based trend was inferred.");

        var lines = new List<string>();
        foreach (var windowDays in new[] { 7, 15 })
        {
            lines.Add($"\n{windowDays}-day windows:");
            foreach (var group in matches.GroupBy(m => (today - m.Date).Days / windowDays).OrderBy(g => g.Key))
            {
                DateTime end;
                DateTime start;
                try
                {
                    end = today.AddDays(-windowDays * group.Key);
                    start = end.AddDays(1 - windowDays);
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new FormatException("Leetify match history contains a timestamp outside the supported date range.");
                }
                var kills = group.Sum(m => (long)m.Kills);
                var deaths = group.Sum(m => (long)m.Deaths);
                var kd = deaths == 0 ? "undefined (no deaths)" : ((double)kills / deaths).ToString("0.##", CultureInfo.InvariantCulture);
                lines.Add($"{start:yyyy-MM-dd} to {end:yyyy-MM-dd} UTC: {group.Count()} matches | {kills} kills / {deaths} deaths | K/D {kd}");
                var roundsSample = group.Where(m => m.Rounds > 0).ToArray();
                var hsSample = group.Where(m => m.Headshots is not null).ToArray();
                var damageSample = group.Where(m => m.Rounds > 0 && m.Damage is not null).ToArray();
                static string Ratio(long numerator, long denominator) => denominator == 0
                    ? "undefined (zero denominator)" : ((double)numerator / denominator).ToString("0.##", CultureInfo.InvariantCulture);
                static string Labels(IEnumerable<string?> labels) => string.Join(", ", labels
                    .GroupBy(label => label ?? "unavailable", StringComparer.Ordinal)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}: {item.Count()}"));
                lines.Add(roundsSample.Length == 0
                    ? "  K/R: unavailable (no valid round counts)."
                    : $"  K/R: {Ratio(roundsSample.Sum(m => (long)m.Kills), roundsSample.Sum(m => (long)m.Rounds!.Value))} " +
                      $"({roundsSample.Length}/{group.Count()} matches with valid rounds).");
                var hsKills = hsSample.Sum(m => (long)m.Kills);
                lines.Add(hsSample.Length == 0
                    ? "  Headshot kills: unavailable (no valid headshot counts)."
                    : $"  Headshot kills: {(hsKills == 0 ? "undefined (zero kills)" : Ratio(100 * hsSample.Sum(m => (long)m.Headshots!.Value), hsKills) + "%")} " +
                      $"({hsSample.Length}/{group.Count()} matches with valid headshot counts; percentage of kills, not head accuracy).");
                lines.Add(damageSample.Length == 0
                    ? "  Damage per round: unavailable (no valid damage and round counts)."
                    : $"  Reported damage per round (ADR-like): {Ratio(damageSample.Sum(m => (long)m.Damage!.Value), damageSample.Sum(m => (long)m.Rounds!.Value))} " +
                      $"({damageSample.Length}/{group.Count()} matches with valid damage and rounds).");
                lines.Add($"  Data sources: {Labels(group.Select(m => m.Source))} | Maps: {Labels(group.Select(m => m.Map))}.");
            }
        }
        return new PublicSection("7-day and 15-day trends (Leetify official API)",
            "Within each view, UTC calendar windows do not overlap and are newest first; the newest includes today. " +
            "The 7-day and 15-day views overlap and must not be added together. " +
            $"Only periods with returned matches are shown ({matches.Count} unique matches). " +
            "The API history may be incomplete; absent periods are not proof of no play. " +
            "K/D is total kills divided by total deaths in each period. Other ratios use only matches with valid relevant counters, " +
            "so their sample sizes may differ; missing, duplicate or invalid counters are excluded. " +
            "The exact scope of the API's rounds and damage counters has not been independently verified. Small samples are volatile. " +
            "Context only, not scored: changes in form, opponents or modes can explain spikes.\n" + string.Join("\n", lines));
    }

    public static PlayerStats Leetify(JsonElement metadata, JsonElement recent, string id) =>
        LeetifySample(metadata, recent, id).Stats;

    public static (PlayerStats Stats, IReadOnlyList<PublicSection> Sections) LeetifySample(JsonElement metadata, JsonElement recent, string id)
    {
        VerifyId(metadata, id);
        var matches = Number(recent, "matchesPlayed");
        if (matches is null or <= 0 || !double.IsFinite(matches.Value) ||
            matches > int.MaxValue || matches != Math.Truncate(matches.Value))
            throw new FormatException("Leetify's public website returned no valid recent 5v5 match sample.");
        var sections = new List<PublicSection>();
        double? Metric(string key, string label, double? maximum = null)
        {
            try
            {
                var value = Number(recent, key);
                if (value is { } number && (!double.IsFinite(number) || number < 0 || number > maximum))
                    throw new FormatException($"Invalid Leetify {label}.");
                return value;
            }
            catch (FormatException ex)
            {
                sections.Add(new PublicSection($"Recent {label} unavailable",
                    $"Parsing failed: {ex.Message} Other valid metrics from this sample are retained."));
                return null;
            }
        }
        var kd = Metric("kdRatio", "K/D");
        var hs = Metric("headshotKillPercentage", "headshot kill percentage", 100);
        var winRate = Metric("winRate", "win rate", 1);
        var result = new PlayerStats((int)matches.Value, kd, hs, winRate * 100);
        result.Validate();
        if (!result.HasMetrics) throw new FormatException("The website returned no recognized performance metrics.");
        return (result, sections);
    }

    public static void VerifyId(JsonElement metadata, string id)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("steam64Id", out var value) || value.ToString() != id)
            throw new FormatException("The website returned a missing or mismatched SteamID; data was discarded.");
    }

    private static double? Number(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) ||
            value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new FormatException($"Unexpected website metric format: {key}.");
    }
}
