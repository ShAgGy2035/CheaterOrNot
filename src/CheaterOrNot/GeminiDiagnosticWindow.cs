using System;
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

public sealed class GeminiDiagnosticWindow : Window
{
    private readonly GeminiReviewClient _client = new();
    private readonly string? _key;
    private readonly TextBox _model = new() { Name = "DiagnosticModelBox", Width = 260 };
    private readonly CheckBox _structured = new() { Name = "StructuredDiagnosticBox", Content = "Structured JSON (no media)" };
    private readonly TextBox _preview = new() { Name = "DiagnosticPreviewBox", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _result = new() { Name = "DiagnosticResultBox", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Name = "RunDiagnosticButton", Content = "Run diagnostic" };
    private readonly Button _cancel = new() { Name = "CancelDiagnosticButton", Content = "Cancel", IsEnabled = false };
    private readonly TextBlock _status = new() { Name = "DiagnosticStatus", TextWrapping = TextWrapping.Wrap };
    private GeminiGenerationDiagnostic? _prepared;
    private CancellationTokenSource? _cancellation;
    private bool _closing;

    public GeminiDiagnosticWindow(string? key, string model)
    {
        _key = key;
        Title = "Gemini generation diagnostic - no gameplay";
        Width = 1050;
        Height = 760;
        MinWidth = 800;
        MinHeight = 600;
        Background = new SolidColorBrush(Color.Parse("#101827"));
        _model.Text = model;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _model, _structured, _run, _cancel })
        {
            control.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(control);
        }
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        layout.Children.Add(new TextBlock
        {
            Text = "NO GAMEPLAY OR FOOTAGE: tests model access, then a fixed plain-text or structured-JSON prompt. Inspect the selected mode's exact preview. " +
                "Run diagnostic sends the selected text-only request to Google; quotas/charges and data-use terms apply. " +
                "The saved key stays private. Opening this window makes no request. No retries or fallback. " +
                "A pass does not validate video review; a failure does not establish permanent infeasibility. " +
                "Redacted results are saved locally; provider bodies and generated text are not saved.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);
        Grid.SetRow(_status, 2);
        _status.Margin = new Thickness(0, 8, 0, 8);
        layout.Children.Add(_status);
        var tabs = new TabControl
        {
            ItemsSource = new[] { new TabItem { Header = "Exact diagnostic preview", Content = _preview },
                new TabItem { Header = "Redacted diagnostic result", Content = _result } }
        };
        Grid.SetRow(tabs, 3);
        layout.Children.Add(tabs);
        Content = layout;
        _model.TextChanged += (_, _) => Prepare();
        _structured.IsCheckedChanged += (_, _) => Prepare();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _run.Click += async (_, _) =>
        {
            if (_prepared is not { } prepared || _key is not { } savedKey ||
                _cancellation is not null)
            {
                _status.Text = "Save a key and select a valid diagnostic first.";
                return;
            }
            _result.Text = "";
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            UpdateActions();
            _status.Text = "Running the diagnostic. No footage or gameplay is sent.";
            try
            {
                var result = await _client.DiagnoseGenerationAsync(prepared, savedKey, cancellation.Token);
                var json = result.ToJson();
                _result.Text = json;
                tabs.SelectedIndex = 1;
                _status.Text = result.Passed ? "Text-only diagnostic passed; video review remains unverified." :
                    "Diagnostic did not pass: " + result.Steps.Last().Outcome;
                var directory = Path.Combine(Program.PortableDirectory, "diagnostics");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "gemini-generation-" + Guid.NewGuid().ToString("N") + ".json");
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await output.WriteAsync(bytes, cancellation.Token);
                }
                _result.Text += "\n\nSaved locally: " + path;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Diagnostic canceled; delivered requests cannot be retracted. A partial report may remain. No automatic retry.";
            }
            catch (Exception error) when (error is IOException or FormatException or UnauthorizedAccessException)
            {
                _status.Text = "Diagnostic or report save failed: " + error.Message;
            }
            finally
            {
                _cancellation = null;
                UpdateActions();
                if (_closing) Close();
            }
        };
        Closing += (_, args) =>
        {
            if (_cancellation is null) return;
            args.Cancel = true;
            _closing = true;
            _cancellation.Cancel();
            _status.Text = "Waiting for cancellation; delivered requests cannot be retracted.";
        };
        Closed += (_, _) => _client.Dispose();
        Prepare();
    }

    private void Prepare()
    {
        _prepared = null;
        _result.Text = "";
        _preview.Text = "";
        try
        {
            _prepared = new GeminiGenerationDiagnostic(_model.Text ?? "", _structured.IsChecked == true);
            _preview.Text = _prepared.Preview;
            _status.Text = _key is null ? "Save a Gemini key, then reopen this diagnostic. Nothing sent." :
                "Preview ready; nothing sent. Select Run diagnostic to send the selected text-only request.";
        }
        catch (FormatException error) { _status.Text = error.Message; }
        UpdateActions();
    }

    private void UpdateActions()
    {
        var idle = _cancellation is null;
        _model.IsEnabled = idle;
        _structured.IsEnabled = idle;
        _run.IsEnabled = idle && _prepared is not null && _key is not null;
        _cancel.IsEnabled = !idle;
    }
}
