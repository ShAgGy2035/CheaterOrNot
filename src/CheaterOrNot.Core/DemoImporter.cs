using System.Globalization;
using System.Text;
using DemoFile;
using DemoFile.Game.Cs;

namespace CheaterOrNot.Core;

public sealed record DemoPlayer(string SteamId, string Name);

public sealed record DemoBookmark(int Tick, TimeSpan Elapsed, string Description);

public sealed record DemoImportResult(string FileName, string Map, IReadOnlyList<DemoPlayer> Players,
    IReadOnlyList<DemoBookmark> Bookmarks, int DeathEvents, bool IsIncomplete, int LastTick)
{
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public DemoGameplayReport? Gameplay { get; init; }

    public string PlayerInput => string.Join(" ", Players.Select(player => player.SteamId));

    public string Render()
    {
        var report = new StringBuilder();
        report.AppendLine("LOCAL CS2 DEMO | " + FileName);
        report.AppendLine($"Imported {ImportedAt:u} | Map: {Map} | Last observed demo tick: {LastTick}");
        report.AppendLine("Local recording context only, not a cheating or smurf assessment. No AI analysis was performed to produce this local report; optional Gemini commentary remains separate.");
        report.AppendLine("Local import makes no network requests and does not change statistical verdicts. Check player sends selected SteamIDs to websites/APIs. Analyze demo with Gemini continues from import through verified video preparation and automatically sends identifiable footage plus bounded telemetry to Google. The separate summary-only action sends identity-stripped telemetry without video.");
        if (IsIncomplete)
            report.AppendLine("PARTIAL RECORDING: the parser could not establish a complete demo footer. The roster and events cover only the readable recording, not necessarily the full match.");
        report.AppendLine("\nRECORDED ROSTER");
        report.AppendLine("Human accounts observed on T or CT, deduplicated by SteamID; includes players who later disconnected. Bots and spectator-only accounts are excluded. Names are the last usable recorded names, not verified current Steam names. A demo can be edited; these identities are not authenticated by Steam.");
        foreach (var player in Players) report.AppendLine($"{player.SteamId} | {player.Name}");
        if (Gameplay is not null) report.Append(Gameplay.Render());
        report.AppendLine("\nREVIEW BOOKMARKS (neutral death-event timeline, not suspicious-play detections)");
        report.AppendLine("Includes warmup and other recorded phases; these events are not used to calculate competitive-match statistics. Unresolved players/world events may appear. Timestamps use the parser's 64-tick demo timebase, not round time or wall-clock time; the demo tick is the seeking reference.");
        report.AppendLine($"Recorded death events: {DeathEvents.ToString(CultureInfo.InvariantCulture)}. Displayed bookmarks: {Bookmarks.Count.ToString(CultureInfo.InvariantCulture)}.");
        if (Bookmarks.Count < DeathEvents)
            report.AppendLine("Bookmark display limit reached; only the earliest bookmarks are retained. The roster still covers the parsed recording.");
        if (DeathEvents == 0) report.AppendLine("No death events were decoded; this is not evidence that no kills occurred.");
        foreach (var bookmark in Bookmarks)
            report.AppendLine($"{bookmark.Elapsed:hh\\:mm\\:ss\\.fff} | demo tick {bookmark.Tick.ToString(CultureInfo.InvariantCulture)} | {bookmark.Description}");
        report.AppendLine("\nReview selected gameplay ticks and neutral death-event ticks in CS2 demo playback (for example with demo_gototick). Experimental gameplay selections help prioritize manual review; they do not establish visibility/hearing, detect aim assistance, or identify cheating. Video is not rendered.");
        return report.ToString();
    }
}

public static class DemoImporter
{
    public const long MaxDemoBytes = 1_073_741_824;

    public static Task<DemoImportResult> ImportAsync(Stream stream, string fileName, CancellationToken token,
        IProgress<int>? progress = null) =>
        Task.Run(() => ReadAsync(stream, fileName, token, progress), token);

