using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheaterOrNot.Core;

public sealed record ScreeningRules
{
    public double KdThreshold { get; init; } = 2;
    public double HeadshotPercentThreshold { get; init; } = 75;
    public double WinPercentThreshold { get; init; } = 80;
    public double KillsPerRoundThreshold { get; init; } = 1;
    public int HeadshotMinimumMatches { get; init; } = 10;
    public double UtilityMinimumKd { get; init; } = 1.5;
    public double UtilityDamageThreshold { get; init; } = 30;
    public double RecentMinimumKd { get; init; } = 1.8;
    public double RecentKdJumpThreshold { get; init; } = 0.75;
    public int WeaponMinimumKills { get; init; } = 100;
    public int WeaponMinimumShots { get; init; } = 1000;
    public int WeaponMinimumCount { get; init; } = 3;
    public int WeaponMinimumCategories { get; init; } = 2;
    public double WeaponSpreadPercentThreshold { get; init; } = 3;
    public int MapMinimumMatches { get; init; } = 20;
    public int MapMinimumCount { get; init; } = 3;
    public double MapMinimumBestKd { get; init; } = 1.5;
    public double MapOutlierRatioThreshold { get; init; } = 2;
    public int OpeningMinimumDuels { get; init; } = 100;
    public double OpeningWinPercentThreshold { get; init; } = 70;
    public int OscillationBlockSize { get; init; } = 10;
    public int OscillationMinimumBlocks { get; init; } = 5;
    public double OscillationHighKd { get; init; } = 2;
    public double OscillationLowKd { get; init; } = 0.6;
    public int OscillationExtremeBlocks { get; init; } = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public void Validate()
    {
        var ratios = new[] { KdThreshold, KillsPerRoundThreshold, UtilityMinimumKd, UtilityDamageThreshold,
            RecentMinimumKd, RecentKdJumpThreshold, MapMinimumBestKd, MapOutlierRatioThreshold,
            OscillationHighKd, OscillationLowKd };
        var percentages = new[] { HeadshotPercentThreshold, WinPercentThreshold, WeaponSpreadPercentThreshold,
            OpeningWinPercentThreshold };
        if (ratios.Any(value => !double.IsFinite(value) || value < 0) ||
            percentages.Any(value => !double.IsFinite(value) || value < 0 || value > 100))
            throw new FormatException("Thresholds must be finite and nonnegative; percentages must be between 0 and 100.");
        if (HeadshotMinimumMatches < 1 || WeaponMinimumKills < 1 || WeaponMinimumShots < 1 ||
            WeaponMinimumCount < 2 || WeaponMinimumCategories < 1 || WeaponMinimumCategories > WeaponMinimumCount ||
            MapMinimumMatches < 1 || MapMinimumCount < 2 || OpeningMinimumDuels < 1 ||
            OscillationBlockSize < 1 || OscillationBlockSize > 500 || OscillationMinimumBlocks < 1 ||
            OscillationMinimumBlocks > 500 || OscillationExtremeBlocks < 1 || OscillationExtremeBlocks > 500)
            throw new FormatException("Sample requirements must be positive; weapon and map counts must be at least 2, categories cannot exceed weapons, and oscillation counts cannot exceed 500.");
        if (OscillationLowKd >= OscillationHighKd)
            throw new FormatException("The low K/D block threshold must be less than the high K/D threshold.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public string ToEditorJson()
    {
        var json = ToJson();
        foreach (var firstField in new[]
        {
            nameof(UtilityMinimumKd), nameof(RecentMinimumKd), nameof(WeaponMinimumKills),
            nameof(MapMinimumMatches), nameof(OpeningMinimumDuels), nameof(OscillationBlockSize)
        })
            json = json.Replace($"\n  \"{firstField}\"", $"\n\n  \"{firstField}\"", StringComparison.Ordinal);
        return json;
    }

    public static ScreeningRules Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Rules must be a JSON object.");
            var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            var expected = typeof(ScreeningRules).GetProperties().Select(property => property.Name).ToArray();
            if (properties.Length != expected.Length || properties.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
                expected.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
                throw new FormatException("Rules must contain every listed field exactly once, with no extra fields.");
            var rules = JsonSerializer.Deserialize<ScreeningRules>(json, Options)
                ?? throw new FormatException("Rules are missing.");
            rules.Validate();
            return rules;
        }
        catch (JsonException ex) { throw new FormatException("Invalid rules JSON: " + ex.Message, ex); }
    }

    public static ScreeningRules Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? SettingsStore.DefaultDirectory, "screening-rules.json");
        try { return Parse(File.ReadAllText(path)); }
        catch (FileNotFoundException) { return new ScreeningRules(); }
        catch (DirectoryNotFoundException) { return new ScreeningRules(); }
    }

    public void Save(string? directory = null)
    {
        Validate();
        directory ??= SettingsStore.DefaultDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "screening-rules.json");
        var temporary = Path.Combine(directory, $".screening-rules-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, ToJson());
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
