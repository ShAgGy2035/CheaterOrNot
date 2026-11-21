using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;

namespace CheaterOrNot.Core;

public enum DemoGeminiStage { CheckingCapture, Capturing, Preparing, Sending, Saving }
public sealed record DemoGeminiProgress(DemoGeminiStage Stage, string Message, string? RequestPreview = null);

public static class DemoGeminiAnalysis
{
    public static async Task<GeminiSavedVideoReview?> RunAsync(string toolsRoot, string outputsRoot,
        string demoPath, DemoImportResult demo, string sourceHash, string model, string apiKey,
        GeminiReviewClient client,
        IProgress<DemoGeminiProgress>? progress, CancellationToken token)
    {
        if (!GeminiReviewClient.IsValidKey(apiKey))
            throw new FormatException("Save a valid Gemini API key before analyzing a demo.");
        _ = GeminiReviewRequest.Create(demo.Gameplay ??
            throw new FormatException("Import gameplay telemetry before analysis."), demo.IsIncomplete, model);
        await using var source = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(source, token)) != sourceHash)
            throw new IOException("The demo changed after import. Select it again before analysis.");
        var plan = DemoVideoCapture.Plan(demo);
        progress?.Report(new(DemoGeminiStage.Capturing,
            $"Creating {plan.Count} fresh kill-centered clips. Older captures for this exact demo remain until the new set verifies."));
        Directory.CreateDirectory(outputsRoot);
        if ((File.GetAttributes(outputsRoot) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Linked capture output folders cannot be used for fresh video capture.");
        var folder = Path.Combine(outputsRoot, Guid.NewGuid().ToString("N"));
        await DemoVideoCapture.CaptureAsync(toolsRoot, demoPath, folder, plan,
            progress is null ? null : new InlineProgress<string>(message =>
                progress.Report(new(DemoGeminiStage.Capturing, message))), token);
        DemoVideoManifest manifest;
        try { manifest = await DemoVideoReview.ReadManifestAsync(folder, sourceHash, token); }
        catch (FormatException error)
        { throw new IOException("The fresh capture manifest could not be verified. Older captures were retained.", error); }
        var captureProblem = await CaptureVerificationProblemAsync(folder, manifest, sourceHash, source.Length, plan, token);
        if (captureProblem is not null)
            throw new IOException("The fresh capture failed verification: " + captureProblem + " Older captures were retained.");
        DemoVideoReview.Validate(manifest, sourceHash, plan);
        var replaced = await DemoVideoReview.ReplaceOlderCapturesAsync(outputsRoot, sourceHash, source.Length,
            plan, folder, token);
        progress?.Report(new(DemoGeminiStage.Capturing,
            $"Fresh capture verified. Replaced {replaced} older capture folder(s) for this exact demo; other demos were preserved."));
        token.ThrowIfCancellationRequested();
        progress?.Report(new(DemoGeminiStage.Preparing, "Preparing verified kill clips in bounded Gemini request batches..."));
        var prepared = await DemoVideoReview.PrepareBatchesAsync(toolsRoot, outputsRoot, demo, sourceHash, model,
            progress is null ? null : new InlineProgress<string>(message =>
                progress.Report(new(DemoGeminiStage.Preparing, message))), token, captureFolder: folder);
        token.ThrowIfCancellationRequested();
        progress?.Report(new(DemoGeminiStage.Sending,
            $"Prepared {prepared.Count} request batch(es) from {prepared.Sum(batch => batch.Files.Count)} verified clip(s). Sending automatically."));
        return await SubmitBatchesAsync(prepared, demo.Players, apiKey, client, progress, token);
    }

    internal static async Task<GeminiSavedVideoReview> SubmitAsync(GeminiPreparedVideoReview prepared,
        IReadOnlyList<DemoPlayer> roster, string apiKey, GeminiReviewClient client,
        IProgress<DemoGeminiProgress>? progress, CancellationToken token)
    {
        return await SubmitBatchesAsync([prepared], roster, apiKey, client, progress, token);
    }

    public static async Task<GeminiSavedVideoReview> SubmitBatchesAsync(
        IReadOnlyList<GeminiPreparedVideoReview> prepared, IReadOnlyList<DemoPlayer> roster,
        string apiKey, GeminiReviewClient client, IProgress<DemoGeminiProgress>? progress,
        CancellationToken token)
    {
        if (prepared.Count == 0) throw new FormatException("No Gemini video request batches were prepared.");
        var results = new List<GeminiReviewResult>(prepared.Count);
        for (var index = 0; index < prepared.Count; index++)
        {
            var batch = prepared[index];
            var request = await batch.PrepareRequestAsync(token);
            var checkpoint = await ReadBatchCheckpointAsync(batch, index, token);
            if (checkpoint is not null)
            {
                var restored = GeminiReviewResult.Parse(checkpoint.ResponseJson, request);
                results.Add(restored);
                batch.ReleaseRequest();
                progress?.Report(new(DemoGeminiStage.Sending,
                    $"Reused accepted Gemini batch checkpoint {index + 1} of {prepared.Count}; it was not uploaded again."));
                continue;
            }
            GeminiReviewResult result;
            try
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(DemoGeminiStage.Sending,
                    $"Sending Gemini request {index + 1} of {prepared.Count} with {batch.Files.Count} kill clip(s). Cancellation cannot retract delivered data.",
                    batch.PreviewText));
                token.ThrowIfCancellationRequested();
                try { result = await client.ReviewAsync(request, apiKey, token); }
                catch (Exception error) when (error is HttpRequestException or IOException or FormatException or InvalidOperationException)
                {
                    throw new FormatException(
                        $"Gemini request {index + 1} of {prepared.Count} failed after {results.Count} earlier request(s) succeeded. " +
                        "No combined review was accepted; requests already delivered cannot be recalled. Details: " + error.Message, error);
                }
            }
            finally
            {
                batch.ReleaseRequest();
            }
            results.Add(result);
            await SaveBatchCheckpointAsync(prepared, index, result, token);
            token.ThrowIfCancellationRequested();
            if (index + 1 < prepared.Count)
            {
                progress?.Report(new(DemoGeminiStage.Sending,
                    $"Request {index + 1} completed. Waiting briefly before Gemini request {index + 2} to reduce provider rate limiting."));
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
        progress?.Report(new(DemoGeminiStage.Saving, "All batches passed response validation. Saving the unverified combined review locally..."));
        return await GeminiPreparedVideoReview.SaveBatchResultsAsync(prepared, results, roster, token);
    }

    private static async Task<DemoGeminiBatchCheckpoint?> ReadBatchCheckpointAsync(
        GeminiPreparedVideoReview batch, int index, CancellationToken token)
    {
        var root = Path.GetDirectoryName(batch.Files[0].Path);
        while (root is not null && !File.Exists(Path.Combine(root, "review-manifest.json")))
            root = Path.GetDirectoryName(root);
        if (root is null) return null;
        var path = Path.Combine(root, "gemini-batch-checkpoints.json");
        if (!File.Exists(path)) return null;
        List<DemoGeminiBatchCheckpoint>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<DemoGeminiBatchCheckpoint>>(
                await File.ReadAllTextAsync(path, token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            throw new IOException("The Gemini batch checkpoint is invalid. No upload was attempted from this checkpoint.");
        }
        var entry = entries?.SingleOrDefault(item => item.Index == index);
        return entry is null || entry.RequestHash != batch.GetRequestHash() ? null : entry;
    }

    private static async Task SaveBatchCheckpointAsync(IReadOnlyList<GeminiPreparedVideoReview> batches,
        int index, GeminiReviewResult result, CancellationToken token)
    {
        var firstPath = Path.GetDirectoryName(batches[0].Files[0].Path) ??
            throw new IOException("Could not locate the retained video preparation folder.");
        var captureRoot = firstPath;
        while (captureRoot is not null && !File.Exists(Path.Combine(captureRoot, "review-manifest.json")))
            captureRoot = Path.GetDirectoryName(captureRoot);
        if (captureRoot is null)
            return;
        var checkpoint = Path.Combine(captureRoot, "gemini-batch-checkpoints.json");
        var temporary = checkpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var entries = new List<DemoGeminiBatchCheckpoint>();
        if (File.Exists(checkpoint))
        {
            try
            {
                entries = JsonSerializer.Deserialize<List<DemoGeminiBatchCheckpoint>>(
                    await File.ReadAllTextAsync(checkpoint, token),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            }
            catch (JsonException)
            {
                throw new IOException("The existing Gemini batch checkpoint is invalid and was not overwritten.");
            }
        }
        entries.RemoveAll(entry => entry.Index == index);
        entries.Add(new(index, batches[index].GetRequestHash(), result.RawJson));
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(entries.OrderBy(entry => entry.Index),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
        File.Move(temporary, checkpoint, true);
    }

    internal sealed record DemoGeminiBatchCheckpoint(int Index, string RequestHash, string ResponseJson);

    internal static async Task<string?> CaptureVerificationProblemAsync(string folder, DemoVideoManifest manifest,
        string sourceHash, long sourceBytes, IReadOnlyList<DemoVideoClip> plan, CancellationToken token)
    {
        try
        {
            DemoVideoReview.Validate(manifest, sourceHash, plan);
            if (manifest.SourceBytes != sourceBytes) return "Capture has a mismatched source size.";
            foreach (var video in manifest.Videos)
            {
                token.ThrowIfCancellationRequested();
                var path = DemoVideoReview.ResolveClip(folder, video.Path);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != video.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != video.Sha256)
                    return $"Clip {video.Number} failed size/hash verification.";
            }
            return null;
        }
        catch (FormatException error) { return "Capture did not verify: " + error.Message; }
        catch (FileNotFoundException) { return "A captured clip is missing."; }
        catch (DirectoryNotFoundException) { return "Capture directory is missing."; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
