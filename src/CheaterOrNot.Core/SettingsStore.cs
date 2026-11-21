using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

/// <summary>
/// Stores the optional API keys (Steam, Leetify, FACEIT and Gemini) in the user's
/// application-data folder. On Windows the payload is encrypted with DPAPI bound to the current user
/// account; on other platforms the file is written with owner-only permissions. Keys are never
/// written to logs, evidence, or reports.
/// </summary>
public static class SettingsStore
{
    private const string FileName = "apikeys.dat";
    private static readonly byte[] WindowsMarker = "v1win:"u8.ToArray();
    private static readonly byte[] PlainMarker = "v1raw:"u8.ToArray();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CheaterOrNot");

    /// <summary>Validates and persists the Steam key, or removes it when null/blank.</summary>
    /// <exception cref="FormatException">The key is not a 32-character hexadecimal Steam key.</exception>
    public static void SaveSteamApiKey(string? key, string? directory = null)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToUpperInvariant();
        if (key is not null && !IsValidSteamKey(key))
            throw new FormatException("A Steam Web API key is 32 hexadecimal characters. Copy it exactly from " +
                "steamcommunity.com/dev/apikey.");
        var stored = Load(directory);
        Save(directory, key, stored.Leetify, stored.Faceit, stored.Gemini);
    }

    /// <summary>Validates and persists the Leetify key, or removes it when null/blank.</summary>
    /// <exception cref="FormatException">The key is not a single printable token.</exception>
    public static void SaveLeetifyApiKey(string? key, string? directory = null)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (key is not null && !IsValidLeetifyKey(key))
            throw new FormatException("A Leetify API key is a single token of 8-128 visible characters. Copy it " +
                "exactly from leetify.com/app/developer.");
        var stored = Load(directory);
        Save(directory, stored.Steam, key, stored.Faceit, stored.Gemini);
    }

    public static void SaveFaceitApiKey(string? key, string? directory = null)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (key is not null && !IsValidFaceitKey(key))
            throw new FormatException("A FACEIT API key must be a single bearer token of 8-256 characters.");
        var stored = Load(directory);
        Save(directory, stored.Steam, stored.Leetify, key, stored.Gemini);
    }

    public static void SaveGeminiApiKey(string? key, string? directory = null)
    {
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (key is not null && !GeminiReviewClient.IsValidKey(key))
            throw new FormatException("A Gemini API key must be a single token of 20-128 letters, digits, periods, underscores or hyphens.");
        var stored = Load(directory);
        Save(directory, stored.Steam, stored.Leetify, stored.Faceit, key);
    }

    /// <summary>Returns the stored Steam key, or null when absent or unreadable.</summary>
    public static string? LoadSteamApiKey(string? directory = null) => Load(directory).Steam;

    /// <summary>Returns the stored Leetify key, or null when absent or unreadable.</summary>
    public static string? LoadLeetifyApiKey(string? directory = null) => Load(directory).Leetify;
    public static string? LoadFaceitApiKey(string? directory = null) => Load(directory).Faceit;
    public static string? LoadGeminiApiKey(string? directory = null) => Load(directory).Gemini;

    private static bool IsValidSteamKey(string key) =>
        Regex.IsMatch(key, "^[0-9A-F]{32}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static bool IsValidLeetifyKey(string key) =>
        Regex.IsMatch(key, "^[\\x21-\\x7E]{8,128}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static bool IsValidFaceitKey(string key) =>
        key.Length is >= 8 and <= 256 &&
        Regex.IsMatch(key, "^[A-Za-z0-9._~+/-]+=*$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static void Save(string? directory, string? steam, string? leetify, string? faceit, string? gemini)
    {
        directory ??= DefaultDirectory;
        var path = Path.Combine(directory, FileName);
        if (steam is null && leetify is null && faceit is null && gemini is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(directory);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (steam is not null)
                writer.WriteString("steam", steam);
            if (leetify is not null)
                writer.WriteString("leetify", leetify);
            if (faceit is not null)
                writer.WriteString("faceit", faceit);
            if (gemini is not null)
                writer.WriteString("gemini", gemini);
            writer.WriteEndObject();
        }
        var payload = buffer.ToArray();
        byte[] content;
        if (OperatingSystem.IsWindows())
            content = [.. WindowsMarker, .. ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser)];
        else
            content = [.. PlainMarker, .. payload];
        File.WriteAllBytes(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static (string? Steam, string? Leetify, string? Faceit, string? Gemini) Load(string? directory)
    {
        var path = Path.Combine(directory ?? DefaultDirectory, FileName);
        try
        {
            if (!File.Exists(path))
                return (null, null, null, null);
            var content = File.ReadAllBytes(path);
            byte[] payload;
            if (content.AsSpan().StartsWith(WindowsMarker))
            {
                if (!OperatingSystem.IsWindows())
                    return (null, null, null, null);
                payload = ProtectedData.Unprotect(content[WindowsMarker.Length..], null, DataProtectionScope.CurrentUser);
            }
            else if (content.AsSpan().StartsWith(PlainMarker))
            {
                payload = content[PlainMarker.Length..];
            }
            else
            {
                return (null, null, null, null);
            }
            string? steam = null;
            string? leetify = null;
            string? faceit = null;
            string? gemini = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (null, null, null, null);
                if (doc.RootElement.TryGetProperty("steam", out var s) && s.ValueKind == JsonValueKind.String)
                    steam = s.GetString();
                if (doc.RootElement.TryGetProperty("leetify", out var l) && l.ValueKind == JsonValueKind.String)
                    leetify = l.GetString();
                if (doc.RootElement.TryGetProperty("faceit", out var f) && f.ValueKind == JsonValueKind.String)
                    faceit = f.GetString();
                if (doc.RootElement.TryGetProperty("gemini", out var g) && g.ValueKind == JsonValueKind.String)
                    gemini = g.GetString();
            }
            catch (JsonException)
            {
                // Legacy single-key format: the payload is the bare Steam key itself.
                steam = Encoding.UTF8.GetString(payload);
            }
            return (steam is not null && IsValidSteamKey(steam) ? steam : null,
                leetify is not null && IsValidLeetifyKey(leetify) ? leetify : null,
                faceit is not null && IsValidFaceitKey(faceit) ? faceit : null,
                gemini is not null && GeminiReviewClient.IsValidKey(gemini) ? gemini : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
            or DecoderFallbackException)
        {
            return (null, null, null, null);
        }
    }
}
