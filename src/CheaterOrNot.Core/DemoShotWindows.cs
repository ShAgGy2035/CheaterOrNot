namespace CheaterOrNot.Core;

public sealed record DemoViewPoint(int OffsetTicks, double PitchDegrees, double YawRelativeToShotDegrees);

public sealed record DemoShotWindow(string SteamId, int Round, int ShotTick, string Weapon,
    int BeforeTicks, int AfterTicks, double? BeforeNetTurnDegrees, double? BeforePathTurnDegrees,
    double? AfterNetTurnDegrees, double? AfterPathTurnDegrees, IReadOnlyList<DemoViewPoint> Samples);

public sealed record DemoDamageEvent(string AttackerSteamId, string VictimSteamId, int Round, int Tick,
    string Weapon, int HealthDamage);

internal sealed class DemoShotWindowCollector
{
    public const int SideTicks = 8;
    private readonly Dictionary<ulong, List<DemoAimSample>> _history = new();
    private readonly Dictionary<ulong, List<Window>> _pending = new();
    private readonly List<Window> _retained = new();
    public int TotalWindows { get; private set; }

    public void Observe(DemoAimSample sample)
    {
        if (!_history.TryGetValue(sample.SteamId, out var history))
            _history[sample.SteamId] = history = new List<DemoAimSample>();
        if (history.Count > 0 && sample.Tick < history[^1].Tick)
        {
            history.Clear();
            if (_pending.TryGetValue(sample.SteamId, out var interrupted)) interrupted.Clear();
        }
        if (history.Count > 0 && sample.Tick == history[^1].Tick) history[^1] = sample;
        else history.Add(sample);
        while (history.Count > SideTicks + 1) history.RemoveAt(0);
        if (!_pending.TryGetValue(sample.SteamId, out var windows)) return;
        for (var index = windows.Count - 1; index >= 0; index--)
        {
            var window = windows[index];
            var offset = (long)sample.Tick - window.Shot.Tick;
            if (offset > SideTicks) windows.RemoveAt(index);
            else if (offset > 0) window.After[(int)offset - 1] = sample;
        }
    }

    public void Shot(DemoAimSample sample, string weapon)
    {
        if (!DemoGameplayAnalyzer.Valid(sample)) return;
        TotalWindows++;
        if (_retained.Count >= 2_000) return;
        var before = new List<DemoAimSample> { sample };
        if (_history.TryGetValue(sample.SteamId, out var history))
        {
            for (var index = history.Count - 1; index >= 0 && before.Count <= SideTicks; index--)
            {
                var previous = history[index];
                if (previous.Tick >= sample.Tick) continue;
                if (!DemoGameplayAnalyzer.Consecutive(previous, before[^1])) break;
                before.Add(previous);
            }
        }
        before.Reverse();
        var window = new Window(sample, weapon, before.ToArray());
        _retained.Add(window);
        if (!_pending.TryGetValue(sample.SteamId, out var windows))
            _pending[sample.SteamId] = windows = new List<Window>();
        windows.Add(window);
    }

    public IReadOnlyList<DemoShotWindow> Build() => _retained.Select(window => window.Build()).ToArray();

    private sealed class Window(DemoAimSample shot, string weapon, DemoAimSample[] before)
    {
        public DemoAimSample Shot { get; } = shot;
        public DemoAimSample?[] After { get; } = new DemoAimSample?[SideTicks];

        public DemoShotWindow Build()
        {
            var after = new List<DemoAimSample> { Shot };
            foreach (var sample in After)
            {
                if (sample is null || !DemoGameplayAnalyzer.Consecutive(after[^1], sample)) break;
                after.Add(sample);
            }
            static double Path(IReadOnlyList<DemoAimSample> samples) =>
                Enumerable.Range(1, samples.Count - 1).Sum(index =>
                    DemoGameplayAnalyzer.TurnDegrees(samples[index - 1], samples[index]));
            return new DemoShotWindow(Shot.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Shot.Round, Shot.Tick, weapon, before.Length - 1, after.Count - 1,
                before.Length > 1 ? DemoGameplayAnalyzer.TurnDegrees(before[0], Shot) : null,
                before.Length > 1 ? Path(before) : null,
                after.Count > 1 ? DemoGameplayAnalyzer.TurnDegrees(Shot, after[^1]) : null,
                after.Count > 1 ? Path(after) : null,
                before.Concat(after.Skip(1)).Select(sample => new DemoViewPoint(sample.Tick - Shot.Tick,
                    sample.Pitch, Math.IEEERemainder(sample.Yaw - Shot.Yaw, 360))).ToArray());
        }
    }
}
