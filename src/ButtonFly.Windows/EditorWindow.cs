using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ButtonFly.Core;
using MenuItem = ButtonFly.Core.MenuItem;

namespace ButtonFly.Windows;

internal sealed class EditorWindow : Window
{
    private readonly Configuration draft;
    private readonly Action<Configuration> save;
    private readonly TreeView tree = new();
    private readonly TextBox name = new() { Name = "ItemName" };
    private readonly TextBox color = new();
    private readonly ComboBox iconKind = new() { Name = "IconKind", ItemsSource = new[] { "None (title only)", "Application icon", ".ico file" } };
    private readonly TextBox iconPath = new() { Name = "IconPath" };
    private readonly Image iconPreview = new() { Name = "IconPreview", Width = 48, Height = 48, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock iconStatus = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock iconHint = Ui.Text("", 12, Ui.Muted);
    private readonly StackPanel iconFields = new();
    private readonly DispatcherTimer iconTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ComboBox kind = new() { ItemsSource = new[] { "Menu (folder)", "Executable", "Open with default app (file, folder, or URL)", "Command" } };
    private readonly TextBox target = new() { Name = "ActionTarget" };
    private readonly TextBox arguments = new() { AcceptsReturn = true, Height = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox working = new();
    private readonly ComboBox shell = new() { ItemsSource = new[] { "cmd", "Windows PowerShell", "PowerShell 7" } };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Show output in ButtonFly (non-interactive)", "Open console (interactive)" } };
    private readonly ComboBox encoding = new() { ItemsSource = new[] { "UTF-8", "CP932 (Japanese Windows commands)" } };
    private readonly CheckBox reduced = new() { Content = "Reduce motion" };
    private readonly CheckBox startup = new() { Content = "Start when I sign in to Windows" };
    private readonly TextBox hotkey = new();
    private readonly StackPanel actionFields = new();
    private readonly StackPanel commandFields = new();
    private readonly StackPanel argumentFields = new();
    private MenuItem? current;
    private bool refreshing;
    private bool loadingForm;
    public EditorWindow(Configuration config, Action<Configuration> save)
    {
        Ui.ConfigureWindow(this);
        draft = ConfigJson.Clone(config); this.save = save;
        Title = "ButtonFly — Menu & Settings"; Width = 980; Height = 680; MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20), Background = Ui.Background }; Content = root;
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Ui.Button("Cancel", Close));
        bottom.Children.Add(Ui.Button("Save", Save)); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var title = Ui.Text("Menu & Settings", 24); title.Margin = new Thickness(0, 0, 0, 16); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(grid);
        var left = new DockPanel(); grid.Children.Add(left);
        var tools = new WrapPanel();
        tools.Children.Add(Ui.Button("＋ App", () => Add(false)));
        tools.Children.Add(Ui.Button("＋ Menu", () => Add(true)));
        tools.Children.Add(Ui.Button("Delete", Delete));
        tools.Children.Add(Ui.Button("↑", () => Move(-1), "Move up")); tools.Children.Add(Ui.Button("↓", () => Move(1), "Move down"));
        tools.Children.Add(Ui.Button("←", Outdent, "Move out of parent menu")); tools.Children.Add(Ui.Button("→", Indent, "Move into preceding folder"));
        tools.Children.Add(Ui.Button("Import .menu", Import));
        DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools);
        var hint = Ui.Text("Drop files or folders here to add them to the selected menu or its current level.", 12, Ui.Muted); hint.Margin = new Thickness(5, 10, 5, 5); DockPanel.SetDock(hint, Dock.Bottom); left.Children.Add(hint);
        left.Children.Add(tree); tree.AllowDrop = true;
        tree.PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        tree.Drop += DropFiles;
        tree.SelectedItemChanged += (_, _) =>
        {
            if (refreshing) return;
            var next = (tree.SelectedItem as TreeViewItem)?.Tag as MenuItem;
            if (next == current) return;
            try { ApplyForm(); current = next; LoadForm(); }
            catch (Exception e) { Ui.Error(this, e); RefreshTree(current?.Id); }
        };
        var form = new StackPanel(); var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
        Grid Row(string label, UIElement value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var text = Ui.Text(label, 12, Ui.Muted); text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text); Grid.SetColumn(value, 1); row.Children.Add(value); form.Children.Add(row); return row;
        }
        void NestedRow(Panel parent, string label, UIElement value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var text = Ui.Text(label, 12, Ui.Muted); text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text); Grid.SetColumn(value, 1); row.Children.Add(value); parent.Children.Add(row);
        }
        Row("Button name", name);
        Row("Color #RRGGBB (Modern)", color);
        Row("Button icon", iconKind);
        form.Children.Add(iconFields);
        NestedRow(iconFields, "Icon file", iconPath);
        var iconTools = new WrapPanel { Margin = new Thickness(148, 0, 0, 2) };
        iconTools.Children.Add(Ui.Button("Choose icon…", BrowseIcon));
        iconTools.Children.Add(Ui.Button("Use launch target", () => { iconPath.Text = ""; iconKind.SelectedIndex = (int)ButtonFly.Core.IconKind.Application; QueueIconPreview(); }));
        iconFields.Children.Add(iconTools);
        var previewRow = new DockPanel { Margin = new Thickness(148, 2, 0, 6) };
        DockPanel.SetDock(iconPreview, Dock.Left); previewRow.Children.Add(iconPreview); previewRow.Children.Add(iconStatus); iconFields.Children.Add(previewRow);
        iconHint.Margin = new Thickness(148, 0, 0, 2); iconFields.Children.Insert(0, iconHint);
        Row("Item type", kind);
        form.Children.Add(actionFields);
        NestedRow(actionFields, "Target / URL / command", target);
        var browse = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(148, 0, 0, 2) };
        browse.Children.Add(Ui.Button("Choose file", BrowseFile)); browse.Children.Add(Ui.Button("Choose folder", BrowseFolder)); actionFields.Children.Add(browse);
        actionFields.Children.Add(argumentFields); NestedRow(argumentFields, "Arguments (one per line)", arguments);
        NestedRow(actionFields, "Working folder", working);
        actionFields.Children.Add(commandFields);
        NestedRow(commandFields, "Shell", shell); NestedRow(commandFields, "Output", mode); NestedRow(commandFields, "Output encoding", encoding);
        kind.SelectionChanged += (_, _) => { UpdateFields(); QueueIconPreview(); };
        iconKind.SelectionChanged += (_, _) => { UpdateFields(); QueueIconPreview(); };
        iconPath.TextChanged += (_, _) => QueueIconPreview();
        target.TextChanged += (_, _) => QueueIconPreview();
        working.TextChanged += (_, _) => QueueIconPreview();
        iconTimer.Tick += (_, _) => { iconTimer.Stop(); UpdateIconPreview(); };
        Closed += (_, _) => iconTimer.Stop();
        form.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        form.Children.Add(Ui.Text("Application settings", 19));
        form.Children.Add(reduced); form.Children.Add(startup); Row("Show/hide hotkey", hotkey);
        reduced.IsChecked = draft.Preferences.ReduceMotion; startup.IsChecked = draft.Preferences.StartWithWindows; hotkey.Text = draft.Preferences.Hotkey;
        current = draft.Items.FirstOrDefault(); RefreshTree(current?.Id); LoadForm();
    }
    private void UpdateFields()
    {
        actionFields.Visibility = kind.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        argumentFields.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        commandFields.Visibility = kind.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        name.IsEnabled = color.IsEnabled = kind.IsEnabled = current is not null;
        iconKind.IsEnabled = iconFields.IsEnabled = current is not null;
        iconFields.Visibility = iconKind.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        iconHint.Text = iconKind.SelectedIndex == (int)ButtonFly.Core.IconKind.Application
            ? "Leave empty to use the launch target. You can also choose another .exe or .lnk file."
            : "Choose the .ico file to display.";
    }
    private void LoadForm()
    {
        loadingForm = true;
        name.Text = current?.Name ?? ""; color.Text = current?.Color ?? "#596DBF";
        iconKind.SelectedIndex = (int)(current?.Icon.Kind ?? ButtonFly.Core.IconKind.None); iconPath.Text = current?.Icon.Path ?? "";
        kind.SelectedIndex = current is null || current.IsFolder ? 0 : (int)current.Action!.Kind + 1;
        var a = current?.Action ?? new(); target.Text = a.Target; arguments.Text = string.Join("\n", a.Arguments); working.Text = a.WorkingDirectory;
        shell.SelectedIndex = (int)a.Shell; mode.SelectedIndex = (int)a.Output; encoding.SelectedIndex = a.Encoding == "cp932" ? 1 : 0;
        loadingForm = false; UpdateFields(); iconTimer.Stop(); UpdateIconPreview();
    }
    private MenuIcon FormIcon() => new() { Kind = (IconKind)Math.Max(0, iconKind.SelectedIndex), Path = iconPath.Text.Trim() };
    private void QueueIconPreview()
    {
        if (loadingForm) return;
        iconTimer.Stop(); iconTimer.Start();
    }
    private void UpdateIconPreview()
    {
        var item = new MenuItem { Icon = FormIcon(), Action = kind.SelectedIndex > 0 ? new() { Kind = (ActionKind)(kind.SelectedIndex - 1), Target = target.Text.Trim(), WorkingDirectory = working.Text.Trim() } : null };
        iconPreview.Source = MenuIcons.Load(item, out var error);
        iconStatus.Text = error is not null ? error + " The title will be shown by itself." : iconPreview.Source is not null ? "This icon will appear on the button." : "";
    }
    private void ApplyForm()
    {
        if (current is null) return;
        if (kind.SelectedIndex > 0 && current.Children is { Count: > 0 }) throw new InvalidDataException("A menu with child items cannot be changed into an app. Move its children first.");
        var candidate = new MenuItem { Id = current.Id, Name = name.Text.Trim(), Color = color.Text.Trim(), Icon = FormIcon(), Children = kind.SelectedIndex == 0 ? current.Children ?? [] : null,
            Action = kind.SelectedIndex == 0 ? null : new() {
                Kind = (ActionKind)(kind.SelectedIndex - 1), Target = target.Text.Trim(),
                Arguments = arguments.Text.Length == 0 ? [] : arguments.Text.Replace("\r\n", "\n").Split('\n').ToList(),
                WorkingDirectory = working.Text.Trim(), Shell = (CommandShell)Math.Max(0, shell.SelectedIndex), Output = (OutputMode)Math.Max(0, mode.SelectedIndex), Encoding = encoding.SelectedIndex == 1 ? "cp932" : "utf-8"
            } };
        ConfigJson.Validate(new Configuration { Items = [candidate] });
        current.Name = candidate.Name; current.Color = candidate.Color; current.Icon = candidate.Icon; current.Children = candidate.Children; current.Action = candidate.Action;
        void UpdateHeader(ItemsControl parent)
        {
            foreach (TreeViewItem node in parent.Items)
            {
                if (node.Tag == current) node.Header = Header(current);
                else UpdateHeader(node);
            }
        }
        UpdateHeader(tree);
    }
    private static object Header(MenuItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (MenuIcons.Load(item) is { } icon)
            row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = (item.IsFolder ? "▸  " : "•  ") + item.Name.Replace('\n', ' '), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    private void RefreshTree(Guid? select)
    {
        refreshing = true; tree.Items.Clear();
        void AddNodes(ItemsControl parent, List<MenuItem> items)
        {
            foreach (var item in items)
            {
                var node = new TreeViewItem { Header = Header(item), Tag = item, IsExpanded = true };
                parent.Items.Add(node); if (item.Children is not null) AddNodes(node, item.Children);
                if (item.Id == select) node.IsSelected = true;
            }
        }
        AddNodes(tree, draft.Items); refreshing = false;
    }
    private (List<MenuItem> List, MenuItem? Parent) Location(MenuItem item)
    {
        (List<MenuItem>, MenuItem?)? Find(List<MenuItem> items, MenuItem? parent)
        {
            if (items.Contains(item)) return (items, parent);
            foreach (var child in items) if (child.Children is not null && Find(child.Children, child) is { } location) return location;
            return null;
        }
        return Find(draft.Items, null) ?? throw new InvalidOperationException("The item was not found.");
    }
    private List<MenuItem> Destination() => current?.Children ?? (current is null ? draft.Items : Location(current).List);
    private void Change(Action action)
    {
        try { ApplyForm(); action(); RefreshTree(current?.Id); LoadForm(); }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void Add(bool folder) => Change(() =>
    {
        var item = new MenuItem { Name = folder ? "New menu" : "New app", Children = folder ? [] : null, Action = folder ? null : new() { Target = "notepad.exe" } };
        Destination().Add(item); current = item;
    });
    private void Delete()
    {
        if (current is null) return;
        if (MessageBox.Show(this, $"Delete '{current.Name}' and its child items? Cancel restores them until you save.", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var location = Location(current); location.List.Remove(current); current = location.Parent ?? draft.Items.FirstOrDefault(); RefreshTree(current?.Id); LoadForm();
    }
    private void Move(int delta) => Change(() =>
    {
        if (current is null) return; var list = Location(current).List; int from = list.IndexOf(current), to = from + delta;
        if (to < 0 || to >= list.Count) return; list.RemoveAt(from); list.Insert(to, current);
    });
    private void Indent() => Change(() =>
    {
        if (current is null) return; var list = Location(current).List; int index = list.IndexOf(current);
        if (index <= 0 || list[index - 1].Children is not { } children) throw new InvalidOperationException("Move an item into the preceding menu folder.");
        list.Remove(current); children.Add(current);
    });
    private void Outdent() => Change(() =>
    {
        if (current is null) return; var (list, parent) = Location(current); if (parent is null) return;
        var outer = Location(parent).List; list.Remove(current); outer.Insert(outer.IndexOf(parent) + 1, current);
    });
    private void BrowseFile()
    {
        var dialog = new OpenFileDialog { Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }
    private void BrowseIcon()
    {
        bool application = iconKind.SelectedIndex == (int)ButtonFly.Core.IconKind.Application;
        var dialog = new OpenFileDialog { Title = application ? "Choose an app icon source" : "Choose an icon file", Filter = application ? "Apps and shortcuts|*.exe;*.lnk|All files|*.*" : "Icon files|*.ico", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var candidate = new MenuItem { Icon = new() { Kind = application ? ButtonFly.Core.IconKind.Application : ButtonFly.Core.IconKind.File, Path = dialog.FileName } };
        if (MenuIcons.Load(candidate, out var error) is null) { Ui.Error(this, new InvalidDataException(error)); return; }
        iconPath.Text = dialog.FileName; iconTimer.Stop(); UpdateIconPreview();
    }
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) { kind.SelectedIndex = 2; target.Text = dialog.FolderName; }
    }
    private void DropFiles(object sender, DragEventArgs e) => Change(() =>
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var destination = Destination();
        foreach (var file in files)
        {
            var item = new MenuItem { Name = Path.GetFileNameWithoutExtension(file), Action = new() { Kind = Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ActionKind.Executable : ActionKind.Open, Target = file } };
            destination.Add(item); current = item;
        }
    });
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "ButtonFly menus|*.menu;*.buttonfly|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        Change(() =>
        {
            var result = MenuImporter.Parse(File.ReadAllText(dialog.FileName));
            string message = $"Add {result.Items.Count} top-level item(s). Commands are imported as cmd commands. Review them before saving.\n\n" + string.Join("\n", result.Warnings.Take(20));
            if (result.Warnings.Count > 20) message += $"\n{result.Warnings.Count - 20} more";
            if (MessageBox.Show(this, message, "Import result", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            Destination().AddRange(result.Items); current = result.Items.FirstOrDefault() ?? current;
        });
    }
    private void Save()
    {
        try
        {
            ApplyForm(); draft.Preferences.ReduceMotion = reduced.IsChecked == true; draft.Preferences.StartWithWindows = startup.IsChecked == true; draft.Preferences.Hotkey = hotkey.Text.Trim();
            ConfigJson.Validate(draft); save(draft); DialogResult = true;
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
}
