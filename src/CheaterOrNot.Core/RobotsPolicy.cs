using System.Text.RegularExpressions;
using System.Globalization;

namespace CheaterOrNot.Core;

public static class RobotsPolicy
{
    public static bool Allows(string text, string path) => Evaluate(text, path).Allowed;

    public static (bool Allowed, TimeSpan Delay) Evaluate(string text, string path)
    {
        var groups = new List<(List<string> Agents, List<(bool Allow, string Path)> Rules, double Delay)>();
        var agents = new List<string>();
        var rules = new List<(bool, string)>();
        var hasDirectives = false;
        var delay = 0d;
        void Save()
        {
            if (agents.Count > 0) groups.Add((agents, rules, delay));
            agents = [];
            rules = [];
            hasDirectives = false;
            delay = 0;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (hasDirectives) Save();
                agents.Add(value.ToLowerInvariant());
            }
            else if (agents.Count > 0)
            {
                hasDirectives = true;
                if (key is "allow" or "disallow" && value.Length > 0) rules.Add((key == "allow", value));
                if (key == "crawl-delay")
                    delay = double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
                        double.IsFinite(seconds) && seconds >= 0 ? Math.Max(delay, seconds) : double.PositiveInfinity;
            }
        }
        Save();
        var specific = groups.Where(g => g.Agents.Contains("cheaterornot")).ToArray();
        var selected = specific.Length > 0 ? specific : groups.Where(g => g.Agents.Contains("*")).ToArray();
        var maximumDelay = selected.Length > 0 ? selected.Max(g => g.Delay) : 0;
        if (maximumDelay > 30) return (false, TimeSpan.Zero);
        var wait = TimeSpan.FromSeconds(maximumDelay);
        var applicable = selected.SelectMany(g => g.Rules).Where(r =>
        {
            var end = r.Path.EndsWith('$');
            var pattern = "^" + Regex.Escape(end ? r.Path[..^1] : r.Path).Replace("\\*", ".*") + (end ? "$" : "");
            return Regex.IsMatch(path, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }).ToArray();
        if (applicable.Length == 0) return (true, wait);
        var longest = applicable.Max(r => r.Path.Replace("*", "").TrimEnd('$').Length);
        return (applicable.Any(r => r.Allow && r.Path.Replace("*", "").TrimEnd('$').Length == longest), wait);
    }
}
