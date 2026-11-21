using System.Security.Cryptography;
using System.Text.Json;

namespace CheaterOrNot.Core;

public sealed record DemoVideoTimelineEvent(int Tick, double NominalSeconds, string Weapon)
{
    public string? OpponentAlias { get; init; }
    public bool? Headshot { get; init; }
}

public sealed record DemoVideoClipTimeline(int Number, string Alias, int Round, int StartTick, int EndTick,
    IReadOnlyList<DemoVideoTimelineEvent> RetainedShots, IReadOnlyList<DemoVideoTimelineEvent> RetainedDeaths)
{
    public IReadOnlyList<DemoVideoTimelineEvent> RetainedKills { get; init; } = [];
    public DemoVideoTimelineEvent? FocusKill { get; init; }
}

public sealed record DemoVideoAuditedClip(DemoVideoClipTimeline Timeline, string CapturedSha256,
    double CapturedDurationSeconds, string ReviewCopySha256, long ReviewCopyBytes);

public sealed record DemoVideoContextAuditResult(string Schema, string SourceSha256, string ManifestSha256,
    bool PartialRecording, int LastDemoTick, int TotalShotWindows, int RetainedShotWindows,
    int TotalCombatEvents, int RetainedCombatEvents, string Scope, string ReviewCopyProvenance,
    IReadOnlyList<DemoVideoAuditedClip> Clips);

public static class DemoVideoContextAudit
{
    public const string Scope = "Positive retained event context only. Shots are eligible live-phase firearm shot windows; kills/deaths are eligible opposing-human kill events with any recorded weapon, not all death/respawn events. Events are matched by the requested player's identity, recorded round and inclusive planned tick range. NominalSeconds = (event tick - planned start tick) / 64; these are not verified frame timestamps. Do not stretch offsets to match encoded duration. No event listed does not establish no shot, survival or a missing camera transition. A recorded death calls subsequent camera/player attribution into question; it does not identify the actual camera, transition frame or continuous tracking. Demo/event sampling, filtering, retention, partial recordings and capture offsets limit coverage. No visibility, hearing, intent, reaction-time, cheating or clean-play findings.";

    internal static DemoVideoClipTimeline Timeline(DemoGameplayReport gameplay, DemoVideoClip clip)
    {
        if (clip.Number < 1 || clip.Round < 1 || clip.StartTick < 0 ||
            clip.EndTick <= clip.StartTick || (long)clip.EndTick - clip.StartTick > 512 ||
            !gameplay.Players.Any(p => p.SteamId == clip.SteamId && p.Alias == clip.Alias))
            throw new FormatException("The timeline clip does not match an imported player or supported tick range.");
        string AliasFor(string steamId) => gameplay.Players.SingleOrDefault(p => p.SteamId == steamId)?.Alias ??
            throw new FormatException("A clip event does not match the imported player roster.");
        DemoVideoTimelineEvent Event(int tick, string weapon, string? opponentSteamId = null, bool? headshot = null) =>
            new(tick, (tick - (double)clip.StartTick) / 64, DemoGameplayAnalyzer.NormalizeKillWeapon(weapon))
            {
                OpponentAlias = opponentSteamId is null ? null : AliasFor(opponentSteamId),
                Headshot = headshot
            };
        var shots = gameplay.ShotWindows
            .Where(s => s.SteamId == clip.SteamId && s.Round == clip.Round &&
                s.ShotTick >= clip.StartTick && s.ShotTick <= clip.EndTick)
            .OrderBy(s => s.ShotTick).Select(s => Event(s.ShotTick, s.Weapon)).ToArray();
        var deaths = gameplay.CombatEvents
            .Where(e => e.VictimSteamId == clip.SteamId && e.Round == clip.Round &&
                e.Tick >= clip.StartTick && e.Tick <= clip.EndTick)
            .OrderBy(e => e.Tick).Select(e => Event(e.Tick, e.Weapon)).ToArray();
        var kills = gameplay.CombatEvents
            .Where(e => e.AttackerSteamId == clip.SteamId && e.Round == clip.Round &&
                e.Tick >= clip.StartTick && e.Tick <= clip.EndTick)
            .OrderBy(e => e.Tick).ThenBy(e => e.VictimSteamId, StringComparer.Ordinal)
            .Select(e => Event(e.Tick, e.Weapon, e.VictimSteamId, e.Headshot)).ToArray();
        DemoVideoTimelineEvent? focusKill = null;
        if (clip.KillTick is { } killTick && clip.VictimSteamId is { } victimId &&
            clip.Weapon is { } weapon && clip.Headshot is { } headshot)
        {
            var matches = gameplay.CombatEvents.Where(e => e.AttackerSteamId == clip.SteamId &&
                e.VictimSteamId == victimId && e.Round == clip.Round && e.Tick == killTick &&
                e.Weapon == weapon && e.Headshot == headshot).ToArray();
            if (matches.Length != 1)
                throw new FormatException("The planned focus kill does not match exactly one retained event.");
            focusKill = Event(killTick, weapon, victimId, headshot);
        }
        return new DemoVideoClipTimeline(clip.Number, clip.Alias, clip.Round, clip.StartTick,
            clip.EndTick, shots, deaths) { RetainedKills = kills, FocusKill = focusKill };
    }

