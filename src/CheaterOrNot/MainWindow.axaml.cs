using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CheaterOrNot.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Threading.Tasks;

namespace CheaterOrNot;

public partial class MainWindow : Window
{
    private readonly TrackerClient _client = new();
    private readonly GeminiReviewClient _gemini = new();
    private readonly List<Evidence> _evidence = new();
    private readonly List<Evidence> _browserFallback = new();
    private CancellationTokenSource? _cancellation;
    private string? _websiteFinal;
    private string? _websiteEvidence;
    private string? _demoEvidence;
    private string? _demoCommentary;
    private bool _showDemoReport;
    private bool _newReportPending;
    private string? _firstId;
    private string? _steamApiKey;
    private string? _leetifyApiKey;
    private string? _faceitApiKey;
    private string? _geminiApiKey;
    private ScreeningRules? _rules;
    private Bitmap? _leetifyLogo;
    private DemoImportResult? _demo;
    private string? _demoPath;
    private long? _demoLength;
    private DateTime? _demoModified;
    private string? _demoSha256;
    private GeminiReviewRequest? _geminiRequest;
    private readonly ProPlayerStats _proStats = new(Path.Combine(Program.PortableDirectory, "pro-stats-v1.json"));
    private readonly DispatcherTimer _proStatsTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly CancellationTokenSource _proStatsLifetime = new();
    private ProStatsSnapshot _proStatsSnapshot = ProStatsSnapshot.Empty;
    private bool _proStatsReady;
    private bool _refreshingProStats;
    private DateTimeOffset _lastProAttempt;
    private (IReadOnlyList<(string Id, IReadOnlyList<Evidence> Evidence)> Players, ScreeningRules Rules,
        string FinalResult)? _websiteReviewSnapshot;
    private bool _closing;
    private bool _capturingVideo;
    private System.Threading.Tasks.TaskCompletionSource? _checkFinished;

    public MainWindow()
    {
        InitializeComponent();
        GeminiModelBox.TextChanged += (_, _) => InvalidateGeminiDraft();
        using (var logoStream = AssetLoader.Open(new Uri("avares://CheaterOrNot/Assets/leetify-logo.png")))
            _leetifyLogo = new Bitmap(logoStream);
        LeetifyLogo.Source = _leetifyLogo;
        ProLeetifyLogo.Source = _leetifyLogo;
        CoreRulesDetails.Text = "K/D, headshot kills %, win rate and kills per round (K/R): flag values at or above " +
            "KdThreshold, HeadshotPercentThreshold, WinPercentThreshold and KillsPerRoundThreshold. These highlight unusually " +
            "high results, but skill, opponents, mode and sample selection can explain them. Headshot % counts kills made " +
            "with headshots, not head accuracy. HeadshotMinimumMatches excludes smaller samples when a match count is known; " +
            "unknown counts are still assessed tentatively. Steam's lifetime kills/rounds ratio is context only: " +
            "its counters may cover different modes or periods, so it never triggers the K/R rule or counts toward screening coverage.";
        OpeningRuleDetails.Text = "Opening duels: flag win rate at or above OpeningWinPercentThreshold only when " +
            "OpeningMinimumDuels are recorded. The count reduces volatility but does not eliminate chance, positioning or team effects.";
        WebsiteRulesDetails.Text = "Website cutoffs are read-only and take precedence over duplicate core rules in the same panel. " +
            "A published sync time affects only which equivalent sample is retained; retrieval freshness does not change a threshold. " +
            "Different websites and non-comparable sample windows remain separate, even when they report the same metric. " +
            "CS2 rank, profile level and displayed Service Medal year are unscored account/progression context; lifetime medal count remains unavailable unless a source publishes verifiable history.";
        ProReferenceSource.Text = "Final result can show cached Top Pro Players examples for flags from the same source and panel. " +
            "They provide context, not a basis for setting or validating thresholds. HLTV tournament samples remain separate in Top Pro Players.";
        _steamApiKey = SettingsStore.LoadSteamApiKey();
        _leetifyApiKey = SettingsStore.LoadLeetifyApiKey();
        _faceitApiKey = SettingsStore.LoadFaceitApiKey();
        _geminiApiKey = SettingsStore.LoadGeminiApiKey();
        try
        {
            _rules = ScreeningRules.Load();
            RulesEditor.Text = _rules.ToEditorJson();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            RulesEditor.Text = new ScreeningRules().ToEditorJson();
            StatusText.Text = "Could not load screening rules: " + ex.Message + " Fix or restore the rules and save them before checking.";
        }
        UpdateKeyStatus();
        _proStatsTimer.Tick += async (_, _) => await RefreshProStatsAsync(manual: false);
        Opened += async (_, _) => await LoadProStatsAsync();
        Closing += async (_, args) =>
        {
            if (_cancellation is null) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            _cancellation.Cancel();
            if (_checkFinished is not null) await _checkFinished.Task;
            Close();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _proStatsTimer.Stop();
            _proStatsLifetime.Cancel();
            _proStatsLifetime.Dispose();
            _cancellation?.Cancel();
            _client.Dispose();
            _gemini.Dispose();
            _evidence.Clear();
            ReportBox.Text = "";
            FinalResultBox.Text = "";
            _demo = null;
            _leetifyLogo?.Dispose();
        };
    }

    private async Task LoadProStatsAsync()
    {
        var token = _proStatsLifetime.Token;
        try
        {
            _proStatsSnapshot = await _proStats.LoadAsync(token);
            if (token.IsCancellationRequested) return;
            ProStatsStatus.Text = ProStatsIdleStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            if (token.IsCancellationRequested) return;
            ProStatsStatus.Text = "Could not read the saved pro stats: " + ex.Message;
        }
        _proStatsReady = true;
        RenderProStats();
        UpdateProStatsButton();
        _proStatsTimer.Start();
    }

    private string ProStatsIdleStatus() => _proStatsSnapshot.LastAttempt == DateTimeOffset.MinValue
            ? "No snapshot yet. Select Refresh stats or wait for the next daily refresh check."
            : _proStatsSnapshot.SourceResults.Count == 0
                ? "Loaded an older snapshot without provider diagnostics. Select Refresh stats to run the full account checks."
                : $"Last account refresh: {_proStatsSnapshot.LastAttempt:u}. See provider results below.";

    private void UpdateProStatsButton() =>
        RefreshProStatsButton.IsEnabled = _proStatsReady && !_refreshingProStats && _cancellation is null;

