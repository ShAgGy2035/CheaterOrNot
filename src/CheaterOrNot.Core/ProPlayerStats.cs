using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CheaterOrNot.Core;

public sealed record FaceitLifetimeStats(double? Matches, double? Wins, double? WinRate, double? Kd, double? AverageKd)
{
    private (string Label, double? Value)[] Fields =>
        [("Matches", Matches), ("Wins", Wins), ("Win Rate %", WinRate), ("Average K/D Ratio", AverageKd)];

    internal void Validate()
    {
        if (Fields.All(field => field.Value is null))
            throw new FormatException("FACEIT returned no supported lifetime statistics fields.");
        if (Fields.Any(field => field.Value is { } value && (!double.IsFinite(value) || value < 0)) ||
            WinRate > 100 || Matches is { } matches && matches != Math.Truncate(matches) ||
            Wins is { } wins && wins != Math.Truncate(wins))
            throw new FormatException("Invalid FACEIT lifetime statistics.");
    }

    public string Describe() => string.Join(" | ", Fields.Where(field => field.Value.HasValue)
        .Select(field => $"{field.Label}: {field.Value!.Value.ToString("G", CultureInfo.InvariantCulture)}"));
}

public sealed record ProPlayer(string Name, string SteamId);
public sealed record ProMetric(string Source, string Scope, string Name, double Value, string Unit,
    double? Matches, DateTimeOffset FetchedAt, long? Kills = null, long? Rounds = null);
public sealed record ProPlayerSample(string SteamId, DateTimeOffset FetchedAt, FaceitLifetimeStats? Stats = null)
{
    public IReadOnlyList<ProMetric> Metrics { get; init; } = [];
}
public sealed record ProStatsSnapshot(DateTimeOffset LastAttempt, IReadOnlyList<ProPlayerSample> Samples)
{
    public IReadOnlyDictionary<string, IReadOnlyList<ProSourceResult>> SourceResults { get; init; } =
        new Dictionary<string, IReadOnlyList<ProSourceResult>>();
    public static ProStatsSnapshot Empty { get; } = new(DateTimeOffset.MinValue, []);
    public bool RefreshDue(DateTimeOffset now) => LastAttempt == DateTimeOffset.MinValue || now - LastAttempt >= TimeSpan.FromDays(1);
}
public sealed record ProSourceResult(string Source, string Status);

public sealed class ProPlayerStats
{
    public const string RankingUrl = "https://www.hltv.org/players/top20/2025";
    public static IReadOnlyList<ProPlayer> Roster { get; } = Array.AsReadOnly(new ProPlayer[]
    {
        new("ZywOo", "76561198113666193"),
        new("donk", "76561198386265483"),
        new("ropz", "76561197991272318"),
        new("m0NESY", "76561198074762801"),
        new("sh1ro", "76561198081484775"),
        new("molodoy", "76561198200982290"),
        new("flameZ", "76561197978835160"),
        new("frozen", "76561198068422762"),
        new("KSCERATO", "76561198058500492"),
        new("Spinx", "76561198063336407")
    });

    public static IEnumerable<string> RuleMetrics => GeminiWebsiteReview.Metrics;
    private const int CacheLimit = 512_000;
    private readonly string _path;

    public ProPlayerStats(string path) => _path = Path.GetFullPath(path);

