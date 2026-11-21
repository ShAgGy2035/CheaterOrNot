namespace CheaterOrNot.Core;

public sealed record HltvProSample(string SourceUrl, string Scope, int? Maps, double Kpr,
    double? Kast = null, double? Adr = null, double? Headshots = null, double? Rating3 = null, double? Kd = null)
{
    public IEnumerable<(string Name, double Value, string Unit)> Metrics()
    {
        yield return ("Kills per round", Kpr, "");
        if (Kast is { } kast) yield return ("KAST", kast, "%");
        if (Adr is { } adr) yield return ("Average damage per round (ADR)", adr, "");
        if (Headshots is { } hs) yield return ("Headshot kills", hs, "%");
        if (Rating3 is { } rating) yield return ("HLTV Rating 3.0", rating, "");
        if (Kd is { } kd) yield return ("K/D", kd, "");
    }
}

public static class HltvProReference
{
    public const string Reviewed = "2026-11-17";
    private const string BaseUrl = "https://www.hltv.org/";
    private const string Career = "Career overview (may include CS:GO)";

    private static readonly IReadOnlyDictionary<string, HltvProSample> Samples = new Dictionary<string, HltvProSample>
    {
        ["76561198113666193"] = Comparison("11893/zywoo", true, 183, .83, 75.8, 43.5, 1.34),
        ["76561198386265483"] = Comparison("21167/donk", true, 140, .93, 75.1, 61.1, 1.43),
        ["76561197991272318"] = Comparison("11816/ropz", false, 200, .74, 73.2, 52.9, 1.16),
        ["76561198074762801"] = Comparison("19230/m0nesy", true, 166, .82, 74.8, 42.4, 1.28),
        ["76561198081484775"] = Overview("16920/sh1ro", .77, 76.3, 78.1),
        ["76561198200982290"] = new(BaseUrl + "news/43576/top-20-players-of-2025-molodoy-6",
            "2025 notable events (annual ranking article)", 162, .76),
        ["76561197978835160"] = Overview("16693/flamez", .70, 73.4, 77.3),
        ["76561198068422762"] = Overview("9960/frozen", .75, 73.6, 82.2),
        ["76561198058500492"] = new(BaseUrl + "stats/players/individual/15631/kscerato?csVersion=CS2",
            "CS2 career (individual statistics)", null, .72, Kd: 1.19),
        ["76561198063336407"] = Overview("18221/spinx", .73, 73.2, 77.8)
    };

    public static HltvProSample? ForPlayer(string steamId) => Samples.GetValueOrDefault(steamId);

    private static HltvProSample Overview(string player, double kpr, double kast, double adr) =>
        new(BaseUrl + "stats/players/" + player, Career, null, kpr, kast, adr);

    private static HltvProSample Comparison(string player, bool bigEvents, int maps, double kpr,
        double kast, double headshots, double rating) =>
        new(BaseUrl + "stats/players/compare/" + player + "?p1TimeFilter=2025&p1MatchFilter=" +
            (bigEvents ? "BigEvents" : "allMatches") + "&p1MapFilter=allMaps",
            bigEvents ? "2025 Big Events, all maps" : "2025 all tracked matches, all maps",
            maps, kpr, kast, Headshots: headshots, Rating3: rating);
}