    private void RenderProStats()
    {
        ProLeetifyAttribution.IsVisible = _proStatsSnapshot.Samples.SelectMany(ProPlayerStats.Rows)
            .Any(row => row.Source.StartsWith("Data Provided by Leetify", StringComparison.Ordinal));
        var text = new StringBuilder("HLTV 2025 ANNUAL RANKING | TOP 10\n");
        text.AppendLine(ProPlayerStats.RankingUrl);
        text.AppendLine($"HLTV tournament figures are a bundled snapshot reviewed {HltvProReference.Reviewed}, not a live feed. " +
            "Refresh stats updates account sources only. Tournament, FACEIT, and other account samples are not interchangeable. Missing metrics are not a pass.");
        var now = DateTimeOffset.UtcNow;
        var rank = 0;
        foreach (var player in ProPlayerStats.Roster)
        {
            text.AppendLine($"\n#{++rank} {player.Name} | SteamID64 {player.SteamId}");
            var hltv = HltvProReference.ForPlayer(player.SteamId);
            if (hltv is not null)
            {
                var mapCount = hltv.Maps is { } maps ? $" | {maps} maps" : "";
                text.AppendLine($"  HLTV tournament reference | {hltv.Scope}{mapCount}");
                text.AppendLine($"    {hltv.SourceUrl}");
                foreach (var metric in hltv.Metrics())
                    text.AppendLine(FormattableString.Invariant($"    {metric.Name}: {metric.Value:0.####}{metric.Unit}"));
            }
            var sample = _proStatsSnapshot.Samples.FirstOrDefault(row => row.SteamId == player.SteamId);
            var rows = sample is null ? Array.Empty<ProMetric>() : ProPlayerStats.Rows(sample).ToArray();
            if (rows.Length == 0) text.AppendLine("  No account-source metrics cached.");
            foreach (var group in rows.GroupBy(row => (row.Source, row.Scope, row.FetchedAt))
                         .OrderBy(x => x.Key.Source, StringComparer.Ordinal).ThenByDescending(x => x.Key.FetchedAt))
            {
                text.AppendLine($"  {group.Key.Source} | {group.Key.Scope} | fetched {group.Key.FetchedAt:u}" +
                    (now - group.Key.FetchedAt >= TimeSpan.FromDays(1) ? " (over 24 hours old)" : ""));
                foreach (var row in group.OrderBy(row => row.Name == "Kills per round" ? 0 : 1)
                             .ThenBy(row => row.Name, StringComparer.Ordinal))
                {
                    var size = row.Matches is { } count ? FormattableString.Invariant($"{count:G} matches") : "match count unknown";
                    var counts = row.Kills is { } kills && row.Rounds is { } rounds ? $"; {kills} kills / {rounds} rounds" : "";
                    text.AppendLine(FormattableString.Invariant($"    {row.Name}: {row.Value:0.####}{row.Unit} | {size}{counts}"));
                }
            }
            if (_proStatsSnapshot.SourceResults.TryGetValue(player.SteamId, out var results))
            {
                text.AppendLine("  Latest account refresh:");
                foreach (var result in results)
                    text.AppendLine($"    {result.Source}: {result.Status}");
            }
            var available = rows.Select(row => row.Name).Concat(hltv?.Metrics().Select(metric => metric.Name) ?? []);
            var missing = ProPlayerStats.RuleMetrics.Except(available, StringComparer.Ordinal);
            text.AppendLine("  No comparable rule metric shown: " + (missing.Any() ? string.Join(", ", missing.Order(StringComparer.Ordinal)) : "none"));
        }
        ProStatsBox.Text = text.ToString();
    }

    private async void RefreshProStatsClick(object? sender, RoutedEventArgs args) =>
        await RefreshProStatsAsync(manual: true);

    private async Task RefreshProStatsAsync(bool manual)
    {
        if (_closing || !_proStatsReady || _refreshingProStats || _cancellation is not null)
            return;
        var now = DateTimeOffset.UtcNow;
        if (!manual && (!_proStatsSnapshot.RefreshDue(now) || now - _lastProAttempt < TimeSpan.FromDays(1))) return;
        _lastProAttempt = now;
        _refreshingProStats = true;
        UpdateProStatsButton();
        ProStatsStatus.Text = "Refreshing the HLTV 2025 roster from configured sources...";
        var token = _proStatsLifetime.Token;
        var (steam, leetify, faceit) = (_steamApiKey, _leetifyApiKey, _faceitApiKey);
        var rules = _rules ?? new ScreeningRules();
        try
        {
            using var client = new TrackerClient { ProReferenceRefresh = true };
            var progress = new Progress<string>(message =>
            {
                if (_refreshingProStats && !token.IsCancellationRequested)
                    ProStatsStatus.Text = message;
            });
            var result = await _proStats.RefreshAsync(_proStatsSnapshot, async (id, ct) =>
            {
                await using var renderer = new ChromiumPageRenderer();
                return await client.FetchAsync(id, ct, _leetifyLogo is not null, renderer, steamApiKey: steam,
                    leetifyApiKey: leetify, rules: rules, faceitApiKey: faceit);
            }, progress, token);
            if (token.IsCancellationRequested) return;
            _proStatsSnapshot = result.Snapshot;
            ProStatsStatus.Text = ProStatsIdleStatus() + (result.Errors.Count == 0 ? "" :
                $" {result.Errors.Count} refresh issue(s); previously verified data was retained where available.");
            RenderProStats();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            if (!token.IsCancellationRequested)
                ProStatsStatus.Text = "Could not update the pro stats cache: " + ex.Message + " Previous stats were kept.";
        }
        finally
        {
            _refreshingProStats = false;
            if (!token.IsCancellationRequested) UpdateProStatsButton();
        }
    }

