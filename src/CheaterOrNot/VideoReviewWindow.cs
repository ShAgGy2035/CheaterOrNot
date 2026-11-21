using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CheaterOrNot.Core;

namespace CheaterOrNot;

public sealed class VideoReviewWindow : Window
{
    private readonly string _toolsRoot = Path.Combine(Program.PortableDirectory, "tools");
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220 };
    private readonly Button _check = new() { Content = "Check component updates", Name = "CheckComponentUpdatesButton" };
    private readonly Button _update = new() { Content = "Update capture tools (~300 MB)", Name = "UpdateCaptureToolsButton" };
    private readonly Button _browser = new() { Content = "Repair matching browser", Name = "RepairBrowserButton" };
    private readonly Button _capture = new() { Content = "Capture review clips locally", Name = "CaptureVideoButton" };
    private readonly Button _review = new() { Content = "Preview Gemini video review", Name = "PreviewVideoAiButton" };
    private readonly Button _summary = new() { Content = "Preview Gemini summary (no video)", Name = "PreviewGeminiButton" };
    private readonly Button _open = new() { Content = "Open output folder", IsEnabled = false, Name = "OpenVideoFolderButton" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false, Name = "CancelVideoButton" };
    private readonly DemoImportResult? _demo;
    private readonly string? _demoPath;
    private readonly long? _length;
    private readonly DateTime? _modified;
    private readonly bool _canReview;
    private CancellationTokenSource? _cancellation;
    private bool _canCancel;
    private bool _finishStepBeforeStopping;
    private string? _output;

    public VideoReviewWindow(DemoImportResult? demo, string? demoPath, long? sourceLength, DateTime? sourceModified,
        string? sourceSha256, string? geminiKey, string geminiModel, bool setupOnly = false)
    {
        (_demo, _demoPath) = (demo, demoPath);
        (_length, _modified) = (sourceLength, sourceModified);
        Title = setupOnly ? "Set up capture tools" : "Advanced video tools";
        Width = 1000;
        Height = 680;
        MinWidth = 720;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.Parse("#101827"));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (setupOnly) _update.Content = "Set up capture tools (~300 MB)";
        foreach (var button in setupOnly ? new[] { _update, _cancel } : new[] { _capture, _review, _summary, _open, _cancel })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(button);
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(24) };
        var heading = new TextBlock { Text = Title.ToUpperInvariant(), FontSize = 22, Foreground = new SolidColorBrush(Color.Parse("#63D8BD")) };
        var notice = new TextBlock
        {
            Text = setupOnly
                ? "This one-time setup downloads the capture tools into the portable folder. It does not import a demo, start CS2, or send footage. Close this window and select Analyze demo with Gemini when setup finishes."
                : "Steam and CS2 must already be installed. Capture uses upstream CS Demo Manager and FFmpeg components, downloaded and verified automatically when missing, and can modify game integration files. Close any running game first; never use this session for matchmaking. One attacker-POV clip is planned per eligible human-vs-human kill, not per suspicious event. Large matches can take well over 20 minutes; MP4 encoding starts after recording finishes. Native recording can temporarily use many GB on the game drive. X-ray and player voices are requested off; game audio/HUD may remain. No video is sent to Google during capture. The main Analyze demo with Gemini button automates import, capture and authorized upload. Rendered footage cannot recover mouse input or prove cheating.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12)
        };
        layout.Children.Add(heading);
        Grid.SetRow(notice, 1);
        layout.Children.Add(notice);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        if (!setupOnly)
        {
            var maintenanceActions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var button in new[] { _check, _update, _browser })
            {
                button.Margin = new Thickness(0, 0, 8, 8);
                maintenanceActions.Children.Add(button);
            }
            var maintenance = new Expander { Header = "Maintenance", Content = maintenanceActions, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(maintenance, 3);
            layout.Children.Add(maintenance);
        }
        Grid.SetRow(_details, 4);
        layout.Children.Add(_details);
        Content = layout;
        _capture.IsEnabled = demo?.Gameplay is not null && demoPath is not null;
        _review.IsEnabled = _canReview = demo?.Gameplay is not null && sourceSha256 is not null;
        _summary.IsEnabled = demo?.Gameplay is not null;
        _summary.Click += (_, _) => Close(true);
        _review.Click += async (_, _) =>
        {
            if (demo is not null && sourceSha256 is not null)
                await new GeminiVideoWindow(demo, sourceSha256, geminiKey, geminiModel).ShowDialog(this);
            else
                _details.Text = "Reimport a local demo to bind its recording before video review.";
        };
        var savedOutputs = Path.Combine(Program.PortableDirectory, "review-videos");
        if (Directory.Exists(savedOutputs)) _output = savedOutputs;
        _open.IsEnabled = _output is not null;
        _details.Text = setupOnly ? "Select Set up capture tools to begin. Nothing has been downloaded." :
            "Downloaded tools and their private data stay inside this app's tools folder. Never redistribute that folder.\n" +
            "Browser upgrades must match the app's Playwright package. Repair downloads this app's compatible browser revision; a newer engine requires an app update.\n" +
            "Component checks contact official GitHub release metadata. Downloads are SHA-256 verified and staged; previous tool versions are retained. Capture retains a local demo index under tools/data; never distribute that private data or recordings with the app. No app-binary update service is configured.\n\n" +
            (demo is null ? "Import a demo first to prepare local video." : DescribePlan(demo));
        _check.Click += async (_, _) => await RunAsync(async token =>
        {
            using var tools = new PortableCaptureTools(_toolsRoot);
            var installed = File.Exists(Path.Combine(_toolsRoot, "capture-tools.json")) ? tools.Installed() : null;
            var releases = await tools.CheckAsync(token);
            _details.Text = string.Join("\n", releases.Select(release =>
                $"{release.Component}: {(installed?.Releases.Any(current => current.Sha256 == release.Sha256) == true ? "up to date" : "update / verification available")}; official release {release.Version}, {release.Bytes / 1_000_000} MB, SHA256 {release.Sha256}")) +
                "\nNo component was installed. Browser updates remain coupled to the app build.";
        });
        _update.Click += async (_, _) => await RunAsync(async token =>
        {
            using var tools = new PortableCaptureTools(_toolsRoot);
            await tools.UpdateAsync(new Progress<string>(message => _details.Text = message), token);
            _details.Text = "Portable capture tools verified and ready. Previous versions were retained. No system installer was run." +
                (setupOnly ? " Close this window, then select Analyze demo with Gemini." : "");
        });
        _browser.Click += async (_, _) => await RunAsync(async token =>
        {
            _details.Text = "Repairing the exact browser revision supported by this app. This is not an independent Chromium upgrade.";
            var code = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium", "--only-shell"]));
            if (code != 0) throw new IOException($"Browser repair failed (exit {code}).");
            _details.Text = "The matching browser installer completed. Use website checks to verify browser functionality.";
        }, canCancel: false);
        _capture.Click += async (_, _) => await RunAsync(async token =>
        {
            if (!await CheckGameClosedAsync(this))
            {
                _details.Text = "Capture not started. Close CS2, then select Capture review clips locally again. No video was sent to Google.";
                return;
            }
            if (_demo is null || _demoPath is null) throw new FormatException("Import a local demo first.");
            var file = new FileInfo(_demoPath);
            if (!file.Exists || file.Length != _length || file.LastWriteTimeUtc != _modified)
                throw new IOException("The demo changed or moved. Close this window and import it again.");
            using (var tools = new PortableCaptureTools(_toolsRoot))
            {
                try { _ = tools.Installed(); }
                catch (CaptureToolsSetupRequiredException)
                {
                    _details.Text = "Capture tools are missing. Downloading verified upstream components automatically...";
                    await tools.UpdateAsync(new Progress<string>(message => _details.Text = message), token);
                }
            }
            var clips = DemoVideoCapture.Plan(_demo);
            var rawBytes = DemoVideoCapture.RequiredFreeBytes(clips);
            _output = Path.Combine(Program.PortableDirectory, "review-videos", Guid.NewGuid().ToString("N"));
            _details.Text = $"Preparing {clips.Count} local clips; raw images can use about {rawBytes / 1_000_000_000.0:0.0} GB on the game drive.";
            var videos = await DemoVideoCapture.CaptureAsync(_toolsRoot, _demoPath, _output, clips,
                new Progress<string>(message => _details.Text = message), token);
            _open.IsEnabled = true;
            _details.Text = $"Verified {videos.Count} local MP4 clips; {videos.Count(video => video.HasAudio)} have an audio stream.\nOutput: {_output}\n" +
                "The local review manifest records nominal ticks, identities and measured video metadata. Inspect the footage before sharing. No video AI review or upload occurred.";
        }, finishStepBeforeStopping: true);
        _open.Click += (_, _) =>
        {
            try
            {
                if (_output is null || !Directory.Exists(_output)) throw new IOException("No capture output folder is available.");
                Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                _details.Text = "Could not open capture output: " + ex.Message;
            }
        };
        _cancel.Click += (_, _) =>
        {
            _cancel.IsEnabled = false;
            _details.Text = _finishStepBeforeStopping
                ? "Stop requested. Waiting for the current indexing or recording/encoding step and its cleanup to finish safely..."
                : "Canceling; waiting for confirmation...";
            _cancellation?.Cancel();
        };
        Closing += (_, args) =>
        {
            if (_cancellation is null) return;
            args.Cancel = true;
            if (!_canCancel)
            {
                _details.Text = "Wait for the matching browser installer to finish before closing.";
                return;
            }
            _cancellation.Cancel();
            _details.Text = _finishStepBeforeStopping
                ? "Stop requested. This window must wait for the current engine step and cleanup to finish safely."
                : "Waiting for cancellation before closing. Some partial local files may remain.";
        };
    }

    internal static async Task<bool> CheckGameClosedAsync(Window owner)
    {
        if (!DemoVideoCapture.IsGameRunning()) return true;
        var warning = new Window
        {
            Title = "Close CS2 before capturing video",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#101827"))
        };
        var close = new Button
        {
            Name = "AcknowledgeCs2RunningButton",
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 90
        };
        close.Click += (_, _) => warning.Close();
        warning.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "CS2 is already running",
                    FontSize = 22,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Video capture cannot start while CS2 is open. Close CS2 yourself when safe, then try the action again.\n\n" +
                        "The app will not close your game. No new capture has started and nothing has been sent to Gemini. Existing reports and recordings are unchanged.",
                    TextWrapping = TextWrapping.Wrap
                },
                close
            }
        };
        await warning.ShowDialog(owner);
        return false;
    }

    private static string DescribePlan(DemoImportResult demo)
    {
        try
        {
            return "Prepared kill-centered clips (not detections):\n" + string.Join("\n", DemoVideoCapture.Plan(demo).Select(clip =>
                $"{clip.Number}. {clip.Alias}, round {clip.Round}, demo ticks {clip.StartTick}-{clip.EndTick}"));
        }
        catch (FormatException ex) { return "Video not ready: " + ex.Message; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool canCancel = true, bool finishStepBeforeStopping = false)
    {
        if (_cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _canCancel = canCancel;
        _finishStepBeforeStopping = finishStepBeforeStopping;
        _cancel.Content = finishStepBeforeStopping ? "Stop after current step" : "Cancel";
        foreach (var button in new[] { _check, _update, _browser, _capture, _review, _summary, _open }) button.IsEnabled = false;
        _cancel.IsEnabled = canCancel;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            _details.Text = finishStepBeforeStopping
                ? "Stopped safely. No complete result was accepted; partial local files may remain."
                : "Canceled. No complete result was accepted; partial local files may remain.";
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { _details.Text = "Operation failed: " + ex.Message; }
        finally
        {
            _cancellation = null;
            _check.IsEnabled = _update.IsEnabled = _browser.IsEnabled = true;
            _capture.IsEnabled = _demo?.Gameplay is not null && _demoPath is not null;
            _review.IsEnabled = _canReview;
            _summary.IsEnabled = _demo?.Gameplay is not null;
            _open.IsEnabled = _output is not null && Directory.Exists(_output);
            _cancel.IsEnabled = false;
            _cancel.Content = "Cancel";
            _finishStepBeforeStopping = false;
        }
    }
}
