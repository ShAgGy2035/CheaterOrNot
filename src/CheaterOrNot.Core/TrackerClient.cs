using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheaterOrNot.Core;

public sealed class TrackerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HashSet<string> _deferredReferenceHosts = new(StringComparer.OrdinalIgnoreCase);
    public bool ProReferenceRefresh { get; init; }

    private void DeferReferenceHost(string host, HttpStatusCode? status)
    {
        if (ProReferenceRefresh && (status == HttpStatusCode.TooManyRequests ||
            (host is "open.faceit.com" or "api-public.cs-prod.leetify.com" or "api.steampowered.com" &&
             status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)))
            _deferredReferenceHosts.Add(host);
    }

    private void CheckReferenceHost(string host)
    {
        if (ProReferenceRefresh && _deferredReferenceHosts.Contains(host))
            throw new HttpRequestException("Source deferred for the rest of the pro refresh.", null, HttpStatusCode.TooManyRequests);
    }
    private const int MaxBytes = 2_000_000;
    private const int LeetifyHistoryMaxBytes = 16_000_000;

    public TrackerClient() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false
    }) { }

    public TrackerClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CheaterOrNot/1.0");
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    public async Task ValidateLeetifyKeyAsync(string key, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new FormatException("No Leetify API key is saved.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api-public.cs-prod.leetify.com/api-key/validate");
        request.Headers.Add("_leetify_key", key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new FormatException($"Leetify returned an unexpected HTTP {(int)response.StatusCode} for key validation.");
    }

    public async Task ValidateSteamKeyAsync(string key, string player, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new FormatException("No Steam API key is saved.");
        var id = PlayerId.Parse(player);
        using var doc = JsonDocument.Parse(await GetAsync(
            new Uri($"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={Uri.EscapeDataString(key)}&steamids={id}"), token));
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array ||
            players.GetArrayLength() != 1 || players[0].ValueKind != JsonValueKind.Object ||
            !players[0].TryGetProperty("SteamId", out var steamId) || steamId.ValueKind != JsonValueKind.String ||
            steamId.GetString() != id)
            throw new FormatException("Steam returned an unexpected player response.");
    }

    public async Task ValidateFaceitKeyAsync(string key, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new FormatException("No FACEIT API key is saved.");
        using var doc = JsonDocument.Parse(await GetAsync(new Uri("https://open.faceit.com/data/v4/games/cs2"), token, key));
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("game_id", out var gameId) || gameId.ValueKind != JsonValueKind.String ||
            gameId.GetString() != "cs2")
            throw new FormatException("FACEIT returned an unexpected game response.");
    }

    public async Task<IReadOnlyList<Evidence>> FetchAsync(string input, CancellationToken cancellationToken,
        bool leetifyAttributionReady = false, IPageRenderer? renderer = null, IProgress<FetchProgress>? progress = null,
        string? steamApiKey = null, string? leetifyApiKey = null, ScreeningRules? rules = null,
        string? faceitApiKey = null)
    {
        rules ??= new ScreeningRules();
        rules.Validate();
        var id = PlayerId.Parse(input);
        cancellationToken.ThrowIfCancellationRequested();
        var sources = ProReferenceRefresh
            ? Sources.PublicPages(id).Where(item => item.Url.Host is not ("steamcommunity.com" or "cswatch.gg")).ToArray()
            : Sources.PublicPages(id);
        var totalSources = sources.Count + (string.IsNullOrWhiteSpace(steamApiKey) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(leetifyApiKey) ? 0 : 1) + (string.IsNullOrWhiteSpace(faceitApiKey) ? 0 : 1);
        var evidence = new List<Evidence>();
        var completed = 0;
        void Report(string source, string status) => progress?.Report(new FetchProgress(completed, totalSources,
            source, status, evidence.Count(e => e.Stats is { HasMetrics: true })));
        async Task<Evidence> RetryAsync(string source, Func<Task<Evidence>> read)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var result = await read();
                    return attempt == 1 || result.Note is null
                        ? result
                        : result with { Note = result.Note + $" The source succeeded after {attempt} attempts." };
                }
                catch (Exception ex) when (IsRetryableSourceFailure(ex) && attempt < 3)
                {
                    last = ex;
                    Report(source, $"Retry {attempt}/3");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
            }
            throw new InvalidOperationException("Source retry attempts were exhausted.", last);
        }

        static bool IsRetryableSourceFailure(Exception ex)
        {
            if (ex is OperationCanceledException)
                return true;
            if (ex is HttpRequestException http)
                return http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int?)http.StatusCode >= 500;
            if (ex is JsonException or RegexMatchTimeoutException or IOException)
                return true;
            if (ex is not FormatException format)
                return false;
            var message = format.Message;
            return !message.Contains("private", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("missing player", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("disallowed", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("rejected", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("no eligible", StringComparison.OrdinalIgnoreCase);
        }

        var robotsByOrigin = new ConcurrentDictionary<string, Lazy<Task<string>>>(StringComparer.OrdinalIgnoreCase);
        var scheduleLock = new object();
        var nextRequests = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        async Task<string> ReadRobots(Uri origin)
        {
            string text;
            try { text = await GetAsync(new Uri(origin, "/robots.txt"), cancellationToken); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { text = ""; }
            lock (scheduleLock) nextRequests[origin.GetLeftPart(UriPartial.Authority)] = DateTimeOffset.UtcNow;
            return text;
        }
        async Task<bool> Permits(Uri target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (target.Scheme != "https" || !target.IsDefaultPort || target.UserInfo.Length != 0) return false;
            var origin = target.GetLeftPart(UriPartial.Authority);
            var read = robotsByOrigin.GetOrAdd(origin,
                key => new Lazy<Task<string>>(() => ReadRobots(new Uri(key))));
            var policy = RobotsPolicy.Evaluate(await read.Value.WaitAsync(token), target.PathAndQuery);
            if (!policy.Allowed) return false;
            if (policy.Delay > TimeSpan.Zero)
            {
                TimeSpan wait;
                lock (scheduleLock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var earliest = nextRequests.TryGetValue(origin, out var previous) ? previous + policy.Delay : now;
                    var due = earliest > now ? earliest : now;
                    nextRequests[origin] = due;
                    wait = due - now;
                }
                await Task.Delay(wait, token);
            }
            return true;
        }

        foreach (var (name, url) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = url.Host;
            var source = host == "leetify.com" ? "Data Provided by Leetify (public website)" : name + " public page";
            Report(source, "Checking");
            try
            {
                CheckReferenceHost(host);
                evidence.Add(await RetryAsync(source, ReadSource));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                evidence.Add(new Evidence(source, url, "Timed out",
                    Note: "The website did not respond within the request limit. No data was inferred."));
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException or RegexMatchTimeoutException)
            {
                var detail = ex switch
                {
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}: {StatusMessage(status)}",
                    HttpRequestException => "Network/TLS request failed. Check connectivity.",
                    JsonException => "Unexpected website JSON; data discarded.",
                    RegexMatchTimeoutException => "Parsing time limit exceeded; data discarded.",
                    _ => ex.Message
                };
                evidence.Add(new Evidence(source, url, "Unavailable", Note: detail));
            }
            completed++;
            Report(source, evidence[^1].Status);

            async Task<Evidence> ReadSource()
            {
                if (!await Permits(url, cancellationToken))
                    return new Evidence(source, url, "Not fetched",
                        Note: "robots.txt disallows retrieval or specifies an unsupported crawl delay (invalid or over 30 seconds). No page was downloaded.");
                if (host == "leetify.com")
                {
                    if (!leetifyAttributionReady)
                        return new Evidence(source, url, "Attribution setup required",
                            Note: "Load the Leetify attribution image before displaying website statistics. No website data was requested.");
                    async Task<string> ReadPublicData(string route)
                    {
                        var endpoint = new Uri($"https://api.cs-prod.leetify.com/api/profile/{id}/{route}");
                        if (!await Permits(endpoint, cancellationToken))
                            throw new FormatException("Leetify's website data route is disallowed by robots.txt or has an unsupported crawl delay.");
                        return await GetAsync(endpoint, cancellationToken);
                    }
                    using var metadata = JsonDocument.Parse(await ReadPublicData("meta"));
                    WebsiteDataParser.VerifyId(metadata.RootElement, id);
                    using var recent = JsonDocument.Parse(await ReadPublicData("recent-games/5v5"));
                    var sample = WebsiteDataParser.LeetifySample(metadata.RootElement, recent.RootElement, id);
                    return new Evidence(source, url, "Public website recent 5v5 data",
                        sample.Stats,
                        "Identity verified against profile metadata. Matches and ratios use the website's recent 5v5 overview (all 5v5 sources), not lifetime totals. Headshot kill percentage is not head accuracy. This adapter does not extract kills per round from this feed.",
                        Sections: sample.Sections.Count > 0 ? sample.Sections : null);
                }
                if (host == "steamcommunity.com")
                {
                    var export = new Uri(url.AbsoluteUri.TrimEnd('/') + "/?xml=1");
                    if (!await Permits(export, cancellationToken))
                        return new Evidence(source, url, "Not fetched", Note: "Steam's public XML route is disallowed by robots.txt.");
                    var steam = WebsiteDataParser.SteamContext(await GetAsync(export, cancellationToken), id);
                    return new Evidence(source, url, "Public account context", Note: steam.Note,
                        Cautions: steam.Cautions.Count > 0 ? steam.Cautions : null)
                        { DisplayName = steam.DisplayName, SmurfReviewContext = steam.SmurfReviewContext };
                }
                if (host == "cstracker.gg")
                {
                    var retrievedAt = DateTimeOffset.UtcNow;
                    async Task<string> ReadMatchPage(Uri page) => renderer is null
                        ? await GetAsync(page, cancellationToken)
                        : (await ReadRendered(page)).Html;
                    var pages = new List<string> { await ReadMatchPage(url) };
                    var progressionContext = WebsiteDataParser.CsTrackerProgressionContext(pages[0], id);
                    DateTimeOffset? dataUpdatedAt = null;
                    string? freshnessNote = null;
                    try { dataUpdatedAt = WebsiteDataParser.CsTrackerLastSync(pages[0], id); }
                    catch (FormatException ex)
                    {
                        freshnessNote = "Website data synchronization timestamp was not used: " + ex.Message;
                    }
                    IReadOnlyList<PublicSection> sections;
                    try { sections = WebsiteDataParser.CsTrackerSections(pages[0], id, rules); }
                    catch (Exception ex) when (ex is FormatException or JsonException)
                    {
                        sections = [new PublicSection("Additional public sections unavailable",
                            "Additional public sections could not be parsed: " + ex.Message)];
                    }
                    PlayerStats stats;
                    try { stats = WebsiteDataParser.CsTracker(pages, id); }
                    catch (FormatException ex)
                    {
                        return PartialHistory(null, $"The initial match history could not be parsed: {ex.Message}");
                    }
                    Uri? next;
                    try { next = WebsiteDataParser.CsTrackerOlderPage(pages[^1], id, pages.Count); }
                    catch (FormatException ex)
                    {
                        return PartialHistory(stats, $"Older-match pagination stopped: {ex.Message}");
                    }
                    var pageLimit = ProReferenceRefresh ? 10 : 25;
                    while (next is not null && pages.Count < pageLimit)
                    {
                        if (!await Permits(next, cancellationToken))
                            return WithFreshness(new Evidence(source, url, "Public match history (pagination restricted)", stats,
                                $"Read {pages.Count} public page(s). Older-match retrieval was disallowed by robots.txt. " + HistoryNote(), WithHistoryAnalysis(stats)));
                        try { pages.Add(await ReadMatchPage(next)); }
                        catch (HttpRequestException ex)
                        {
                            var failure = ex.StatusCode is { } code ? $"HTTP {(int)code}: {StatusMessage(code)}" : "Network/TLS request failed.";
                            return WithFreshness(new Evidence(source, url, "Public match history (partial retrieval)", stats,
                                $"Read {pages.Count} public page(s); the older-page request failed. {failure} " + HistoryNote(), WithHistoryAnalysis(stats)));
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            return WithFreshness(new Evidence(source, url, "Public match history (partial retrieval)", stats,
                                $"Read {pages.Count} public page(s); the older-page request timed out. " + HistoryNote(), WithHistoryAnalysis(stats)));
                        }
                        try { stats = WebsiteDataParser.CsTracker(pages, id); }
                        catch (FormatException ex)
                        {
                            return PartialHistory(stats, $"An older match page could not be parsed and was excluded: {ex.Message}");
                        }
                        try { next = WebsiteDataParser.CsTrackerOlderPage(pages[^1], id, pages.Count); }
                        catch (FormatException ex)
                        {
                            return PartialHistory(stats, $"Older-match pagination stopped: {ex.Message}");
                        }
                    }
                    sections = WithHistoryAnalysis(stats);
                    return WithFreshness(new Evidence(source, url, "Public match-history data", stats,
                        $"Read {pages.Count} public HTML page(s), following verified same-player older-match links. " +
                        (next is null ? "End of returned match history reached. " : $"Stopped at the {pageLimit}-page request limit. ") + HistoryNote(), sections));

                    string HistoryNote() => SampleNote();
                    IReadOnlyList<PublicSection> WithHistoryAnalysis(PlayerStats? validStats)
                    {
                        var result = sections.ToList();
                        try
                        {
                            result.AddRange(WebsiteDataParser.CsTrackerRecentWindows(pages, id, retrievedAt));
                        }
                        catch (FormatException ex)
                        {
                            result.Add(new PublicSection("Recent CSTracker trends unavailable",
                                $"The 7-day intervals through 63 days were not calculated: {ex.Message} Other verified statistics are retained."));
                        }
                        try
                        {
                            var oscillation = WebsiteDataParser.CsTrackerOscillation(pages, id, rules);
                            result.Add(oscillation ?? new PublicSection("Performance oscillation (not assessed)",
                                $"The retrieved history contains {validStats?.Matches?.ToString(CultureInfo.InvariantCulture) ?? "an unverified number of"} matches. At least {rules.OscillationMinimumBlocks * rules.OscillationBlockSize} matches in {rules.OscillationMinimumBlocks} complete {rules.OscillationBlockSize}-match blocks are required."));
                        }
                        catch (FormatException ex)
                        {
                            result.Add(new PublicSection("Performance oscillation unavailable",
                                $"Parsing failed: {ex.Message} Other verified statistics are retained."));
                        }
                        return result;
                    }
                    Evidence PartialHistory(PlayerStats? validStats, string reason) =>
                        WithFreshness(new(source, url, "Public match history (partial retrieval)", validStats, reason + " " + HistoryNote(),
                            [.. WithHistoryAnalysis(validStats), new PublicSection("Match history unavailable", reason +
                                " Other verified public sections are retained.")]));
                    Evidence WithFreshness(Evidence evidence) => evidence with
                    {
                        DataUpdatedAt = dataUpdatedAt,
                        Note = string.Join(" ", evidence.Note, freshnessNote).Trim(),
                        SmurfReviewContext = progressionContext
                    };
                }
                if (host == "csst.at" && renderer is not null)
                {
                    bool ScopePresent(string content)
                    {
                        try { return WebsiteDataParser.CsstScopeIfPresent(content, id) is not null; }
                        catch (FormatException) { return false; }
                    }
                    var rendered = await ReadRendered(url);
                    if (!ProReferenceRefresh && !ScopePresent(rendered.Html))
                    {
                        // A transient first-hit Cloudflare challenge or cache-miss shell can hide the server-rendered
                        // Scope panel. The browser session is now warm, so one same-session retry usually lands on the
                        // real page. No access control is bypassed; this just re-requests the same public page.
                        var retry = await ReadRendered(url);
                        if (ScopePresent(retry.Html))
                            rendered = retry with { Note = retry.Note + " The first navigation returned no Scope panel; this was recovered on one same-session retry." };
                    }
                    return ReadHtml(rendered.Html, "Browser-rendered page data", rendered.Note);
                }
                if (renderer is not null)
                {
                    var rendered = await ReadRendered(url);
                    return ReadHtml(rendered.Html, "Browser-rendered page data", rendered.Note);
                }
                var html = await GetAsync(url, cancellationToken);
                var links = host == "csst.at" ? PageParser.DiscoverLinks(html, url, id) : [];
                var note = links.Count > 0
                    ? $"Supported profile links discovered: {string.Join(", ", links)}. Each supported site is attempted separately."
                    : "";
                return ReadHtml(html, "Public page data", note);

                Evidence ReadHtml(string content, string status, string details)
                {
                    try
                    {
                        if (host == "cswatch.gg")
                        {
                            var account = WebsiteDataParser.CsWatchContext(content, id);
                            return new Evidence(source, url, "Public account context",
                                Note: details + " " + account.Note,
                                Cautions: account.Cautions.Count > 0 ? account.Cautions : null) { DisplayName = account.DisplayName };
                        }
                        IReadOnlyList<PublicSection> sections = [];
                        if (host == "csst.at")
                        {
                            try { sections = WebsiteDataParser.CsstScopeSections(content, id, rules); }
                            catch (FormatException ex)
                            {
                                sections = [new PublicSection("Opening duels unavailable",
                                    $"Parsing failed: {ex.Message} Only this section was discarded; other verified samples are retained.")];
                            }
                            var scope = WebsiteDataParser.CsstScopeSample(content, id);
                            if (scope.Stats is null) throw new FormatException("No usable csst.at Scope performance metrics were returned.");
                            sections = [.. sections, .. scope.Sections];
                            return new Evidence(source, url, status, scope.Stats, details + " " + SampleNote(),
                                Sections: sections.Count > 0 ? sections : null);
                        }
                        return new Evidence(source, url, status, ParsePerformance(content), details + " " + SampleNote(),
                            Sections: sections.Count > 0 ? sections : null);
                    }
                    catch (FormatException ex)
                    {
                        if (host == "csstats.gg")
                            return new Evidence(source, url, "Public profile context (performance unavailable)",
                                Note: details + " " + WebsiteDataParser.CsStatsContext(content, id) + " Performance parsing: " + ex.Message);
                        return new Evidence(source, url, renderer is null ? "No usable summary" : "No usable rendered summary",
                            Note: details + " " + ex.Message);
                    }
                }

                PlayerStats ParsePerformance(string html) => host switch
                {
                    "cstracker.gg" => WebsiteDataParser.CsTracker(html, id),
                    "csst.at" => WebsiteDataParser.CsstScope(html, id),
                    _ => PageParser.Parse(PageParser.VisibleText(html))
                };
                string SampleNote() => host switch
                {
                    "cstracker.gg" => "K/D pools raw kills and deaths from unique match rows across the retrieved pages; HS, win rate, and K/R are not part of this match-history sample. Profile totals, the recent panel, and gameplay tabs are separate samples.",
                    "csst.at" => "Uses only csst.at's Scope performance panel. The sample is the card's own published window (for example \"Last 15 matches\") when shown; otherwise the match count stays unknown. K/R is the displayed Scope value; this adapter does not extract the underlying kill and round counts for independent verification. This adapter does not extract headshot kill percentage or match win rate from this panel.",
                    _ => ""
                };

                async Task<RenderedPage> ReadRendered(Uri page)
                {
                    try { return await renderer!.ReadAsync(page, Permits, cancellationToken); }
                    catch (HttpRequestException ex)
                    {
                        DeferReferenceHost(page.Host, ex.StatusCode);
                        throw;
                    }
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(leetifyApiKey))
        {
            // Leetify's official Public CS API: free developer key, sent only as a request header to
            // api-public.cs-prod.leetify.com. Governed by Leetify's developer guidelines.
            const string leetifySource = "Leetify API (official, keyed)";
            var leetifyDisplay = new Uri("https://api-public.cs-prod.leetify.com/v3/profile");
            Report(leetifySource, "Checking");
            try
            {
                evidence.Add(await RetryAsync(leetifySource,
                    () => ReadLeetifyApiAsync(leetifySource, leetifyApiKey.Trim(), id, cancellationToken)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                evidence.Add(new Evidence(leetifySource, leetifyDisplay, "Timed out",
                    Note: "Leetify's official API did not respond within the request limit. No data was inferred."));
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
            {
                var detail = ex switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                        "HTTP 401: the Leetify API key was rejected. Check the key in the API Keys tab.",
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                        "HTTP 404: Leetify's official API did not return this profile. The cause is not confirmed; " +
                        "account registration, privacy or availability may differ from the public website. Match history was not requested.",
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}: {StatusMessage(status)}",
                    HttpRequestException => "Network/TLS request failed. Check connectivity.",
                    JsonException => "Unexpected Leetify API JSON; data discarded.",
                    _ => ex.Message
                };
                evidence.Add(new Evidence(leetifySource, leetifyDisplay, "Unavailable", Note: detail));
            }
            completed++;
            Report(leetifySource, evidence[^1].Status);
        }
        if (!string.IsNullOrWhiteSpace(steamApiKey))
        {
            // Valve's official keyed Web API: the sanctioned route, governed by its API terms rather
            // than robots.txt. The key is sent only to api.steampowered.com and never stored in
            // evidence, URLs, or notes.
            const string apiSource = "Steam Web API (official, keyed)";
            var apiDisplay = new Uri("https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/");
            Report(apiSource, "Checking");
            try
            {
                var steam = await RetryAsync(apiSource,
                    () => ReadSteamApiAsync(apiSource, steamApiKey.Trim(), id, cancellationToken, rules));
                if (!ProReferenceRefresh && !evidence.Any(item =>
                    item.Url.Host == "steamcommunity.com" && item.DisplayName is not null))
                {
                    var (name, status) = await ReadSteamDisplayNameAsync(steamApiKey.Trim(), id, cancellationToken);
                    steam = steam with { DisplayName = name, Note = steam.Note + " " + status };
                }
                evidence.Add(steam);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                evidence.Add(new Evidence(apiSource, apiDisplay, "Timed out",
                    Note: "Valve's Web API did not respond within the request limit. No data was inferred."));
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
            {
                var detail = ex switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                        "HTTP 403: the Steam Web API key was rejected. Check the key in the API Keys tab.",
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}: {StatusMessage(status)}",
                    HttpRequestException => "Network/TLS request failed. Check connectivity.",
                    JsonException => "Unexpected Valve API JSON; data discarded.",
                    _ => ex.Message
                };
                evidence.Add(new Evidence(apiSource, apiDisplay, "Unavailable", Note: detail));
            }
            completed++;
            Report(apiSource, evidence[^1].Status);
        }
        if (!string.IsNullOrWhiteSpace(faceitApiKey))
        {
            const string faceitSource = "FACEIT Data API (official, keyed)";
            var display = new Uri("https://open.faceit.com/data/v4/players");
            Report(faceitSource, "Checking");
            try
            {
                evidence.Add(await RetryAsync(faceitSource,
                    () => ReadFaceitApiAsync(faceitSource, display, faceitApiKey.Trim(), id, cancellationToken)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                evidence.Add(new Evidence(faceitSource, display, "Timed out", Note: "FACEIT did not respond within the request limit."));
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
            {
                var detail = ex switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                        "HTTP 401: FACEIT did not authorize the request. Check the API key.",
                    HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                        "HTTP 403: FACEIT refused the request. Check the API key and its permissions.",
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                        "FACEIT's CS2 player lookup by SteamID returned HTTP 404. No matching player was returned by this lookup; this does not establish that no FACEIT account exists. This was not a statistics request.",
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}: {StatusMessage(status)}",
                    HttpRequestException => "Network/TLS request failed. Check connectivity.",
                    JsonException => "Unexpected FACEIT JSON; data discarded.",
                    _ => ex.Message
                };
                evidence.Add(new Evidence(faceitSource, display, "Unavailable", Note: detail));
            }
            completed++;
            Report(faceitSource, evidence[^1].Status);
        }
        foreach (var (name, url, reason) in Sources.UnsupportedSources)
            evidence.Add(new Evidence(name, new Uri(url), "Unsupported automatic source",
                Note: reason + " No player page was requested and no statistics were inferred."));
        return evidence;
    }

    private async Task<Evidence> ReadFaceitApiAsync(string source, Uri display, string key, string id, CancellationToken token)
    {
        var (statsUrl, stats) = await FetchFaceitLifetimeAsync(id, key, token);
        if (stats is null)
            return new Evidence(source, statsUrl, "Official FACEIT account context",
                Note: "FACEIT account found and linked Steam identity verified. " +
                    "The CS2 lifetime statistics endpoint returned HTTP 404 for this verified FACEIT player. " +
                    "No lifetime statistics were returned; the reason is not established. " +
                    "This does not mean the account is missing, that it has zero matches, or that a screening check passed. " +
                    "Account context only; no gameplay metrics were assessed.");
        return new Evidence(source, display, "Official FACEIT CS2 lifetime context",
            Note: "Steam identity and FACEIT player/game identity verified. " + stats.Describe() +
                ". FACEIT's raw lifetime K/D Ratio field is omitted because it is not verified as total kills divided by total deaths" +
                ". FACEIT lifetime values are a separate sample from other sources and are shown as context only; " +
                "their exact aggregation and sample definitions have not been independently verified, so they do not affect screening.")
            { FaceitLifetime = stats };
    }

    internal async Task<(Uri StatsUrl, FaceitLifetimeStats? Stats)> FetchFaceitLifetimeAsync(string id, string key, CancellationToken token)
    {
        id = PlayerId.Parse(id);
        static bool TryUnique(JsonElement element, string name, out JsonElement value)
        {
            var matches = element.EnumerateObject().Where(property => property.Name == name).ToArray();
            if (matches.Length > 1) throw new FormatException($"FACEIT returned duplicate {name} fields.");
            value = matches.Length == 1 ? matches[0].Value : default;
            return matches.Length == 1;
        }

        using var profile = JsonDocument.Parse(await GetAsync(
            new Uri($"https://open.faceit.com/data/v4/players?game=cs2&game_player_id={id}"), token, key));
        var root = profile.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryUnique(root, "player_id", out var player) || player.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(player.GetString(), "D", out var playerId))
            throw new FormatException("FACEIT returned an invalid player identity.");
        var ids = new List<string>();
        void AddIdentity(JsonElement parent, string name)
        {
            if (!TryUnique(parent, name, out var value) || value.ValueKind == JsonValueKind.Null) return;
            if (value.ValueKind != JsonValueKind.String) throw new FormatException("FACEIT returned an invalid linked Steam identity.");
            if (!string.IsNullOrEmpty(value.GetString())) ids.Add(value.GetString()!);
        }
        AddIdentity(root, "steam_id_64");
        if (TryUnique(root, "games", out var games) && games.ValueKind == JsonValueKind.Object &&
            TryUnique(games, "cs2", out var cs2) && cs2.ValueKind == JsonValueKind.Object)
            AddIdentity(cs2, "game_player_id");
        if (ids.Count == 0 || ids.Any(value => value != id))
            throw new FormatException("FACEIT's linked Steam identity was missing or mismatched; data discarded.");

        var statsUrl = new Uri($"https://open.faceit.com/data/v4/players/{playerId:D}/stats/cs2");
        string statsJson;
        try
        {
            statsJson = await GetAsync(statsUrl, token, key);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (statsUrl, null);
        }
        using var stats = JsonDocument.Parse(statsJson);
        var data = stats.RootElement;
        if (data.ValueKind != JsonValueKind.Object ||
            !TryUnique(data, "player_id", out var statsPlayer) || statsPlayer.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(statsPlayer.GetString(), "D", out var returnedPlayerId) || returnedPlayerId != playerId ||
            !TryUnique(data, "game_id", out var game) || game.ValueKind != JsonValueKind.String || game.GetString() != "cs2" ||
            !TryUnique(data, "lifetime", out var lifetime) || lifetime.ValueKind != JsonValueKind.Object)
            throw new FormatException("FACEIT's CS2 statistics response did not match the verified player or contained no lifetime data.");

        double? Read(string field)
        {
            var values = lifetime.EnumerateObject().Where(property => property.Name == field).ToArray();
            if (values.Length > 1) throw new FormatException($"FACEIT returned duplicate {field} values.");
            if (values.Length == 0) return null;
            var raw = values[0].Value.ValueKind switch
            {
                JsonValueKind.String => values[0].Value.GetString(),
                JsonValueKind.Number => values[0].Value.GetRawText(),
                _ => null
            };
            if (!double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value < 0 ||
                field == "Win Rate %" && value > 100 ||
                field is "Matches" or "Wins" && value != Math.Truncate(value))
                throw new FormatException($"FACEIT returned an invalid {field} value.");
            return value;
        }
        var result = new FaceitLifetimeStats(Read("Matches"), Read("Wins"), Read("Win Rate %"),
            null, Read("Average K/D Ratio"));
        result.Validate();
        return (statsUrl, result);
    }

    /// <summary>
    /// Best-effort live check of the professional reference list used for scale context. Reads HLTV's
    /// robots.txt first and only proceeds when the stats pages are permitted. HLTV's bot verification
    /// routinely refuses honest automated requests. Returns an explicit diagnostic for Evidence;
    /// the numeric reference bands always use the embedded snapshot. Never affects any verdict.
    /// </summary>
    public async Task<string> FetchProBenchmarkAsync(CancellationToken token)
    {
        try
        {
            var robots = await GetAsync(new Uri("https://www.hltv.org/robots.txt"), token);
            if (!RobotsPolicy.Allows(robots, "/stats/players"))
                return "HLTV live reference list not requested: disallowed by robots.txt. Reference bands were not refreshed.";
            var html = await GetAsync(new Uri("https://www.hltv.org/stats/players"), token);
            var leader = Regex.Match(html,
                "href=\"/stats/players/\\d{1,8}/[a-z0-9-]{1,40}\"[^>]*>\\s*([A-Za-z0-9_ .'-]{1,30}?)\\s*</a>",
                RegexOptions.None, TimeSpan.FromSeconds(2));
            if (!leader.Success || leader.Groups[1].Value.Trim().Length == 0)
                return "HLTV live reference list unavailable: no recognized top-player entry was returned. Reference bands were not refreshed.";
            return $"live HLTV top-rated player list (retrieved {DateTimeOffset.UtcNow:u}); top-listed professional: " +
                $"{leader.Groups[1].Value.Trim()}. Band values shown are the app's dated snapshot";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            var failure = ex.StatusCode is { } code ? $"HTTP {(int)code}: {StatusMessage(code)}" : "Network/TLS request failed.";
            return $"HLTV live reference list unavailable: {failure} Reference bands were not refreshed.";
        }
        catch (OperationCanceledException)
        {
            return "HLTV live reference list unavailable: request timed out. Reference bands were not refreshed.";
        }
        catch (RegexMatchTimeoutException)
        {
            return "HLTV live reference list unavailable: response parsing timed out. Reference bands were not refreshed.";
        }
    }

    /// <summary>Per-weapon Valve statistic suffixes with display names and recoil-behaviour categories.</summary>
    private static readonly (string Key, string Display, string Category)[] SteamWeapons =
    [
        ("ak47", "AK-47", "rifle"), ("m4a1", "M4 family", "rifle"), ("galilar", "Galil AR", "rifle"),
        ("famas", "FAMAS", "rifle"), ("aug", "AUG", "rifle"), ("sg556", "SG 553", "rifle"),
        ("awp", "AWP", "sniper"), ("ssg08", "SSG 08", "sniper"), ("scar20", "SCAR-20", "sniper"), ("g3sg1", "G3SG1", "sniper"),
        ("mac10", "MAC-10", "SMG"), ("mp7", "MP7", "SMG"), ("mp9", "MP9", "SMG"), ("ump45", "UMP-45", "SMG"),
        ("p90", "P90", "SMG"), ("bizon", "PP-Bizon", "SMG"),
        ("deagle", "Desert Eagle", "pistol"), ("glock", "Glock-18", "pistol"), ("hkp2000", "P2000/USP family", "pistol"),
        ("p250", "P250", "pistol"), ("fiveseven", "Five-SeveN", "pistol"), ("tec9", "Tec-9", "pistol"),
        ("elite", "Dual Berettas", "pistol"),
        ("nova", "Nova", "shotgun"), ("xm1014", "XM1014", "shotgun"), ("mag7", "MAG-7", "shotgun"),
        ("sawedoff", "Sawed-Off", "shotgun"),
        ("m249", "M249", "machine gun"), ("negev", "Negev", "machine gun"),
    ];

    private async Task<Evidence> ReadLeetifyApiAsync(string source, string key, string id, CancellationToken token)
    {
        var display = new Uri("https://api-public.cs-prod.leetify.com/v3/profile");
        using var doc = JsonDocument.Parse(await GetAsync(
            new Uri($"https://api-public.cs-prod.leetify.com/v3/profile?steam64_id={id}"), token, leetifyKey: key));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("steam64_id", out var sid) || sid.GetString() != id)
            throw new FormatException("Leetify's API response did not match the requested SteamID; data discarded.");
        double? Number(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble() : null;
        var privacy = root.TryGetProperty("privacy_mode", out var pm) && pm.ValueKind == JsonValueKind.String
            ? pm.GetString() : null;
        var totalMatches = Number("total_matches");
        var winrate = Number("winrate");
        PlayerStats? stats = null;
        if (totalMatches is > 0 and <= int.MaxValue && totalMatches % 1 == 0 && winrate is >= 0 and <= 1)
        {
            stats = new PlayerStats((int)totalMatches.Value, WinRate: winrate.Value * 100);
            stats.Validate();
        }
        List<string>? cautions = null;
        if (root.TryGetProperty("bans", out var bans) && bans.ValueKind == JsonValueKind.Array)
        {
            foreach (var ban in bans.EnumerateArray())
            {
                var platform = ban.ValueKind == JsonValueKind.Object && ban.TryGetProperty("platform", out var p) &&
                    p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(platform))
                    throw new FormatException("Leetify returned a malformed ban entry; data discarded.");
                DateTimeOffset? since = ban.TryGetProperty("banned_since", out var b) &&
                    b.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(b.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date : null;
                (cautions ??= []).Add($"Leetify's official API records a {platform} ban" +
                    (since is null ? "" : $" since {since:yyyy-MM-dd}") +
                    ". A platform ban is that platform's history/context, not proof of current CS2 cheating.");
            }
        }
        List<PublicSection>? sections = null;
        if (root.TryGetProperty("stats", out var telemetry) && telemetry.ValueKind == JsonValueKind.Object)
        {
            double? Stat(string name) => telemetry.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetDouble() : null;
            static string R(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
            var parts = new List<string>();
            if (Stat("preaim") is { } preaim)
                parts.Add($"preaim {R(preaim)} deg");
            if (Stat("reaction_time_ms") is { } reaction)
                parts.Add($"reaction time {R(reaction)} ms");
            if (Stat("accuracy_head") is { } headAccuracy)
                parts.Add($"head accuracy {R(headAccuracy)}% (not headshot kill share)");
            if (Stat("spray_accuracy") is { } spray)
                parts.Add($"spray accuracy {R(spray)}%");
            if (Stat("accuracy_enemy_spotted") is { } spotted)
                parts.Add($"accuracy with enemy spotted {R(spotted)}%");
            if (parts.Count > 0)
                sections =
                [
                    new PublicSection("Aim telemetry (Leetify official API)",
                        string.Join(" | ", parts) +
                        "\nLeetify-computed aim telemetry, shown as context. No published warning cutoffs exist for " +
                        "these API values, so they are not scored."),
                ];
        }
        if (privacy == "public")
        {
            try
            {
                using var history = JsonDocument.Parse(await GetAsync(
                    new Uri($"https://api-public.cs-prod.leetify.com/v3/profile/matches?steam64_id={id}"), token,
                    leetifyKey: key, maxBytes: LeetifyHistoryMaxBytes));
                (sections ??= []).Add(WebsiteDataParser.LeetifyDatedHistory(history.RootElement, id, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                (sections ??= []).Add(new PublicSection("7-day and 15-day trends unavailable",
                    "Leetify match history timed out. Other profile data is retained."));
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException or RegexMatchTimeoutException)
            {
                var detail = ex switch
                {
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}: {StatusMessage(status)}",
                    HttpRequestException => "Network/TLS request failed.",
                    JsonException => "Unexpected match-history JSON.",
                    RegexMatchTimeoutException => "Timestamp parsing time limit exceeded.",
                    _ => ex.Message
                };
                (sections ??= []).Add(new PublicSection("7-day and 15-day trends unavailable",
                    $"{detail} Other profile data is retained."));
            }
        }
        else
            (sections ??= []).Add(new PublicSection("7-day and 15-day trends unavailable",
                "Leetify did not identify this profile as public; match history was not requested."));
        var note = "Identity verified against Leetify's official Public CS API (the key is sent only as a request header). " +
            (stats is null
                ? "No usable lifetime totals were returned"
                : $"Lifetime scope: {stats.Matches} tracked matches; win rate " +
                  $"{(winrate!.Value * 100).ToString("0.##", CultureInfo.InvariantCulture)}%") +
            (privacy is not null && privacy != "public"
                ? $". Profile privacy mode: {privacy}; Leetify withholds restricted fields and nothing was inferred."
                : ".") +
            " This lifetime sample is separate from the Leetify recent-matches website panel and is never added to it.";
        return new Evidence(source, display,
            stats is null ? "Official API profile context" : "Official API lifetime profile data",
            stats, note, sections, cautions)
            { SmurfReviewContext = WebsiteDataParser.LeetifyRankContext(root) };
    }

    private async Task<Evidence> ReadSteamApiAsync(string source, string key, string id, CancellationToken token, ScreeningRules rules)
    {
        // Display URL deliberately excludes the query string so the key can never appear in evidence.
        var display = new Uri("https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/");
        var escapedKey = Uri.EscapeDataString(key);
        using var bans = JsonDocument.Parse(await GetAsync(
            new Uri($"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={escapedKey}&steamids={id}"), token));
        if (bans.RootElement.ValueKind != JsonValueKind.Object ||
            !bans.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array ||
            players.GetArrayLength() != 1 ||
            !players[0].TryGetProperty("SteamId", out var steamId) || steamId.GetString() != id)
            throw new FormatException("Valve's ban response did not match the requested SteamID; data discarded.");
        var player = players[0];
        int Count(string name) => player.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
            p.TryGetInt32(out var v) && v >= 0 ? v : throw new FormatException($"Missing or invalid Valve ban field: {name}.");
        var vacBans = Count("NumberOfVACBans");
        var gameBans = Count("NumberOfGameBans");
        var daysSince = vacBans + gameBans > 0 ? Count("DaysSinceLastBan") : 0;
        var communityBanned = player.TryGetProperty("CommunityBanned", out var cb) && cb.ValueKind == JsonValueKind.True;
        List<string>? cautions = null;
        if (vacBans + gameBans > 0)
            cautions = [$"Valve's official record shows {gameBans} game ban(s) and {vacBans} VAC ban(s), most recent " +
                $"{daysSince} days ago. These account-wide counts can include bans in other games; " +
                "this report does not determine which games were involved. A historical ban is context, not proof of current CS2 cheating."];
        var note = $"Valve's official ban record (authoritative, works regardless of profile privacy): VAC bans {vacBans}, " +
            $"game bans {gameBans}" + (vacBans + gameBans > 0 ? $", days since last ban {daysSince}" : "") +
            (communityBanned ? ", community banned: yes" : "") +
            ". Counts can include bans in other games; this report does not identify the affected games.";

        PlayerStats? stats = null;
        List<PublicSection>? sections = null;
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync(
                new Uri($"https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v2/?appid=730&key={escapedKey}&steamid={id}"),
                token));
            if (!doc.RootElement.TryGetProperty("playerstats", out var ps) || ps.ValueKind != JsonValueKind.Object ||
                !ps.TryGetProperty("steamID", out var sid) || sid.GetString() != id ||
                !ps.TryGetProperty("stats", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new FormatException("Valve's statistics response did not match the requested SteamID; data discarded.");
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            var invalidNames = new HashSet<string>(StringComparer.Ordinal);
            var issues = new List<PublicSection>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var n) ||
                    n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 } statName)
                {
                    issues.Add(new PublicSection("Lifetime counter unavailable",
                        "Valve returned a malformed or unnamed counter; other valid counters are retained."));
                    continue;
                }
                if (invalidNames.Contains(statName) || values.ContainsKey(statName))
                {
                    invalidNames.Add(statName);
                    values.Remove(statName);
                    issues.Add(new PublicSection("Lifetime counter unavailable",
                        $"Valve returned duplicate entries for {statName}; this counter is not used. Other valid counters are retained."));
                    continue;
                }
                if (!item.TryGetProperty("value", out var v) ||
                    v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var statValue) || statValue < 0)
                {
                    invalidNames.Add(statName);
                    issues.Add(new PublicSection("Lifetime counter unavailable",
                        $"Valve returned an invalid value for {statName}; this counter is not used. Other valid counters are retained."));
                    continue;
                }
                values.Add(statName, statValue);
            }
            long? Stat(string name) => values.TryGetValue(name, out var v) ? v : null;
            var kills = Stat("total_kills");
            var deaths = Stat("total_deaths");
            var rounds = Stat("total_rounds_played");
            var headshots = Stat("total_kills_headshot");
            var won = Stat("total_matches_won");
            var playedMatches = Stat("total_matches_played");
            var seconds = Stat("total_time_played");
            if (kills is null || deaths is null or 0)
                issues.Add(new PublicSection("Lifetime K/D unavailable",
                    "A valid kill total and a positive death total are needed to calculate K/D. Other available metrics are retained."));
            if (headshots > kills)
            {
                issues.Add(new PublicSection("Lifetime headshot percentage unavailable",
                    "Valve's headshot kills exceed its total kills; this percentage cannot be calculated. Other available metrics are retained."));
                headshots = null;
            }
            else if (headshots is null || kills is null or 0)
                issues.Add(new PublicSection("Lifetime headshot percentage unavailable",
                    "Headshot kills and a positive kill total are needed to calculate this percentage. Other available metrics are retained."));
            if (won > playedMatches)
            {
                issues.Add(new PublicSection("Lifetime win rate unavailable",
                    "Valve's wins exceed its total matches; this percentage cannot be calculated. Other available metrics are retained."));
                won = null;
            }
            else if (won is null || playedMatches is null or 0)
                issues.Add(new PublicSection("Lifetime win rate unavailable",
                    "Wins and a positive match total are needed to calculate win rate. Other available metrics are retained."));
            if (kills is null || rounds is null or 0)
                issues.Add(new PublicSection("Lifetime K/R unavailable",
                    "A kill total and a positive round total are needed to calculate K/R. Other available metrics are retained."));
            stats = new PlayerStats(
                playedMatches is > 0 and <= int.MaxValue ? (int)playedMatches : null,
                kills is not null && deaths > 0 ? (double)kills.Value / deaths.Value : null,
                kills > 0 && headshots is not null ? 100.0 * headshots.Value / kills.Value : null,
                playedMatches > 0 && won is not null ? 100.0 * won.Value / playedMatches.Value : null)
            {
                CombatMatchCountUnverified = true,
                ContextKpr = kills is not null && rounds > 0 ? (double)kills.Value / rounds.Value : null,
                KprCounts = kills is not null && rounds is > 0 ? new KprCounters(kills.Value, rounds.Value, playedMatches) : null
            };
            stats.Validate();
            if (!stats.HasMetrics && stats.ContextKpr is null) stats = null;
            if (issues.Count > 0) sections = issues;
            var totals = new List<string>();
            if (kills is not null) totals.Add($"kills {kills}");
            if (deaths is not null) totals.Add($"deaths {deaths}");
            if (headshots is not null) totals.Add($"headshot kills {headshots}");
            if (rounds is not null) totals.Add($"rounds {rounds}");
            if (won is not null) totals.Add($"wins {won}");
            if (playedMatches is not null) totals.Add($"matches {playedMatches}");
            if (seconds is > 0) totals.Add($"time played {(seconds.Value / 3600.0).ToString("0", CultureInfo.InvariantCulture)} hours");
            note += " Lifetime totals from Valve: " + string.Join(", ", totals) +
                ". These totals are account-lifetime and can include CS:GO-era play; Valve does not date them, so treat " +
                "them as long-run context rather than current form. K/D divides kills by deaths; headshot percentage divides headshot kills by kills; " +
                "The lifetime kills/rounds ratio is context only, not assessed, and cannot trigger a K/R flag or count toward screening coverage. " +
                "Win rate divides wins by total matches, including any ties or other results counted in that total. " +
                "The API does not establish that the counters cover the same game modes or periods, so ratios derived from them may not be comparable to match-based metrics. " +
                "Ratios are calculated only when their required counters are available.";

            try
            {
                var qualifying = new List<(string Name, string Category, double Accuracy)>();
                foreach (var (statKey, weaponName, category) in SteamWeapons)
                {
                    var wKills = Stat($"total_kills_{statKey}");
                    var shots = Stat($"total_shots_{statKey}");
                    var hits = Stat($"total_hits_{statKey}");
                    if (wKills is null || shots is null || hits is null || wKills < rules.WeaponMinimumKills || shots < rules.WeaponMinimumShots)
                        continue;
                    if (hits > shots)
                        throw new FormatException($"Valve's hits exceed shots for {weaponName}; its accuracy is not comparable.");
                    qualifying.Add((weaponName, category, 100.0 * hits.Value / shots.Value));
                }
                if (qualifying.Count >= rules.WeaponMinimumCount && qualifying.Select(w => w.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= rules.WeaponMinimumCategories)
                {
                    var spread = qualifying.Max(w => w.Accuracy) - qualifying.Min(w => w.Accuracy);
                    (sections ??= []).Add(
                        new PublicSection(
                            $"Weapon accuracy variance (Valve lifetime, {qualifying.Count} weapons with {rules.WeaponMinimumKills:N0}+ kills and {rules.WeaponMinimumShots:N0}+ shots)",
                            string.Join(" | ", qualifying.Select(w =>
                                $"{w.Name} {w.Accuracy.ToString("0.#", CultureInfo.InvariantCulture)}%")) +
                            "\nComputed from Valve's official per-weapon shots and hits. " +
                            "Weapon usage and shot selection can affect the spread; it does not establish assistance.",
                            Checks: [new MetricCheck("Weapon accuracy spread", spread, rules.WeaponSpreadPercentThreshold, "%",
                                MetricComparison.Below)]));
                }
                else
                {
                    var categories = qualifying.Select(w => w.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    (sections ??= []).Add(new PublicSection("Weapon accuracy variance (Valve lifetime, not assessed)",
                        $"{qualifying.Count} qualifying weapons across {categories} categories. At least {rules.WeaponMinimumCount} weapons with {rules.WeaponMinimumKills:N0}+ kills and {rules.WeaponMinimumShots:N0}+ shots across {rules.WeaponMinimumCategories} categories are required."));
                }
            }
            catch (FormatException ex)
            {
                (sections ??= []).Add(new PublicSection("Weapon accuracy variance unavailable",
                    $"Parsing failed: {ex.Message} Only this section was discarded; other verified statistics are retained."));
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden or HttpStatusCode.InternalServerError)
        {
            var detail = ex.StatusCode switch
            {
                HttpStatusCode.BadRequest => "Steam rejected the statistics request; the reason is not established. Private game details or missing statistics are possible, but not confirmed.",
                HttpStatusCode.Unauthorized => "Steam did not authorize the statistics request; the reason is not established.",
                HttpStatusCode.Forbidden => "Steam refused access to the statistics request; the reason is not established.",
                _ => "Steam reported an internal server error; the reason is not established."
            };
            var failure = $"Steam GetUserStatsForGame returned HTTP {(int)ex.StatusCode!}. {detail}";
            sections = [new PublicSection("Lifetime game statistics unavailable", failure)];
            note += $" Lifetime game statistics were not available (HTTP {(int)ex.StatusCode!}); see the section below. Ban status above is authoritative regardless of privacy.";
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.StatusCode is { } code ? $"HTTP {(int)code}: {StatusMessage(code)}" : "Network/TLS request failed.";
            note += $" Lifetime game statistics were not available ({detail}). Ban status above remains available.";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            note += " Lifetime game statistics timed out. Ban status above remains available.";
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            stats = null;
            var detail = ex is FormatException ? ex.Message : "Unexpected Valve statistics JSON.";
            sections = [new PublicSection("Lifetime game statistics unavailable",
                $"Parsing failed: {detail} Ban status is retained separately.")];
            note += " Lifetime game statistics could not be parsed; see the section below. Ban status above remains available.";
        }
        string levelContext;
        try
        {
            using var level = JsonDocument.Parse(await GetAsync(
                new Uri($"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={escapedKey}&steamid={id}"), token));
            var responses = level.RootElement.ValueKind == JsonValueKind.Object
                ? level.RootElement.EnumerateObject().Where(p => p.Name == "response").ToArray() : [];
            var levels = responses.Length == 1 && responses[0].Value.ValueKind == JsonValueKind.Object
                ? responses[0].Value.EnumerateObject().Where(p => p.Name == "player_level").ToArray() : [];
            if (levels.Length != 1 || levels[0].Value.ValueKind != JsonValueKind.Number ||
                !levels[0].Value.TryGetInt32(out var value) || value < 0)
                throw new FormatException("Steam returned a missing, invalid or duplicate player level.");
            levelContext = $"Steam level: {value.ToString(CultureInfo.InvariantCulture)} (GetSteamLevel; community level, not CS skill).";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            levelContext = "Steam level: unknown (GetSteamLevel timed out).";
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or JsonException)
        {
            var detail = ex switch
            {
                HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
                HttpRequestException => "Network/TLS request failed",
                JsonException => "Unexpected Steam level JSON",
                _ => ex.Message
            };
            levelContext = $"Steam level: unknown (GetSteamLevel: {detail}). Other retrieved Steam data is retained.";
        }
        return new Evidence(source, display,
            stats is null ? "Official ban record retrieved" : stats.HasMetrics
                ? "Official ban record and lifetime statistics retrieved" : "Official ban record and lifetime context retrieved",
            stats, note, sections, cautions) { SmurfReviewContext = [levelContext] };
    }

    private async Task<(string? Name, string Status)> ReadSteamDisplayNameAsync(string key, string id, CancellationToken token)
    {
        static JsonElement Unique(JsonElement parent, string field)
        {
            if (parent.ValueKind != JsonValueKind.Object) return default;
            var matches = parent.EnumerateObject().Where(property => property.Name == field).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Value : default;
        }

        try
        {
            using var doc = JsonDocument.Parse(await GetAsync(new Uri(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={id}"), token));
            var players = Unique(Unique(doc.RootElement, "response"), "players");
            if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() != 1)
                return (null, "Steam display name unavailable: GetPlayerSummaries did not return exactly one profile.");
            var steamId = Unique(players[0], "steamid");
            if (steamId.ValueKind != JsonValueKind.String || steamId.GetString() != id)
                return (null, "Steam display name unavailable: GetPlayerSummaries did not verify the requested SteamID.");
            var persona = Unique(players[0], "personaname");
            var name = persona.ValueKind == JsonValueKind.String ? WebsiteDataParser.ValidDisplayName(persona.GetString()) : null;
            return name is null
                ? (null, "Steam display name unavailable: GetPlayerSummaries did not return a usable, unambiguous name.")
                : (name, "Steam display name retrieved via GetPlayerSummaries; SteamID matched.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (null, "Steam display name unavailable: GetPlayerSummaries timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or IOException)
        {
            return (null, ex is HttpRequestException { StatusCode: { } status }
                ? $"Steam display name unavailable: GetPlayerSummaries returned HTTP {(int)status}."
                : "Steam display name unavailable: GetPlayerSummaries request failed or returned invalid data.");
        }
    }

    private async Task<string> GetAsync(Uri uri, CancellationToken token, string? bearer = null, int maxBytes = MaxBytes,
        string? leetifyKey = null)
    {
        CheckReferenceHost(uri.Host);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (leetifyKey is not null)
            request.Headers.Add("_leetify_key", leetifyKey);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        DeferReferenceHost(uri.Host, response.StatusCode);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Redirects are not followed.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new FormatException($"Response exceeds the {maxBytes / 1_000_000} MB limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (bytes.Length + read > maxBytes)
                throw new FormatException($"Response exceeds the {maxBytes / 1_000_000} MB limit.");
            bytes.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string StatusMessage(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Forbidden => "Blocked or access not granted; no bypass attempted.",
        HttpStatusCode.Unauthorized => "The website requires authorization. Anonymous public retrieval cannot access it.",
        HttpStatusCode.NotFound => "Player or public page not available.",
        HttpStatusCode.TooManyRequests => "Rate limited. No automatic retry; try later.",
        HttpStatusCode.NetworkAuthenticationRequired => "The server or network gateway requires network authentication; no credentials or login were supplied.",
        _ when (int)code is >= 300 and < 400 => "Redirect declined because its destination has not been verified.",
        _ => "Website request failed; no data inferred."
    };

    public void Dispose() => _http.Dispose();
}
