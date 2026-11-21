using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

public sealed record CaptureToolRelease(string Component, string Version, string Url, string Sha256, long Bytes);
public sealed record CaptureToolInstallation(string CaptureDirectory, string EncoderDirectory)
{
    public IReadOnlyList<CaptureToolRelease> Releases { get; init; } = [];
}

public sealed class CaptureToolsSetupRequiredException(string message) : IOException(message);

public sealed class PortableCaptureTools : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _root;
    public const string EncoderAsset = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";

    public PortableCaptureTools(string root, HttpMessageHandler? handler = null)
    {
        _root = Path.GetFullPath(root);
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CheaterOrNot-PortableComponents/1.0");
    }

    public CaptureToolInstallation Installed()
    {
        var path = Path.Combine(_root, "capture-tools.json");
        if (!File.Exists(path))
            throw new CaptureToolsSetupRequiredException("Portable capture tools are missing.");
        CaptureToolInstallation installation;
        try
        {
            installation = JsonSerializer.Deserialize<CaptureToolInstallation>(File.ReadAllText(path)) ??
                throw new FormatException("The portable capture manifest is empty.");
        }
        catch (JsonException) { throw new FormatException("The portable capture manifest is invalid."); }
        installation = installation with
        {
            CaptureDirectory = ResolveChild(installation.CaptureDirectory),
            EncoderDirectory = ResolveChild(installation.EncoderDirectory)
        };
        if (!File.Exists(Path.Combine(installation.CaptureDirectory, "cs-demo-manager.exe")) ||
            !File.Exists(Path.Combine(installation.EncoderDirectory, "bin", "ffmpeg.exe")) ||
            !File.Exists(Path.Combine(installation.EncoderDirectory, "bin", "ffprobe.exe")))
            throw new CaptureToolsSetupRequiredException("Portable capture tools are incomplete.");
        return installation;
    }

    public async Task<IReadOnlyList<CaptureToolRelease>> CheckAsync(CancellationToken token)
    {
        var capture = await ReleaseAsync("akiver/cs-demo-manager", null, token);
        var encoder = await ReleaseAsync("FFmpeg-Builds/FFmpeg-Builds", EncoderAsset, token);
        return [capture, encoder];
    }

    private async Task<CaptureToolRelease> ReleaseAsync(string repository, string? assetName, CancellationToken token, string? tag = null)
    {
        var endpoint = tag is null ? "latest" : "tags/" + tag;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/{endpoint}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Component update check failed (HTTP {(int)response.StatusCode}). Installed tools were not changed.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, 1_000_000, token);
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            var version = root.GetProperty("tag_name").GetString() ?? throw new FormatException("Missing release version.");
            if (assetName is null && !Regex.IsMatch(version, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant))
                throw new FormatException("Unsupported capture release version; no update was accepted.");
            var expectedName = assetName ?? $"CS-Demo-Manager-Setup-{version[1..]}.exe";
            var matches = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == expectedName).ToArray();
            if (matches.Length != 1) throw new FormatException("The expected portable component asset is unavailable.");
            var match = matches[0];
            var digest = match.GetProperty("digest").GetString() ?? throw new FormatException("Missing release checksum.");
            var url = match.GetProperty("browser_download_url").GetString() ?? throw new FormatException("Missing release URL.");
            var bytes = match.GetProperty("size").GetInt64();
            if (!Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
                bytes is <= 0 or > 350_000_000 ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal) ||
                !uri.AbsolutePath.EndsWith("/" + expectedName, StringComparison.Ordinal) || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || !uri.IsDefaultPort)
                throw new FormatException("Component release metadata failed validation. No update was accepted.");
            return new(assetName is null ? "Capture" : "Encoder", version, url, digest[7..], bytes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Component release metadata is incomplete or invalid. No update was accepted.");
        }
    }

    public async Task<CaptureToolInstallation> UpdateAsync(IProgress<string>? progress, CancellationToken token)
    {
        var releases = await CheckAsync(token);
        var extractor = Path.Combine(_root, "extractor", "7z.exe");
        if (!File.Exists(extractor))
            await PrepareExtractorAsync(token);
        Directory.CreateDirectory(_root);
        var installed = new List<string>();
        foreach (var release in releases)
        {
            var directory = Path.Combine(_root, release.Component.ToLowerInvariant() + "-" + release.Sha256[..16]);
            if (!Directory.Exists(directory))
            {
                var stage = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    progress?.Report($"Downloading and verifying {release.Component} ({release.Bytes / 1_000_000} MB)...");
                    var archive = Path.Combine(stage, "download");
                    await DownloadAsync(release, archive, token);
                    var destination = Path.Combine(stage, "unpacked");
                    Directory.CreateDirectory(destination);
                    progress?.Report($"Extracting {release.Component}; no system installer is run...");
                    if (release.Component == "Capture")
                    {
                        var inner = Path.Combine(stage, "inner");
                        await ToolProcess.RunAsync(extractor, ["x", archive, "-o" + inner, @"$PLUGINSDIR\app-64.7z", "-y"], null, token);
                        await ToolProcess.RunAsync(extractor, ["x", Path.Combine(inner, "$PLUGINSDIR", "app-64.7z"), "-o" + destination, "-y"], null, token);
                        await ToolProcess.RunAsync(Path.Combine(destination, "cs-demo-manager.exe"),
                            [Path.Combine(destination, "resources", "app.asar", "cli.js"), "--version"],
                            new Dictionary<string, string> { ["ELECTRON_RUN_AS_NODE"] = "1" }, token);
                    }
                    else
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(archive, destination);
                        var entries = Directory.GetDirectories(destination);
                        if (entries.Length != 1) throw new FormatException("Unexpected encoder archive layout.");
                        destination = entries[0];
                        await ToolProcess.RunAsync(Path.Combine(destination, "bin", "ffmpeg.exe"), ["-version"], null, token);
                    }
                    token.ThrowIfCancellationRequested();
                    Directory.Move(destination, directory);
                }
                finally
                {
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                }
            }
            installed.Add(directory);
        }
        var installation = new CaptureToolInstallation(Path.GetRelativePath(_root, installed[0]), Path.GetRelativePath(_root, installed[1]))
        { Releases = releases };
        if (!File.Exists(Path.Combine(installed[0], "cs-demo-manager.exe")) ||
            !File.Exists(Path.Combine(installed[0], "resources", "app.asar")) ||
            !File.Exists(Path.Combine(installed[1], "bin", "ffmpeg.exe")) ||
            !File.Exists(Path.Combine(installed[1], "bin", "ffprobe.exe")))
            throw new IOException("Staged components are incomplete. The active component manifest was not changed.");
        var captureVersion = await ToolProcess.RunAsync(Path.Combine(installed[0], "cs-demo-manager.exe"),
            [Path.Combine(installed[0], "resources", "app.asar", "cli.js"), "--version"],
            new Dictionary<string, string> { ["ELECTRON_RUN_AS_NODE"] = "1" }, token);
        if (captureVersion.Trim() != releases[0].Version.TrimStart('v'))
            throw new FormatException("The capture executable does not report the expected release version. The active manifest was not changed.");
        await ToolProcess.RunAsync(Path.Combine(installed[1], "bin", "ffprobe.exe"), ["-version"], null, token);
        var manifest = Path.Combine(_root, "capture-tools.json");
        var pending = Path.Combine(_root, "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(installation), token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, manifest, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return Installed();
    }

    private async Task PrepareExtractorAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        var stage = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var bootstrap = await ReleaseAsync("ip7z/7zip", "7zr.exe", token, "26.06");
            var package = await ReleaseAsync("ip7z/7zip", "7z2606-x64.exe", token, "26.06");
            var executable = Path.Combine(stage, "7zr.exe");
            var archive = Path.Combine(stage, "7zip-package");
            await DownloadAsync(bootstrap, executable, token);
            await DownloadAsync(package, archive, token);
            var unpacked = Path.Combine(stage, "unpacked");
            await ToolProcess.RunAsync(executable, ["x", archive, "-o" + unpacked, "-y"], null, token);
            if (!File.Exists(Path.Combine(unpacked, "7z.exe")) || !File.Exists(Path.Combine(unpacked, "7z.dll")))
                throw new FormatException("The portable extractor package is incomplete.");
            Directory.Move(unpacked, Path.Combine(_root, "extractor"));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal async Task DownloadAsync(CaptureToolRelease release, string destination, CancellationToken token)
    {
        var uri = new Uri(release.Url);
        for (var redirects = 0; redirects < 5; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new FormatException("Component download redirected to an unsupported destination.");
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = response.Headers.Location is { } location ? new Uri(uri, location) :
                    throw new FormatException("Component download redirect is missing its destination.");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new IOException($"Component download failed (HTTP {(int)response.StatusCode}).");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(input, output, release.Bytes, token);
            await using var file = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (file.Length != release.Bytes || !hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Component checksum/size mismatch. The download must not be executed.");
            return;
        }
        throw new IOException("Too many component download redirects.");
    }

    private string ResolveChild(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory))
            throw new FormatException("The component manifest points outside the portable tools folder.");
        var resolved = Path.GetFullPath(Path.Combine(_root, directory));
        if (!resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The component manifest points outside the portable tools folder.");
        return resolved;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var bytes = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(bytes, token)) > 0)
        {
            length += read;
            if (length > limit) throw new FormatException("Component response exceeded its declared size limit.");
            await output.WriteAsync(bytes.AsMemory(0, read), token);
        }
    }

    public void Dispose() => _http.Dispose();
}

internal static class ToolProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(info) ?? throw new IOException("Could not start the portable component.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var text = await output;
        var error = await errors;
        if (process.ExitCode != 0)
            throw new IOException($"Portable component failed (exit {process.ExitCode}). " + error[..Math.Min(error.Length, 1200)]);
        return text;
    }
}
