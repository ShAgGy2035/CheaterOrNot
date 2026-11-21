using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

public sealed record DemoVideoClip(int Number, string SteamId, string Alias, int Round, int StartTick, int EndTick)
{
    public int? KillTick { get; init; }
    public string? VictimSteamId { get; init; }
    public string? Weapon { get; init; }
    public bool? Headshot { get; init; }
}
public sealed record DemoVideoFile(int Number, string Path, double DurationSeconds, int Width, int Height, bool HasAudio,
    string Sha256 = "", long Bytes = 0);

public sealed record DemoVideoManifest(int Version, string SourceSha256, long SourceBytes,
    IReadOnlyList<DemoVideoClip> Clips, IReadOnlyList<DemoVideoFile> Videos);

public sealed class DemoVideoCapture
{
    public const int MaximumKillClips = 2000;

    public static IReadOnlyList<DemoVideoClip> Plan(DemoImportResult demo)
    {
        if (demo.Gameplay is not { } gameplay || demo.LastTick <= 0)
            throw new FormatException("Import a demo with kill and gameplay measurements before capturing video.");
        if (gameplay.TotalCombatEvents != gameplay.CombatEvents.Count)
            throw new FormatException("Not every eligible human-vs-human kill was retained. Complete per-kill video review is unavailable for this demo.");
        if (gameplay.CombatEvents.Count == 0)
            throw new FormatException("No eligible human-vs-human kills were decoded; no per-kill video clips can be created.");
        if (gameplay.CombatEvents.Count > MaximumKillClips)
            throw new FormatException($"The demo has more than {MaximumKillClips} eligible kills; complete per-kill video review is not supported.");
        var aliases = gameplay.Players.ToDictionary(p => p.SteamId, p => p.Alias, StringComparer.Ordinal);
        var deathsByPlayerRound = gameplay.CombatEvents
            .GroupBy(e => (e.VictimSteamId, e.Round))
            .ToDictionary(group => group.Key, group => group.OrderBy(e => e.Tick).ToArray());
        var orderedKills = gameplay.CombatEvents.OrderBy(e => e.Tick)
            .ThenBy(e => e.AttackerSteamId, StringComparer.Ordinal)
            .ThenBy(e => e.VictimSteamId, StringComparer.Ordinal).ToArray();
        var clips = orderedKills.Select((kill, index) =>
        {
            if (!aliases.TryGetValue(kill.AttackerSteamId, out var alias) ||
                !aliases.ContainsKey(kill.VictimSteamId) || kill.Round is < 1 or > 1000 ||
                kill.Tick < 0 || kill.Tick > demo.LastTick)
                throw new FormatException("An eligible kill does not match the imported roster or demo timeline.");
            var startTick = Math.Max(0, kill.Tick - 128);
            var endTick = (int)Math.Min(demo.LastTick, (long)kill.Tick + 128);
            if (deathsByPlayerRound.TryGetValue((kill.AttackerSteamId, kill.Round), out var deaths))
            {
                var nextDeath = deaths.FirstOrDefault(death => death.Tick >= kill.Tick);
                if (nextDeath is not null)
                    endTick = Math.Min(endTick, nextDeath.Tick == kill.Tick ? kill.Tick : nextDeath.Tick - 1);
            }
            if (startTick >= endTick)
                throw new FormatException($"Kill event {index + 1} at tick {kill.Tick} has no usable pre-death video range.");
            return new DemoVideoClip(index + 1, kill.AttackerSteamId, alias, kill.Round, startTick, endTick)
            {
                KillTick = kill.Tick,
                VictimSteamId = kill.VictimSteamId,
                Weapon = kill.Weapon,
                Headshot = kill.Headshot
            };
        }).ToArray();
        if (clips.Any(c => c.EndTick - c.StartTick > 512))
            throw new FormatException("A per-kill clip exceeds the supported four-second range.");
        return clips;
    }