    private IReadOnlyList<string> SelectPlayers()
    {
        var tokens = (PlayerInput.Text ?? "").Split([' ', ',', ';', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            throw new FormatException("Enter a SteamID64 or profile URL; separate several players with spaces or commas.");
        var ids = tokens.Select(PlayerId.Parse).Distinct().ToArray();
        return ids;
    }

    private async void FetchClick(object? sender, RoutedEventArgs args) => await FetchPlayersAsync(withAi: false);

    private async void FetchWithAiClick(object? sender, RoutedEventArgs args) => await FetchPlayersAsync(withAi: true);

    private async Task FetchPlayersAsync(bool withAi)
    {
        if (_cancellation is not null) return;
        var geminiKey = withAi ? _geminiApiKey : null;
        if (withAi && geminiKey is null)
        {
            StatusText.Text = "Save a Gemini key under API Keys, or select Check player to check without AI.";
            return;
        }
        try
        {
            var rules = _rules ?? throw new FormatException("Screening rules could not be loaded. Fix or restore and save them first.");
            var ids = SelectPlayers();
            var steamApiKey = _steamApiKey;
            var leetifyApiKey = _leetifyApiKey;
            var faceitApiKey = _faceitApiKey;
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _checkFinished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            BeginReportReplacement();
            SetBusy(true);
            var perPlayer = Sources.PublicPages(ids[0]).Count + (steamApiKey is null ? 0 : 1) +
                (leetifyApiKey is null ? 0 : 1) + (faceitApiKey is null ? 0 : 1);
            var totalSteps = perPlayer * ids.Count;
            ProgressPanel.IsVisible = true;
            CheckProgress.Maximum = totalSteps + 1;
            CheckProgress.Value = 0;
            ProgressDetails.Text = $"0/{totalSteps} website checks complete | 0 sources with gameplay data";
            StatusText.Text = steamApiKey is null && leetifyApiKey is null && faceitApiKey is null
                ? "Starting anonymous public website checks. No API keys or login are used."
                : "Starting public website checks plus the official API(s) for your saved key(s). No login is used.";
            var acceptProgress = true;
            try
            {
                var results = new List<(string Id, IReadOnlyList<Evidence> Evidence)>();
                var gameplaySources = 0;
                for (var index = 0; index < ids.Count; index++)
                {
                    var id = ids[index];
                    var offset = index * perPlayer;
                    var prefix = ids.Count > 1 ? $"Player {index + 1}/{ids.Count} ({id}): " : "";
                    var progress = new Progress<FetchProgress>(update =>
                    {
                        if (!acceptProgress || _closing || _cancellation != cancellation || cancellation.IsCancellationRequested) return;
                        CheckProgress.Value = offset + update.CompletedSources;
                        ProgressDetails.Text = $"{offset + update.CompletedSources}/{totalSteps} website checks complete | {gameplaySources + update.PerformanceSummaries} sources with gameplay data";
                        StatusText.Text = prefix + (update.Status == "Checking"
                            ? $"Checking {update.Source}..."
                            : $"{update.Source}: {update.Status}.");
                    });
                    IReadOnlyList<Evidence> fetched;
                    await using (IPageRenderer renderer = new ChromiumPageRenderer())
                    {
                        try
                        {
                            fetched = await _client.FetchAsync(id, cancellation.Token,
                                _leetifyLogo is not null, renderer, progress, steamApiKey, leetifyApiKey, rules, faceitApiKey);
                        }
                        finally
                        {
                            if (index == ids.Count - 1)
                            {
                                acceptProgress = false;
                                StatusText.Text = "Closing Chromium and removing temporary browsing data...";
                            }
                        }
                    }
                    results.Add((id, fetched));
                    gameplaySources += fetched.Count(e => e.Stats is { HasMetrics: true });
                }
                var proStats = _proStatsSnapshot;
                string finalReport;
                string evidenceText;
                if (results.Count == 1)
                {
                    evidenceText = Analysis.Render(results[0].Id, results[0].Evidence, rules: rules);
                    finalReport = Analysis.RenderFinal(results[0].Id, results[0].Evidence, rules, proStats);
                }
                else
                {
                    finalReport = Analysis.RenderBatch(results, rules, proStats);
                    var evidenceReport = new StringBuilder();
                    foreach (var player in results)
                    {
                        evidenceReport.AppendLine(new string('=', 60));
                        evidenceReport.Append(Analysis.Render(player.Id, player.Evidence, rules: rules));
                        evidenceReport.AppendLine();
                    }
                    evidenceText = evidenceReport.ToString();
                }
                cancellation.Token.ThrowIfCancellationRequested();
                _evidence.Clear();
                foreach (var player in results) _evidence.AddRange(player.Evidence);
                _websiteEvidence = evidenceText;
                _websiteFinal = withAi
                    ? "AI FINAL REVIEW | Waiting for Gemini\n\n" +
                      "Website collection is complete. The Final result is withheld until the AI response finishes " +
                      "so local hard-coded results are not mistaken for the requested AI assessment."
                    : finalReport;
                _firstId = results[0].Id;
                _websiteReviewSnapshot = null;
                _browserFallback.Clear();
                _browserFallback.AddRange(_evidence.Where(item =>
                    Sources.PageHosts.Contains(item.Url.Host) && item.Stats is null));
                OpenBlockedButton.Content = _browserFallback.Count == 1
                    ? $"Automated access to {_browserFallback[0].Url.Host} was blocked \u2014 open it in your own browser"
                    : "Some pages blocked automated access \u2014 open them in your own browser";
                CompleteReportReplacement(demo: false);
                MainTabs.SelectedItem = FinalResultTab;
                CheckProgress.Value = CheckProgress.Maximum;
                ProgressDetails.Text = $"{totalSteps}/{totalSteps} website checks complete | {gameplaySources} sources with gameplay data | " +
                    (withAi ? "Preparing AI review" : "Report ready");
                StatusText.Text = withAi
                    ? "Website collection complete. Waiting for the AI-authored Final result before displaying it."
                    : results.Count > 1
                    ? $"Batch finished: {results.Count} players checked, {gameplaySources} sources supplied gameplay data in total. Players are ranked in Final result."
                    : gameplaySources == 0
                        ? "Check finished, but no analyzable performance summaries were found. See website limitations below."
                        : $"Check finished: {gameplaySources} sources supplied gameplay data ({perPlayer} websites checked). See separate samples and website limitations below.";
                if (withAi && !cancellation.IsCancellationRequested &&
                    results.Any(player => GeminiWebsiteReview.HasRows(player.Evidence, rules)))
                {
                    _websiteReviewSnapshot = (results, rules, finalReport);
                    if (geminiKey is not null)
                        await RunWebsiteStatsReviewAsync(_websiteReviewSnapshot.Value, geminiKey, cancellation.Token);
                }
                else if (withAi && !cancellation.IsCancellationRequested)
                {
                    _websiteFinal = "AI FINAL REVIEW | Not completed\n\n" +
                        "No eligible statistical rows were available to send to Gemini.\n\n" +
                        "LOCAL SCREENING BASELINE (calculated without AI)\n" + finalReport;
                    ShowReport(demo: false);
                    StatusText.Text = "AI review not sent because no eligible statistics were available. The local baseline is shown explicitly.";
                }
            }
            finally
            {
                _cancellation = null;
                RestorePendingReport();
                SetBusy(false);
                _checkFinished.TrySetResult();
            }
        }
        catch (FormatException ex) { StatusText.Text = ex.Message; ProgressDetails.Text = "Check could not finish."; }
        catch (OperationCanceledException) { StatusText.Text = "Check canceled. No partial results were imported."; ProgressDetails.Text = "Canceled; temporary browser data removed."; }
        catch (IOException ex) { StatusText.Text = "The check could not finish safely: " + ex.Message; ProgressDetails.Text = "Cleanup or processing failed; see the error above."; }
    }

    private void ClearClick(object? sender, RoutedEventArgs args)
    {
        ClearWebResults();
        _demo = null;
        _demoEvidence = _demoCommentary = null;
        ResetGeminiReview();
        ShowReport(demo: false);
        SetBusy(false);
        StatusText.Text = "Player data, website reports and local demo context cleared.";
    }

    private void ClearWebResults()
    {
        _websiteReviewSnapshot = null;
        _evidence.Clear();
        _browserFallback.Clear();
        _firstId = null;
        PlayerInput.Text = _websiteEvidence = _websiteFinal = null;
        LeetifyAttribution.IsVisible = false;
        OpenBlockedButton.IsVisible = false;
        SaveButton.IsEnabled = false;
        CopyFinalButton.IsEnabled = CopyEvidenceButton.IsEnabled = false;
        ProgressPanel.IsVisible = false;
        CheckProgress.Value = 0;
        ProgressDetails.Text = "";
    }

    private void ReportSourceChanged(object? sender, RoutedEventArgs args)
    {
        if (sender is not RadioButton { IsChecked: true } button) return;
        var demo = button == DemoReportButton;
        if (demo != _showDemoReport) ShowReport(demo);
    }

    private void BeginReportReplacement()
    {
        _newReportPending = true;
        ShowReport(_showDemoReport);
    }

    private void CompleteReportReplacement(bool demo)
    {
        _newReportPending = false;
        ShowReport(demo);
    }

    private void RestorePendingReport()
    {
        if (_newReportPending) CompleteReportReplacement(_showDemoReport);
    }

    private void ShowReport(bool demo)
    {
        _showDemoReport = demo;
        FinalResultBox.Text = _newReportPending ? "" : demo ? RenderDemoFinal() : _websiteFinal ?? "";
        ReportBox.Text = _newReportPending ? "" : (demo ? _demoEvidence : _websiteEvidence) ?? "";
        WebsiteReportButton.IsChecked = !demo;
        DemoReportButton.IsChecked = demo;
        DemoRequestExpander.IsVisible = !_newReportPending && demo && !string.IsNullOrEmpty(GeminiPreviewBox.Text);
        LeetifyAttribution.IsVisible = !_newReportPending && !demo && _evidence.Any(item =>
            item.Url.Host is "leetify.com" or "api-public.cs-prod.leetify.com" && item.Stats is { HasMetrics: true });
        UpdateReportControls(_cancellation is not null);
    }

    private async void RetryWebsiteAiClick(object? sender, RoutedEventArgs args)
    {
        if (_cancellation is not null || _websiteReviewSnapshot is not { } snapshot ||
            _geminiApiKey is not { } key) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _checkFinished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _websiteFinal = "AI FINAL REVIEW | Waiting for Gemini\n\n" +
            "Retrying the AI review from the completed website snapshot. Website checks are not repeated.";
        ShowReport(demo: false);
        SetBusy(true);
        try
        {
            await RunWebsiteStatsReviewAsync(snapshot, key, cancellation.Token);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);
            _checkFinished.TrySetResult();
        }
    }

    private string RenderDemoFinal()
    {
        return _demo is { } demo
            ? $"DEMO SUMMARY | {demo.FileName}\n" +
              $"{demo.Players.Count} recorded human accounts | {demo.Gameplay?.TotalFindings ?? 0} experimental gameplay selections | {demo.Bookmarks.Count} death-event bookmarks\n" +
              (demo.IsIncomplete ? "Partial recording. " : "") +
              "Local measurements are not a cheating or clean-play verdict. See Evidence for the roster, measurements, and bookmarks." +
              (_demoCommentary is { } review ? "\n\n" + GeminiReviewResult.FinalSection(review) : "")
            : "";
    }

    private async Task RunWebsiteStatsReviewAsync(
        (IReadOnlyList<(string Id, IReadOnlyList<Evidence> Evidence)> Players, ScreeningRules Rules, string FinalResult) snapshot,
        string key, CancellationToken token)
    {
        ShowReport(demo: false);
        MainTabs.SelectedItem = FinalResultTab;
        var started = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var request = GeminiWebsiteReview.Create(snapshot.Players.Select(player => player.Evidence).ToArray(),
                snapshot.Rules, GeminiModelBox.Text ?? GeminiReviewRequest.DefaultModel);
            StatusText.Text = "Sending assessed statistics from the completed check to Gemini. No website lookup is running.";
            started = true;
            var review = await _gemini.ReviewWebsiteStatsAsync(request, key, token);
            token.ThrowIfCancellationRequested();
            var mapping = request.ReviewedPlayerNumbers.Select(number =>
                FormattableString.Invariant($"Player{number:00}: {snapshot.Players[number - 1].Id}"));
            _websiteFinal = review +
                "\n\nLOCAL SCREENING BASELINE (calculated without AI)\n" +
                snapshot.FinalResult.TrimEnd() +
                "\nLOCAL PLAYER MAPPING (not sent to Gemini)\n" +
                string.Join("\n", mapping) +
                (request.ReviewedPlayerNumbers.Count < snapshot.Players.Count
                    ? $"\n{snapshot.Players.Count - request.ReviewedPlayerNumbers.Count} checked player(s) had no eligible rows and were not sent." : "");
            ShowReport(demo: false);
            StatusText.Text = "AI-authored Final review complete. Local calculations remain below it as the audit baseline.";
            ProgressDetails.Text = $"{snapshot.Players.Count} player AI review complete | local audit baseline retained";
        }
        catch (OperationCanceledException)
        {
            _websiteFinal = snapshot.FinalResult.TrimEnd();
            StatusText.Text = started
                ? "AI stats review canceled. Any data already delivered to Google cannot be retracted."
                : "AI stats review canceled before sending. Only the local baseline is available.";
            _websiteFinal = "AI FINAL REVIEW | Canceled\n\n" +
                (started ? "The AI response was canceled before it completed; any submitted data may already have reached Google.\n\n"
                    : "Nothing was sent to Gemini.\n\n") +
                "LOCAL SCREENING BASELINE (calculated without AI)\n" + snapshot.FinalResult.TrimEnd();
            ShowReport(demo: false);
        }
        catch (Exception ex) when (ex is FormatException or IOException or System.Text.Json.JsonException)
        {
            var detail = ex is System.Text.Json.JsonException
                ? "Gemini returned malformed structured output."
                : ex.Message;
            _websiteFinal = "AI STATS REVIEW | Not completed\n" +
                "The AI-authored Final result was not accepted. " + detail +
                "\n\nLOCAL SCREENING BASELINE (calculated without AI)\n" +
                snapshot.FinalResult.TrimEnd();
            ShowReport(demo: false);
            ProgressDetails.Text = "AI review unavailable | local audit baseline retained";
            StatusText.Text = "AI stats review failed: " + detail;
        }
    }

