using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CheaterOrNot.Core;

public static class PageParser
{
    public const int MaxCharacters = 2_000_000;

    public static string VisibleText(string html)
    {
        using var document = VisibleDocument(html);
        NormalizeSummaryLayout(document);
        foreach (var node in document.QuerySelectorAll("br")) node.Replace(document.CreateTextNode("\n"));
        foreach (var node in document.QuerySelectorAll("td,th")) node.AppendChild(document.CreateTextNode("\t"));
        foreach (var node in document.QuerySelectorAll("p,div,tr,li,h1,h2,h3,section,article,dt,dd"))
            node.AppendChild(document.CreateTextNode("\n"));
        return (document.Body?.TextContent ?? "").Replace('\u00a0', ' ');
    }

    public static string PublicTabText(string html)
    {
        using var document = VisibleDocument(html);
        var publicSection = document.QuerySelector("section[id^='player-']");
        if (publicSection is not null)
        {
            foreach (var header in publicSection.QuerySelectorAll("header"))
            {
                var badge = header.QuerySelectorAll("*").FirstOrDefault(element =>
                    element.Children.Length == 0 && !string.IsNullOrWhiteSpace(element.TextContent));
                if (badge is not null && Regex.IsMatch(badge.TextContent.Trim(), @"^\d{2}$",
                    RegexOptions.None, TimeSpan.FromSeconds(1)))
                    badge.Remove();
            }
        }
        var mapGrid = document.QuerySelector("#player-maps-section div.grid");
        string OneLine(string text) => Regex.Replace(text, @"\s+", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        bool IsBlock(IElement element) => element.LocalName is
            "p" or "div" or "tr" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "section" or "article" or "header" or "footer" or "ul" or "ol" or "dl" or "dt" or "dd" or
            "table" or "a" or "button";
        string Render(INode node)
        {
            if (node is IText text) return OneLine(text.TextContent);
            if (node is not IElement element) return "";
            if (element.LocalName == "br") return "\n";
            if (element.LocalName == "table")
            {
                var rows = element.QuerySelectorAll("tr").Where(row => row.Closest("table") == element).ToArray();
                var headers = rows.FirstOrDefault()?.Children;
                if (headers is { Length: > 0 } && headers.All(cell => cell.LocalName == "th") &&
                    rows.Skip(1).All(row => row.Children.Length == headers.Length))
                {
                    return string.Join("\n", rows.Skip(1).Select(row =>
                        string.Join(" | ", headers.Zip(row.Children, (header, cell) =>
                            $"{OneLine(Render(header))}: {OneLine(Render(cell))}"))));
                }
            }
            var children = element.Children;
            if (element == mapGrid)
                return string.Join("\n", children.Select(card => string.Join(" | ",
                    Render(card).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
            if (element.LocalName == "div" && children.Length == 2 &&
                children[0].ClassList.Contains("tabular-nums") && children[1].Children.Length == 0 &&
                Regex.IsMatch(children[0].TextContent.Trim(), @"^\d+(?:,\d{3})*(?:\.\d+)?(?:%|ms|s|\u00b0)?$",
                    RegexOptions.None, TimeSpan.FromSeconds(1)) &&
                Regex.IsMatch(children[1].TextContent.Trim(), @"^[A-Za-z][A-Za-z /&()-]*$",
                    RegexOptions.None, TimeSpan.FromSeconds(1)))
                return $"{OneLine(Render(children[1]))}: {OneLine(Render(children[0]))}";
            if (element.ClassList.Contains("items-baseline") && children.Length == 2)
                return $"{OneLine(Render(children[0]))}: {OneLine(Render(children[1]))}";
            if (element.ClassList.Contains("grid") && children.Length > 1 &&
                element.ChildNodes.OfType<IText>().All(text => string.IsNullOrWhiteSpace(text.TextContent)))
                return string.Join("\n", children.Select(Render).Where(content => content.Length > 0));
            var result = new StringBuilder();
            foreach (var child in element.ChildNodes)
            {
                var content = Render(child);
                if (content.Length == 0) continue;
                var block = child is IElement childElement && IsBlock(childElement);
                if (result.Length > 0 && result[^1] != '\n') result.Append(block ? '\n' : ' ');
                result.Append(content);
                if (block && result[^1] != '\n') result.Append('\n');
            }
            return result.ToString().Trim();
        }
        var output = document.Body is { } body ? Render(body) : "";
        if (publicSection?.Id == "player-kills-section")
            output = Regex.Replace(output,
                @"(\d+(?:\.\d+)?%)\s+([A-Za-z][A-Za-z -]*?)\s+([\d,]+\s*/\s*[\d,]+\s+kills)",
                match => $"{OneLine(match.Groups[2].Value)}: {match.Groups[1].Value} | {OneLine(match.Groups[3].Value)}",
                RegexOptions.None, TimeSpan.FromSeconds(1));
        if (publicSection?.Id == "player-clutches-section")
            output = Regex.Replace(output,
                @"(\d+v\d+)\s+([\d,]+\s*\u00b7\s*[\d,]+)\s+(\d+(?:\.\d+)?%)",
                match => $"{match.Groups[1].Value}: {OneLine(match.Groups[2].Value)} | {match.Groups[3].Value}",
                RegexOptions.None, TimeSpan.FromSeconds(1));
        return output;
    }

    private static IDocument VisibleDocument(string html)
    {
        CheckSize(html);
        var document = new HtmlParser().ParseDocument(html);
        foreach (var node in document.QuerySelectorAll("script,style,noscript,template,iframe,object,embed,[hidden],[aria-hidden=true]"))
            node.Remove();
        return document;
    }

    private static void NormalizeSummaryLayout(IDocument document)
    {
        const string labels = @"Matches(?: Played)?|Total Matches|Games Played|K/D(?: Ratio)?|KD(?: Ratio)?|Kill/Death Ratio|HS%|Headshots?(?: Percentage| Percent| %|%)|Win Rate|Winrate|Win%|K/R(?: Ratio)?|KPR|Kills Per Round";
        bool IsLabel(string text) => Regex.IsMatch(text.Trim(), $"^(?:{labels})\\s*:?$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        bool IsNumber(string text) => Regex.IsMatch(text.Trim(), @"^\d+(?:,\d{3})*(?:\.\d+)?\s*%?$", RegexOptions.None, TimeSpan.FromSeconds(1));
        foreach (var table in document.QuerySelectorAll("table"))
        {
            var rows = table.QuerySelectorAll("tr");
            if (rows.Length != 2) continue;
            var headers = rows[0].Children.Where(e => e.LocalName is "th" or "td").ToArray();
            var values = rows[1].Children.Where(e => e.LocalName is "th" or "td").ToArray();
            if (headers.Length < 2 || headers.Length != values.Length ||
                !headers.All(e => IsLabel(e.TextContent)) || !values.All(e => IsNumber(e.TextContent))) continue;
            var summary = document.CreateElement("div");
            summary.TextContent = string.Join("\n", headers.Zip(values, (h, v) => $"{h.TextContent.Trim().TrimEnd(':')}: {v.TextContent.Trim()}"));
            table.Replace(summary);
        }
        foreach (var container in document.QuerySelectorAll("div,li,section"))
        {
            var children = container.Children;
            if (children.Length != 2) continue;
            var first = children[0].TextContent.Trim();
            var second = children[1].TextContent.Trim();
            if (IsLabel(first) && IsNumber(second)) container.TextContent = $"{first.TrimEnd(':')}: {second}";
            else if (IsNumber(first) && IsLabel(second)) container.TextContent = $"{second.TrimEnd(':')}: {first}";
        }
    }

    public static IReadOnlyList<Uri> DiscoverLinks(string html, Uri origin, string id)
    {
        CheckSize(html);
        using var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelectorAll("a[href]")
            .Select(a => Uri.TryCreate(origin, a.GetAttribute("href"), out var uri) ? uri : null)
            .Where(uri => uri is not null && Sources.PageHosts.Contains(uri.Host) &&
                uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                uri.AbsolutePath.TrimEnd('/') == Sources.Profile(uri.Host, id).AbsolutePath)
            .Select(uri => Sources.Profile(uri!.Host, id))
            .Distinct().Take(3).ToArray();
    }

    public static PlayerStats Parse(string text)
    {
        CheckSize(text);
        double? Read(string labels, bool percent = false)
        {
            var pattern = $@"(?im)^[ \t]*(?:{labels})[ \t]*(?::|\t)?[ \t]*(?:\r?\n[ \t]*)?([^\r\n]+?)[ \t]*$";
            var matches = Regex.Matches(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            var values = new List<double>();
            foreach (Match match in matches)
            {
                var raw = match.Groups[1].Value.Trim();
                if (raw is "-" or "N/A" or "n/a" or "unknown") continue;
                if (!Regex.IsMatch(raw, @"^\d+(?:,\d{3})*(?:\.\d+)?[ \t]*%?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                    throw new FormatException("A recognized metric has an invalid value. The source must provide one nonnegative numeric value per labeled line.");
                if (!percent && raw.EndsWith('%')) throw new FormatException("A ratio or match count cannot have a percent sign.");
                values.Add(double.Parse(raw.TrimEnd('%').Trim().Replace(",", ""), CultureInfo.InvariantCulture));
            }
            var distinct = values.Distinct().ToArray();
            if (distinct.Length > 1) throw new FormatException("Conflicting stats found. The page mixes multiple summaries or match-table values; no unambiguous aggregate was obtained.");
            return distinct.Length == 1 ? distinct[0] : null;
        }
        var count = Read(@"Matches(?: Played)?|Total Matches|Games Played");
        if (count.HasValue && (count > int.MaxValue || count != Math.Truncate(count.Value)))
            throw new FormatException("Match count must be a whole number.");
        var result = new PlayerStats(count.HasValue ? (int)count.Value : null,
            Read(@"K/D(?: Ratio)?|KD(?: Ratio)?|Kill/Death Ratio"),
            Read(@"HS%|Headshot(?:s)?(?: Percentage| Percent| %|%)", true),
            Read(@"Win Rate|Winrate|Win%", true),
            Read(@"K/R(?: Ratio)?|KPR|Kills Per Round"));
        result.Validate();
        if (!result.HasMetrics) throw new FormatException("No recognized aggregate stats. The page did not provide an unambiguous supported performance summary.");
        return result;
    }

    private static void CheckSize(string text)
    {
        if (text.Length > MaxCharacters) throw new FormatException("Input exceeds the 2 MB text limit.");
    }
}