    public async Task<ProStatsSnapshot> LoadAsync(CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > CacheLimit) throw new FormatException("The pro stats cache exceeds its size limit.");
            var data = new byte[file.Length];
            await file.ReadExactlyAsync(data, token);
            var snapshot = JsonSerializer.Deserialize<ProStatsSnapshot>(data)
                ?? throw new FormatException("The pro stats cache is empty.");
            if (snapshot.Samples is null || snapshot.LastAttempt == DateTimeOffset.MinValue ||
                snapshot.LastAttempt > DateTimeOffset.UtcNow.AddMinutes(5) || snapshot.Samples.Count > Roster.Count ||
                snapshot.Samples.Any(sample => sample is null || sample.Metrics is null ||
                    (sample.Stats is null && sample.Metrics.Count == 0) || sample.FetchedAt == DateTimeOffset.MinValue ||
                    sample.FetchedAt > snapshot.LastAttempt || !Roster.Any(player => player.SteamId == sample.SteamId)) ||
                snapshot.Samples.Select(sample => sample.SteamId).Distinct(StringComparer.Ordinal).Count() != snapshot.Samples.Count)
                throw new FormatException("The pro stats cache has invalid players or timestamps.");
            foreach (var sample in snapshot.Samples)
            {
                sample.Stats?.Validate();
                if (sample.Metrics.Any(row => row is null || string.IsNullOrWhiteSpace(row.Source) ||
                    !ValidMetric(row) ||
                    row.FetchedAt == DateTimeOffset.MinValue || row.FetchedAt > sample.FetchedAt) ||
                    sample.Metrics.Select(MetricKey).Distinct().Count() != sample.Metrics.Count)
                    throw new FormatException("The pro stats cache has invalid metric rows.");
            }
            if (snapshot.SourceResults is null || snapshot.SourceResults.Any(pair =>
                !Roster.Any(player => player.SteamId == pair.Key) || pair.Value is null || pair.Value.Count > 8 ||
                pair.Value.Any(result => result is null || string.IsNullOrWhiteSpace(result.Source) ||
                    string.IsNullOrWhiteSpace(result.Status) || result.Source.Length > 100 || result.Status.Length > 250)))
                throw new FormatException("The pro stats cache has invalid source results.");
            return snapshot;
        }
        catch (FileNotFoundException) { return ProStatsSnapshot.Empty; }
        catch (DirectoryNotFoundException) { return ProStatsSnapshot.Empty; }
        catch (JsonException ex) { throw new FormatException("The pro stats cache is malformed.", ex); }
    }

    public async Task<(ProStatsSnapshot Snapshot, IReadOnlyList<string> Errors)> RefreshAsync(
        ProStatsSnapshot previous, Func<string, CancellationToken, Task<IReadOnlyList<Evidence>>> fetch,
        IProgress<string>? progress, CancellationToken token)
    {
        var samples = previous.Samples.ToDictionary(sample => sample.SteamId, StringComparer.Ordinal);
        var errors = new List<string>();
        var sourceResults = new Dictionary<string, IReadOnlyList<ProSourceResult>>();
        foreach (var player in Roster)
        {
            token.ThrowIfCancellationRequested();
            var id = player.SteamId;
            progress?.Report($"Refreshing {player.Name} from configured sources...");
            try
            {
                var evidence = await fetch(id, token);
                var old = samples.GetValueOrDefault(id);
                var merged = (old is null ? [] : Rows(old)).ToDictionary(MetricKey);
                var received = false;
                var statuses = new List<ProSourceResult>();
                sourceResults[id] = statuses;
                foreach (var item in evidence)
                {
                    if (item.Status == "Unsupported automatic source") continue;
                    try
                    {
                        var fresh = Project([item]).ToArray();
                        foreach (var row in fresh) merged[MetricKey(row)] = row;
                        received |= fresh.Length > 0;
                        var status = fresh.Length > 0 ? $"{fresh.Length} metric rows; {item.Status}" : $"No metrics; {item.Status}";
                        if (item.Note is { } note && System.Text.RegularExpressions.Regex.Match(note, @"\bHTTP [1-5][0-9]{2}\b") is { Success: true } match)
                            status += $" ({match.Value})";
                        statuses.Add(new(item.Source, status));
                    }
                    catch (FormatException)
                    {
                        errors.Add($"{player.Name}: invalid metric data from {item.Source}.");
                        statuses.Add(new(item.Source, "Invalid metric data"));
                    }
                }
                if (!received)
                {
                    errors.Add(player.Name + ": no usable metrics returned.");
                    continue;
                }
                samples[id] = new(id, DateTimeOffset.UtcNow) { Metrics = merged.Values.ToArray() };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or IOException)
            {
                var status = ex switch
                {
                    HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                    HttpRequestException => "Network request failed",
                    IOException => "Source response or browser cleanup failed",
                    _ => "Retrieved data could not be verified"
                };
                errors.Add($"{player.Name}: {status}.");
                sourceResults[id] = [new("Account-source refresh", status)];
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                errors.Add($"{player.Name}: source request timed out.");
                sourceResults[id] = [new("Account-source refresh", "Source request timed out")];
            }
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = new ProStatsSnapshot(now, samples.Values.Where(sample => Rows(sample).Any()).ToArray()) { SourceResults = sourceResults };
        var pending = _path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(file, snapshot, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
        return (snapshot, errors);
    }

    private static (string Source, string Scope, string Name) MetricKey(ProMetric row) =>
        (row.Source, row.Scope, row.Name);

    private static bool ValidMetric(ProMetric row) =>
        (GeminiWebsiteReview.Metrics.Contains(row.Name) || row.Name is "Average K/D" or "Matches" or "Wins") &&
        !string.IsNullOrWhiteSpace(row.Scope) && double.IsFinite(row.Value) && row.Value >= 0 &&
        (row.Unit != "%" || row.Value <= 100) &&
        (row.Matches is not { } matches || double.IsFinite(matches) && matches >= 0) &&
        ((row.Kills, row.Rounds) is (null, null) ||
            (row.Name == "Kills per round" && row.Kills is >= 0 && row.Rounds is > 0));

    public static IEnumerable<ProMetric> Rows(ProPlayerSample sample) =>
        (sample.Stats is { } legacy ? FaceitRows(legacy, sample.FetchedAt).Concat(sample.Metrics) : sample.Metrics)
            .Where(row => row.Source != "FACEIT Data API" || row.Name != "K/D");

    private static IEnumerable<ProMetric> FaceitRows(FaceitLifetimeStats stats, DateTimeOffset fetchedAt)
    {
        foreach (var (name, value, unit) in new[]
        {
            ("Matches", stats.Matches, ""), ("Wins", stats.Wins, ""), ("Win rate", stats.WinRate, "%"),
            ("Average K/D", stats.AverageKd, "")
        })
            if (value is { } number)
                yield return new("FACEIT Data API", "CS2 lifetime (context; aggregation not independently verified)",
                    name, number, unit, stats.Matches, fetchedAt);
    }

    internal static IEnumerable<ProMetric> Project(IReadOnlyList<Evidence> evidence)
    {
        foreach (var item in evidence)
        {
            if (item.FaceitLifetime is { } faceit)
                foreach (var row in FaceitRows(faceit, item.RetrievedAt)) yield return row;
            var scope = Scope(item, null);
            foreach (var (panel, sample) in Analysis.WebsitePanelChecks(item, new ScreeningRules(), includeShortHeadshots: true))
            {
                var check = sample.Check;
                _ = check.IsFlagged;
                if (!GeminiWebsiteReview.Metrics.Contains(check.Name)) continue;
                var counts = check.Name == "Kills per round" ? sample.KprCounts : null;
                yield return new(item.Source, Scope(item, panel), check.Name,
                    check.Value, check.Unit, sample.Matches, item.RetrievedAt, counts?.Kills, counts?.Rounds);
            }
            foreach (var (panelScope, stats) in new[] { (scope, item.Stats) }
                         .Concat((item.Sections ?? []).Select(panel => (PanelScope(panel), panel.Stats))))
            {
                if (stats is null) continue;
                stats.Validate();
                if (stats is { ContextKpr: { } kpr, KprCounts: { } counts, Kpr: null })
                    yield return new(item.Source, panelScope + " (context only)", "Kills per round", kpr,
                        "", null, item.RetrievedAt, counts.Kills, counts.Rounds);
                if (stats is { ContextWinRate: { } winRate, WinRate: null })
                    yield return new(item.Source, panelScope + " (context only)", "Win rate", winRate,
                        "%", null, item.RetrievedAt);
                if (stats is { ContextHeadshots: { } hs, Headshots: null })
                    yield return new(item.Source, panelScope + " (context only)", "Headshot kills", hs,
                        "%", null, item.RetrievedAt);
            }
        }
    }

    internal static string Scope(Evidence item, PublicSection? panel) =>
        panel is not null ? PanelScope(panel) : item.Url.Host switch
        {
            "csst.at" => "website Scope panel; K/R as displayed",
            "api.steampowered.com" => "Steam lifetime; modes and periods unverified",
            "cstracker.gg" => "retrieved match history",
            "leetify.com" => "recent overview (all 5v5 sources)",
            _ => "source-reported profile sample"
        };

    internal static bool CanCompare(string host, string scope, string metric) =>
        (host, scope) is ("csst.at", "website Scope panel; K/R as displayed")
            or ("cstracker.gg", "retrieved match history" or "Profile results" or "Recent analysis" or "Detailed combat aggregates")
            or ("leetify.com", "recent overview (all 5v5 sources)") &&
        metric is "K/D" or "Headshot kills" or "Win rate" or "Kills per round" or "Time to damage (TTD)"
            or "Preaim" or "Aim offset" or "HLTV rating" or "KAST" or "Average damage per round (ADR)"
            or "Accuracy (not headshot percentage)" or "Utility damage per match";

    internal static IEnumerable<(string Player, ProMetric Metric)> Examples(ProStatsSnapshot snapshot, string checkedId,
        string source, string scope, MetricCheck check, int matches) =>
        (from player in Roster
         where player.SteamId != checkedId
         join sample in snapshot.Samples on player.SteamId equals sample.SteamId
         from row in Rows(sample)
         where row.Source == source && row.Scope == scope && row.Name == check.Name && row.Unit == check.Unit &&
               row.Matches > 0 && ValidMetric(row)
         orderby Math.Abs(row.Matches!.Value - matches), row.FetchedAt descending
         select (player.Name, row)).Take(3);

    private static string PanelScope(PublicSection panel) => panel.Title.StartsWith("Weapon accuracy variance (Valve lifetime, ",
        StringComparison.Ordinal) ? "Weapon accuracy variance (Valve lifetime)" : panel.Title.Split(':', 2)[0];
}