    public static string Configuration(string demoPath, string outputPath, string encoderPath, IReadOnlyList<DemoVideoClip> clips) =>
        JsonSerializer.Serialize(new
        {
            demoPath, outputFolderPath = outputPath, outputFileName = "review",
            recordingSystem = "CS", recordingOutput = "video", encoderSoftware = "FFmpeg",
            width = 1280, height = 720, framerate = 30, trueView = true,
            closeGameAfterRecording = true, keepRawFilesOnGameError = true, concatenateSequences = false,
            ffmpegSettings = new
            {
                customLocationEnabled = true, customExecutableLocation = encoderPath,
                videoCodec = "libopenh264", videoContainer = "mp4", audioCodec = "aac",
                audioBitrate = 128, constantRateFactor = 23, inputParameters = "", outputParameters = "-pix_fmt yuv420p -b:v 4000k"
            },
            sequences = clips.Select(clip => new
            {
                number = clip.Number, startTick = clip.StartTick, endTick = clip.EndTick,
                showXRay = false, showAssists = false, showOnlyDeathNotices = false,
                playersOptions = Array.Empty<object>(), cameras = Array.Empty<object>(),
                playerVoicesEnabled = false, recordAudio = true, deathNoticesDuration = 0,
                playerCameras = new[] { new { tick = clip.StartTick, playerSteamId = clip.SteamId, playerName = clip.Alias } }
            })
        }, new JsonSerializerOptions { WriteIndented = true });

