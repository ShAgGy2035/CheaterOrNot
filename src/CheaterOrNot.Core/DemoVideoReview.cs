using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CheaterOrNot.Core;

public sealed record GeminiVideoContext(int Number, string Alias, int Round, int StartTick, int EndTick,
    double DurationSeconds, bool HasAudio)
{
    public DemoVideoClipTimeline? EventTimeline { get; internal init; }
}
public sealed record GeminiVideoPreviewFile(int Number, string Path, string Sha256, long Bytes);
public sealed record GeminiSavedVideoReview(string Path, string Text);

public sealed class GeminiPreparedVideoReview
{
    private GeminiReviewRequest? _request;
    private GeminiReviewRequest? _telemetry;
    private IReadOnlyList<GeminiVideoContext>? _clips;
    private readonly bool _videoOnly;
    private readonly bool _clipEventContextOnly;
    private string? _requestHash;
    private Uri? _requestEndpoint;
    private string _previewText;
    public GeminiReviewRequest Request => _request ??
        throw new InvalidOperationException("The batched request is materialized immediately before sending.");
    public string PreviewText => _previewText;
    public string SourceSha256 { get; }
    public IReadOnlyList<GeminiVideoPreviewFile> Files { get; }
    public int RequestJsonBytes { get; private set; }
    public IReadOnlyList<string> ClipLabels => (_clips ?? Request.VideoClips).Select(c =>
        System.FormattableString.Invariant($"Clip {c.Number} | {c.Alias} | round {c.Round} | {c.DurationSeconds:0.###}s")).ToArray();

    internal GeminiPreparedVideoReview(GeminiReviewRequest request, string preview, IReadOnlyList<GeminiVideoPreviewFile> files,
        string sourceSha256)
    {
        (_request, _previewText, Files, SourceSha256) = (request, preview, files, sourceSha256);
        RequestJsonBytes = Encoding.UTF8.GetByteCount(request.JsonBody);
        _requestEndpoint = request.Endpoint;
    }

    internal GeminiPreparedVideoReview(GeminiReviewRequest telemetry, IReadOnlyList<GeminiVideoContext> clips,
        IReadOnlyList<GeminiVideoPreviewFile> files, string sourceSha256, int requestJsonBytes,
        string preview, bool videoOnly, bool clipEventContextOnly)
    {
        (_telemetry, _clips, Files, SourceSha256, RequestJsonBytes, _previewText, _videoOnly, _clipEventContextOnly) =
            (telemetry, clips, files, sourceSha256, requestJsonBytes, preview, videoOnly, clipEventContextOnly);
    }

