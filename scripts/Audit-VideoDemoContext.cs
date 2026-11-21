#:project ..\src\CheaterOrNot.Core\CheaterOrNot.Core.csproj
#:property PublishAot=false

using System.Text.Json;
using CheaterOrNot.Core;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: dotnet run --file scripts\\Audit-VideoDemoContext.cs -- <source.dem> <capture-folder> <preview-folder> <new-output.json>");
    return 1;
}

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var result = await DemoVideoContextAudit.RunAsync(args[0], args[1], args[2], cancellation.Token);
    await using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(output, result,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, cancellation.Token);
    Console.WriteLine($"Saved local event context for {result.Clips.Count} clips. No upload or API request.");
    foreach (var clip in result.Clips)
    {
        Console.WriteLine($"Clip {clip.Timeline.Number}: {clip.Timeline.RetainedShots.Count} retained shot(s), {clip.Timeline.RetainedDeaths.Count} retained death(s).");
        foreach (var death in clip.Timeline.RetainedDeaths)
            Console.WriteLine(FormattableString.Invariant($"  Recorded death at tick {death.Tick}, nominal +{death.NominalSeconds:0.######}s; subsequent camera attribution requires review."));
    }
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or
    JsonException or OperationCanceledException or ArgumentException)
{
    Console.Error.WriteLine($"Local video/demo context audit failed: {error.Message}");
    return 1;
}
