using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CheaterOrNot.Core;

public sealed record PublicHtmlResponse(int Status, string? Html, string? Error = null, string? Note = null);

public sealed record RenderedPage(string Html, string Note, PublicHtmlResponse? Statistics = null);

public interface IPageRenderer : IAsyncDisposable
{
    Task<RenderedPage> ReadAsync(Uri url, Func<Uri, CancellationToken, Task<bool>> permits,
        CancellationToken cancellationToken, Uri? statisticsUrl = null);
}

public sealed class ChromiumPageRenderer : IPageRenderer
{
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private bool _disposed;

    public string SessionDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "CheaterOrNot-browser-" + Guid.NewGuid().ToString("N"));

    public async Task<RenderedPage> ReadAsync(Uri url,
        Func<Uri, CancellationToken, Task<bool>> permits, CancellationToken cancellationToken, Uri? statisticsUrl = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (statisticsUrl is not null && (statisticsUrl.GetLeftPart(UriPartial.Authority) != url.GetLeftPart(UriPartial.Authority) ||
            statisticsUrl.AbsolutePath != url.AbsolutePath.TrimEnd('/') + "/stats" ||
            statisticsUrl.Query.Length != 0 || statisticsUrl.Fragment.Length != 0 || statisticsUrl.UserInfo.Length != 0))
            throw new ArgumentException("Only the same public profile's statistics response may be requested.", nameof(statisticsUrl));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(url.Host == "cstracker.gg"
            ? TimeSpan.FromSeconds(90)
            : TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        try
        {
            if (_context is null)
            {
                _playwright ??= await Playwright.CreateAsync();
                Directory.CreateDirectory(SessionDirectory);
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                    Path.Combine(SessionDirectory, "profile"), new()
                    {
                        Headless = true,
                        ChromiumSandbox = true,
                        AcceptDownloads = false,
                        ServiceWorkers = ServiceWorkerPolicy.Block,
                        DownloadsPath = Path.Combine(SessionDirectory, "downloads"),
                        UserAgent = "CheaterOrNot/1.0 (Chromium public-profile renderer)",
                        Timeout = 20_000
                    });
                await _context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
            }
            token.ThrowIfCancellationRequested();
            var page = await _context.NewPageAsync();
            try
            {
                var requests = 0;
                string? restriction = null;
                IRequest? statisticsRequest = null;
                var statisticsAttempts = 0;
                var allowedStatisticsAttempts = 1;
                var verificationPhase = false;
                var verificationPaths = new List<string>();
                var statisticsResponse = new TaskCompletionSource<IResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
                page.Request += (_, request) =>
                {
                    if (statisticsUrl is not null && request.Url == statisticsUrl.AbsoluteUri && statisticsRequest is null)
                        statisticsRequest = request;
                };
                page.Response += (_, received) =>
                {
                    if (statisticsRequest == received.Request) statisticsResponse.TrySetResult(received);
                };
                page.RequestFailed += (_, request) =>
                {
                    if (statisticsRequest == request) statisticsResponse.TrySetResult(null);
                };
                // Recorded from the start so a checkpoint's clearing reload is never missed.
                var navigations = new List<IResponse>();
                page.Response += (_, received) =>
                {
                    try
                    {
                        if (received.Request.IsNavigationRequest && received.Request.Frame == page.MainFrame)
                            lock (navigations) navigations.Add(received);
                    }
                    catch (PlaywrightException) { }
                };
                await _context.RouteAsync("**/*", async route =>
                {
                    try
                    {
                        var request = route.Request;
                        var isMainNavigation = false;
                        if (request.IsNavigationRequest && Uri.TryCreate(request.Url, UriKind.Absolute, out var navigation) &&
                            Sources.IsSamePlayerPage(url, navigation))
                        {
                            try { isMainNavigation = request.Frame == page.MainFrame; }
                            catch (PlaywrightException)
                            {
                                restriction = "An unverified popup/navigation frame was blocked.";
                            }
                        }
                        // While the website's own Cloudflare checkpoint runs, it may load challenge resources and
                        // renavigate/submit the same already-permitted public route. Nothing else is opened up.
                        Uri.TryCreate(request.Url, UriKind.Absolute, out var target);
                        var verificationRoute = verificationPhase && target is not null &&
                            (target is { Scheme: "https", Host: "challenges.cloudflare.com" } ||
                             (target.GetLeftPart(UriPartial.Authority) == url.GetLeftPart(UriPartial.Authority) &&
                              (verificationPaths.Contains(target.AbsolutePath) ||
                               target.AbsolutePath.StartsWith("/cdn-cgi/", StringComparison.Ordinal))));
                        var allowed = target is not null &&
                            target.Scheme is "http" or "https" && target.UserInfo.Length == 0 &&
                            (request.Method is "GET" or "HEAD" || (verificationRoute && request.Method == "POST")) &&
                            (request.ResourceType is not ("image" or "media" or "font") || verificationRoute) &&
                            (!request.IsNavigationRequest || isMainNavigation || verificationRoute) &&
                            Interlocked.Increment(ref requests) <= 150;
                        if (statisticsUrl is not null && request.Url == statisticsUrl.AbsoluteUri && !request.IsNavigationRequest &&
                            Interlocked.Increment(ref statisticsAttempts) > allowedStatisticsAttempts)
                        {
                            allowed = false;
                            restriction = "A repeated statistics request was blocked; no automatic retry is allowed.";
                        }
                        if (allowed)
                        {
                            try { allowed = await permits(target!, token); }
                            catch (Exception ex) when (ex is HttpRequestException or FormatException or RegexMatchTimeoutException)
                            {
                                allowed = false;
                                restriction = "Some resources were blocked because their robots.txt policy could not be verified.";
                            }
                        }
                        if (!allowed)
                        {
                            if (request.IsNavigationRequest) restriction = "A navigation/redirect was blocked; only verified same-player routes are permitted.";
                            else if (request.ResourceType is not ("image" or "media" or "font"))
                                restriction ??= "Some scripts or data requests were blocked by robots.txt, request limits, or the read-only navigation policy.";
                            await route.AbortAsync();
                            return;
                        }
                        await route.ContinueAsync();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (!page.IsClosed) await route.AbortAsync();
                    }
                    catch (PlaywrightException) when (page.IsClosed || token.IsCancellationRequested)
                    {
                        // Closing the page interrupts outstanding intercepted requests.
                    }
                });
                var response = await page.GotoAsync(url.AbsoluteUri,
                    new() { WaitUntil = WaitUntilState.Commit,
                        Timeout = url.Host == "cstracker.gg" ? 35_000 : 25_000 }).WaitAsync(token);
                if (response is null) throw new FormatException("The browser received no HTTP player page.");
                var checkpoint = "";
                if (!response.Ok)
                {
                    // Cloudflare's managed checkpoint answers 403 first and reloads the same public page once its
                    // standard browser check finishes on its own. Only that completion is awaited; interactive
                    // challenges are never solved, and any other failure is reported unchanged.
                    if (response.Status != 403 ||
                        await response.HeaderValueAsync("cf-mitigated").WaitAsync(token) != "challenge")
                        throw new HttpRequestException("The browser player-page request failed.", null, (HttpStatusCode)response.Status);
                    deadline.CancelAfter(TimeSpan.FromSeconds(40));
                    verificationPaths.Add(url.AbsolutePath);
                    verificationPhase = true;
                    int start;
                    lock (navigations) start = navigations.Count;
                    var landed = await AwaitCheckpointAsync([url.AbsolutePath], start);
                    verificationPhase = false;
                    if (landed is null)
                        throw new FormatException("The website placed a Cloudflare verification in front of this public page, and the check did not complete on its own in the isolated anonymous browser. Interactive challenges are never solved; no statistics were inferred.");
                    response = landed;
                    checkpoint = "The website's standard Cloudflare browser check completed on its own before the public page loaded; no CAPTCHA was solved and no personal browser session was used. ";
                }

                async Task<IResponse?> AwaitCheckpointAsync(string[] paths, int fromIndex)
                {
                    // Non-interactive clearance, when a website grants it to this honestly declared automated
                    // browser, arrives within seconds. Verification that demands interaction is never solved.
                    var limit = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                    while (DateTimeOffset.UtcNow < limit)
                    {
                        lock (navigations)
                        {
                            for (var i = navigations.Count - 1; i >= fromIndex; i--)
                            {
                                var candidate = navigations[i];
                                if (candidate.Status is >= 200 and < 300 &&
                                    Uri.TryCreate(candidate.Url, UriKind.Absolute, out var landedUrl) &&
                                    landedUrl.GetLeftPart(UriPartial.Authority) == url.GetLeftPart(UriPartial.Authority) &&
                                    paths.Contains(landedUrl.AbsolutePath))
                                    return candidate;
                            }
                        }
                        await Task.Delay(250, token);
                    }
                    return null;
                }
                string? originalPartialScope = null;
                if (url.Host != "cstracker.gg" && statisticsUrl is null)
                {
                    var initialHtml = await response.TextAsync().WaitAsync(token);
                    if (initialHtml.Contains("scope-card__body", StringComparison.Ordinal) &&
                        WebsiteDataParser.CsstScopeIfPresent(initialHtml, url.Segments.Last().Trim('/')) is { } initialScope)
                    {
                        if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var documentUrl) ||
                            !Sources.IsSamePlayerPage(url, documentUrl))
                            throw new FormatException("The original browser response has an unverified player URL; statistics were discarded.");
                        if (initialScope is { Kd: not null, Kpr: not null })
                            return new RenderedPage(initialHtml, checkpoint +
                                "Identity-verified Scope panel read from the original page response, before page scripts could change it.");
                        originalPartialScope = initialHtml;
                    }
                }
                try
                {
                    await page.WaitForFunctionAsync("""
                    args => {
                        const text = document.body?.innerText || '';
                        const unavailable = /access denied|verify you are human|captcha|profile (?:is )?private|private profile|player not found|sign in to view|just a moment|performing security verification|verifying you are not a bot|enable javascript and cookies|403 forbidden/i.test(document.title + '\n' + text);
                        if (!unavailable && (args.host === 'csst.at' || document.querySelector('.scope-card__body'))) {
                            const panels = document.querySelectorAll(".scope-card__body .scope-grid[aria-label='Performance']");
                            return document.readyState !== 'loading' && panels.length === 1 &&
                                ['K/D', 'KPR'].every(label => {
                                    const fields = [...panels[0].querySelectorAll('.scope-stat')]
                                        .filter(field => field.querySelector('dt')?.textContent.trim() === label);
                                    if (fields.length !== 1) return false;
                                    const values = fields[0].querySelectorAll('dd > .text-white');
                                    return values.length === 1 && /^\d+(?:\.\d+)?$/.test(values[0].textContent.trim());
                                });
                        }
                        return unavailable || (document.readyState !== 'loading' &&
                            (/(?:K\/D(?: Ratio)?|KD Ratio|HS%|Headshot[s]?\s*%|Win Rate|K\/R|KPR)\s*:?\s*\d/i.test(text) ||
                             /\d+(?:\.\d+)?\s*%?\s*(?:K\/D|HS%|Win Rate|K\/R|KPR)/i.test(text) ||
                             (args.host === 'cswatch.gg' && !!document.querySelector('main h1') && text.includes(args.id)) ||
                             (args.host === 'steamcommunity.com' && !!document.querySelector('.profile_header, .profile_private_info'))));
                    }
                    """, new { host = url.Host, id = url.Segments.Last().Trim('/') },
                        new() { Timeout = url.Host == "cstracker.gg" ? 30_000 : 15_000 }).WaitAsync(token);
                }
                catch (TimeoutException) when (url.Host is "csstats.gg" or "csst.at" || originalPartialScope is not null)
                {
                    var hasProfile = await page.EvaluateAsync<bool>("""
                        id => !!document.querySelector('h1') &&
                            [...document.querySelectorAll('a[href]')].some(a =>
                                a.href.replace(/\/$/, '') === 'https://steamcommunity.com/profiles/' + id)
                        """, url.Segments.Last().Trim('/')).WaitAsync(token);
                    if (!hasProfile && originalPartialScope is null) throw;
                    restriction = "The performance-summary readiness limit was reached; verified public profile content was returned for provider-specific parsing. " + restriction;
                }
                if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUrl) || !Sources.IsSamePlayerPage(url, finalUrl))
                    throw new FormatException("The rendered page changed to an unverified URL; statistics were discarded.");
                var unavailable = await page.EvaluateAsync<string?>("""
                    args => {
                        const text = (document.title || '') + '\n' + (document.body?.innerText || '');
                        if (/access denied|verify you are human|captcha|just a moment|performing security verification|verifying you are not a bot|enable javascript and cookies|403 forbidden/i.test(text)) return 'an access/verification page';
                        const publicAccount = args.host === 'cswatch.gg' &&
                            !!document.querySelector('main h1') &&
                            [...document.querySelectorAll('main button')].some(button =>
                                button.textContent.trim() === args.id || button.getAttribute('aria-label') === 'Copy Steam ID ' + args.id) &&
                            !!document.querySelector('main .v-tile');
                        if (/profile (?:is )?private|private profile|sign in to view/i.test(text) && !publicAccount)
                            return 'a private/login-restricted profile';
                        if (/player not found|profile not found/i.test(text)) return 'a missing player profile';
                        return null;
                    }
                    """, new { host = url.Host, id = url.Segments.Last().Trim('/') }).WaitAsync(token);
                if (unavailable is not null)
                    throw new FormatException($"The rendered page is {unavailable}. Any displayed statistics were discarded.");
                var html = await page.EvaluateAsync<string>("""
                    args => {
                        if (!document.body) throw new Error('No rendered page body');
                        const bodyStyle = getComputedStyle(document.body);
                        if (bodyStyle.display === 'none' || bodyStyle.visibility !== 'visible' || bodyStyle.opacity === '0')
                            throw new Error('The rendered page body is hidden');
                        const matches = document.querySelector('#player-match-history-section');
                        const owners = document.querySelectorAll('#player-ranks-section[data-vaclive-steam-id]');
                        const projectMatches = !!matches && owners.length === 1 &&
                            owners[0].getAttribute('data-vaclive-steam-id') === args.id;
                        const linkedSections = new Set([...document.querySelectorAll('a[href^="#player-"]')]
                            .map(link => link.getAttribute('href').slice(1)));
                        const roots = projectMatches
                            ? [matches, ...args.sections.filter(id => linkedSections.has(id) ||
                                id === 'player-profile-section' || id === 'player-stats-section')
                                .flatMap(id => [...document.querySelectorAll('#' + id)])]
                            : [document.body];
                        let html = '', nodes = 0;
                        for (const root of roots) {
                            const rootStyle = getComputedStyle(root);
                            if (rootStyle.display === 'none' || rootStyle.visibility !== 'visible' || rootStyle.opacity === '0') {
                                if (root === matches || !projectMatches)
                                    throw new Error('The requested statistics section is hidden');
                                continue;
                            }
                            const originals = [...root.querySelectorAll('*')];
                            nodes += originals.length;
                            if (nodes > 50000) throw new Error('Rendered DOM exceeds node limit');
                            const copy = root.cloneNode(true);
                            const copies = [...copy.querySelectorAll('*')];
                            for (let i = originals.length - 1; i >= 0; i--) {
                                const node = originals[i], style = getComputedStyle(node);
                                if (node.matches('script,style,noscript,template,iframe,object,embed,[hidden],[aria-hidden=true]') ||
                                    (projectMatches && node.matches('svg,[data-histogram-chart]')) ||
                                    style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' ||
                                    style.opacity === '0' || style.contentVisibility === 'hidden')
                                    copies[i].remove();
                            }
                            html += copy.outerHTML;
                        }
                        if (projectMatches) {
                            const identity = document.createElement('section');
                            identity.id = 'player-ranks-section';
                            identity.setAttribute('data-vaclive-steam-id', owners[0].getAttribute('data-vaclive-steam-id'));
                            const heading = document.querySelector('main h1');
                            const headerContent = heading?.parentElement?.parentElement;
                            const credential = headerContent?.querySelector('[data-header-credentials]');
                            const profile = headerContent?.querySelector('[data-demo-profile]');
                            const medal = document.querySelector(
                                'img[data-demo-profile-medal][title^="Displayed: "][alt$=" Service Medal"]');
                            const rankData = owners[0].querySelector('script[data-player-json="ranks"]');
                            const matchmaking = credential?.querySelector('img[alt^="Matchmaking rank "]');
                            const profileLevels = profile
                                ? [...profile.querySelectorAll('[title^="CS2 profile level "]')]
                                : [];
                            const profileLevel = profileLevels.length === 1 ? profileLevels[0] : null;
                            if (matchmaking)
                                identity.setAttribute('data-matchmaking-rank', matchmaking.getAttribute('alt'));
                            if (profileLevel)
                                identity.setAttribute('data-profile-level', profileLevel.getAttribute('title'));
                            if (medal) identity.setAttribute('data-displayed-medal', medal.getAttribute('alt'));
                            for (const node of [credential, profile, medal, rankData])
                                if (node) identity.appendChild(node.cloneNode(true));
                            html += identity.outerHTML;
                        }
                        if (html.length > args.max) throw new Error('Rendered text exceeds size limit');
                        return html;
                    }
                    """, new { max = PageParser.MaxCharacters, id = url.Segments.Last().Trim('/'),
                        sections = WebsiteDataParser.CsTrackerSectionIds }).WaitAsync(token);
                if (originalPartialScope is not null &&
                    (!html.Contains("scope-card__body", StringComparison.Ordinal) ||
                     WebsiteDataParser.CsstScopeIfPresent(html, url.Segments.Last().Trim('/')) is not { Kd: not null, Kpr: not null }))
                    return new RenderedPage(originalPartialScope, checkpoint +
                        "A partial identity-verified Scope panel was retained from the original page response after waiting for the complete panel. " + restriction);
                PublicHtmlResponse? statistics = null;
                if (statisticsUrl is not null)
                {
                    WebsiteDataParser.CsStatsContext(html, PlayerId.Parse(url.Segments.Last().Trim('/')));
                    if (!await permits(statisticsUrl, token))
                        statistics = new(0, null, "The statistics response is disallowed by robots.txt; no additional request was made.");
                    else
                    {
                        statistics = await RequestStatisticsAsync();
                        if (statistics is { Status: 403, Html: null, Error: null })
                            statistics = await CompleteWebsiteRetryAsync(statistics);
                    }

                    async Task<PublicHtmlResponse> RequestStatisticsAsync()
                    {
                        string? fetchError = null;
                        if (statisticsRequest is null)
                            fetchError = await page.EvaluateAsync<string?>("""
                                async url => {
                                    try {
                                        await fetch(url, {method: 'GET', credentials: 'same-origin', redirect: 'error', cache: 'no-store'});
                                        return null;
                                    } catch (error) {
                                        return 'The browser statistics request failed or was blocked: ' + error.message;
                                    }
                                }
                                """, statisticsUrl.AbsoluteUri).WaitAsync(token);
                        if (fetchError is not null) return new(0, null, fetchError);
                        IResponse? received;
                        try { received = await statisticsResponse.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                        catch (TimeoutException) { received = null; }
                        if (received is null)
                            return new(0, null, "The browser statistics request was blocked, failed, or did not return a response; no retry was attempted.");
                        if (!received.Ok) return new(received.Status, null);
                        var length = await received.HeaderValueAsync("content-length").WaitAsync(token);
                        if (long.TryParse(length, out var bytes) && bytes > PageParser.MaxCharacters)
                            return new(received.Status, null, "The statistics response exceeds the 2 MB limit.");
                        try
                        {
                            var body = await received.BodyAsync().WaitAsync(token);
                            return body.Length > PageParser.MaxCharacters
                                ? new(received.Status, null, "The statistics response exceeds the 2 MB limit.")
                                : new(received.Status, System.Text.Encoding.UTF8.GetString(body));
                        }
                        catch (PlaywrightException)
                        {
                            return new(received.Status, null, "The browser could not read the statistics response body; no data was inferred.");
                        }
                    }

                    // The website's own statistics error page offers a "Try again" link to the same route with
                    // cf_return=1, where its standard Cloudflare browser check runs and returns the public data.
                    // This follows that published route in the same disposable anonymous session. Interactive
                    // challenges are never solved; if the check does not finish on its own, the refusal is kept.
                    async Task<PublicHtmlResponse> CompleteWebsiteRetryAsync(PublicHtmlResponse refused)
                    {
                        var retry = new UriBuilder(statisticsUrl) { Query = "cf_return=1" }.Uri;
                        if (!await permits(retry, token))
                            return refused with { Error = "HTTP 403 with a verification checkpoint. The website's own statistics retry route is disallowed by robots.txt, so the refusal was kept." };
                        deadline.CancelAfter(TimeSpan.FromSeconds(40));
                        verificationPaths.Add(statisticsUrl.AbsolutePath);
                        verificationPaths.Add(url.AbsolutePath);
                        verificationPhase = true;
                        try
                        {
                            int start;
                            lock (navigations) start = navigations.Count;
                            await page.GotoAsync(retry.AbsoluteUri, new() { WaitUntil = WaitUntilState.Commit, Timeout = 20_000 }).WaitAsync(token);
                            var landed = await AwaitCheckpointAsync([statisticsUrl.AbsolutePath, url.AbsolutePath], start);
                            if (landed is null)
                                return refused with { Error = "The website requires a Cloudflare verification that did not complete on its own in the isolated anonymous browser. Interactive challenges are never solved and no personal browser session is used." };
                            const string note = "The first statistics response was refused with a Cloudflare checkpoint, so the website's own public retry route was opened in the same isolated anonymous session, where the standard browser check completed on its own. No CAPTCHA was solved and no personal browser cookies were imported.";
                            if (new Uri(landed.Url).AbsolutePath == statisticsUrl.AbsolutePath)
                            {
                                string body;
                                try { body = await landed.TextAsync().WaitAsync(token); }
                                catch (PlaywrightException)
                                {
                                    return refused with { Error = "The verified statistics response body could not be read; no data was inferred." };
                                }
                                return body.Length > PageParser.MaxCharacters
                                    ? new(landed.Status, null, "The statistics response exceeds the 2 MB limit.")
                                    : new(landed.Status, body, Note: note);
                            }
                            statisticsRequest = null;
                            statisticsResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            allowedStatisticsAttempts = 2;
                            var repeated = await RequestStatisticsAsync();
                            return repeated.Html is null ? repeated : repeated with { Note = note };
                        }
                        catch (PlaywrightException ex)
                        {
                            return refused with { Error = $"The website's verification retry navigation failed: {ex.Message.Split('\n')[0].Trim()} No interactive challenge was solved." };
                        }
                    }
                }
                return new RenderedPage(html, checkpoint + (restriction ?? ""), statistics);
            }
            finally
            {
                var interrupted = token.IsCancellationRequested;
                deadline.Cancel();
                if (interrupted)
                {
                    await _context.CloseAsync();
                    _context = null;
                }
                else
                {
                    if (!page.IsClosed) await page.CloseAsync();
                    await _context.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.Wait });
                }
            }
        }
        catch (TimeoutException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new FormatException("Browser loading timed out before a recognizable rendered summary was ready. No statistics were inferred.", ex);
        }
        catch (PlaywrightException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = ex.Message.Split('\n')[0].Trim();
            throw new FormatException($"Browser navigation or extraction failed: {detail} No access-control bypass was attempted.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        PlaywrightException? closeError = null;
        try
        {
            if (_context is not null) await _context.CloseAsync();
        }
        catch (PlaywrightException ex) { closeError = ex; }
        finally { _playwright?.Dispose(); }
        for (var attempt = 0; Directory.Exists(SessionDirectory); attempt++)
        {
            try { Directory.Delete(SessionDirectory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                    throw new IOException($"Could not remove temporary browser data at {SessionDirectory}. Close Chromium and remove this session folder manually.", ex);
                await Task.Delay(150);
            }
        }
        if (closeError is not null)
            throw new IOException("Chromium did not close normally. Temporary browser data was removed, but the check could not finish normally.", closeError);
    }
}
