using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;
using CheaterOrNot.Core;

namespace CheaterOrNot;

class Program
{
    public static string PortableDirectory => Environment.ProcessPath is { } path &&
        !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(path)! : AppContext.BaseDirectory;

    [STAThread]
    public static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(AppContext.BaseDirectory, "browsers"));
        if (args.Length == 1 && args[0] == "--prepare-capture-tools")
        {
            using var tools = new PortableCaptureTools(Path.Combine(PortableDirectory, "tools"));
            tools.UpdateAsync(new Progress<string>(Console.WriteLine), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--install-browser")
        {
            Environment.ExitCode = Microsoft.Playwright.Program.Main(["install", "chromium", "--only-shell"]);
            return;
        }
        if (args.Length == 1 && args[0] == "--verify-browser")
        {
            VerifyBrowserAsync().GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task VerifyBrowserAsync()
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ChromiumSandbox = true
        });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<html><body><script>document.body.textContent = 'browser-ready';</script></body></html>");
        if (await page.TextContentAsync("body") != "browser-ready")
            throw new InvalidOperationException("The bundled browser did not execute JavaScript.");
        Console.WriteLine($".NET {Environment.Version}; Chromium {browser.Version}; bundled JavaScript execution verified.");
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