    private static async Task<DemoImportResult> ReadAsync(Stream stream, string fileName, CancellationToken token,
        IProgress<int>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(fileName), ".dem", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Select an uncompressed CS2 .dem file, not .dem.info, a compressed archive, or a video.");
        if (!stream.CanRead || !stream.CanSeek)
            throw new FormatException("Demo import requires a readable, seekable local file.");
        if (stream.Length is < 16 or > MaxDemoBytes)
            throw new FormatException("The demo must be at least 16 bytes and no larger than 1 GiB.");
        stream.Position = 0;
        var magic = new byte[8];
        await stream.ReadExactlyAsync(magic, token);
        if (!magic.AsSpan().SequenceEqual("PBDEMS2\0"u8))
            throw new FormatException("This is not a Source 2 .dem recording. CS:GO/Source 1 demos, archives and videos are not supported.");
        stream.Position = 0;

        var demo = new CsDemoParser();
        var roster = new DemoRosterBuilder();
        var gameplay = new DemoGameplayAnalyzer();
        var bookmarks = new List<DemoBookmark>();
        var deathEvents = 0;
        var lastTick = 0;
        var lastProgress = -1;
        var reader = DemoFileReader.Create(demo, stream);
        void Observe(CCSPlayerController? player)
        {
            if (player is null) return;
            roster.Observe(player.SteamID, player.PlayerName, player.PlayerInfo?.Fakeplayer == true,
                player.CSTeamNum is CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist);
        }
        DemoAimSample? Sample(CCSPlayerController? player)
        {
            if (player is null || player.PlayerInfo?.Fakeplayer == true ||
                !DemoRosterBuilder.IsHumanId(player.SteamID) ||
                player.CSTeamNum is not (CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist) ||
                player.PlayerPawn is not { } pawn) return null;
            var rules = demo.GameRules;
            var round = rules is { TotalRoundsPlayed: >= 0 and < 1000 } ? rules.TotalRoundsPlayed + 1 : 0;
            var eligible = rules is { WarmupPeriod: false, FreezePeriod: false, RoundWinStatus: 0 } &&
                player.PawnIsAlive && pawn.Health > 0 && pawn.LifeState == 0 && !player.HasControlledBotThisRound;
            var offset = pawn.ViewOffset;
            var hasEye = float.IsFinite(offset.X) && float.IsFinite(offset.Y) && float.IsFinite(offset.Z) &&
                offset.Z is > 0 and <= 128 && Math.Abs(offset.X) <= 128 && Math.Abs(offset.Y) <= 128;
            return new DemoAimSample(player.SteamID, demo.CurrentDemoTick.Value, round,
                player.PlayerPawnHandle.Value, player.PawnLifetimeStart, eligible,
                pawn.EyeAngles.Pitch, pawn.EyeAngles.Yaw, pawn.Origin.X, pawn.Origin.Y, pawn.Origin.Z)
            {
                Team = (int)player.CSTeamNum,
                EyeX = hasEye ? (double)pawn.Origin.X + offset.X : null,
                EyeY = hasEye ? (double)pawn.Origin.Y + offset.Y : null,
                EyeZ = hasEye ? (double)pawn.Origin.Z + offset.Z : null
            };
        }
        bool CombatEligible(CCSPlayerController attacker, CCSPlayerController victim) =>
            demo.GameRules is { WarmupPeriod: false, FreezePeriod: false, RoundWinStatus: 0 } &&
            attacker.PlayerInfo?.Fakeplayer != true && victim.PlayerInfo?.Fakeplayer != true &&
            !attacker.HasControlledBotThisRound && !victim.HasControlledBotThisRound &&
            attacker.CSTeamNum is CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist &&
            victim.CSTeamNum is CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist &&
            attacker.CSTeamNum != victim.CSTeamNum;
        demo.OnCommandFinishPersistent += () =>
        {
            token.ThrowIfCancellationRequested();
            lastTick = Math.Max(lastTick, demo.CurrentDemoTick.Value);
            foreach (var player in demo.Players)
            {
                Observe(player);
                if (Sample(player) is { } sample) gameplay.Observe(sample);
            }
            var percent = (int)Math.Min(99, 100.0 * stream.Position / stream.Length);
            if (percent > lastProgress)
            {
                lastProgress = percent;
                progress?.Report(percent);
            }
        };
        demo.Source1GameEvents.WeaponFire += e =>
        {
            Observe(e.Player);
            if (Sample(e.Player) is { } sample)
            {
                var opponents = new List<DemoAimSample>();
                var unavailableOpponents = 0;
                foreach (var player in demo.Players)
                {
                    Observe(player);
                    if (Sample(player) is { } opponent)
                    {
                        if (opponent.Team != sample.Team) opponents.Add(opponent);
                    }
                    else if (player.PawnIsAlive && player.CSTeamNum is CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist &&
                        (int)player.CSTeamNum != sample.Team && player.PlayerInfo?.Fakeplayer != true &&
                        DemoRosterBuilder.IsHumanId(player.SteamID) && !player.HasControlledBotThisRound)
                        unavailableOpponents++;
                }
                gameplay.Shot(sample, e.Weapon, opponents, unavailableOpponents);
            }
        };
        demo.Source1GameEvents.PlayerHurt += e =>
        {
            Observe(e.Attacker);
            Observe(e.Player);
            if (e.Attacker is not { } attacker || e.Player is not { } victim) return;
            gameplay.Damage(attacker.SteamID, victim.SteamID,
                demo.GameRules is { TotalRoundsPlayed: >= 0 and < 1000 } rules ? rules.TotalRoundsPlayed + 1 : 0,
                demo.CurrentDemoTick.Value, e.Weapon, e.DmgHealth, CombatEligible(attacker, victim));
        };
        demo.Source1GameEvents.PlayerDeath += e =>
        {
            if (demo.CurrentDemoTick.Value < 0) return;
            Observe(e.Attacker);
            Observe(e.Player);
            if (e.Attacker is { } killer && e.Player is { } killedPlayer)
            {
                var rules = demo.GameRules;
                gameplay.Kill(killer.SteamID, killedPlayer.SteamID,
                    rules is { TotalRoundsPlayed: >= 0 and < 1000 } ? rules.TotalRoundsPlayed + 1 : 0,
                    demo.CurrentDemoTick.Value, e.Weapon, e.Headshot, CombatEligible(killer, killedPlayer));
            }
            deathEvents++;
            if (bookmarks.Count >= 20_000) return;
            var attacker = WebsiteDataParser.ValidDisplayName(e.Attacker?.PlayerName) ?? "unresolved/world";
            var victim = WebsiteDataParser.ValidDisplayName(e.Player?.PlayerName) ?? "unresolved player";
            var weapon = WebsiteDataParser.ValidDisplayName(e.Weapon) ?? "unknown weapon";
            bookmarks.Add(new DemoBookmark(demo.CurrentDemoTick.Value, demo.Elapsed,
                $"{attacker} -> {victim} | {weapon}" + (e.Headshot ? " | headshot" : "")));
        };
        try
        {
            await reader.ReadAllAsync(token);
        }
        catch (Exception ex) when (ex is InvalidDemoException or EndOfStreamException or InvalidOperationException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or NotSupportedException or OverflowException)
        {
            throw new FormatException($"Demo parsing failed ({ex.GetType().Name}). The recording may be damaged or incompatible with this parser; no partial import was accepted.", ex);
        }
        token.ThrowIfCancellationRequested();
        var players = roster.Build();
        if (players.Count == 0)
            throw new FormatException("No usable human SteamIDs on T or CT were found in this recording. No roster was imported.");
        var name = WebsiteDataParser.ValidDisplayName(Path.GetFileName(fileName)) ?? "local demo";
        var map = WebsiteDataParser.ValidDisplayName(demo.FileHeader?.MapName) ?? "unavailable";
        progress?.Report(100);
        return new DemoImportResult(name, map, players, bookmarks.ToArray(), deathEvents, reader.IsIncompleteFile, lastTick)
        {
            Gameplay = gameplay.Build(players)
        };
    }
}

internal sealed class DemoRosterBuilder
{
    private readonly Dictionary<string, string> _players = new(StringComparer.Ordinal);
    internal static bool IsHumanId(ulong steamId) => steamId > 76561197960265728UL && steamId <= 76561202255233023UL;

    public void Observe(ulong steamId, string? name, bool isBot, bool onPlayingTeam)
    {
        if (isBot || !onPlayingTeam || !IsHumanId(steamId))
            return;
        var id = steamId.ToString(CultureInfo.InvariantCulture);
        var safeName = WebsiteDataParser.ValidDisplayName(name);
        if (safeName is not null || !_players.ContainsKey(id)) _players[id] = safeName ?? "name unavailable";
        if (_players.Count > 128)
            throw new FormatException("The demo contains more than 128 human accounts. Import was stopped; no partial roster was accepted.");
    }

    public IReadOnlyList<DemoPlayer> Build() => _players.OrderBy(player => player.Key, StringComparer.Ordinal)
        .Select(player => new DemoPlayer(player.Key, player.Value)).ToArray();
}

