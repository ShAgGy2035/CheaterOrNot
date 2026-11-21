using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CheaterOrNot.Core;

namespace CheaterOrNot;

public sealed class GeminiVideoWindow : Window
{
    private readonly GeminiReviewClient _client = new();
    private readonly TextBox _model = new() { Name = "VideoAiModelBox", Width = 260 };
    private readonly CheckBox _firstClipOnly = new() { Name = "FirstClipOnlyBox", Content = "First clip only (video-only diagnostic)" };
    private readonly CheckBox _includeClipEvents = new()
    {
        Name = "IncludeClipEventsBox", Content = "Include first clip's local event context (no aggregate telemetry)", IsEnabled = false
    };
    private readonly Button _prepare = new() { Content = "Prepare local video preview", Name = "PrepareVideoAiButton" };
    private readonly Button _play = new() { Content = "Open selected preview clip", Name = "PlayVideoAiClipButton", IsEnabled = false };
    private readonly Button _send = new() { Content = "Send all prepared batches to Gemini", Name = "SendVideoAiButton", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", Name = "CancelVideoAiButton", IsEnabled = false };
    private readonly ComboBox _clips = new() { Name = "VideoAiClipPicker", MinWidth = 250 };
    private readonly TextBlock _status = new() { Name = "VideoAiStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _preview = new() { Name = "VideoAiPreviewBox", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _response = new() { Name = "VideoAiResponseBox", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TabControl _tabs;
    private readonly DemoImportResult _demo;
    private readonly string _sourceHash;
    private readonly string? _key;
    private IReadOnlyList<GeminiPreparedVideoReview>? _preparedBatches;
    private (GeminiPreparedVideoReview Batch, GeminiVideoPreviewFile File)[] _previewFiles = [];
    private CancellationTokenSource? _cancellation;
    private bool _closing;

    public GeminiVideoWindow(DemoImportResult demo, string sourceHash, string? key, string model)
    {
        (_demo, _sourceHash, _key) = (demo, sourceHash, key);
        Title = "Gemini video review";
        Width = 1100;
        Height = 820;
        MinWidth = 850;
        MinHeight = 650;
        Background = new SolidColorBrush(Color.Parse("#101827"));
        _model.Text = model;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _model, _firstClipOnly, _includeClipEvents, _prepare, _clips, _play, _send, _cancel })
        {
            control.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(control);
        }
        _tabs = new TabControl
        {
            ItemsSource = new[] { new TabItem { Header = "Video request preview", Content = _preview },
                new TabItem { Header = "Unverified video commentary", Content = _response } }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = "VIDEO-ASSISTED REVIEW - NOT A CHEATING VERDICT\n" +
                "Local preparation matches demo/clip hashes and creates smaller review copies. Selecting Send all is the upload authorization and sends every prepared batch to Google; quotas/charges and data-use terms apply. " +
                "HUD names, avatars, map text and game audio may remain. Sampling can miss fast/fine motion. " +
                "No raw demo is uploaded; each bounded inline request goes only to generativelanguage.googleapis.com. " +
                "Transient 502/503/504 failures receive bounded retries; no model fallback or cloud file upload session is used. A billing-enabled project may incur charges. " +
                "Successful commentary is saved locally beside the preview copies WITH locally mapped names/SteamIDs; do not distribute private review folders.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);
        Grid.SetRow(_status, 2);
        _status.Margin = new Thickness(0, 8, 0, 8);
        layout.Children.Add(_status);
        Grid.SetRow(_tabs, 3);
        layout.Children.Add(_tabs);
        Content = layout;
        _model.TextChanged += (_, _) => ResetPreview();
        _firstClipOnly.IsCheckedChanged += (_, _) =>
        {
            if (_firstClipOnly.IsChecked != true) _includeClipEvents.IsChecked = false;
            ResetPreview();
        };
        _includeClipEvents.IsCheckedChanged += (_, _) => ResetPreview();
        _prepare.Click += async (_, _) => await PrepareAsync();
        _play.Click += (_, _) =>
        {
            try
            {
                if (_clips.SelectedIndex < 0 || _clips.SelectedIndex >= _previewFiles.Length)
                    throw new IOException("Prepare and select a preview clip first.");
                Process.Start(new ProcessStartInfo(_previewFiles[_clips.SelectedIndex].File.Path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            { _status.Text = "Could not open preview footage: " + ex.Message; }
        };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        Opened += async (_, _) => await PrepareAsync();
        Closing += (_, args) =>
        {
            if (_cancellation is null) return;
            args.Cancel = true;
            _closing = true;
            _cancellation.Cancel();
            _status.Text = "Waiting for cancellation. Data already received by Google cannot be retracted.";
        };
        Closed += (_, _) => _client.Dispose();
    }

    private void ResetPreview()
    {
        _preparedBatches = null;
        _previewFiles = [];
        _preview.Text = _response.Text = "";
        _clips.ItemsSource = null;
        _status.Text = "Model or clip scope changed. Prepare a new preview; nothing was sent.";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var idle = _cancellation is null;
        _prepare.IsEnabled = _model.IsEnabled = idle;
        _firstClipOnly.IsEnabled = idle;
        _includeClipEvents.IsEnabled = idle && _firstClipOnly.IsChecked == true;
        _clips.IsEnabled = idle && _previewFiles.Length > 0;
        _play.IsEnabled = idle && _previewFiles.Length > 0;
        _send.IsEnabled = idle && _preparedBatches is { Count: > 0 } && _key is not null;
        _cancel.IsEnabled = !idle;
    }

    private async Task PrepareAsync()
    {
        if (_cancellation is not null) return;
        ResetPreview();
        await RunAsync(async token =>
        {
            var prepared = await DemoVideoReview.PrepareBatchesAsync(Path.Combine(Program.PortableDirectory, "tools"),
                Path.Combine(Program.PortableDirectory, "review-videos"), _demo, _sourceHash, _model.Text ?? "",
                new Progress<string>(message => _status.Text = message), token, _firstClipOnly.IsChecked == true,
                _includeClipEvents.IsChecked == true);
            token.ThrowIfCancellationRequested();
            _preparedBatches = prepared;
            _previewFiles = prepared.SelectMany(batch => batch.Files.Select(file => (batch, file))).ToArray();
            _preview.Text = prepared[0].PreviewText +
                (prepared.Count > 1 ? $"\n\n{prepared.Count - 1} additional request preview(s) are prepared. Select Send all to upload every listed request." : "");
            _clips.ItemsSource = prepared.SelectMany((batch, index) => batch.ClipLabels.Select(label =>
                $"Request {index + 1} | {label}")).ToArray();
            _clips.SelectedIndex = 0;
            _tabs.SelectedIndex = 0;
            _status.Text = _key is null
                ? $"Prepared {prepared.Sum(batch => batch.Files.Count)} clip(s) in {prepared.Count} request(s); nothing sent. Save/test a Gemini key under API Keys, then reopen this window."
                : $"Prepared {prepared.Sum(batch => batch.Files.Count)} clip(s) in {prepared.Count} request(s); nothing sent. Select Send all prepared batches to Gemini.";
        }, false);
    }

    private async Task SendAsync()
    {
        if (_preparedBatches is not { Count: > 0 } prepared || _key is not { } key || _cancellation is not null)
        {
            _status.Text = "Prepare a video preview and save a Gemini key before sending.";
            return;
        }
        _response.Text = "";
        await RunAsync(async token =>
        {
            var progress = new Progress<DemoGeminiProgress>(update =>
            {
                _status.Text = update.Message;
                if (update.RequestPreview is { } preview) _preview.Text = preview;
            });
            var saved = await DemoGeminiAnalysis.SubmitBatchesAsync(prepared, _demo.Players, key, _client,
                progress, token);
            token.ThrowIfCancellationRequested();
            _response.Text = saved.Text + "\n\nSaved locally: " + saved.Path;
            _tabs.SelectedIndex = 1;
            _status.Text = "All batches passed validation. Combined unverified commentary was saved locally; verify every claim.";
        }, true);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool sending)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        UpdateActions();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            _status.Text = sending
                ? "Canceled; Google may already have received this or earlier batch footage. No combined commentary accepted."
                : "Local preparation canceled; nothing sent. Partial local review copies may remain.";
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { _status.Text = "Video review failed: " + ex.Message; }
        finally
        {
            _cancellation = null;
            UpdateActions();
            if (_closing) Close();
        }
    }
}
