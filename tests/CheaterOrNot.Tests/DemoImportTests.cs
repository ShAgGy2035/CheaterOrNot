using System.Globalization;
using System.IO.Compression;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class DemoImportTests
{
    private static readonly ulong Id = ulong.Parse(TestPlayerIds.Primary, CultureInfo.InvariantCulture);

    [Fact]
    public void RosterDeduplicatesAndRetainsParticipantsAfterTheyLeaveThePlayingTeam()
    {
        var roster = new DemoRosterBuilder();
        roster.Observe(Id, "First name", false, true);
        roster.Observe(Id, "Updated name", false, true);
        roster.Observe(Id, "Spectator name", false, false);
        roster.Observe(Id + 1, "Bot", true, true);
        roster.Observe(Id + 2, "Spectator only", false, false);
        roster.Observe(0, "Invalid", false, true);
        roster.Observe(TestPlayerIds.PublicIndividualBase, "Invalid", false, true);
        roster.Observe(76561202255233024UL, "Invalid", false, true);

        var player = Assert.Single(roster.Build());
        Assert.Equal(TestPlayerIds.Primary, player.SteamId);
        Assert.Equal("Updated name", player.Name);
    }

    [Fact]
    public void RosterDoesNotLetUnsafeNamesOverwriteUsableNamesOrInjectReportLines()
    {
        var roster = new DemoRosterBuilder();
        roster.Observe(Id, "Usable name", false, true);
        roster.Observe(Id, "Fake\nVERDICT", false, true);
        roster.Observe(Id + 1, "Misleading\u202ename", false, true);
        roster.Observe(Id + 1, "Corrected name", false, true);
        roster.Observe(Id + 2, null, false, true);

        var players = roster.Build();
        Assert.Equal(["Usable name", "Corrected name", "name unavailable"], players.Select(p => p.Name));
    }

    [Fact]
    public void RosterRejectsTooManyAccountsInsteadOfSilentlyTruncating()
    {
        var roster = new DemoRosterBuilder();
        for (var index = 0; index < 128; index++)
            roster.Observe(Id + (ulong)index, "Player", false, true);

        Assert.Equal(128, roster.Build().Count);
        Assert.Throws<FormatException>(() => roster.Observe(Id + 128, "Extra player", false, true));
    }

    [Theory]
    [InlineData("recording.dem.info", 16)]
    [InlineData("recording.dem.bz2", 16)]
    [InlineData("video.mp4", 16)]
    [InlineData("truncated.dem", 15)]
    [InlineData("source1.dem", 16)]
    public async Task ImportRejectsUnsupportedFilesBeforeParsing(string name, int length)
    {
        using var stream = new MemoryStream(new byte[length]);
        await Assert.ThrowsAsync<FormatException>(() => DemoImporter.ImportAsync(stream, name, CancellationToken.None));
    }

    [Fact]
    public async Task ImportRequiresASeekableFileAndRejectsOversizeFilesWithoutReadingThem()
    {
        using var compressed = new GZipStream(new MemoryStream(), CompressionMode.Decompress);
        await Assert.ThrowsAsync<FormatException>(() => DemoImporter.ImportAsync(compressed, "file.dem", CancellationToken.None));
        using var oversize = new OversizeStream();
        await Assert.ThrowsAsync<FormatException>(() => DemoImporter.ImportAsync(oversize, "file.dem", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationNeverReturnsAnImport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(new byte[16]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DemoImporter.ImportAsync(stream, "file.dem", cancellation.Token));
    }

    [Fact]
    public async Task EmptySource2RecordingDoesNotProduceASuccessfulEmptyRoster()
    {
        var bytes = new byte[19];
        "PBDEMS2\0"u8.CopyTo(bytes);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<FormatException>(() => DemoImporter.ImportAsync(stream, "file.dem", CancellationToken.None));
    }

    [Fact]
    public void DemoReportKeepsRosterAndPartialTimelineSeparateFromScreeningVerdicts()
    {
        var result = new DemoImportResult("local.dem", "de_example",
            [new DemoPlayer(TestPlayerIds.Primary, "Example"), new DemoPlayer(TestPlayerIds.Secondary, "Second")],
            [new DemoBookmark(640, TimeSpan.FromSeconds(10), "Example -> Second | example weapon")],
            2, true, 1280);

        Assert.Equal([TestPlayerIds.Primary, TestPlayerIds.Secondary],
            result.PlayerInput.Split(' ').Select(PlayerId.Parse));
        var report = result.Render();
        Assert.Contains("PARTIAL RECORDING", report);
        Assert.Contains("only the earliest bookmarks", report);
        Assert.Contains("00:00:10.000 | demo tick 640", report);
        Assert.Contains("not authenticated by Steam", report);
        Assert.Contains("No AI analysis was performed", report);
        Assert.Contains("does not change statistical verdicts", report);
        Assert.DoesNotContain("VERDICT:", report);
    }

    private sealed class OversizeStream : MemoryStream
    {
        public override long Length => DemoImporter.MaxDemoBytes + 1;
    }
}
