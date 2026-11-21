using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class SettingsStoreTests
{
    [Theory]
    [InlineData("AIza_synthetic_test_key_1234567890")]
    [InlineData("AQ.synthetic_test_key_1234567890")]
    public void GeminiKeyUsesProtectedStorageAndSurvivesChangesToAllOtherKeys(string key)
    {
        var dir = TempDirectory();
        try
        {
            SettingsStore.SaveGeminiApiKey(key, dir);
            SettingsStore.SaveSteamApiKey("0123456789ABCDEF0123456789ABCDEF", dir);
            SettingsStore.SaveLeetifyApiKey("leetify-test-token", dir);
            SettingsStore.SaveFaceitApiKey("faceit-test-token", dir);
            SettingsStore.SaveSteamApiKey(null, dir);
            SettingsStore.SaveLeetifyApiKey(null, dir);
            SettingsStore.SaveFaceitApiKey(null, dir);
            Assert.Equal(key, SettingsStore.LoadGeminiApiKey(dir));
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(dir, "apikeys.dat")));
            Assert.Throws<FormatException>(() => SettingsStore.SaveGeminiApiKey("bad key", dir));
            Assert.Equal(key, SettingsStore.LoadGeminiApiKey(dir));
            SettingsStore.SaveLeetifyApiKey("preserved-test-token", dir);
            SettingsStore.SaveGeminiApiKey(null, dir);
            Assert.Equal("preserved-test-token", SettingsStore.LoadLeetifyApiKey(dir));
            Assert.Null(SettingsStore.LoadGeminiApiKey(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "CheaterOrNotTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RulesEditorJsonGroupsSettingsAndPreservesCustomValues()
    {
        var rules = new ScreeningRules { KdThreshold = 2.5, OpeningMinimumDuels = 150 };
        var json = rules.ToEditorJson();
        Assert.Equal(rules, ScreeningRules.Parse(json));
        Assert.Contains("\n\n  \"UtilityMinimumKd\"", json);
        Assert.Contains("\n\n  \"RecentMinimumKd\"", json);
        Assert.Contains("\n\n  \"WeaponMinimumKills\"", json);
        Assert.Contains("\n\n  \"MapMinimumMatches\"", json);
        Assert.Contains("\n\n  \"OpeningMinimumDuels\"", json);
        Assert.Contains("\n\n  \"OscillationBlockSize\"", json);
    }

    [Fact]
    public void ScreeningRulesRoundTripAndInvalidEditsDoNotReplaceSavedSettings()
    {
        var dir = TempDirectory();
        try
        {
            Assert.Equal(new ScreeningRules(), ScreeningRules.Load(dir));
            var rules = new ScreeningRules { KdThreshold = 2.5, OpeningMinimumDuels = 150 };
            rules.Save(dir);
            Assert.Equal(rules, ScreeningRules.Load(dir));
            Assert.Throws<FormatException>(() => ScreeningRules.Parse(rules.ToJson().Replace("\"KdThreshold\": 2.5", "\"KdThreshold\": -1")));
            Assert.Throws<FormatException>(() => ScreeningRules.Parse("{}"));
            Assert.Throws<FormatException>(() => ScreeningRules.Parse(rules.ToJson().Replace("\"KdThreshold\": 2.5", "\"KdThreshold\": 2.5, \"KdThreshold\": 3")));
            Assert.Throws<FormatException>(() => (rules with { WeaponMinimumCategories = 4 }).Save(dir));
            Assert.Equal(rules, ScreeningRules.Load(dir));
            File.WriteAllText(Path.Combine(dir, "screening-rules.json"), "{");
            Assert.Throws<FormatException>(() => ScreeningRules.Load(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FaceitKeyIsPreservedWhenOtherKeysAreChanged()
    {
        var dir = TempDirectory();
        try
        {
            SettingsStore.SaveSteamApiKey("0123456789ABCDEF0123456789ABCDEF", dir);
            SettingsStore.SaveLeetifyApiKey("leetify-test-token", dir);
            SettingsStore.SaveFaceitApiKey("faceit-test-token", dir);
            SettingsStore.SaveSteamApiKey(null, dir);
            SettingsStore.SaveLeetifyApiKey("leetify-replacement", dir);
            Assert.Equal("faceit-test-token", SettingsStore.LoadFaceitApiKey(dir));
            Assert.Throws<FormatException>(() => SettingsStore.SaveFaceitApiKey("bad key", dir));
            Assert.Equal("faceit-test-token", SettingsStore.LoadFaceitApiKey(dir));
            SettingsStore.SaveFaceitApiKey(null, dir);
            Assert.Equal("leetify-replacement", SettingsStore.LoadLeetifyApiKey(dir));
            Assert.Null(SettingsStore.LoadFaceitApiKey(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SteamKeyRoundTripsThroughProtectedStorageAndCanBeRemoved()
    {
        var dir = TempDirectory();
        try
        {
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
            SettingsStore.SaveSteamApiKey(" 0123456789abcdef0123456789abcdef ", dir);
            Assert.Equal("0123456789ABCDEF0123456789ABCDEF", SettingsStore.LoadSteamApiKey(dir));
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain("0123456789ABCDEF", File.ReadAllText(Path.Combine(dir, "apikeys.dat")));
            SettingsStore.SaveSteamApiKey(null, dir);
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
            Assert.False(File.Exists(Path.Combine(dir, "apikeys.dat")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0")]
    [InlineData("GHIJKLMNOPQRSTUVGHIJKLMNOPQRSTUV")]
    public void MalformedSteamKeysAreRejectedBeforeStorage(string key)
    {
        var dir = TempDirectory();
        try
        {
            Assert.Throws<FormatException>(() => SettingsStore.SaveSteamApiKey(key, dir));
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LeetifyReplacementPreservesCaseAndPunctuation()
    {
        var dir = TempDirectory();
        try
        {
            SettingsStore.SaveLeetifyApiKey("older-test-key", dir);
            SettingsStore.SaveLeetifyApiKey("  AbC-123_+.~=/xYz  ", dir);
            Assert.Equal("AbC-123_+.~=/xYz", SettingsStore.LoadLeetifyApiKey(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BothKeysRoundTripIndependentlyAndLegacySingleKeyFilesStillLoad()
    {
        var dir = TempDirectory();
        try
        {
            SettingsStore.SaveSteamApiKey("0123456789ABCDEF0123456789ABCDEF", dir);
            SettingsStore.SaveLeetifyApiKey(" lk_example-key-123 ", dir);
            Assert.Equal("0123456789ABCDEF0123456789ABCDEF", SettingsStore.LoadSteamApiKey(dir));
            Assert.Equal("lk_example-key-123", SettingsStore.LoadLeetifyApiKey(dir));
            SettingsStore.SaveSteamApiKey(null, dir);
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
            Assert.Equal("lk_example-key-123", SettingsStore.LoadLeetifyApiKey(dir));
            SettingsStore.SaveLeetifyApiKey(null, dir);
            Assert.False(File.Exists(Path.Combine(dir, "apikeys.dat")));

            // Legacy v1 files stored the bare Steam key as the whole payload.
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "apikeys.dat"),
                [.. "v1raw:"u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes("FEDCBA9876543210FEDCBA9876543210")]);
            Assert.Equal("FEDCBA9876543210FEDCBA9876543210", SettingsStore.LoadSteamApiKey(dir));
            Assert.Null(SettingsStore.LoadLeetifyApiKey(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it which is invalid")]
    public void MalformedLeetifyKeysAreRejectedBeforeStorage(string key)
    {
        var dir = TempDirectory();
        try
        {
            Assert.Throws<FormatException>(() => SettingsStore.SaveLeetifyApiKey(key, dir));
            Assert.Null(SettingsStore.LoadLeetifyApiKey(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CorruptedKeyFilesAreTreatedAsNoKey()
    {
        var dir = TempDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "apikeys.dat"), "v1win:garbage");
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
            File.WriteAllText(Path.Combine(dir, "apikeys.dat"), "unknown-format");
            Assert.Null(SettingsStore.LoadSteamApiKey(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
