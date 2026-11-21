using System.Globalization;
using System.Text;

namespace CheaterOrNot.Core;

public sealed record DemoAimFinding(string SteamId, int Round, int Tick, string Weapon,
    double TurnDegrees, double MovementUnits);

public sealed record DemoRoundTelemetry(string SteamId, int Round, int EligibleShots, int AssessedShots,
    double? MeanTurnDegrees, double? MaximumTurnDegrees, int? MaximumTurnTick,
    int FirearmKills, int HeadshotKills, int FirearmDeaths)
{
    public int TotalKills { get; init; }
    public int TotalHeadshotKills { get; init; }
    public int TotalDeaths { get; init; }
}

public sealed record DemoCombatEvent(string AttackerSteamId, string VictimSteamId, int Round, int Tick,
    string Weapon, bool Headshot);

public sealed record DemoGameplayPlayer(string SteamId, string Alias, int Shots, int AssessedShots,
    int LargeTurns, int RoundsWithLargeTurns)
{
    public bool Repeated => LargeTurns >= 3 && RoundsWithLargeTurns >= 2;
}

public sealed record DemoGameplayReport(IReadOnlyList<DemoGameplayPlayer> Players,
    IReadOnlyList<DemoAimFinding> Findings, int TotalFindings)
{
    public IReadOnlyList<DemoRoundTelemetry> RoundTelemetry { get; init; } = [];
    public IReadOnlyList<DemoCombatEvent> CombatEvents { get; init; } = [];
    public int TotalCombatEvents { get; init; }
    public IReadOnlyList<DemoDamageEvent> DamageEvents { get; init; } = [];
    public int TotalDamageEvents { get; init; }
    public IReadOnlyList<DemoShotWindow> ShotWindows { get; init; } = [];
    public int TotalShotWindows { get; init; }
    public IReadOnlyList<DemoEngagement> Engagements { get; init; } = [];
    public IReadOnlyList<DemoEngagementCoverage> EngagementCoverage { get; init; } = [];
    public int TotalEngagements { get; init; }
    public const string EngagementScope = "Local counts cover eligible observed player/round snapshots and firearm shots throughout the readable recording, not necessarily a complete match. Engagements group same-player/round/pawn/lifetime/team/weapon shots at gaps of at most 32 ticks (nominally 0.5 seconds), splitting after a 256-tick shot span (4 seconds). Invalid/discontinuous snapshots split groups. Each retained sequence requests 32 contiguous ticks before and after the group; actual coverage is reported. First/latest engagement examples per player/round are retained, at most two per pair within a global 2,000-example cap; later pairs can lack examples. Counts still cover all eligible observations. Grouping and retention are unvalidated context-selection rules, not cheating criteria. Geometry uses the firing player's view and eye-to-eye positions of alive opposing humans at the shot callback, choosing the smallest 3D angular separation among usable opponents. This is not an intended-target, hitbox, visibility, smoke/occlusion, hearing, acquisition, recoil or reaction-time measurement. Eye offsets must be finite with positive Z <=128 units; unavailable offsets are not replaced with assumed heights. Geometry shots can have incomplete opponent coverage; no-opponent and incomplete counts are separate and may overlap assessed shots. Trajectory points use recorded entity angles and origins, relative to the first shot; latest same-tick updates are used except for the first-shot anchor. Geometry uses event snapshots, so same-tick ordering can differ. Missing frames stop a sequence, not imply zero movement. No video or CS2 playback validation was performed by import.";
    public const string SelectionRule = "Review bookmarks select a >=20 degree change in recorded 3D viewing direction between adjacent demo ticks ending at a firearm shot. Repetition means >=3 selected shots across >=2 recorded round numbers. These are unvalidated bookmark-selection rules, not cheating thresholds.";
    public const string Limits = "Recorded entity angles are not raw mouse input or subtick commands. A demo tick is nominally 1/64 second; this is not measured reaction time. Flicks, recoil, network/demo sampling and ordinary target switching can explain selections. Visibility, hearing, enemy alignment, target acquisition, recoil compensation and assistance are not established. No selections is not proof of clean play.";
    public const string TelemetryScope = "Round rows summarize eligible live-phase firearm shots, including turns below the bookmark threshold. Assessed turns require the same adjacent-tick checks as bookmarks. Null turn measurements mean no assessed shots, not zero turn. Kill events include decoded opposing-human kills with any recorded weapon outside warmup/freeze/ended-round states, excluding controlled-bot rounds; totals are retained for all eligible events, while the detailed event list is capped at 2,000. Event ordering can exclude round-ending events. Round numbers come from the rules state at the event and are not independently verified. These rows do not measure visibility, hearing, actual target/hitbox alignment, reaction time or recoil control; the separate engagement geometry is an eye-position proxy only.";
    public const string ShotWindowScope = "Shot windows retain the earliest 2,000 eligible shots, irrespective of bookmarks. Each side requests eight contiguous valid demo ticks (nominally 125 ms); before/after tick counts disclose partial windows. Missing frames, phase/round/pawn/lifetime changes and movement discontinuities stop a side; zero covered ticks gives null turn measurements, not zero movement. Samples contain tick offsets, recorded pitch and wrapped yaw relative to the shot, not mouse input. Net turn is 3D endpoint separation; path turn is the sum of adjacent 3D separations, not smoothness or assistance. Yaw alone is not 3D turn, especially near vertical views. Nearby matching-weapon damage/death events within eight ticks after a shot are temporal associations from retained events, not attribution to that bullet. Multiple shot windows can reference the same event, including shotgun pellets; do not sum links as unique hits or kills. Same-tick ordering is unknown. No bookmark does not imply no flick: a multi-tick turn can stay below 20 degrees in each tick. No validated normal-play baseline or recoil/target model is supplied.";

    public string Render()
    {
        var text = new StringBuilder("\nLOCAL GAMEPLAY REVIEW (experimental; separate from website verdicts)\n");
        text.AppendLine(SelectionRule);
        text.AppendLine("Counts deduplicate firearm events by player, weapon and demo tick. Assessment requires finite positions/angles, alive T/CT snapshots in the same round and pawn lifetime, adjacent ticks, <=64 units of movement, and no warmup/freeze/ended-round state. Missing snapshots and excluded shots are not passes.");
        text.AppendLine(Limits);
        foreach (var player in Players.OrderByDescending(p => p.Repeated).ThenByDescending(p => p.LargeTurns).ThenBy(p => p.Alias))
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{player.Alias} | {player.SteamId} | decoded firearm shots {player.Shots}; assessed {player.AssessedShots}; selected {player.LargeTurns} in {player.RoundsWithLargeTurns} round(s)") +
                (player.Repeated ? " | repeated selection pattern: review these ticks first, not an accusation" : ""));
        text.AppendLine($"Selected events: {TotalFindings}; retained: {Findings.Count}. Only the earliest 2,000 selections are retained; all selections are counted.");
        var aliases = Players.ToDictionary(p => p.SteamId, p => p.Alias);
        foreach (var finding in Findings)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{aliases[finding.SteamId]} | round {finding.Round} | demo tick {finding.Tick} | {finding.Weapon} | recorded turn {finding.TurnDegrees:0.00} deg | movement {finding.MovementUnits:0.00} units"));
        text.AppendLine("\nROUND AND COMBAT TELEMETRY (context, not additional cheating flags)");
        text.AppendLine(TelemetryScope);
        foreach (var round in RoundTelemetry)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{aliases[round.SteamId]} | round {round.Round} | eligible shots {round.EligibleShots}; assessed {round.AssessedShots}; mean turn {round.MeanTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} deg; max turn {round.MaximumTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} deg at tick {round.MaximumTurnTick?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} | all kills {round.TotalKills}; headshot kills {round.TotalHeadshotKills}; deaths {round.TotalDeaths}; firearm kills {round.FirearmKills}; firearm headshot kills {round.HeadshotKills}; firearm deaths {round.FirearmDeaths}"));
        text.AppendLine($"Eligible human-vs-human kill events: {TotalCombatEvents}; retained: {CombatEvents.Count}. Only the earliest 2,000 event rows are retained; per-round totals cover all eligible events.");
        foreach (var combat in CombatEvents)
            text.AppendLine($"round {combat.Round} | demo tick {combat.Tick} | {aliases[combat.AttackerSteamId]} -> {aliases[combat.VictimSteamId]} | {combat.Weapon}" +
                (combat.Headshot ? " | headshot" : ""));
        text.AppendLine("\nMULTI-TICK SHOT WINDOWS AND DAMAGE CONTEXT");
        text.AppendLine(ShotWindowScope);
        text.AppendLine($"Eligible shot windows: {TotalShotWindows}; retained: {ShotWindows.Count}. Eligible damage events: {TotalDamageEvents}; retained: {DamageEvents.Count} (earliest 2,000).");
        foreach (var damage in DamageEvents)
            text.AppendLine($"round {damage.Round} | demo tick {damage.Tick} | {aliases[damage.AttackerSteamId]} -> {aliases[damage.VictimSteamId]} | {damage.Weapon} | reported health damage {damage.HealthDamage}");
        foreach (var window in ShotWindows)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{aliases[window.SteamId]} | round {window.Round} | shot tick {window.ShotTick} | {window.Weapon} | coverage -{window.BeforeTicks}/+{window.AfterTicks} ticks | pre net/path {window.BeforeNetTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"}/{window.BeforePathTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} deg | post net/path {window.AfterNetTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"}/{window.AfterPathTurnDegrees?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} deg"));
            text.AppendLine("  offset:pitch/yaw-relative " + string.Join("; ", window.Samples.Select(sample =>
                string.Create(CultureInfo.InvariantCulture, $"{sample.OffsetTicks}:{sample.PitchDegrees:0.000}/{sample.YawRelativeToShotDegrees:0.000}"))));
        }
        text.AppendLine("\nENGAGEMENT COVERAGE AND OPPONENT GEOMETRY (unverified context, not detections)");
        text.AppendLine(EngagementScope);
        text.AppendLine($"Eligible engagement groups: {TotalEngagements}; retained examples: {Engagements.Count}.");
        foreach (var row in EngagementCoverage)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{aliases[row.SteamId]} | round {row.Round} | observed eligible ticks {row.ObservedTicks}; eligible shots {row.EligibleShots}; engagement groups {row.Engagements}; retained examples {row.RetainedEngagements}; geometry-assessed shots {row.GeometryAssessedShots}; no opposing humans observed {row.NoOpponentShots}; incomplete geometry shots {row.IncompleteGeometryShots}; minimum eye-angle proxy {row.MinimumEyeAngleDegrees?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} deg at tick {row.MinimumEyeAngleTick?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"));
        foreach (var engagement in Engagements)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{aliases[engagement.SteamId]} | round {engagement.Round} | engagement shot ticks {engagement.FirstShotTick}-{engagement.LastShotTick} | {engagement.Weapon} | shots {engagement.Shots} | coverage -{engagement.BeforeTicks}/+{engagement.AfterTicks} ticks; contiguous points {engagement.Points.Count}; full-sequence net/path turn {engagement.NetTurnDegrees?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}/{engagement.PathTurnDegrees?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} deg; movement path {engagement.MovementPathUnits?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} units"));
            foreach (var shot in engagement.ShotContext)
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  tick {shot.Tick} | opponent snapshot available {shot.OpponentSnapshotAvailable}; usable/observed opponents {shot.AssessedOpponents}/{shot.ObservedOpponents}; closest angular opponent {(shot.ClosestAngularOpponent is { } opponent ? aliases[opponent] : "unknown")}; eye-angle proxy {shot.EyeAngleDegrees?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} deg; eye distance {shot.EyeDistanceUnits?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} units; movement since previous tick {shot.MovementUnitsSincePreviousTick?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} units"));
        }
        return text.ToString();
    }
}