    private async void ImportDemoClick(object? sender, RoutedEventArgs args) => await ImportDemoAsync(false);

    private async void ImportGeminiDemoClick(object? sender, RoutedEventArgs args) => await ImportDemoAsync(true);

    private async System.Threading.Tasks.Task ImportDemoAsync(bool prepareGemini)
    {
        if (_cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _checkFinished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        SetBusy(true);
        var acceptProgress = true;
        try
        {
            if (prepareGemini && _geminiApiKey is null)
                throw new FormatException("Save a Gemini API key under API Keys before using Analyze demo with Gemini.");
            if (prepareGemini && !await VideoReviewWindow.CheckGameClosedAsync(this))
            {
                StatusText.Text = "Analysis not started: close CS2, then select Analyze demo with Gemini again. Nothing was sent to Gemini.";
                return;
            }
            if (prepareGemini)
            {
                using var tools = new PortableCaptureTools(Path.Combine(Program.PortableDirectory, "tools"));
                try { _ = tools.Installed(); }
                catch (CaptureToolsSetupRequiredException)
                {
                    ProgressPanel.IsVisible = true;
                    StatusText.Text = "Preparing capture tools automatically. This is the one-time upstream component download.";
                    ProgressDetails.Text = "No demo or footage is uploaded during setup.";
                    await tools.UpdateAsync(new Progress<string>(message => ProgressDetails.Text = message),
                        cancellation.Token);
                    StatusText.Text = "Capture tools are ready. Continue by selecting the demo.";
                }
            }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = prepareGemini ? "Analyze CS2 demo with Gemini (uploads video)" : "Import a local CS2 demo (no upload)",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Uncompressed CS2 demo") { Patterns = ["*.dem"] }]
            });
            cancellation.Token.ThrowIfCancellationRequested();
            if (files.Count == 0)
            {
                StatusText.Text = "Demo selection canceled. Previous reports are unchanged.";
                return;
            }
            using var file = files[0];
            BeginReportReplacement();
            ProgressPanel.IsVisible = true;
            CheckProgress.Maximum = 100;
            CheckProgress.Value = 0;
            ProgressDetails.Text = "Reading local demo; no network requests.";
            StatusText.Text = "Importing demo on a background worker. This can take a moment; Cancel stops parsing.";
            var progress = new Progress<int>(percent =>
            {
                if (!acceptProgress || _closing || _cancellation != cancellation || cancellation.IsCancellationRequested) return;
                CheckProgress.Value = percent;
                ProgressDetails.Text = $"Local demo parsing: {percent}% | No player checks requested";
            });
            await using var stream = await file.OpenReadAsync();
            string? sourceHash = null;
            if (stream.CanSeek)
            {
                sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation.Token));
                stream.Position = 0;
            }
            var demo = await DemoImporter.ImportAsync(stream, file.Name, cancellation.Token, progress);
            var demoPath = file.TryGetLocalPath();
            var source = demoPath is null ? null : new FileInfo(demoPath);
            var length = source?.Length;
            var modified = source?.LastWriteTimeUtc;
            var evidence = demo.Render();
            cancellation.Token.ThrowIfCancellationRequested();
            acceptProgress = false;
            _demo = demo;
            _demoPath = demoPath;
            _demoLength = length;
            _demoModified = modified;
            _demoSha256 = sourceHash;
            ResetGeminiReview();
            _demoCommentary = null;
            _demoEvidence = evidence;
            CompleteReportReplacement(demo: true);
            MainTabs.SelectedItem = FinalResultTab;
            CheckProgress.Value = 100;
            ProgressDetails.Text = "Local demo preview ready. No network requests were made.";
            StatusText.Text = $"Imported {demo.Players.Count} human accounts, {demo.Gameplay?.TotalFindings ?? 0} experimental gameplay selections and {demo.Bookmarks.Count} death-event bookmarks" +
                (demo.IsIncomplete ? " from a partial recording." : ".") +
                " Existing website reports are unchanged. Use roster for checks loads these IDs; Check player starts online checks.";
            if (prepareGemini)
            {
                if (_demoPath is null || sourceHash is null)
                    throw new FormatException("Automatic analysis needs an existing local demo file.");
                GeminiDestinationText.Text = "Fresh kill-centered clips + available game audio + bounded telemetry. Destination: Google Gemini. " +
                    "Selecting Analyze demo with Gemini is the upload authorization; prepared batches are sent automatically. Footage is not anonymized; quotas/charges and data-use terms apply.";
                CheckProgress.Maximum = 5;
                CheckProgress.Value = 0;
                var workflowProgress = new Progress<DemoGeminiProgress>(update =>
                {
                    if (_closing || _cancellation != cancellation) return;
                    if (update.RequestPreview is { } preview)
                    {
                        GeminiPreviewBox.Text = preview;
                        DemoRequestExpander.IsVisible = true;
                    }
                    if (cancellation.IsCancellationRequested) return;
                    _capturingVideo = update.Stage == DemoGeminiStage.Capturing;
                    CancelButton.Content = _capturingVideo ? "Stop after current step" : "Cancel";
                    CheckProgress.Value = (int)update.Stage;
                    ProgressDetails.Text = update.Message;
                    StatusText.Text = update.Stage switch
                    {
                        DemoGeminiStage.CheckingCapture => "Checking local demo and capture prerequisites.",
                        DemoGeminiStage.Capturing => "Capturing and encoding local review clips; no upload is occurring yet.",
                        DemoGeminiStage.Preparing => "Preparing verified Gemini video requests locally.",
                        DemoGeminiStage.Sending => "Uploading prepared video batches to Gemini.",
                        DemoGeminiStage.Saving => "Validating responses and saving the combined review locally.",
                        _ => "Demo AI analysis is in progress."
                    };
                });
                var saved = await DemoGeminiAnalysis.RunAsync(Path.Combine(Program.PortableDirectory, "tools"),
                    Path.Combine(Program.PortableDirectory, "review-videos"), _demoPath, demo, sourceHash,
                    GeminiModelBox.Text ?? GeminiReviewRequest.DefaultModel,
                    _geminiApiKey ?? throw new InvalidOperationException("The saved Gemini key is unavailable."), _gemini,
                    workflowProgress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                CheckProgress.Value = 5;
                if (saved is null)
                {
                    throw new FormatException("Automatic demo analysis ended without a saved Gemini review.");
                }
                else
                {
                    _demoCommentary = saved.Text + "\n\nSaved locally: " + saved.Path;
                    _demoEvidence += "\n\n" + GeminiReviewResult.DetailedSection(saved.Text);
                    ShowReport(demo: true);
                    ProgressDetails.Text = "Demo analysis finished. The combined unverified review was saved locally; website reports are unchanged.";
                    StatusText.Text = "Demo analysis complete. Verify AI claims; this is not a cheating or clean-play verdict.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = prepareGemini
                ? "Analysis stopped. The current capture step, if any, finished safely. Some earlier Gemini batches may already have been delivered and cannot be retracted; no combined review was accepted."
                : "Demo import canceled. Previous reports are unchanged; no partial import was accepted.";
            ProgressDetails.Text = prepareGemini ? "Analysis stopped; partial local files may remain." : "Local demo import canceled.";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException
            or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = (prepareGemini ? "Demo analysis failed: " : "Could not import demo: ") + ex.Message;
            ProgressDetails.Text = prepareGemini
                ? "Automatic demo AI review did not complete. Local demo files and any earlier delivered-batch information are retained; website reports are unchanged."
                : "Demo import failed. Previous reports are unchanged; no partial import was accepted.";
        }
        finally
        {
            acceptProgress = false;
            _cancellation = null;
            _capturingVideo = false;
            CancelButton.Content = "Cancel";
            RestorePendingReport();
            SetBusy(false);
            _checkFinished.TrySetResult();
        }
    }

    private async void VideoComponentsClick(object? sender, RoutedEventArgs args)
    {
        var tools = new VideoReviewWindow(_demo, _demoPath, _demoLength, _demoModified,
            _demoSha256, _geminiApiKey, GeminiModelBox.Text ?? GeminiReviewRequest.DefaultModel);
        if (await tools.ShowDialog<bool>(this)) PreviewGeminiSummary();
    }

    private async void GeminiDiagnosticClick(object? sender, RoutedEventArgs args) =>
        await new GeminiDiagnosticWindow(_geminiApiKey,
            GeminiModelBox.Text ?? GeminiReviewRequest.DefaultModel).ShowDialog(this);

    private void UseDemoRosterClick(object? sender, RoutedEventArgs args)
    {
        if (_demo is null)
        {
            StatusText.Text = "Import a local demo before loading a roster.";
            return;
        }
        PlayerInput.Text = _demo.PlayerInput;
        MainTabs.SelectedIndex = 0;
        StatusText.Text = $"Loaded {_demo.Players.Count} recorded SteamIDs. Review or edit the list, then press Check player to request public website/API data. No requests have been made.";
    }

    private void CancelClick(object? sender, RoutedEventArgs args)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = _capturingVideo
            ? "Stop requested. Waiting for the current capture/encoding step and cleanup; no Gemini send will start after stopping."
            : "Canceling; waiting for processing and cleanup. Delivered Gemini data cannot be retracted.";
    }

    private void SetBusy(bool busy)
    {
        UpdateProStatsButton();
        FetchButton.IsEnabled = FetchWithAiButton.IsEnabled = ClearButton.IsEnabled = PlayerInput.IsEnabled = !busy;
        UpdateReportControls(busy);
        CancelButton.IsEnabled = busy;
        RulesEditor.IsEnabled = SaveRulesButton.IsEnabled = DefaultRulesButton.IsEnabled = !busy;
        ImportDemoButton.IsEnabled = ImportGeminiDemoButton.IsEnabled = !busy;
        VideoComponentsButton.IsEnabled = GeminiDiagnosticButton.IsEnabled = !busy;
        TestSteamKeyButton.IsEnabled = TestLeetifyKeyButton.IsEnabled = TestFaceitKeyButton.IsEnabled = TestGeminiKeyButton.IsEnabled = !busy;
        GeminiModelBox.IsEnabled = SaveGeminiKeyButton.IsEnabled = RemoveGeminiKeyButton.IsEnabled = GeminiKeyBox.IsEnabled = !busy;
        CopyGeminiButton.IsEnabled = !busy && !string.IsNullOrEmpty(GeminiPreviewBox.Text);
        SendGeminiButton.IsEnabled = !busy && _geminiRequest is not null && _geminiApiKey is not null;
        SendGeminiButton.IsVisible = _geminiRequest is not null;
    }

    private void UpdateReportControls(bool busy)
    {
        ReportSelector.IsVisible = !_newReportPending && (_websiteFinal is not null || _demo is not null);
        WebsiteReportButton.IsEnabled = !busy && _websiteFinal is not null;
        DemoReportButton.IsEnabled = !busy && _demo is not null;
        CopyFinalButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(FinalResultBox.Text);
        RetryWebsiteAiButton.IsVisible = !_newReportPending && !_showDemoReport &&
            _websiteReviewSnapshot is not null && _geminiApiKey is not null;
        RetryWebsiteAiButton.IsEnabled = RetryWebsiteAiButton.IsVisible && !busy;
        CopyEvidenceButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(ReportBox.Text);
        SaveButton.IsEnabled = CopyFinalButton.IsEnabled || CopyEvidenceButton.IsEnabled;
        OpenBlockedButton.IsVisible = !busy && !_showDemoReport && _browserFallback.Count > 0;
        UseDemoRosterButton.IsVisible = !_newReportPending && _showDemoReport && _demo is not null;
        UseDemoRosterButton.IsEnabled = !busy && UseDemoRosterButton.IsVisible;
    }

    private void SaveRulesClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var rules = ScreeningRules.Parse(RulesEditor.Text ?? "");
            rules.Save();
            _rules = rules;
            RulesEditor.Text = rules.ToEditorJson();
            StatusText.Text = "Screening rules saved. They will apply to the next check; existing reports are unchanged.";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save screening rules: " + ex.Message;
        }
    }

    private void DefaultRulesClick(object? sender, RoutedEventArgs args)
    {
        RulesEditor.Text = new ScreeningRules().ToEditorJson();
        StatusText.Text = "Default rules loaded in the editor. Select Save rules to apply them to future checks.";
    }

    private async void CopyReportClick(object? sender, RoutedEventArgs args)
    {
        var isFinal = sender == CopyFinalButton;
        var isGemini = sender == CopyGeminiButton;
        var text = isGemini ? GeminiPreviewBox.Text : isFinal ? FinalResultBox.Text : ReportBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Nothing to copy yet; run a check or import a demo first.";
            return;
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Could not copy: the system clipboard is unavailable.";
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            StatusText.Text = (isGemini ? "Gemini request preview" : isFinal ? "Final result" : "Evidence") + " copied to the clipboard.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or System.Runtime.InteropServices.ExternalException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            StatusText.Text = "Could not copy to the clipboard: " + ex.Message;
        }
    }

    private void OpenLeetifyClick(object? sender, RoutedEventArgs args)
    {
        try { if (_firstId is not null) Open(Sources.LeetifyProfile(_firstId).AbsoluteUri); }
        catch (FormatException ex) { StatusText.Text = ex.Message; }
    }

    private async void SaveClick(object? sender, RoutedEventArgs args)
    {
        var report = "===== FINAL RESULT =====" + Environment.NewLine + Environment.NewLine +
            FinalResultBox.Text + Environment.NewLine + Environment.NewLine +
            "===== EVIDENCE =====" + Environment.NewLine + Environment.NewLine + ReportBox.Text;
        if (string.IsNullOrWhiteSpace(ReportBox.Text) && string.IsNullOrWhiteSpace(FinalResultBox.Text))
        {
            StatusText.Text = "Nothing to save yet; run a check or import a demo first.";
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Cheater or Not report",
                SuggestedFileName = $"CheaterOrNot-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text report") { Patterns = ["*.txt"] }]
            });
            if (file is null)
            {
                StatusText.Text = "Save canceled.";
                return;
            }
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(report);
            StatusText.Text = $"Report saved as {file.Name}. Share it responsibly; it is screening context, not an accusation.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save the report: " + ex.Message;
        }
    }

    private void UpdateKeyStatus()
    {
        SteamKeyBox.Text = "";
        LeetifyKeyBox.Text = "";
        FaceitKeyBox.Text = "";
        GeminiKeyBox.Text = "";
        SteamKeyBox.PlaceholderText = _steamApiKey is null ? "32-character key" : "Key saved - paste a replacement";
        LeetifyKeyBox.PlaceholderText = _leetifyApiKey is null ? "Leetify developer key" : "Key saved - paste a replacement";
        FaceitKeyBox.PlaceholderText = _faceitApiKey is null ? "FACEIT Data API key" : "Key saved - paste a replacement";
        UpdateProStatsButton();
        GeminiKeyBox.PlaceholderText = _geminiApiKey is null ? "Google AI Studio API key" : "Key saved - paste a replacement";
        var steam = _steamApiKey is null ? "Steam: no key saved." : "Steam: key saved.";
        var leetify = _leetifyApiKey is null ? "Leetify: no key saved." : "Leetify: key saved.";
        var faceit = _faceitApiKey is null ? "FACEIT: no key saved." : "FACEIT: key saved.";
        var gemini = _geminiApiKey is null ? "Gemini: no key saved." : "Gemini: key saved. Analyze demo sends video and telemetry automatically.";
        KeyStatusText.Text = steam + " " + leetify + " " + faceit + " " + gemini + " " + (_steamApiKey is null && _leetifyApiKey is null && _faceitApiKey is null && _geminiApiKey is null
            ? "Checks use anonymous public pages only."
            : $"Keys are stored in {SettingsStore.DefaultDirectory} " +
              (OperatingSystem.IsWindows()
                  ? "(encrypted with Windows DPAPI for your user account)"
                  : "(owner-only file permissions)") +
              " and are sent only to the matching official API; they never appear in reports.");
    }

    private void ResetGeminiReview()
    {
        _geminiRequest = null;
        SendGeminiButton.IsEnabled = CopyGeminiButton.IsEnabled = false;
        SendGeminiButton.IsVisible = false;
        GeminiPreviewBox.Text = GeminiDestinationText.Text = "";
        DemoRequestExpander.IsExpanded = DemoRequestExpander.IsVisible = false;
    }

    private void InvalidateGeminiDraft()
    {
        if (_geminiRequest is not null) ResetGeminiReview();
    }

    private void PreviewGeminiSummary()
    {
        try
        {
            PrepareGeminiPreview();
            SetBusy(false);
        }
        catch (FormatException ex) { StatusText.Text = "Could not prepare Gemini preview: " + ex.Message; }
    }

    private void PrepareGeminiPreview()
    {
        if (_demo?.Gameplay is not { } gameplay)
            throw new FormatException("Import a demo with gameplay measurements first.");
        _geminiRequest = GeminiReviewRequest.Create(gameplay, _demo.IsIncomplete, GeminiModelBox.Text ?? "");
        GeminiPreviewBox.Text = _geminiRequest.JsonBody;
        GeminiDestinationText.Text = "Destination: " + _geminiRequest.Endpoint +
            " | One bounded review: full compact player audit; up to 16 engagement examples, 400 coverage rows, 32 short windows, 400 round rows, 200 combat events and 100 bookmarks within 64 KB. Omissions are disclosed. Check billing and quotas.";
        ShowReport(demo: true);
        MainTabs.SelectedItem = EvidenceTab;
        DemoRequestExpander.IsVisible = DemoRequestExpander.IsExpanded = true;
        StatusText.Text = "Gameplay telemetry preview prepared locally. Select Send summary to Gemini to send it. No request has been sent.";
    }

    private async void SendGeminiClick(object? sender, RoutedEventArgs args)
    {
        if (_geminiRequest is not { } request || _geminiApiKey is not { } key ||
            _demo is not { } demo || _cancellation is not null)
        {
            StatusText.Text = "Save a Gemini key and prepare a summary before sending.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _checkFinished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        SetBusy(true);
        ProgressPanel.IsVisible = true;
        CheckProgress.Maximum = 1;
        CheckProgress.Value = 0;
        ProgressDetails.Text = "Sending the summary to Google Gemini. No retry or model fallback.";
        StatusText.Text = "Waiting for Gemini commentary. Cancel cannot retract a summary already received by Google.";
        try
        {
            var response = await _gemini.ReviewAsync(request, key, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _demoCommentary = response.Render(demo.Players);
            ShowReport(demo: true);
            MainTabs.SelectedItem = FinalResultTab;
            CheckProgress.Value = 1;
            ProgressDetails.Text = "Gemini request finished. AI commentary is separate from all verdicts.";
            StatusText.Text = "Unverified Gemini commentary received. Check its claims against the local demo.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Gemini request canceled. Google may already have received the summary; cancellation cannot retract it.";
            ProgressDetails.Text = "Canceled; no commentary accepted.";
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            StatusText.Text = "Gemini review failed: " + ex.Message;
            ProgressDetails.Text = "No commentary accepted. The local gameplay report remains available.";
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);
            _checkFinished.TrySetResult();
        }
    }

    private void SaveGeminiKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(GeminiKeyBox.Text))
                throw new FormatException("Paste a Gemini key first, or select Remove key.");
            var entered = GeminiKeyBox.Text.Trim();
            SettingsStore.SaveGeminiApiKey(entered);
            _geminiApiKey = SettingsStore.LoadGeminiApiKey();
            if (_geminiApiKey != entered)
                throw new IOException("The stored Gemini key could not be verified after saving. Please try again.");
            InvalidateGeminiDraft();
            UpdateKeyStatus();
            SetBusy(false);
            StatusText.Text = "Gemini key saved using the app's protected storage. No network request was made.";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            StatusText.Text = "Could not save Gemini key: " + ex.Message;
        }
    }

    private void RemoveGeminiKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            SettingsStore.SaveGeminiApiKey(null);
            _geminiApiKey = null;
            InvalidateGeminiDraft();
            UpdateKeyStatus();
            SetBusy(false);
            StatusText.Text = "Gemini key removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not remove Gemini key: " + ex.Message;
        }
    }

    private void OpenGeminiWebsiteClick(object? sender, RoutedEventArgs args) => Open("https://gemini.google.com/");
    private void OpenGeminiKeyPageClick(object? sender, RoutedEventArgs args) => Open("https://aistudio.google.com/apikey");

    private async void TestGeminiKeyClick(object? sender, RoutedEventArgs args)
    {
        if (_geminiApiKey is not { } key)
        {
            StatusText.Text = "Save a Gemini API key before testing it.";
            return;
        }
        var model = GeminiModelBox.Text ?? "";
        await TestKeyAsync("Gemini", token => _gemini.ValidateKeyAsync(key, model, token),
            "Gemini accepted the key for selected-model metadata access and reports generateContent support. No demo data was sent. Generation quota/free usage are not established.");
    }

    private void SaveKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var entered = SteamKeyBox.Text;
            if (string.IsNullOrWhiteSpace(entered))
            {
                StatusText.Text = "Paste your 32-character Steam Web API key first, or use Remove key to clear it.";
                return;
            }
            SettingsStore.SaveSteamApiKey(entered);
            _steamApiKey = SettingsStore.LoadSteamApiKey();
            UpdateKeyStatus();
            StatusText.Text = "Steam Web API key saved. The next check will include Valve's official ban record and statistics.";
        }
        catch (FormatException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save the key: " + ex.Message;
        }
    }

    private void RemoveKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            SettingsStore.SaveSteamApiKey(null);
            _steamApiKey = null;
            UpdateKeyStatus();
            StatusText.Text = "Steam Web API key removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not remove the key: " + ex.Message;
        }
    }

    private void OpenKeyPageClick(object? sender, RoutedEventArgs args) =>
        Open("https://steamcommunity.com/dev/apikey");

    private async void TestSteamKeyClick(object? sender, RoutedEventArgs args)
    {
        var key = _steamApiKey;
        if (key is null)
        {
            StatusText.Text = "Save a Steam API key before testing it.";
            return;
        }
        var players = (PlayerInput.Text ?? "").Split([' ', ',', ';', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (players.Length != 1)
        {
            StatusText.Text = "Enter one SteamID64 or profile URL above to test the Steam key.";
            return;
        }
        try
        {
            var id = PlayerId.Parse(players[0]);
            using var client = new TrackerClient();
            await TestKeyAsync("Steam",
                token => client.ValidateSteamKeyAsync(key, id, token));
        }
        catch (FormatException ex) { StatusText.Text = ex.Message; }
    }

    private async void TestFaceitKeyClick(object? sender, RoutedEventArgs args)
    {
        var key = _faceitApiKey;
        if (key is null)
        {
            StatusText.Text = "Save a FACEIT API key before testing it.";
            return;
        }
        using var client = new TrackerClient();
        await TestKeyAsync("FACEIT",
            token => client.ValidateFaceitKeyAsync(key, token));
    }

    private async System.Threading.Tasks.Task TestKeyAsync(string provider,
        Func<CancellationToken, System.Threading.Tasks.Task> validate, string? successMessage = null)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _checkFinished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        SetBusy(true);
        StatusText.Text = $"Testing the saved {provider} key...";
        ProgressPanel.IsVisible = false;
        try
        {
            await validate(cancellation.Token);
            if (!_closing)
                StatusText.Text = successMessage ?? $"{provider} accepted the saved key for the test request. Other endpoints may have different access requirements.";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            if (!_closing)
                StatusText.Text = ex.StatusCode is { } status
                    ? $"{provider} key test failed (HTTP {(int)status}); check the key and service access."
                    : $"{provider} key test failed: network or TLS error.";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = $"{provider} key test " +
                (cancellation.IsCancellationRequested ? "canceled." : "timed out.");
        }
        catch (System.Text.Json.JsonException)
        {
            if (!_closing) StatusText.Text = $"{provider} key test failed: unexpected response data.";
        }
        catch (FormatException ex)
        {
            if (!_closing) StatusText.Text = $"{provider} key test failed: {ex.Message}";
        }
        catch (IOException ex)
        {
            if (!_closing) StatusText.Text = $"{provider} key test failed: {ex.Message}";
        }
        finally
        {
            _cancellation = null;
            if (!_closing) SetBusy(false);
            _checkFinished.TrySetResult();
        }
    }

    private async void TestLeetifyKeyClick(object? sender, RoutedEventArgs args)
    {
        var key = _leetifyApiKey;
        if (key is null)
        {
            StatusText.Text = "Save a Leetify API key before testing it.";
            return;
        }
        using var client = new TrackerClient();
        await TestKeyAsync("Leetify",
            token => client.ValidateLeetifyKeyAsync(key, token));
    }

    private void SaveLeetifyKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var entered = LeetifyKeyBox.Text;
            if (string.IsNullOrWhiteSpace(entered))
            {
                StatusText.Text = "Paste your Leetify API key first, or use Remove key to clear it.";
                return;
            }
            SettingsStore.SaveLeetifyApiKey(entered);
            _leetifyApiKey = SettingsStore.LoadLeetifyApiKey();
            UpdateKeyStatus();
            StatusText.Text = _leetifyApiKey == entered.Trim()
                ? "Leetify API key saved. Test it using the button below."
                : "Leetify API key could not be verified after saving. Please try again.";
        }
        catch (FormatException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save the key: " + ex.Message;
        }
    }

    private void RemoveLeetifyKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            SettingsStore.SaveLeetifyApiKey(null);
            _leetifyApiKey = null;
            UpdateKeyStatus();
            StatusText.Text = "Leetify API key removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not remove the key: " + ex.Message;
        }
    }

    private void OpenLeetifyKeyPageClick(object? sender, RoutedEventArgs args) =>
        Open("https://leetify.com/app/developer");

    private void SaveFaceitKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var entered = FaceitKeyBox.Text;
            if (string.IsNullOrWhiteSpace(entered))
            {
                StatusText.Text = "Paste your FACEIT Data API key first, or use Remove key to clear it.";
                return;
            }
            SettingsStore.SaveFaceitApiKey(entered);
            _faceitApiKey = SettingsStore.LoadFaceitApiKey();
            UpdateKeyStatus();
            if (!_refreshingProStats) ProStatsStatus.Text = ProStatsIdleStatus();
            StatusText.Text = "FACEIT API key saved. The next check will include FACEIT lifetime context when available.";
        }
        catch (FormatException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save the key: " + ex.Message;
        }
    }

    private void RemoveFaceitKeyClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            SettingsStore.SaveFaceitApiKey(null);
            _faceitApiKey = null;
            UpdateKeyStatus();
            if (!_refreshingProStats) ProStatsStatus.Text = ProStatsIdleStatus();
            StatusText.Text = "FACEIT API key removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not remove the key: " + ex.Message;
        }
    }

    private void OpenFaceitKeyPageClick(object? sender, RoutedEventArgs args) =>
        Open("https://developers.faceit.com/");

    private void OpenBlockedClick(object? sender, RoutedEventArgs args)
    {
        foreach (var source in _browserFallback) Open(source.Url.AbsoluteUri);
        StatusText.Text = _browserFallback.Count == 0
            ? "No blocked public pages to open."
            : "Opened the blocked public page(s) in your own browser. The app cannot import that data automatically; review it there.";
    }

    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Could not open your browser: {ex.Message}";
        }
    }
}