using System.Globalization;

namespace CheaterOrNot.Core;

public sealed record DemoEngagementPoint(int OffsetTicks, double PitchDegrees, double YawRelativeDegrees,
    double RelativeX, double RelativeY, double RelativeZ);

public sealed record DemoEngagementShot(int Tick, bool OpponentSnapshotAvailable, int ObservedOpponents,
    int AssessedOpponents, string? ClosestAngularOpponent, double? EyeAngleDegrees, double? EyeDistanceUnits,
    double? RelativeEyeX, double? RelativeEyeY, double? RelativeEyeZ, double? MovementUnitsSincePreviousTick);

public sealed record DemoEngagement(string SteamId, int Round, int FirstShotTick, int LastShotTick,
    string Weapon, int Shots, int BeforeTicks, int AfterTicks, double? NetTurnDegrees, double? PathTurnDegrees,
    double? MovementPathUnits, IReadOnlyList<DemoEngagementPoint> Points, IReadOnlyList<DemoEngagementShot> ShotContext);

public sealed record DemoEngagementCoverage(string SteamId, int Round, int ObservedTicks, int EligibleShots,
    int Engagements, int RetainedEngagements, int GeometryAssessedShots, int NoOpponentShots,
    int IncompleteGeometryShots, double? MinimumEyeAngleDegrees, int? MinimumEyeAngleTick);

internal sealed class DemoEngagementCollector
{
    public const int SideTicks = 32;
    public const int MaximumShotSpanTicks = 256;
    public const int MaximumRetained = 2000;
    private readonly Dictionary<ulong, List<DemoAimSample>> _history = new();
    private readonly Dictionary<ulong, Engagement> _active = new();
    private readonly Dictionary<ulong, List<Engagement>> _pending = new();
    private readonly Dictionary<(ulong Player, int Round), List<Engagement>> _retained = new();
    private readonly Dictionary<(ulong Player, int Round), Coverage> _coverage = new();
    private int _retainedCount;
    public int TotalEngagements { get; private set; }