internal sealed record DemoAimSample(ulong SteamId, int Tick, int Round, ulong Pawn, int Life,
    bool Eligible, double Pitch, double Yaw, double X, double Y, double Z)
{
    public int Team { get; init; }
    public double? EyeX { get; init; }
    public double? EyeY { get; init; }
    public double? EyeZ { get; init; }
}

internal sealed class DemoGameplayAnalyzer
{
    private readonly Dictionary<ulong, DemoAimSample> _latest = new();
    private readonly Dictionary<ulong, DemoAimSample> _previous = new();
    private readonly Dictionary<(ulong, string), int> _lastShot = new();
    private readonly Dictionary<ulong, Counts> _counts = new();
    private readonly List<DemoAimFinding> _findings = new();
    private readonly Dictionary<(ulong Player, int Round), RoundCounts> _rounds = new();
    private readonly HashSet<ulong> _roundPlayers = new();
    private readonly Dictionary<ulong, (int Tick, ulong Attacker)> _lastDeaths = new();
    private readonly List<DemoCombatEvent> _combatEvents = new();
    private readonly List<DemoDamageEvent> _damageEvents = new();
    private readonly DemoShotWindowCollector _windows = new();
    private readonly DemoEngagementCollector _engagements = new();
    private int _totalDamageEvents;
    private int _totalCombatEvents;
    private int _totalFindings;

