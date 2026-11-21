using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class DemoVideoTests
{
    [Fact]
    public void EventTimelineMatchesOwnPlayerRoundAndInclusiveTicksWithoutRescalingTime()
    {
        DemoShotWindow Shot(string id, int round, int tick) =>
            new(id, round, tick, "ak47", 0, 0, null, null, null, null, []);
        var gameplay = new DemoGameplayReport(
            [new(TestPlayerIds.Primary, "Player01", 4, 4, 0, 0),
                new(TestPlayerIds.Secondary, "Player02", 0, 0, 0, 0)], [], 0)
        {
            ShotWindows = [
                Shot(TestPlayerIds.Primary, 2, 356), Shot(TestPlayerIds.Primary, 2, 100),
                Shot(TestPlayerIds.Primary, 2, 228), Shot(TestPlayerIds.Primary, 2, 99),
                Shot(TestPlayerIds.Primary, 2, 357), Shot(TestPlayerIds.Primary, 3, 228),
                Shot(TestPlayerIds.Secondary, 2, 228)],
            CombatEvents = [
                new(TestPlayerIds.Primary, TestPlayerIds.Secondary, 2, 164, "ak47", false),
                new(TestPlayerIds.Secondary, TestPlayerIds.Primary, 2, 292, "ak47", false),
                new(TestPlayerIds.Secondary, TestPlayerIds.Primary, 3, 300, "ak47", false)]
        };
        var timeline = DemoVideoContextAudit.Timeline(gameplay,
            new DemoVideoClip(1, TestPlayerIds.Primary, "Player01", 2, 100, 356)
            {
                KillTick = 164, VictimSteamId = TestPlayerIds.Secondary, Weapon = "ak47", Headshot = false
            });
        Assert.Equal([100, 228, 356], timeline.RetainedShots.Select(s => s.Tick));
        Assert.Equal([0d, 2d, 4d], timeline.RetainedShots.Select(s => s.NominalSeconds));
        var kill = Assert.Single(timeline.RetainedKills);
        Assert.Equal(164, kill.Tick);
        Assert.Equal("Player02", kill.OpponentAlias);
        Assert.Equal(164, timeline.FocusKill!.Tick);
        Assert.Equal("Player02", timeline.FocusKill.OpponentAlias);
        var death = Assert.Single(timeline.RetainedDeaths);
        Assert.Equal(292, death.Tick);
        Assert.Equal(3d, death.NominalSeconds);
        var json = JsonSerializer.Serialize(timeline);
        Assert.DoesNotContain(TestPlayerIds.Primary, json);
        Assert.DoesNotContain(TestPlayerIds.Secondary, json);
    }

    [Fact]
    public void MissingRetainedEventsRemainEmptyRatherThanInferringSurvivalOrZeroShots()
    {
        var gameplay = new DemoGameplayReport([new(TestPlayerIds.Primary, "Player01", 0, 0, 0, 0)], [], 0)
        { TotalCombatEvents = 2001, TotalShotWindows = 2001 };
        var timeline = DemoVideoContextAudit.Timeline(gameplay,
            new(1, TestPlayerIds.Primary, "Player01", 2, 100, 356));
        Assert.Empty(timeline.RetainedShots);
        Assert.Empty(timeline.RetainedDeaths);
    }

    [Theory]
    [InlineData("Player02", 100, 356)]
    [InlineData("Player01", -1, 356)]
    [InlineData("Player01", 356, 356)]
    [InlineData("Player01", 100, 613)]
    public void EventTimelineRejectsUnboundPlayersAndUnsupportedRanges(string alias, int start, int end)
    {
        var gameplay = new DemoGameplayReport([new(TestPlayerIds.Primary, "Player01", 0, 0, 0, 0)], [], 0);
        Assert.Throws<FormatException>(() => DemoVideoContextAudit.Timeline(gameplay,
            new(1, TestPlayerIds.Primary, alias, 2, start, end)));
    }

    [Fact]
    public async Task EventAuditRejectsADifferentSourceBeforeParsingOrAcceptingClips()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "test.dem");
            File.WriteAllBytes(source, new byte[16]);
            File.WriteAllText(Path.Combine(root, "review-manifest.json"),
                JsonSerializer.Serialize(new DemoVideoManifest(3, new string('A', 64), 16, [], []),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var error = await Assert.ThrowsAsync<FormatException>(() =>
                DemoVideoContextAudit.RunAsync(source, root, Path.Combine(root, "gemini-preview", "test"),
                    CancellationToken.None));
            Assert.Contains("not bound to this exact source demo", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CaptureManifestLimitSupportsLargeValidMultiClipPlans()
    {
        var root = TemporaryDirectory();
        try
        {
            var hash = new string('A', 64);
            var clips = Enumerable.Range(1, 400).Select(i =>
                new DemoVideoClip(i, TestPlayerIds.Primary, "Player01", 1, i * 100, i * 100 + 256)).ToArray();
            var videos = clips.Select(clip =>
                new DemoVideoFile(clip.Number, $"clips/clip-{clip.Number}.mp4", 4, 1280, 720, true,
                    hash, 1)).ToArray();
            var manifest = new DemoVideoManifest(3, hash, 244_417_494, clips, videos);
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(Encoding.UTF8.GetByteCount(json) > 100_000);
            File.WriteAllText(Path.Combine(root, "review-manifest.json"), json);

            var parsed = await DemoVideoReview.ReadManifestAsync(root, hash, CancellationToken.None);

            Assert.Equal(400, parsed.Clips.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CaptureManifestRemainsBounded()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "review-manifest.json");
            await File.WriteAllBytesAsync(path, new byte[2_000_001]);

            await Assert.ThrowsAsync<FormatException>(() =>
                DemoVideoReview.ReadManifestAsync(root, new string('A', 64), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PlansOneClipPerKillAndStopsAtThePovPlayersNextDeath()
    {
        var players = new[] { TestPlayerIds.Primary, TestPlayerIds.Secondary };
        var gameplay = new DemoGameplayReport(players.Select((id, i) =>
            new DemoGameplayPlayer(id, $"Player{i + 1:00}", 10, 9, 0, 0)).ToArray(), [], 0)
        {
            CombatEvents =
            [
                new(players[0], players[1], 2, 10, "ak47", true),
                new(players[1], players[0], 2, 30, "knife", false),
                new(players[1], players[0], 3, 300, "hegrenade", false),
                new(players[0], players[1], 3, 9990, "ak47", false)
            ],
            TotalCombatEvents = 4
        };
        var demo = new DemoImportResult("test.dem", "test", players.Select(id => new DemoPlayer(id, "Local")).ToArray(), [], 0, false, 10000)
        { Gameplay = gameplay };
        var clips = DemoVideoCapture.Plan(demo);
        Assert.Equal(4, clips.Count);
        Assert.Equal([10, 30, 300, 9990], clips.Select(clip => clip.KillTick));
        Assert.Equal(["ak47", "knife", "hegrenade", "ak47"], clips.Select(clip => clip.Weapon));
        Assert.Equal(0, clips[0].StartTick);
        Assert.Equal(29, clips[0].EndTick);
        Assert.Equal(9990, clips[^1].StartTick + 128);
        Assert.Equal(10000, clips[^1].EndTick);
        Assert.All(clips, clip => Assert.InRange(clip.EndTick - clip.StartTick, 1, 512));
    }

    [Theory]
    [InlineData(0, 0, 2_000_000, false)]
    [InlineData(9, 11_000_000, 1_000_000, false)]
    [InlineData(9, 12_000_000, 1, true)]
    [InlineData(10, 12_000_000, 1, true)]
    public void VideoBatchingStartsNewRequestOnlyWhenCountOrByteBudgetWouldBeExceeded(
        int currentClips, long currentBytes, long nextBytes, bool expected)
    {
        Assert.Equal(expected, DemoVideoReview.ShouldStartNewVideoBatch(currentClips, currentBytes, nextBytes));
    }

    [Fact]
    public async Task ReplacingCapturesRemovesMatchingLegacyAndCurrentSourcesAndLeavesOtherFoldersAlone()
    {
        var root = TemporaryDirectory();
        try
        {
            var sourceHash = new string('A', 64);
            var otherHash = new string('B', 64);
            DemoVideoClip[] plan = [new(1, TestPlayerIds.Primary, "Player01", 2, 100, 356)];
            var same = AddCapture(root, "same-demo", sourceHash, plan, version: 2);
            var other = AddCapture(root, "other-demo", otherHash, plan);
            var malformed = Directory.CreateDirectory(Path.Combine(root, "malformed")).FullName;
            File.WriteAllText(Path.Combine(malformed, "review-manifest.json"), "{ invalid");
            var current = AddCapture(root, "fresh", sourceHash, plan);

            var removed = await DemoVideoReview.ReplaceOlderCapturesAsync(root, sourceHash, 16, plan,
                current, CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(same));
            Assert.True(Directory.Exists(other));
            Assert.True(Directory.Exists(malformed));
            Assert.True(Directory.Exists(current));
        }
        finally { Directory.Delete(root, true); }

        static string AddCapture(string root, string name, string hash, IReadOnlyList<DemoVideoClip> plan, int version = 3)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            File.WriteAllText(Path.Combine(folder, "review-manifest.json"),
                JsonSerializer.Serialize(new DemoVideoManifest(version, hash, 16, plan,
                    [new(1, "clip.mp4", 4, 1280, 720, true, new string('C', 64), 1)]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return folder;
        }
    }

    [Fact]
    public void CaptureDeadlineScalesBeyondTheFormerTenClipWorkload()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), DemoVideoCapture.CaptureTimeout(1));
        Assert.Equal(TimeSpan.FromMinutes(20), DemoVideoCapture.CaptureTimeout(10));
        Assert.Equal(TimeSpan.FromMinutes(69.5), DemoVideoCapture.CaptureTimeout(109));
        Assert.True(DemoVideoCapture.CaptureTimeout(DemoVideoCapture.MaximumKillClips) >
            DemoVideoCapture.CaptureTimeout(109));
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoVideoCapture.CaptureTimeout(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DemoVideoCapture.CaptureTimeout(DemoVideoCapture.MaximumKillClips + 1));
    }

    [Fact]
    public void CaptureConfigurationDisablesXrayAndVoicesAndFocusesEachPlannedPlayer()
    {
        DemoVideoClip[] clips = [new(1, TestPlayerIds.Primary, "Player01", 2, 100, 400),
            new(2, TestPlayerIds.Secondary, "Player02", 3, 500, 800)];
        using var document = JsonDocument.Parse(DemoVideoCapture.Configuration(@"C:\demo.dem", @"C:\output", @"C:\tools\ffmpeg.exe", clips));
        var root = document.RootElement;
        Assert.Equal("CS", root.GetProperty("recordingSystem").GetString());
        Assert.False(root.GetProperty("concatenateSequences").GetBoolean());
        Assert.True(root.GetProperty("closeGameAfterRecording").GetBoolean());
        var sequences = root.GetProperty("sequences");
        Assert.Equal(2, sequences.GetArrayLength());
        for (var i = 0; i < clips.Length; i++)
        {
            var sequence = sequences[i];
            Assert.False(sequence.GetProperty("showXRay").GetBoolean());
            Assert.False(sequence.GetProperty("playerVoicesEnabled").GetBoolean());
            Assert.True(sequence.GetProperty("recordAudio").GetBoolean());
            Assert.Equal(clips[i].SteamId, sequence.GetProperty("playerCameras")[0].GetProperty("playerSteamId").GetString());
            Assert.Equal(clips[i].StartTick, sequence.GetProperty("playerCameras")[0].GetProperty("tick").GetInt32());
        }
    }

    [Fact]
    public void MissingToolsCanOfferSetupWithoutTreatingAnInvalidManifestAsAnAbsentInstallation()
    {
        var root = TemporaryDirectory();
        try
        {
            using var tools = new PortableCaptureTools(root);
            Assert.Throws<CaptureToolsSetupRequiredException>(() => tools.Installed());
            var manifest = Path.Combine(root, "capture-tools.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new CaptureToolInstallation("capture", "encoder")));
            Assert.Throws<CaptureToolsSetupRequiredException>(() => tools.Installed());
            File.WriteAllText(manifest, "{ invalid");
            Assert.Throws<FormatException>(() => tools.Installed());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    public void PortableManifestCannotSelectAnExternalExecutable(string path)
    {
        var root = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "capture-tools.json"),
                JsonSerializer.Serialize(new CaptureToolInstallation(path, path)));
            using var tools = new PortableCaptureTools(root);
            Assert.Throws<FormatException>(() => tools.Installed());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstalledComponentPathsRemainRelativeWhenThePortableFolderMoves()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "capture"));
            Directory.CreateDirectory(Path.Combine(root, "encoder", "bin"));
            File.WriteAllText(Path.Combine(root, "capture", "cs-demo-manager.exe"), "");
            File.WriteAllText(Path.Combine(root, "encoder", "bin", "ffmpeg.exe"), "");
            File.WriteAllText(Path.Combine(root, "encoder", "bin", "ffprobe.exe"), "");
            File.WriteAllText(Path.Combine(root, "capture-tools.json"),
                JsonSerializer.Serialize(new CaptureToolInstallation("capture", "encoder")));
            var moved = Path.Combine(root, "moved");
            Directory.CreateDirectory(moved);
            foreach (var name in new[] { "capture", "encoder" })
                Directory.Move(Path.Combine(root, name), Path.Combine(moved, name));
            File.Move(Path.Combine(root, "capture-tools.json"), Path.Combine(moved, "capture-tools.json"));
            using var tools = new PortableCaptureTools(moved);
            Assert.Equal(Path.Combine(moved, "capture"), tools.Installed().CaptureDirectory);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DownloadsRejectTamperingOversizeAndUntrustedRedirects(bool oversize, bool redirect)
    {
        var root = TemporaryDirectory();
        try
        {
            var data = Encoding.UTF8.GetBytes("component");
            using var tools = new PortableCaptureTools(root, new Handler(_ =>
            {
                if (!redirect) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(oversize ? [.. data, 1] : data) };
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://example.com/tool.exe");
                return response;
            }));
            var release = new CaptureToolRelease("Capture", "v1.0.0", "https://github.com/vendor/release/tool.exe",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other"))), data.Length);
            await Assert.ThrowsAsync<FormatException>(() => tools.DownloadAsync(release, Path.Combine(root, "download"), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IncompleteCandidateDoesNotReplaceTheWorkingManifest()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "extractor"));
            File.WriteAllText(Path.Combine(root, "extractor", "7z.exe"), "");
            var digest = new string('a', 64);
            Directory.CreateDirectory(Path.Combine(root, "capture-" + digest[..16]));
            Directory.CreateDirectory(Path.Combine(root, "encoder-" + digest[..16]));
            var manifest = Path.Combine(root, "capture-tools.json");
            var original = JsonSerializer.Serialize(new CaptureToolInstallation("working-capture", "working-encoder"));
            File.WriteAllText(manifest, original);
            using var tools = new PortableCaptureTools(root, new Handler(request =>
            {
                var capture = request.RequestUri!.AbsolutePath.Contains("cs-demo-manager");
                var name = capture ? "CS-Demo-Manager-Setup-1.0.0.exe" : PortableCaptureTools.EncoderAsset;
                var repo = capture ? "akiver/cs-demo-manager" : "FFmpeg-Builds/FFmpeg-Builds";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        assets = new[] { new { name, digest = "sha256:" + digest, size = 1,
                            browser_download_url = $"https://github.com/{repo}/releases/download/v1.0.0/{name}" } }
                    }))
                };
            }));
            await Assert.ThrowsAsync<IOException>(() => tools.UpdateAsync(null, CancellationToken.None));
            Assert.Equal(original, File.ReadAllText(manifest));
        }
        finally { Directory.Delete(root, true); }
    }

    private static DemoEngagement Engagement(string id, int tick) =>
        new(id, 1, tick, tick, "ak47", 1, 0, 0, null, null, null, [], []);

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CheaterOrNot-video-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
}