    public void Observe(DemoAimSample sample)
    {
        if (!_history.TryGetValue(sample.SteamId, out var history))
            _history[sample.SteamId] = history = [];
        if (history.Count > 0)
        {
            var previous = history[^1];
            if (sample.Tick < previous.Tick)
            {
                history.Clear();
                _active.Remove(sample.SteamId);
                if (_pending.TryGetValue(sample.SteamId, out var rewound)) rewound.Clear();
            }
            else if (sample.Tick != previous.Tick ? !DemoGameplayAnalyzer.Consecutive(previous, sample) :
                !DemoGameplayAnalyzer.Valid(sample) || previous.Pawn != sample.Pawn ||
                previous.Life != sample.Life || previous.Round != sample.Round || previous.Team != sample.Team)
                _active.Remove(sample.SteamId);
        }
        if (history.Count > 0 && history[^1].Tick == sample.Tick) history[^1] = sample;
        else history.Add(sample);
        if (history.Count > SideTicks + 1) history.RemoveAt(0);
        if (DemoGameplayAnalyzer.Valid(sample))
        {
            var row = Row(sample);
            if (sample.Tick > row.LastObservedTick)
            {
                row.ObservedTicks++;
                row.LastObservedTick = sample.Tick;
            }
        }
        if (_active.TryGetValue(sample.SteamId, out var active) && (long)sample.Tick - active.LastShot.Tick > SideTicks)
            _active.Remove(sample.SteamId);
        if (!_pending.TryGetValue(sample.SteamId, out var pending)) return;
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var engagement = pending[index];
            if ((long)sample.Tick - engagement.LastShot.Tick > SideTicks) pending.RemoveAt(index);
            else if (sample.Tick > engagement.FirstShot.Tick)
                engagement.Frames[sample.Tick] = sample;
        }
    }

    public void Shot(DemoAimSample sample, string weapon, IReadOnlyList<DemoAimSample>? opponents, int unavailableOpponents)
    {
        if (!DemoGameplayAnalyzer.Valid(sample)) return;
        Observe(sample);
        var row = Row(sample);
        row.EligibleShots++;
        var history = _history[sample.SteamId];
        var before = history.Count > 1 ? history[^2] : null;
        var geometry = Geometry(sample, opponents, before, unavailableOpponents);
        if (geometry.AssessedOpponents > 0)
        {
            row.GeometryShots++;
            if (row.MinimumAngle is null || geometry.EyeAngleDegrees < row.MinimumAngle)
                (row.MinimumAngle, row.MinimumTick) = (geometry.EyeAngleDegrees, sample.Tick);
        }
        if (geometry.OpponentSnapshotAvailable && geometry.ObservedOpponents == 0) row.NoOpponentShots++;
        if (!geometry.OpponentSnapshotAvailable || geometry.AssessedOpponents < geometry.ObservedOpponents)
            row.IncompleteGeometryShots++;
        if (_active.TryGetValue(sample.SteamId, out var current) && current.Weapon == weapon &&
            current.LastShot.Round == sample.Round && current.LastShot.Pawn == sample.Pawn &&
            current.LastShot.Life == sample.Life && current.LastShot.Team == sample.Team &&
            (long)sample.Tick - current.FirstShot.Tick <= MaximumShotSpanTicks)
        {
            current.AddShot(sample, geometry);
            return;
        }
        TotalEngagements++;
        row.Engagements++;
        var key = (sample.SteamId, sample.Round);
        if (!_retained.TryGetValue(key, out var retained) && _retainedCount < MaximumRetained)
            _retained[key] = retained = [];
        var keep = retained is not null && (retained.Count == 2 || _retainedCount < MaximumRetained);
        if (retained is { Count: 2 })
        {
            var removed = retained[1];
            removed.Retained = false;
            if (_pending.TryGetValue(sample.SteamId, out var pending)) pending.Remove(removed);
            retained.RemoveAt(1);
            _retainedCount--;
        }
        var pre = new List<DemoAimSample> { sample };
        if (keep)
        {
            for (var index = history.Count - 1; index >= 0 && pre.Count <= SideTicks; index--)
            {
                var previous = history[index];
                if (previous.Tick >= sample.Tick) continue;
                if (!DemoGameplayAnalyzer.Consecutive(previous, pre[^1])) break;
                pre.Add(previous);
            }
        }
        pre.Reverse();
        var created = new Engagement(sample, weapon, keep, keep ? pre.ToArray() : []);
        created.AddShot(sample, geometry);
        _active[sample.SteamId] = created;
        if (!keep) return;
        retained!.Add(created);
        _retainedCount++;
        if (!_pending.TryGetValue(sample.SteamId, out var windows))
            _pending[sample.SteamId] = windows = [];
        windows.Add(created);
    }

    public IReadOnlyList<DemoEngagement> Build() => _retained.Values.SelectMany(rows => rows)
        .Select(row => row.Build()).OrderBy(row => row.FirstShotTick)
        .ThenBy(row => row.SteamId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<DemoEngagementCoverage> BuildCoverage() => _coverage
        .OrderBy(pair => pair.Key.Round).ThenBy(pair => pair.Key.Player).Select(pair =>
            new DemoEngagementCoverage(pair.Key.Player.ToString(CultureInfo.InvariantCulture), pair.Key.Round,
                pair.Value.ObservedTicks, pair.Value.EligibleShots, pair.Value.Engagements,
                _retained.TryGetValue(pair.Key, out var retained) ? retained.Count : 0,
                pair.Value.GeometryShots, pair.Value.NoOpponentShots, pair.Value.IncompleteGeometryShots,
                pair.Value.MinimumAngle, pair.Value.MinimumTick)).ToArray();

    private Coverage Row(DemoAimSample sample)
    {
        var key = (sample.SteamId, sample.Round);
        if (!_coverage.TryGetValue(key, out var row)) _coverage[key] = row = new Coverage();
        return row;
    }

    internal static DemoEngagementShot Geometry(DemoAimSample sample, IReadOnlyList<DemoAimSample>? opponents,
        DemoAimSample? before = null, int unavailableOpponents = 0)
    {
        var observed = unavailableOpponents;
        var assessed = 0;
        string? closest = null;
        double? angle = null, distance = null, relativeX = null, relativeY = null, relativeZ = null;
        var seen = new HashSet<ulong>();
        var available = opponents is not null && DemoGameplayAnalyzer.Valid(sample) && sample.Team is 2 or 3;
        if (available)
            foreach (var opponent in opponents!)
            {
                if (opponent.SteamId == sample.SteamId ||
                    !DemoRosterBuilder.IsHumanId(opponent.SteamId) || !opponent.Eligible ||
                    opponent.Team is not (2 or 3) || opponent.Team == sample.Team ||
                    opponent.Tick != sample.Tick || opponent.Round != sample.Round || !seen.Add(opponent.SteamId)) continue;
                observed++;
                if (!HasEye(sample) || !HasEye(opponent)) continue;
                var dx = opponent.EyeX!.Value - sample.EyeX!.Value;
                var dy = opponent.EyeY!.Value - sample.EyeY!.Value;
                var dz = opponent.EyeZ!.Value - sample.EyeZ!.Value;
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (!double.IsFinite(length) || length <= 0) continue;
                var pitch = sample.Pitch * Math.PI / 180;
                var yaw = sample.Yaw * Math.PI / 180;
                var cosine = (Math.Cos(pitch) * Math.Cos(yaw) * dx +
                    Math.Cos(pitch) * Math.Sin(yaw) * dy - Math.Sin(pitch) * dz) / length;
                var measured = Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
                assessed++;
                if (angle is not null && measured >= angle) continue;
                angle = measured;
                distance = length;
                (relativeX, relativeY, relativeZ) = (dx, dy, dz);
                closest = opponent.SteamId.ToString(CultureInfo.InvariantCulture);
            }
        return new(sample.Tick, available, observed, assessed, closest, angle, distance,
            relativeX, relativeY, relativeZ,
            before is not null && DemoGameplayAnalyzer.Consecutive(before, sample) ?
                DemoGameplayAnalyzer.Movement(before, sample) : null);
    }

    private static bool HasEye(DemoAimSample sample) => sample.EyeX is { } x && double.IsFinite(x) &&
        sample.EyeY is { } y && double.IsFinite(y) && sample.EyeZ is { } z && double.IsFinite(z);

    private sealed class Coverage
    {
        public int LastObservedTick = -1;
        public int ObservedTicks, EligibleShots, Engagements, GeometryShots, NoOpponentShots, IncompleteGeometryShots;
        public double? MinimumAngle;
        public int? MinimumTick;
    }

    private sealed class Engagement(DemoAimSample first, string weapon, bool retained, DemoAimSample[] before)
    {
        public DemoAimSample FirstShot { get; } = first;
        public DemoAimSample LastShot { get; private set; } = first;
        public string Weapon { get; } = weapon;
        public bool Retained { get; set; } = retained;
        public Dictionary<int, DemoAimSample> Frames { get; } = new();
        private readonly List<DemoEngagementShot> _shots = [];
        private int _shotCount;

        public void AddShot(DemoAimSample sample, DemoEngagementShot geometry)
        {
            LastShot = sample;
            _shotCount++;
            if (!Retained) return;
            _shots.Add(geometry);
            if (sample.Tick > FirstShot.Tick) Frames[sample.Tick] = sample;
        }

        public DemoEngagement Build()
        {
            var frames = before.ToList();
            foreach (var sample in Frames.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                if (!DemoGameplayAnalyzer.Consecutive(frames[^1], sample)) break;
                frames.Add(sample);
            }
            var path = 0.0;
            var movement = 0.0;
            for (var index = 1; index < frames.Count; index++)
            {
                path += DemoGameplayAnalyzer.TurnDegrees(frames[index - 1], frames[index]);
                movement += DemoGameplayAnalyzer.Movement(frames[index - 1], frames[index]);
            }
            return new(FirstShot.SteamId.ToString(CultureInfo.InvariantCulture), FirstShot.Round,
                FirstShot.Tick, LastShot.Tick, Weapon, _shotCount, FirstShot.Tick - frames[0].Tick,
                Math.Max(0, frames[^1].Tick - LastShot.Tick),
                frames.Count > 1 ? DemoGameplayAnalyzer.TurnDegrees(frames[0], frames[^1]) : null,
                frames.Count > 1 ? path : null, frames.Count > 1 ? movement : null,
                frames.Select(sample => new DemoEngagementPoint(sample.Tick - FirstShot.Tick, sample.Pitch,
                    Math.IEEERemainder(sample.Yaw - FirstShot.Yaw, 360), sample.X - FirstShot.X,
                    sample.Y - FirstShot.Y, sample.Z - FirstShot.Z)).ToArray(), _shots.ToArray());
        }
    }
}
