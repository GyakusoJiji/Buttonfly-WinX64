using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ButtonFly.Core;

public sealed record ImportResult(List<MenuItem> Items, List<string> Warnings);

public static class MenuImporter
{
    private sealed class Node
    {
        public string Name = "";
        public string Color = "#4059BF";
        public string[]? Os;
        public string? Command;
        public string? Nbox;
        public List<Node>? Children;
        public int Line;
    }
    public static ImportResult Parse(string source)
    {
        var root = new Node { Children = [] };
        var stack = new Stack<Node>(); stack.Push(root);
        Node? current = null;
        int number = 0;
        InvalidDataException Error(string message) => new($"Line {number}: {message}");
        foreach (string raw in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            number++;
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '"')
            {
                var label = new StringBuilder(); int i = 1;
                for (; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\')
                    {
                        if (++i >= line.Length) throw Error("String is not closed.");
                        label.Append(line[i] switch { 'n' => '\n', 't' => '\t', _ => line[i] });
                    }
                    else label.Append(line[i]);
                }
                if (i >= line.Length) throw Error("String is not closed.");
                if (line[(i + 1)..].Trim().Length != 0) throw Error("Unexpected text follows the name.");
                current = new Node { Name = label.ToString(), Color = stack.Peek().Color, Line = number };
                stack.Peek().Children!.Add(current);
            }
            else if (line == "{")
            {
                if (current is null || current.Command is not null || current.Nbox is not null || current.Children is not null) throw Error("The child-menu declaration is invalid.");
                if (stack.Count > 32) throw Error("Menus can be nested up to 32 levels.");
                current.Children = []; stack.Push(current); current = null;
            }
            else if (line == "}")
            {
                if (stack.Count == 1) throw Error("No matching '{'.");
                current = stack.Pop();
            }
            else
            {
                if (current is null) throw Error("A quoted name is required.");
                var directive = Regex.Match(line, @"^(color|os|nbox)\s+(.*)$");
                if (directive.Success && current.Command is null && current.Children is null)
                {
                    var value = directive.Groups[2].Value;
                    switch (directive.Groups[1].Value)
                    {
                        case "color":
                            var parts = Regex.Split(value, @"[\s,]+");
                            var rgb = parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
                            if (rgb.Length != 3 || rgb.Any(v => !double.IsFinite(v) || v < 0 || v > 1)) throw Error("Use three color values from 0 to 1.");
                            current.Color = "#" + string.Concat(rgb.Select(v => ((int)Math.Round(v * 255)).ToString("X2")));
                            break;
                        case "os": current.Os = Regex.Split(value, @"[\s,]+"); break;
                        case "nbox":
                            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw Error("The nbox URL is invalid.");
                            current.Nbox = value; break;
                    }
                }
                else
                {
                    if (current.Command is not null || current.Children is not null || current.Nbox is not null) throw Error("Launch settings are duplicated.");
                    current.Command = line;
                }
            }
        }
        if (stack.Count != 1) throw Error("A closing '}' is missing.");
        var warnings = new List<string>();
        List<MenuItem> ConvertNodes(List<Node> nodes)
        {
            var result = new List<MenuItem>();
            foreach (var n in nodes)
            {
                if (n.Os is not null && !n.Os.Contains("win32")) { warnings.Add($"Line {n.Line}: {n.Name.Replace('\n', ' ')} — skipped because it is not for Windows"); continue; }
                if (n.Nbox is not null) { warnings.Add($"Line {n.Line}: {n.Name} — skipped because Nbox is not supported"); continue; }
                if (n.Children is null && n.Command is null) { warnings.Add($"Line {n.Line}: {n.Name} — skipped because it has no launch settings"); continue; }
                result.Add(new MenuItem { Name = n.Name, Color = n.Color, Children = n.Children is null ? null : ConvertNodes(n.Children),
                    Action = n.Command is null ? null : new() { Kind = ActionKind.Command, Target = n.Command, Encoding = "cp932" } });
            }
            return result;
        }
        var items = ConvertNodes(root.Children!);
        ConfigJson.Validate(new Configuration { Items = items });
        return new(items, warnings);
    }
    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && quoted) { i++; continue; }
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }
}
