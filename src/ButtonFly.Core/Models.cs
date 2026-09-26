using System.Text.Json;
using System.Text.Json.Serialization;

namespace ButtonFly.Core;

public enum ActionKind { Executable, Open, Command }
public enum CommandShell { Cmd, WindowsPowerShell, PowerShell7 }
public enum OutputMode { Capture, Console }
public enum IconKind { None, Application, File }

public sealed class MenuIcon
{
    public IconKind Kind { get; set; }
    // Empty for Application means use the item's launch target.
    public string Path { get; set; } = "";
}

public sealed class LaunchAction
{
    public ActionKind Kind { get; set; }
    public string Target { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public CommandShell Shell { get; set; }
    public OutputMode Output { get; set; }
    public string Encoding { get; set; } = "utf-8";
}

public sealed class MenuItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New menu";
    public string Color { get; set; } = "#596DBF";
    public MenuIcon Icon { get; set; } = new();
    public List<MenuItem>? Children { get; set; }
    public LaunchAction? Action { get; set; }
    [JsonIgnore] public bool IsFolder => Children is not null;
}

public sealed class Preferences
{
    public string Theme { get; set; } = "classic";
    public bool ReduceMotion { get; set; }
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Alt+B";
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 740;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class Configuration
{
    public int SchemaVersion { get; set; } = 1;
    public Preferences Preferences { get; set; } = new();
    public List<MenuItem> Items { get; set; } = [];
    public static Configuration CreateDefault() => new()
    {
        Items = [
            new() { Name = "Apps", Children = [
                new() { Name = "Notepad", Action = new() { Target = "notepad.exe" } },
                new() { Name = "Calculator", Action = new() { Kind = ActionKind.Open, Target = "calculator:" } }
            ] },
            new() { Name = "Folders", Children = [
                new() { Name = "Home", Action = new() { Kind = ActionKind.Open, Target = "%USERPROFILE%" } },
                new() { Name = "Documents", Action = new() { Kind = ActionKind.Open, Target = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) } }
            ] },
            new() { Name = "System", Children = [
                new() { Name = "Windows\nSettings", Action = new() { Kind = ActionKind.Open, Target = "ms-settings:" } },
                new() { Name = "Version", Action = new() { Kind = ActionKind.Command, Target = "ver", Encoding = "cp932" } },
                new() { Name = "Processes", Action = new() { Kind = ActionKind.Command, Shell = CommandShell.WindowsPowerShell, Target = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 | Format-Table -AutoSize" } }
            ] },
            new() { Name = "Web", Color = "#267DAD", Action = new() { Kind = ActionKind.Open, Target = "https://www.microsoft.com/" } },
            new() { Name = "Network", Color = "#329C8C", Action = new() { Kind = ActionKind.Command, Target = "ipconfig", Encoding = "cp932" } }
        ]
    };
}

public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Serialize(Configuration config) => JsonSerializer.Serialize(config, Options);
    public static Configuration Parse(string json)
    {
        var config = JsonSerializer.Deserialize<Configuration>(json, Options) ?? throw new InvalidDataException("The settings file is empty.");
        Validate(config);
        return config;
    }
    public static Configuration Clone(Configuration config) => Parse(Serialize(config));
    public static void Validate(Configuration config)
    {
        if (config.SchemaVersion != 1) throw new InvalidDataException("The settings version is not supported.");
        if (config.Preferences is null || config.Items is null) throw new InvalidDataException("The settings file is missing required values.");
        var p = config.Preferences;
        if (p.Theme is not ("classic" or "modern")) throw new InvalidDataException("The theme is invalid.");
        if (string.IsNullOrWhiteSpace(p.Hotkey)) throw new InvalidDataException("Enter a hotkey.");
        if (!double.IsFinite(p.Width) || !double.IsFinite(p.Height) || p.Width < 640 || p.Height < 480 ||
            p.Left is double x && !double.IsFinite(x) || p.Top is double y && !double.IsFinite(y))
            throw new InvalidDataException("The window position or size is invalid.");
        var ids = new HashSet<Guid>();
        void Walk(IEnumerable<MenuItem> items, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Menus can be nested up to 32 levels.");
            foreach (var item in items)
            {
                if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id)) throw new InvalidDataException("A menu ID is missing or duplicated.");
                if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256) throw new InvalidDataException("Names must be 1–256 characters.");
                if (item.Color is null || !System.Text.RegularExpressions.Regex.IsMatch(item.Color, "^#[0-9a-fA-F]{6}$")) throw new InvalidDataException($"The color for '{item.Name}' must use #RRGGBB.");
                if (item.Icon is null || !Enum.IsDefined(item.Icon.Kind) || item.Icon.Path is null || item.Icon.Path.Contains('\0'))
                    throw new InvalidDataException($"The icon settings for '{item.Name}' are invalid.");
                if (item.Icon.Kind == IconKind.File && (string.IsNullOrWhiteSpace(item.Icon.Path) || !item.Icon.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"The icon for '{item.Name}' must be an .ico file.");
                if ((item.Children is null) == (item.Action is null)) throw new InvalidDataException($"'{item.Name}' must have either child items or a launch action.");
                if (item.Children is not null) Walk(item.Children, depth + 1);
                if (item.Action is { } a)
                {
                    if (string.IsNullOrWhiteSpace(a.Target)) throw new InvalidDataException($"The launch target for '{item.Name}' is empty.");
                    if (!Enum.IsDefined(a.Kind) || !Enum.IsDefined(a.Shell) || !Enum.IsDefined(a.Output)) throw new InvalidDataException("The launch type is invalid.");
                    if (a.Arguments is null || a.Arguments.Any(v => v is null || v.Contains('\0')) || a.Target.Contains('\0')) throw new InvalidDataException("The arguments or target are invalid.");
                    if (a.WorkingDirectory is null || a.Encoding is not ("utf-8" or "cp932")) throw new InvalidDataException("The working folder or encoding is invalid.");
                }
            }
        }
        Walk(config.Items, 0);
    }
    public static MenuItem? Find(IEnumerable<MenuItem> items, Guid id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return item;
            if (item.Children is { } children && Find(children, id) is { } found) return found;
        }
        return null;
    }
}