    public void Observe(DemoAimSample sample)
    {
        if (!DemoRosterBuilder.IsHumanId(sample.SteamId) || sample.Tick < 0) return;
        if (_latest.TryGetValue(sample.SteamId, out var latest))
        {
            if (sample.Tick > latest.Tick) _previous[sample.SteamId] = latest;
            else if (sample.Tick < latest.Tick) _previous.Remove(sample.SteamId);
        }
        _latest[sample.SteamId] = sample;
        if (_latest.Count > 128) throw new FormatException("Gameplay review stopped: more than 128 human accounts.");
        _windows.Observe(sample);
        _engagements.Observe(sample);
    }

    public void Shot(DemoAimSample sample, string? weapon, IReadOnlyList<DemoAimSample>? opponents = null, int unavailableOpponents = 0)
    {
        var canonicalWeapon = NormalizeWeapon(weapon);
        if (!DemoRosterBuilder.IsHumanId(sample.SteamId) || sample.Tick < 0 || canonicalWeapon is null) return;
        var key = (sample.SteamId, canonicalWeapon);
        if (_lastShot.TryGetValue(key, out var shotTick) && shotTick == sample.Tick) return;
        _lastShot[key] = sample.Tick;
        if (!_counts.TryGetValue(sample.SteamId, out var counts)) _counts[sample.SteamId] = counts = new Counts();
        if (_counts.Count > 128) throw new FormatException("Gameplay review stopped: more than 128 human accounts.");
        counts.Shots++;
        _windows.Shot(sample, canonicalWeapon);
        _engagements.Shot(sample, canonicalWeapon, opponents, unavailableOpponents);
        var round = Valid(sample) ? Round(sample.SteamId, sample.Round) : null;
        if (round is not null) round.Shots++;
        if (!_latest.TryGetValue(sample.SteamId, out var before)) return;
        if (before.Tick == sample.Tick && !_previous.TryGetValue(sample.SteamId, out before)) return;
        if (!Consecutive(before, sample)) return;
        var movement = Movement(before, sample);
        counts.Assessed++;
        var cosine = Cosine(before, sample);
        var turn = TurnDegrees(before, sample);
        if (round is not null)
        {
            round.Assessed++;
            round.TurnTotal += turn;
            if (round.MaximumTurn is null || turn > round.MaximumTurn)
            {
                round.MaximumTurn = turn;
                round.MaximumTurnTick = sample.Tick;
            }
        }
        if (cosine > Math.Cos(20 * (Math.PI / 180))) return;
        counts.Selected++;
        counts.Rounds.Add(sample.Round);
        _totalFindings++;
        if (_findings.Count < 2_000)
            _findings.Add(new DemoAimFinding(sample.SteamId.ToString(CultureInfo.InvariantCulture),
                sample.Round, sample.Tick, canonicalWeapon, turn, movement));
    }