    public static async Task<IReadOnlyList<DemoVideoFile>> CaptureAsync(string toolsRoot, string demoPath,
        string outputPath, IReadOnlyList<DemoVideoClip> clips, IProgress<string>? progress, CancellationToken token)
    {
        if (demoPath.IndexOfAny(['"', ';', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(demoPath) ||
            !File.Exists(demoPath) || Path.GetExtension(demoPath).ToLowerInvariant() != ".dem")
            throw new FormatException("Select an existing local demo with a path containing no quotes, semicolons or line breaks.");
        if (clips.Count is < 1 or > MaximumKillClips || clips.Select(c => c.Number).Distinct().Count() != clips.Count ||
            clips.Any(c => c.StartTick < 0 || c.EndTick <= c.StartTick || (long)c.EndTick - c.StartTick > 512 ||
                !ulong.TryParse(c.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !DemoRosterBuilder.IsHumanId(id)))
            throw new FormatException("The video plan contains unsupported ranges or identities.");
        using var tools = new PortableCaptureTools(toolsRoot);
        var installed = tools.Installed();
        EnsureGameClosed();
        using var sourceGuard = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckDiskSpace(outputPath, clips, sourceGuard.Length);
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceGuard, token));
        sourceGuard.Position = 0;
        var encoder = Path.Combine(installed.EncoderDirectory, "bin", "ffmpeg.exe");
        Directory.CreateDirectory(outputPath);
        if (Directory.EnumerateFileSystemEntries(outputPath).Any())
            throw new IOException("Capture needs a new, empty output directory; existing recordings will not be overwritten.");
        var sourceDirectory = Path.Combine(outputPath, "source");
        Directory.CreateDirectory(sourceDirectory);
        var workingDemo = Path.Combine(sourceDirectory, "capture.dem");
        await using (var copy = new FileStream(workingDemo, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await sourceGuard.CopyToAsync(copy, token);
        var settings = Path.Combine(outputPath, "capture.json");
        await File.WriteAllTextAsync(settings, Configuration(workingDemo, outputPath, encoder, clips), token);
        var home = Path.Combine(toolsRoot, "data");
        Directory.CreateDirectory(home);
        progress?.Report("Indexing the demo locally. Stop after current step waits for indexing to finish; CS2 will not start if stopping was requested.");
        await RunCaptureCliAsync(installed, home, ["analyze", workingDemo, "--quiet"],
            TimeSpan.FromMinutes(20), "Demo indexing", progress, token);
        EnsureGameClosed();
        var captureTimeout = CaptureTimeout(clips.Count);
        var captureStep = $"Recording and encoding {clips.Count} kill clips";
        progress?.Report($"Starting CS2 for {clips.Count} kill clips; time limit {captureTimeout.TotalMinutes:0.#} minutes. " +
            "MP4 encoding starts only after all raw footage is recorded. Stop after current step waits for recording/encoding and game cleanup to finish. X-ray and player voices are requested off.");
        await RunCaptureCliAsync(installed, home,
            ["video", "--config-file", settings, "--ffmpeg-executable-path", encoder],
            captureTimeout, captureStep, progress, token);
        token.ThrowIfCancellationRequested();
        progress?.Report("Checking every generated clip...");
        var paths = Directory.GetFiles(outputPath, "*.mp4", SearchOption.AllDirectories);
        if (paths.Length != clips.Count) throw new IOException("Capture did not produce exactly one video per planned sequence. No complete result was accepted.");
        var videos = new List<DemoVideoFile>();
        foreach (var clip in clips)
        {
            var matches = paths.Where(path => Path.GetFileName(path) ==
                $"sequence-{clip.Number}-tick-{clip.StartTick}-to-{clip.EndTick}.mp4").ToArray();
            if (matches.Length != 1)
                throw new IOException("A generated video could not be matched to its planned player and tick range.");
            var path = matches[0];
            videos.Add(await VerifyClipAsync(Path.Combine(installed.EncoderDirectory, "bin", "ffprobe.exe"),
                path, clip, 1280, 720, token));
        }
        var pendingManifest = Path.Combine(outputPath, "review-manifest.pending.json");
        await File.WriteAllTextAsync(pendingManifest,
            JsonSerializer.Serialize(new DemoVideoManifest(3, sourceHash, sourceGuard.Length, clips,
                videos.Select(video => video with { Path = Path.GetRelativePath(outputPath, video.Path) }).ToArray()),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
        token.ThrowIfCancellationRequested();
        File.Move(pendingManifest, Path.Combine(outputPath, "review-manifest.json"));
        File.Delete(workingDemo);
        if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any()) Directory.Delete(sourceDirectory);
        return videos.AsReadOnly();
    }

    internal static async Task<DemoVideoFile> VerifyClipAsync(string probe, string path, DemoVideoClip clip,
        int width, int height, CancellationToken token)
    {
        await using var videoGuard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = await ToolProcess.RunAsync(probe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration", "-of", "json", path], null, token);
        try
        {
            using var document = JsonDocument.Parse(metadata);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var videoStreams = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "video").ToArray();
            var duration = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
            var expectedSeconds = (clip.EndTick - clip.StartTick) / 64.0;
            if (videoStreams.Length != 1 || videoStreams[0].GetProperty("width").GetInt32() != width ||
                videoStreams[0].GetProperty("height").GetInt32() != height ||
                videoStreams[0].GetProperty("avg_frame_rate").GetString() != "30/1" ||
                !double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds is <= 0 or > 10 ||
                Math.Abs(seconds - expectedSeconds) > Math.Max(0.35, expectedSeconds * 0.1))
                throw new FormatException("A video has unexpected dimensions, frame rate or duration for its planned tick range; inspect it locally.");
            var videoHash = Convert.ToHexString(await SHA256.HashDataAsync(videoGuard, token));
            return new(clip.Number, path, seconds, width, height,
                streams.Any(stream => stream.GetProperty("codec_type").GetString() == "audio"), videoHash, videoGuard.Length);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("A captured video could not be verified; inspect the local output folder.");
        }
    }

    public static long RequiredFreeBytes(IReadOnlyList<DemoVideoClip> clips) =>
        checked((long)Math.Ceiling(clips.Sum(clip => (clip.EndTick - (long)clip.StartTick) / 64.0) * 30 * 1280 * 720 * 4) + 1_000_000_000);

    internal static TimeSpan CaptureTimeout(int clipCount)
    {
        if (clipCount is < 1 or > MaximumKillClips)
            throw new ArgumentOutOfRangeException(nameof(clipCount));
        // Retain the original ten-clip allowance and budget extra recording/seek/encoding time per kill.
        return TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(Math.Max(0, clipCount - 10) * 30);
    }

    public static bool IsGameRunning()
    {
        var running = Process.GetProcessesByName("cs2");
        try { return running.Length > 0; }
        finally { foreach (var process in running) process.Dispose(); }
    }

    private static void EnsureGameClosed()
    {
        if (IsGameRunning())
            throw new IOException("CS2 is already running. Close it yourself before capture; no existing game will be intentionally interrupted.");
    }

    private static void CheckDiskSpace(string outputPath, IReadOnlyList<DemoVideoClip> clips, long sourceBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Portable CS2 capture currently requires Windows.");
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steam = registry?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steam)) throw new IOException("Steam's installation could not be located. Install Steam and CS2 before capture.");
        var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraries)) throw new IOException("Steam's library list is missing. Open Steam and verify your CS2 installation.");
        var roots = Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"\\r\\n]+)\"")
            .Select(match => match.Groups[1].Value.Replace(@"\\", @"\")).Prepend(steam);
        var game = roots.Select(root => Path.Combine(root, "steamapps", "common", "Counter-Strike Global Offensive"))
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "game", "bin", "win64", "cs2.exe")));
        if (game is null) throw new IOException("CS2 was not found in Steam's libraries. Verify its installation before capture.");
        var required = checked(RequiredFreeBytes(clips) + sourceBytes);
        foreach (var root in new[] { Path.GetPathRoot(game)!, Path.GetPathRoot(Path.GetFullPath(outputPath))! }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (new DriveInfo(root).AvailableFreeSpace < required)
                throw new IOException($"Capture needs about {required / 1_000_000_000.0:0.0} GB free on {root}, including raw frames and headroom.");
    }

    private static async Task RunCaptureCliAsync(CaptureToolInstallation tools, string home,
        IEnumerable<string> arguments, TimeSpan timeout, string step, IProgress<string>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(tools.CaptureDirectory, "cs-demo-manager.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[] { Path.Combine(tools.CaptureDirectory, "resources", "app.asar", "cli.js") }.Concat(arguments))
            start.ArgumentList.Add(value);
        start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        start.Environment["USERPROFILE"] = home;
        start.Environment["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
        start.Environment["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
        var temporary = Path.Combine(home, "Temp");
        Directory.CreateDirectory(temporary);
        start.Environment["TEMP"] = start.Environment["TMP"] = temporary;
        using var process = Process.Start(start) ?? throw new IOException("Could not start the portable capture engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        // The CLI submits work to a detached service; terminating it does not stop that work.
        // Honor user cancellation only after the current service step has completed safely.
        using var deadline = new CancellationTokenSource(timeout);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var exit = process.WaitForExitAsync(deadline.Token);
            while (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(15), deadline.Token)) != exit)
            {
                deadline.Token.ThrowIfCancellationRequested();
                progress?.Report($"{step}: elapsed {elapsed.Elapsed:hh\\:mm\\:ss}; time limit {timeout.TotalMinutes:0.#} minutes. " +
                    "MP4s may appear only after recording finishes. Stop after current step waits for safe cleanup.");
            }
            await exit;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            throw new IOException($"{step} exceeded its {timeout.TotalMinutes:0.#}-minute limit. Its background service may still be running; close CS2 before retrying. No complete result was accepted; partial files were kept.");
        }
        finally { deadline.Cancel(); }
        var text = await output;
        var error = await errors;
        if (process.ExitCode != 0)
            throw new IOException($"Capture engine failed (exit {process.ExitCode}). " +
                error[..Math.Min(error.Length, 1000)] + text[..Math.Min(text.Length, 1000)]);
        token.ThrowIfCancellationRequested();
    }
}