    public static async Task<DemoVideoContextAuditResult> RunAsync(string demoPath, string captureFolder,
        string previewFolder, CancellationToken token)
    {
        captureFolder = Path.GetFullPath(captureFolder);
        previewFolder = Path.GetFullPath(previewFolder);
        if ((File.GetAttributes(captureFolder) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Linked capture folders are not supported.");
        var previewRoot = Path.Combine(captureFolder, "gemini-preview");
        if (!string.Equals(Path.GetDirectoryName(previewFolder), previewRoot, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Use an existing preview folder immediately inside this capture's gemini-preview folder.");

        await using var source = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is < 16 or > DemoImporter.MaxDemoBytes)
            throw new FormatException("The source demo exceeds the supported size bounds.");
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        var manifestPath = Path.Combine(captureFolder, "review-manifest.json");
        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Linked capture manifests are not supported.");
        await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifestStream.Length is <= 0 or > 100_000)
            throw new FormatException("The capture manifest exceeds its supported size bounds.");
        var manifestBytes = new byte[checked((int)manifestStream.Length)];
        await manifestStream.ReadExactlyAsync(manifestBytes, token);
        var manifest = JsonSerializer.Deserialize<DemoVideoManifest>(manifestBytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
                throw new FormatException("Invalid capture manifest.");
        if (manifest.SourceSha256 != sourceHash || manifest.SourceBytes != source.Length)
            throw new FormatException("The capture is not bound to this exact source demo.");

        var demo = await DemoImporter.ImportAsync(source, Path.GetFileName(demoPath), token);
        var gameplay = demo.Gameplay ?? throw new FormatException("The source supplied no gameplay telemetry.");
        DemoVideoReview.Validate(manifest, sourceHash, DemoVideoCapture.Plan(demo));
        var clips = new List<DemoVideoAuditedClip>();
        foreach (var clip in manifest.Clips)
        {
            token.ThrowIfCancellationRequested();
            var video = manifest.Videos.Single(v => v.Number == clip.Number);
            var original = DemoVideoReview.ResolveClip(captureFolder, video.Path);
            await using var originalStream = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (originalStream.Length != video.Bytes ||
                Convert.ToHexString(await SHA256.HashDataAsync(originalStream, token)) != video.Sha256)
                throw new IOException("A captured video no longer matches the source-bound manifest.");
            var copy = DemoVideoReview.ResolveClip(captureFolder,
                Path.GetRelativePath(captureFolder, Path.Combine(previewFolder, $"clip-{clip.Number}.mp4")));
            await using var copyStream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (copyStream.Length is <= 0 or > 2_000_000)
                throw new FormatException("A retained review copy exceeds its supported size bounds.");
            clips.Add(new(Timeline(gameplay, clip), video.Sha256, video.DurationSeconds,
                Convert.ToHexString(await SHA256.HashDataAsync(copyStream, token)), copyStream.Length));
        }
        return new("cs2-local-video-event-context-v1", sourceHash,
            Convert.ToHexString(SHA256.HashData(manifestBytes)), demo.IsIncomplete, demo.LastTick,
            gameplay.TotalShotWindows, gameplay.ShotWindows.Count, gameplay.TotalCombatEvents,
            gameplay.CombatEvents.Count, Scope,
            "Current retained-copy hashes only; folder association does not verify derivation from captured video or the bytes of a historical Google request.",
            clips.AsReadOnly());
    }
}