    public void Kill(ulong attacker, ulong victim, int round, int tick, string? weapon, bool headshot, bool eligible)
    {
        var canonicalWeapon = NormalizeKillWeapon(weapon);
        if (!eligible || round is < 1 or > 1000 || tick < 0 || attacker == victim ||
            !DemoRosterBuilder.IsHumanId(attacker) || !DemoRosterBuilder.IsHumanId(victim)) return;
        if (_lastDeaths.TryGetValue(victim, out var previous) && previous == (tick, attacker)) return;
        _lastDeaths[victim] = (tick, attacker);
        var killer = Round(attacker, round);
        killer.Kills++;
        if (headshot) killer.Headshots++;
        var killed = Round(victim, round);
        killed.Deaths++;
        if (NormalizeWeapon(canonicalWeapon) is not null)
        {
            killer.FirearmKills++;
            if (headshot) killer.FirearmHeadshots++;
            killed.FirearmDeaths++;
        }
        _totalCombatEvents++;
        if (_combatEvents.Count < 2_000)
            _combatEvents.Add(new DemoCombatEvent(attacker.ToString(CultureInfo.InvariantCulture),
                victim.ToString(CultureInfo.InvariantCulture), round, tick, canonicalWeapon, headshot));
    }

    public void Damage(ulong attacker, ulong victim, int round, int tick, string? weapon, int healthDamage, bool eligible)
    {
        var canonicalWeapon = NormalizeWeapon(weapon);
        if (!eligible || round is < 1 or > 1000 || tick < 0 || attacker == victim || healthDamage <= 0 ||
            !DemoRosterBuilder.IsHumanId(attacker) || !DemoRosterBuilder.IsHumanId(victim) || canonicalWeapon is null) return;
        Round(attacker, round);
        Round(victim, round);
        _totalDamageEvents++;
        if (_damageEvents.Count < 2_000)
            _damageEvents.Add(new DemoDamageEvent(attacker.ToString(CultureInfo.InvariantCulture),
                victim.ToString(CultureInfo.InvariantCulture), round, tick, canonicalWeapon, healthDamage));
    }