    public async Task<GeminiReviewRequest> PrepareRequestAsync(CancellationToken token)
    {
        if (_request is not null)
        {
            _requestEndpoint ??= _request.Endpoint;
            _requestHash ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_request.JsonBody)));
            return _request;
        }
        if (_telemetry is null || _clips is null)
            throw new InvalidOperationException("The video request cannot be prepared.");
        var data = new List<byte[]>(Files.Count);
        foreach (var file in Files)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file.Path, token);
            if (bytes.LongLength != file.Bytes || Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                throw new IOException("A preview clip changed. Prepare a new preview; nothing was sent.");
            data.Add(bytes);
        }
        var request = GeminiReviewRequest.WithVideo(_telemetry, _clips, data, _videoOnly, _clipEventContextOnly);
        var jsonBytes = Encoding.UTF8.GetByteCount(request.JsonBody);
        if (jsonBytes > GeminiReviewRequest.MaximumVideoRequestJsonBytes)
            throw new IOException("The exact Gemini request exceeds the 18 MB inline request limit; nothing was sent.");
        RequestJsonBytes = jsonBytes;
        _request = request;
        _requestEndpoint = request.Endpoint;
        _requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.JsonBody)));
        _previewText = DemoVideoReview.RequestPreview(request, Files, SourceSha256, jsonBytes, _requestHash,
            _videoOnly, _clipEventContextOnly);
        return request;
    }

    internal string GetRequestHash() => _requestHash ??
        (_request is null ? throw new InvalidOperationException("No request was submitted for this batch.") :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_request.JsonBody))));

    internal Uri GetRequestEndpoint() => _requestEndpoint ??
        throw new InvalidOperationException("No request endpoint was recorded for this batch.");

    internal void ReleaseRequest() => _request = null;

    public async Task VerifyPreviewFilesAsync(CancellationToken token)
    {
        foreach (var file in Files)
        {
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != file.Bytes ||
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != file.Sha256)
                throw new IOException("A preview clip changed. Prepare a new preview; nothing was sent.");
        }
    }

    public async Task<GeminiSavedVideoReview> SaveResultAsync(GeminiReviewResult result,
        IReadOnlyList<DemoPlayer> roster, CancellationToken token)
    {
        return await SaveBatchResultsAsync([this], [result], roster, token);
    }

    internal static async Task<GeminiSavedVideoReview> SaveBatchResultsAsync(
        IReadOnlyList<GeminiPreparedVideoReview> batches, IReadOnlyList<GeminiReviewResult> results,
        IReadOnlyList<DemoPlayer> roster, CancellationToken token)
    {
        if (batches.Count == 0 || batches.Count != results.Count ||
            batches.Any(batch => batch.SourceSha256 != batches[0].SourceSha256 || batch.Files.Count == 0))
            throw new FormatException("The batched review results do not match the prepared requests.");
        if (batches.Count == 1)
        {
            var batch = batches[0];
            var report = results[0].Render(roster);
            var content = "Request SHA256: " + batch.GetRequestHash() + "\nDestination: " + batch.GetRequestEndpoint() +
                "\nSource demo SHA256: " + batch.SourceSha256 + "\n" +
                string.Join("\n", batch.Files.Select(file =>
                    $"Review clip {file.Number} SHA256: {file.Sha256}; bytes: {file.Bytes}")) +
                "\n\n" + report;
            var singlePath = Path.Combine(Path.GetDirectoryName(batch.Files[0].Path)!,
                "video-review-" + Guid.NewGuid().ToString("N") + ".txt");
            token.ThrowIfCancellationRequested();
            var singlePending = singlePath + ".pending";
            await using (var output = new FileStream(singlePending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(Encoding.UTF8.GetBytes(content), token);
            token.ThrowIfCancellationRequested();
            File.Move(singlePending, singlePath);
            return new(singlePath, report);
        }
        var rendered = new StringBuilder();
        rendered.AppendLine($"Batched video review: {batches.Count} request(s), {batches.Sum(batch => batch.Files.Count)} clip(s).");
        for (var index = 0; index < batches.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var batch = batches[index];
            rendered.AppendLine($"\n\n=== Batch metadata {index + 1}/{batches.Count} | {batch.Files.Count} clip(s) ===");
            rendered.AppendLine("Request SHA256: " + batch.GetRequestHash() + "\nDestination: " + batch.GetRequestEndpoint());
            foreach (var file in batch.Files)
                rendered.AppendLine($"Review clip {file.Number} SHA256: {file.Sha256}; bytes: {file.Bytes}");
        }
        rendered.AppendLine();
        rendered.Append(GeminiReviewResult.Combine(results).Render(roster));
        var text = "Source demo SHA256: " + batches[0].SourceSha256 + "\n" + rendered;
        var path = Path.Combine(Path.GetDirectoryName(batches[0].Files[0].Path)!,
            "video-review-" + Guid.NewGuid().ToString("N") + ".txt");
        token.ThrowIfCancellationRequested();
        var pending = path + ".pending";
        await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        token.ThrowIfCancellationRequested();
        File.Move(pending, path);
        return new(path, text);
    }
}

public static class DemoVideoReview
{
    private const int MaximumManifestBytes = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<(string Folder, DemoVideoManifest Manifest)> FindAsync(
        string outputsRoot, string sourceSha256, CancellationToken token)
        => await FindMatchingAsync(outputsRoot, sourceSha256, token) ??
            throw new FormatException("No source-bound capture matches this imported demo. Capture new clips first; older unbound captures cannot be attached automatically.");

    internal static async Task<(string Folder, DemoVideoManifest Manifest)?> FindMatchingAsync(
        string outputsRoot, string sourceSha256, CancellationToken token)
    {
        if (!ValidHash(sourceSha256)) throw new FormatException("Reimport the source demo before video review.");
        if (Directory.Exists(outputsRoot))
        {
            foreach (var folder in Directory.GetDirectories(outputsRoot).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new FormatException("Linked capture folders are not eligible for video review.");
                var path = Path.Combine(folder, "review-manifest.json");
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > MaximumManifestBytes)
                    throw new FormatException("A capture manifest exceeds its size limit.");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
                if (!document.RootElement.TryGetProperty("sourceSha256", out var hash)) continue;
                if (hash.ValueKind != JsonValueKind.String || hash.GetString() != sourceSha256) continue;
                var manifest = document.Deserialize<DemoVideoManifest>(JsonOptions) ??
                    throw new FormatException("Invalid capture manifest.");
                return (folder, manifest);
            }
        }
        return null;
    }

    internal static async Task<DemoVideoManifest> ReadManifestAsync(string folder, string sourceSha256,
        CancellationToken token)
    {
        var path = Path.Combine(folder, "review-manifest.json");
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > MaximumManifestBytes)
            throw new FormatException("The capture manifest is missing, linked, or exceeds its size limit.");
        try
        {
            var manifest = JsonSerializer.Deserialize<DemoVideoManifest>(
                await File.ReadAllTextAsync(path, token), JsonOptions) ??
                throw new JsonException("The capture manifest is empty.");
            if (manifest.SourceSha256 != sourceSha256)
                throw new FormatException("The capture manifest belongs to a different source demo.");
            return manifest;
        }
        catch (JsonException error)
        {
            throw new FormatException("The capture manifest is invalid.", error);
        }
    }

    internal static string ResolveClip(string folder, string relative)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new FormatException("A captured clip must use a relative path.");
        var path = Path.GetFullPath(Path.Combine(folder, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) != ".mp4")
            throw new FormatException("A clip path escapes its capture folder or is not MP4.");
        for (var current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new FormatException("Linked capture paths are not eligible for video upload.");
        return path;
    }

    internal static void Validate(DemoVideoManifest manifest, string sourceHash, IReadOnlyList<DemoVideoClip> plan)
    {
        if (manifest.Version != 3 || manifest.SourceSha256 != sourceHash || !ValidHash(sourceHash) ||
            manifest.SourceBytes <= 0 || manifest.Clips is null || manifest.Videos is null ||
            !manifest.Clips.SequenceEqual(plan) || manifest.Videos.Count != plan.Count ||
            manifest.Videos.Any(v => v is null) || manifest.Clips.Any(c => c is null) ||
            manifest.Videos.Select(v => v.Number).Distinct().Count() != plan.Count ||
            manifest.Videos.Any(v => !plan.Any(c => c.Number == v.Number) || !ValidHash(v.Sha256) ||
                v.Bytes is <= 0 or > 8_000_000 || v.Width != 1280 || v.Height != 720 ||
                !double.IsFinite(v.DurationSeconds) || v.DurationSeconds is <= 0 or > 10))
            throw new FormatException("Capture identity, plan, or metadata does not match this demo. Recapture it before review.");
    }

    internal static async Task<int> ReplaceOlderCapturesAsync(string outputsRoot, string sourceSha256,
        long sourceBytes, IReadOnlyList<DemoVideoClip> plan, string currentFolder, CancellationToken token)
    {
        if (!ValidHash(sourceSha256)) throw new FormatException("Reimport the source demo before replacing old captures.");
        var root = Path.GetFullPath(outputsRoot);
        var current = Path.GetFullPath(currentFolder);
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Linked capture output folders cannot be replaced.");
        if (!string.Equals(Path.GetDirectoryName(current), root, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("The new capture folder is not a direct, verified child of the output folder.");
        var currentManifest = await ReadManifestAsync(current, sourceSha256, token);
        Validate(currentManifest, sourceSha256, plan);
        if (currentManifest.SourceBytes != sourceBytes)
            throw new FormatException("The new capture's source size does not match the imported demo.");
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var folder in Directory.GetDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(folder), current, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                continue;
            var manifestPath = Path.Combine(folder, "review-manifest.json");
            if (!File.Exists(manifestPath) || (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                continue;
            if (new FileInfo(manifestPath).Length > MaximumManifestBytes) continue;
            DemoVideoManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<DemoVideoManifest>(
                    await File.ReadAllTextAsync(manifestPath, token), JsonOptions) ??
                    throw new JsonException("The capture manifest is empty.");
            }
            catch (JsonException) { continue; }
            catch (FormatException) { continue; }
            if (manifest.Version is not (2 or 3) || manifest.SourceSha256 != sourceSha256 ||
                manifest.SourceBytes != sourceBytes || manifest.Clips is null || manifest.Videos is null ||
                manifest.Clips.Count == 0 || manifest.Clips.Count != manifest.Videos.Count ||
                manifest.Clips.Any(clip => clip is null) || manifest.Videos.Any(video => video is null ||
                    !ValidHash(video.Sha256) || video.Bytes <= 0) ||
                manifest.Videos.Select(video => video.Number).Distinct().Count() != manifest.Videos.Count)
                continue;
            if (manifest.Version == 3)
            {
                try { Validate(manifest, sourceSha256, plan); }
                catch (FormatException) { continue; }
            }
            if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                continue;
            Directory.Delete(folder, recursive: true);
            removed++;
        }
        return removed;
    }

    public static async Task<GeminiPreparedVideoReview> PrepareAsync(string toolsRoot, string outputsRoot,
        DemoImportResult demo, string sourceHash, string model, IProgress<string>? progress, CancellationToken token,
        bool firstClipOnly = false, bool includeClipEventContext = false)
    {
        var batches = await PrepareBatchesAsync(toolsRoot, outputsRoot, demo, sourceHash, model, progress,
            token, firstClipOnly, includeClipEventContext);
        if (batches.Count != 1)
            throw new FormatException($"This review requires {batches.Count} requests. Use the batched review workflow.");
        return batches[0];
    }

    public static async Task<IReadOnlyList<GeminiPreparedVideoReview>> PrepareBatchesAsync(string toolsRoot,
        string outputsRoot, DemoImportResult demo, string sourceHash, string model, IProgress<string>? progress,
        CancellationToken token, bool firstClipOnly = false, bool includeClipEventContext = false,
        string? captureFolder = null)
    {
        if (includeClipEventContext && !firstClipOnly)
            throw new FormatException("Isolated event context requires the first-clip diagnostic.");
        var gameplay = demo.Gameplay ?? throw new FormatException("Import gameplay telemetry first.");
        var telemetry = GeminiReviewRequest.Create(gameplay, demo.IsIncomplete, model);
        string folder;
        DemoVideoManifest manifest;
        if (captureFolder is null)
            (folder, manifest) = await FindAsync(outputsRoot, sourceHash, token);
        else
        {
            folder = Path.GetFullPath(captureFolder);
            if (!string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(outputsRoot), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("The selected capture is not a direct child of the configured output folder.");
            manifest = await ReadManifestAsync(folder, sourceHash, token);
        }
        Validate(manifest, sourceHash, DemoVideoCapture.Plan(demo));
        using var tools = new PortableCaptureTools(toolsRoot);
        var encoder = Path.Combine(tools.Installed().EncoderDirectory, "bin", "ffmpeg.exe");
        var probe = Path.Combine(Path.GetDirectoryName(encoder)!, "ffprobe.exe");
        var previewFolder = Path.Combine(folder, "gemini-preview", Guid.NewGuid().ToString("N"));
        var previewRoot = Path.GetDirectoryName(previewFolder)!;
        if (Directory.Exists(previewRoot) && (File.GetAttributes(previewRoot) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Linked preview folders are not eligible for video review.");
        Directory.CreateDirectory(previewFolder);
        var prepared = new List<GeminiPreparedVideoReview>();
        var clips = new List<GeminiVideoContext>();
        var files = new List<GeminiVideoPreviewFile>();
        long batchBytes = 0;
        void CompleteBatch()
        {
            if (clips.Count == 0) return;
            prepared.Add(BuildDeferred(telemetry, clips.ToArray(), files.ToArray(), sourceHash,
                videoOnly: firstClipOnly && !includeClipEventContext,
                clipEventContextOnly: includeClipEventContext));
            clips = [];
            files = [];
            batchBytes = 0;
        }
        var selectedClips = manifest.Clips.Take(firstClipOnly ? 1 : manifest.Clips.Count).ToArray();
        foreach (var clip in selectedClips)
        {
            token.ThrowIfCancellationRequested();
            var video = manifest.Videos.Single(v => v.Number == clip.Number);
            var original = ResolveClip(folder, video.Path);
            await using var guard = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (guard.Length != video.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(guard, token)) != video.Sha256)
                throw new IOException("A captured MP4 changed after verification. Recapture before preparing video review.");
            progress?.Report($"Preparing local review copy {clip.Number}/{(firstClipOnly ? 1 : manifest.Clips.Count)}. No data is being uploaded.");
            var output = Path.Combine(previewFolder, $"clip-{clip.Number}.mp4");
            await ToolProcess.RunAsync(encoder,
                ["-v", "error", "-nostdin", "-i", original, "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=960:540",
                 "-r", "30", "-c:v", "libopenh264", "-b:v", "900k", "-pix_fmt", "yuv420p",
                 "-c:a", "aac", "-b:a", "96k", "-map_metadata", "-1", "-movflags", "+faststart", output], null, token);
            var length = new FileInfo(output).Length;
            if (length is <= 0 or > GeminiReviewRequest.MaximumVideoClipBytes)
                throw new FormatException($"Review copy {clip.Number} exceeds the 2 MB per-clip inline-video budget. No request was prepared.");
            if (ShouldStartNewVideoBatch(clips.Count, batchBytes, length))
                CompleteBatch();
            var verified = await DemoVideoCapture.VerifyClipAsync(probe, output, clip, 960, 540, token);
            if (verified.Bytes != length)
                throw new IOException("A review copy changed while preparing the request. Prepare again; nothing was sent.");
            files.Add(new(clip.Number, output, verified.Sha256, verified.Bytes));
            batchBytes += verified.Bytes;
            var alias = telemetry.PlayerContext.Single(p => p.SteamId == clip.SteamId).Alias;
            clips.Add(new(clip.Number, alias, clip.Round, clip.StartTick, clip.EndTick, verified.DurationSeconds, verified.HasAudio)
            {
                EventTimeline = firstClipOnly && !includeClipEventContext ? null : DemoVideoContextAudit.Timeline(gameplay, clip)
            });
        }
        CompleteBatch();
        return prepared.AsReadOnly();
    }

    internal static bool ShouldStartNewVideoBatch(int currentClipCount, long currentBytes, long nextClipBytes)
    {
        if (currentClipCount is < 0 or > GeminiReviewRequest.MaximumVideoClipsPerRequest ||
            currentBytes < 0 || currentBytes > GeminiReviewRequest.MaximumVideoBytesPerRequest ||
            nextClipBytes is <= 0 or > GeminiReviewRequest.MaximumVideoClipBytes)
            throw new FormatException("Invalid video batch size.");
        return currentClipCount == GeminiReviewRequest.MaximumVideoClipsPerRequest ||
            currentBytes > GeminiReviewRequest.MaximumVideoBytesPerRequest - nextClipBytes;
    }

    internal static GeminiPreparedVideoReview Build(GeminiReviewRequest telemetry, IReadOnlyList<GeminiVideoContext> clips,
        IReadOnlyList<byte[]> data, IReadOnlyList<GeminiVideoPreviewFile> files, string sourceHash, bool videoOnly = false,
        bool clipEventContextOnly = false)
    {
        var request = GeminiReviewRequest.WithVideo(telemetry, clips, data, videoOnly, clipEventContextOnly);
        var jsonBytes = Encoding.UTF8.GetByteCount(request.JsonBody);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.JsonBody)));
        return new(request, RequestPreview(request, files, sourceHash, jsonBytes, hash, videoOnly, clipEventContextOnly),
            files.ToArray(), sourceHash);
    }

    internal static GeminiPreparedVideoReview BuildDeferred(GeminiReviewRequest telemetry,
        IReadOnlyList<GeminiVideoContext> clips, IReadOnlyList<GeminiVideoPreviewFile> files,
        string sourceHash, bool videoOnly, bool clipEventContextOnly)
    {
        var placeholders = files.Select(_ => new byte[] { 0 }).ToArray();
        var request = GeminiReviewRequest.WithVideo(telemetry, clips, placeholders, videoOnly, clipEventContextOnly);
        long jsonBytes = Encoding.UTF8.GetByteCount(request.JsonBody);
        foreach (var file in files)
            jsonBytes += checked(((file.Bytes + 2) / 3) * 4 - 4);
        if (jsonBytes > GeminiReviewRequest.MaximumVideoRequestJsonBytes)
            throw new FormatException("Video review exceeds the 18 MB inline request limit. No request was prepared.");
        var size = checked((int)jsonBytes);
        var preview = RequestPreview(request, files, sourceHash, size, null, videoOnly, clipEventContextOnly);
        return new(telemetry, clips.ToArray(), files.ToArray(), sourceHash, size, preview,
            videoOnly, clipEventContextOnly);
    }

    internal static string RequestPreview(GeminiReviewRequest request, IReadOnlyList<GeminiVideoPreviewFile> files,
        string sourceHash, int jsonBytes, string? requestHash, bool videoOnly, bool clipEventContextOnly)
    {
        var preview = JsonNode.Parse(request.JsonBody)!;
        foreach (var part in preview["contents"]![0]!["parts"]!.AsArray())
            if (part?["inlineData"] is { } inline)
                inline["data"] = "[MP4 binary omitted from this text display; see exact file hashes above]";
        var text = new StringBuilder("VIDEO REVIEW REQUEST PREVIEW\n");
        text.AppendLine("Destination: " + request.Endpoint);
        if (videoOnly)
            text.AppendLine("ONE-CLIP VIDEO-ONLY DIAGNOSTIC: first captured clip only. Only its descriptor and alias roster accompany the media; no demo telemetry/event timeline or other player summaries are sent. Not a full-match/all-player review.");
        if (clipEventContextOnly)
            text.AppendLine("ONE-CLIP EVENT-CONTEXT DIAGNOSTIC: first captured clip, its descriptor, POV alias and retained clip-local own-player shot/kill/death timeline only. No aggregate telemetry or other player summaries. Nominal event offsets are not verified video timestamps; retained events are not exhaustive counts. Not a full-match/all-player review.");
        text.AppendLine("Footage is NOT anonymized. Names, avatars, map/HUD text and game audio may be visible/audible.");
        text.AppendLine("No raw demo, original filenames, paths or explicit roster IDs are added to the JSON.");
        text.AppendLine($"Audio tracks verified in {request.VideoClips.Count(clip => clip.HasAudio)}/{request.VideoClips.Count} copies. A track does not establish intelligible audio or recorded teammate comms.");
        text.AppendLine("Copies: 960x540, 30 fps, lossy H.264/AAC. Requested model sampling: 10 fps; fine/fast details may be missed.");
        text.AppendLine("Clip event timelines, when supplied, contain retained own-player firearm shots and human-vs-human kills/deaths at nominal 64-tick offsets, not verified video timestamps. Missing events do not prove survival or no firing.");
        text.AppendLine("This batch is one inline generateContent request; no separate Files API upload/session, retries or model fallback. Google processing/retention, free-tier data-use terms, quotas and possible charges still apply.");
        text.AppendLine(requestHash is null
            ? $"Estimated JSON request: {jsonBytes} UTF-8 bytes; exact SHA256 will be computed before sending."
            : $"Exact JSON request: {jsonBytes} UTF-8 bytes; SHA256 {requestHash}");
        text.AppendLine("Local source-demo SHA256 (not sent): " + sourceHash);
        foreach (var file in files)
            text.AppendLine($"Clip {file.Number}: {file.Bytes} bytes; SHA256 {file.Sha256}");
        text.AppendLine("\nJSON structure below has ONLY the base64 MP4 data replaced with a placeholder:\n");
        text.Append(preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return text.ToString();
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
