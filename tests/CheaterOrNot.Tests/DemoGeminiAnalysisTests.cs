using System.Security.Cryptography;
using System.Text;
using CheaterOrNot.Core;

namespace CheaterOrNot.Tests;

public class DemoGeminiAnalysisTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("plan")]
    [InlineData("size")]
    public async Task CaptureVerificationRequiresExactPlanSourceSizeAndEveryFile(string scenario)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CheaterOrNot-analysis-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic capture integrity fixture");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            DemoVideoClip[] plan = [new(1, TestPlayerIds.Primary, "Player01", 2, 100, 356)];
            var manifest = new DemoVideoManifest(3, hash, 100, plan,
                [new(1, "clip.mp4", 4, 1280, 720, true, hash, bytes.Length)]);
            var path = Path.Combine(root, "clip.mp4");
            await File.WriteAllBytesAsync(path, bytes);
            if (scenario == "changed")
            {
                var tampered = bytes.ToArray();
                tampered[0] ^= 1;
                await File.WriteAllBytesAsync(path, tampered);
            }
            if (scenario == "missing") File.Delete(path);
            if (scenario == "plan") plan = [plan[0] with { StartTick = 101 }];
            var problem = await DemoGeminiAnalysis.CaptureVerificationProblemAsync(root, manifest, hash,
                scenario == "size" ? 101 : 100, plan, CancellationToken.None);
            if (scenario == "valid") Assert.Null(problem);
            else Assert.False(string.IsNullOrWhiteSpace(problem));
            if (scenario == "changed")
            {
                Assert.Equal(bytes.Length, new FileInfo(path).Length);
                Assert.NotEqual(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingCaptureIsANormalDecisionButCancellationIsNotConvertedToRecapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "CheaterOrNot-analysis-" + Guid.NewGuid().ToString("N"));
        Assert.Null(await DemoVideoReview.FindMatchingAsync(root, new string('A', 64), CancellationToken.None));
        var cancelled = new CancellationToken(true);
        var plan = new[] { new DemoVideoClip(1, TestPlayerIds.Primary, "Player01", 2, 100, 356) };
        var manifest = new DemoVideoManifest(3, new string('A', 64), 100, plan,
            [new(1, "clip.mp4", 4, 1280, 720, true, new string('A', 64), 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DemoGeminiAnalysis.CaptureVerificationProblemAsync(root, manifest, manifest.SourceSha256, 100, plan, cancelled));
    }
}