    private RoundCounts Round(ulong player, int round)
    {
        _roundPlayers.Add(player);
        if (_roundPlayers.Count > 128) throw new FormatException("Gameplay review stopped: more than 128 human accounts.");
        if (!_rounds.TryGetValue((player, round), out var counts))
            _rounds[(player, round)] = counts = new RoundCounts();
        return counts;
    }

    public DemoGameplayReport Build(IReadOnlyList<DemoPlayer> roster)
    {
        var players = roster.Select((player, index) =>
        {
            _counts.TryGetValue(ulong.Parse(player.SteamId, CultureInfo.InvariantCulture), out var counts);
            return new DemoGameplayPlayer(player.SteamId, string.Create(CultureInfo.InvariantCulture, $"Player{index + 1:00}"),
                counts?.Shots ?? 0, counts?.Assessed ?? 0, counts?.Selected ?? 0, counts?.Rounds.Count ?? 0);
        }).ToArray();
        var rosterIds = players.Select(p => p.SteamId).ToHashSet(StringComparer.Ordinal);
        var rounds = _rounds.OrderBy(p => p.Key.Round).ThenBy(p => p.Key.Player).Select(p =>
            new DemoRoundTelemetry(p.Key.Player.ToString(CultureInfo.InvariantCulture), p.Key.Round,
                p.Value.Shots, p.Value.Assessed, p.Value.Assessed > 0 ? p.Value.TurnTotal / p.Value.Assessed : null,
                p.Value.MaximumTurn, p.Value.MaximumTurnTick, p.Value.FirearmKills, p.Value.FirearmHeadshots,
                p.Value.FirearmDeaths)
            {
                TotalKills = p.Value.Kills,
                TotalHeadshotKills = p.Value.Headshots,
                TotalDeaths = p.Value.Deaths
            })
            .Where(p => rosterIds.Contains(p.SteamId)).ToArray();
        return new DemoGameplayReport(players, _findings.ToArray(), _totalFindings)
        {
            RoundTelemetry = rounds,
            CombatEvents = _combatEvents.Where(e => rosterIds.Contains(e.AttackerSteamId) &&
                rosterIds.Contains(e.VictimSteamId)).ToArray(),
            TotalCombatEvents = _totalCombatEvents,
            DamageEvents = _damageEvents.Where(e => rosterIds.Contains(e.AttackerSteamId) &&
                rosterIds.Contains(e.VictimSteamId)).ToArray(),
            TotalDamageEvents = _totalDamageEvents,
            ShotWindows = _windows.Build().Where(w => rosterIds.Contains(w.SteamId)).ToArray(),
            TotalShotWindows = _windows.TotalWindows,
            Engagements = _engagements.Build().Where(e => rosterIds.Contains(e.SteamId)).ToArray(),
            EngagementCoverage = _engagements.BuildCoverage().Where(e => rosterIds.Contains(e.SteamId)).ToArray(),
            TotalEngagements = _engagements.TotalEngagements
        };
    }

    internal static bool Valid(DemoAimSample sample) => sample.Eligible && sample.Round is >= 1 and <= 1000 &&
        double.IsFinite(sample.Pitch) && Math.Abs(sample.Pitch) <= 90 &&
        double.IsFinite(sample.Yaw) && Math.Abs(sample.Yaw) <= 360 &&
        double.IsFinite(sample.X) && double.IsFinite(sample.Y) && double.IsFinite(sample.Z);

    internal static bool Consecutive(DemoAimSample before, DemoAimSample after) =>
        Valid(before) && Valid(after) && before.SteamId == after.SteamId && before.Tick == after.Tick - 1 &&
        before.Round == after.Round && before.Pawn == after.Pawn && before.Life == after.Life &&
        before.Team == after.Team && Movement(before, after) <= 64;

    internal static double Movement(DemoAimSample before, DemoAimSample after) => Math.Sqrt(
        Math.Pow(after.X - before.X, 2) + Math.Pow(after.Y - before.Y, 2) + Math.Pow(after.Z - before.Z, 2));

    private static double Cosine(DemoAimSample before, DemoAimSample after)
    {
        var radians = Math.PI / 180;
        return Math.Sin(before.Pitch * radians) * Math.Sin(after.Pitch * radians) +
            Math.Cos(before.Pitch * radians) * Math.Cos(after.Pitch * radians) *
            Math.Cos((after.Yaw - before.Yaw) * radians);
    }

    internal static double TurnDegrees(DemoAimSample before, DemoAimSample after) =>
        Math.Acos(Math.Clamp(Cosine(before, after), -1, 1)) * 180 / Math.PI;

    internal static string? NormalizeWeapon(string? weapon)
    {
        var name = weapon?.ToLowerInvariant();
        if (name?.StartsWith("weapon_", StringComparison.Ordinal) == true) name = name[7..];
        return name is "ak47" or "m4a1" or "m4a1_silencer" or "galilar" or "famas" or "aug" or "sg556"
            or "awp" or "ssg08" or "scar20" or "g3sg1" or "mac10" or "mp9" or "mp7" or "mp5sd"
            or "ump45" or "p90" or "bizon" or "usp_silencer" or "glock" or "hkp2000" or "p250"
            or "fiveseven" or "tec9" or "cz75a" or "deagle" or "elite" or "revolver"
            or "nova" or "xm1014" or "mag7" or "sawedoff" or "negev" or "m249" ? name : null;
    }

    internal static string NormalizeKillWeapon(string? weapon)
    {
        var name = weapon?.ToLowerInvariant();
        if (name?.StartsWith("weapon_", StringComparison.Ordinal) == true) name = name[7..];
        return name is { Length: > 0 and <= 48 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? name
            : "unknown";
    }

    private sealed class Counts
    {
        public int Shots;
        public int Assessed;
        public int Selected;
        public HashSet<int> Rounds { get; } = new();
    }

    private sealed class RoundCounts
    {
        public int Shots;
        public int Assessed;
        public double TurnTotal;
        public double? MaximumTurn;
        public int? MaximumTurnTick;
        public int Kills;
        public int Headshots;
        public int Deaths;
        public int FirearmKills;
        public int FirearmHeadshots;
        public int FirearmDeaths;
    }
}
