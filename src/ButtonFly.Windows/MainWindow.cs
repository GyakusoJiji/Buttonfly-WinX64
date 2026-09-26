using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ButtonFly.Core;
using Forms = System.Windows.Forms;
using MenuItem = ButtonFly.Core.MenuItem;

namespace ButtonFly.Windows;

public sealed class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly LaunchService launcher = new();
    private readonly SceneView scene = new();
    private readonly TextBlock breadcrumb = Ui.Text("Home", 14);
    private readonly TextBlock status = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock empty = Ui.Text("This menu is empty.\nAdd an app from Menu & Settings.", 20);
    private readonly Button back;
    private readonly Button classic;
    private readonly Button modern;
    private readonly Forms.NotifyIcon tray;
    private readonly FileSystemWatcher watcher;
    private readonly DispatcherTimer reloadTimer;
    private readonly bool background;
    private Configuration config;
    private string? fingerprint;
    private string? hotkeyText;
    private string? initialWarning;
    private HotkeyRegistration? hotkey;
    private DesktopGestureRegistration? desktopGestures;
    private OutputWindow? output;
    private EditorWindow? editor;
    private bool exiting;
    private bool pendingReload;
    private DateTime lastLaunch;
    public MainWindow(bool background = false, string? dataDirectory = null)
    {
        Ui.ConfigureWindow(this);
        this.background = background;
        string directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ButtonFly");
        store = new SettingsStore(directory);
        config = store.Load(out initialWarning);
        Directory.CreateDirectory(directory);
        if (!File.Exists(store.FilePath)) store.Save(config, null);
        fingerprint = initialWarning is null ? store.Fingerprint() : null;
        Title = "ButtonFly"; Width = config.Preferences.Width; Height = config.Preferences.Height; MinWidth = 640; MinHeight = 480;
        if (config.Preferences.Left is double left && config.Preferences.Top is double top) { Left = left; Top = top; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel(); Content = root;
        var header = new DockPanel { Margin = new Thickness(24, 18, 20, 16) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        classic = Ui.Button("Classic", () => Theme("classic")); modern = Ui.Button("Modern", () => Theme("modern"));
        controls.Children.Add(classic); controls.Children.Add(modern); controls.Children.Add(Ui.Button("Menu & Settings", Edit));
        DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        var branding = new StackPanel(); branding.Children.Add(Ui.Text("BUTTONFLY", 23)); branding.Children.Add(Ui.Text("A little IRIX on your desktop.", 11, Ui.Muted)); header.Children.Add(branding);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var navigation = new DockPanel { Background = Ui.Panel, LastChildFill = true };
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 4, 12, 4) };
        back = Ui.Button("←", scene.Back, "Up one menu"); navButtons.Children.Add(back); navButtons.Children.Add(Ui.Button("⌂", scene.Home, "Home"));
        DockPanel.SetDock(navButtons, Dock.Left); navigation.Children.Add(navButtons); breadcrumb.Margin = new Thickness(0, 0, 16, 0); navigation.Children.Add(breadcrumb);
        DockPanel.SetDock(navigation, Dock.Top); root.Children.Add(navigation);
        var footer = new DockPanel { Margin = new Thickness(20, 9, 20, 9) };
        var outputButton = Ui.Button("Open output ↗", () => ShowOutput(null)); DockPanel.SetDock(outputButton, Dock.Right); footer.Children.Add(outputButton); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var stage = new Grid(); stage.Children.Add(scene); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.TextAlignment = TextAlignment.Center; empty.IsHitTestVisible = false; stage.Children.Add(empty); root.Children.Add(stage);
        scene.Activated += Launch; scene.NavigationChanged += UpdateNavigation; scene.HideRequested += Hide;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show ButtonFly", null, (_, _) => Dispatcher.Invoke(ShowLauncher));
        menu.Items.Add("Menu & Settings", null, (_, _) => Dispatcher.Invoke(() => { ShowLauncher(); Edit(); }));
        menu.Items.Add("Command output", null, (_, _) => Dispatcher.Invoke(() => ShowOutput(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        tray = new Forms.NotifyIcon { Text = "ButtonFly", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Toggle);
        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; reloadTimer.Tick += (_, _) => { reloadTimer.Stop(); Reload(); };
        watcher = new FileSystemWatcher(directory, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += QueueReload; watcher.Created += QueueReload; watcher.Renamed += QueueReload; watcher.Deleted += QueueReload; watcher.EnableRaisingEvents = true;
        SourceInitialized += (_, _) =>
        {
            hotkey = new HotkeyRegistration(this, Toggle);
            try { hotkey.Register(config.Preferences.Hotkey); hotkeyText = config.Preferences.Hotkey; desktopGestures = new DesktopGestureRegistration(ShowLauncher); desktopGestures.Configure(config.Preferences.DesktopGestures); }
            catch (Exception e) { initialWarning = (initialWarning is null ? "" : initialWarning + "\n") + e.Message; }
        };
        Loaded += (_, _) =>
        {
            EnsureVisible(); ApplyConfiguration(); scene.Focus();
            if (initialWarning is not null) { status.Text = initialWarning; Notify(initialWarning); }
            if (background && initialWarning is null) Hide();
        };
        Closing += OnClosing;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }
    private void Notify(string message)
    {
        tray.BalloonTipTitle = "ButtonFly"; tray.BalloonTipText = message.Length > 240 ? message[..240] : message; tray.ShowBalloonTip(5000);
    }
    public void ShowLauncher()
    {
        if (editor is { IsVisible: true }) { editor.Activate(); return; }
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureVisible(); Activate(); scene.Focus();
    }
    private void Toggle() { if (editor is { IsVisible: true }) { editor.Activate(); return; } if (IsVisible && IsActive) Hide(); else ShowLauncher(); }
    private void EnsureVisible()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds)) return;
        var rectangle = new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (!Forms.Screen.AllScreens.Any(s => { var intersection = System.Drawing.Rectangle.Intersect(s.WorkingArea, rectangle); return intersection.Width >= 100 && intersection.Height >= 80 && rectangle.Top >= s.WorkingArea.Top - 20; }))
        {
            var work = SystemParameters.WorkArea; Left = work.Left + 30; Top = work.Top + 30; Width = Math.Min(Width, work.Width - 60); Height = Math.Min(Height, work.Height - 60);
        }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(EnsureVisible);
    private void ApplyConfiguration()
    {
        scene.SetConfiguration(config);
        desktopGestures?.Configure(config.Preferences.DesktopGestures);
        classic.Background = config.Preferences.Theme == "classic" ? new SolidColorBrush(Color.FromRgb(108, 76, 156)) : Ui.Panel;
        modern.Background = config.Preferences.Theme == "modern" ? new SolidColorBrush(Color.FromRgb(64, 95, 145)) : Ui.Panel;
        status.Text = $"{config.Preferences.Hotkey}  Show/hide   ·   Arrow keys / Enter / Esc";
        UpdateNavigation();
    }
    private void UpdateNavigation()
    {
        breadcrumb.Text = scene.Breadcrumb; back.IsEnabled = scene.CanGoBack;
        empty.Visibility = scene.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Theme(string theme)
    {
        if (config.Preferences.Theme == theme || scene.IsTransitioning) return;
        try { var changed = ConfigJson.Clone(config); changed.Preferences.Theme = theme; SaveConfiguration(changed, fingerprint); }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void CaptureBounds(Configuration value)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.Left))
        {
            value.Preferences.Left = bounds.Left; value.Preferences.Top = bounds.Top;
            value.Preferences.Width = Math.Max(640, bounds.Width); value.Preferences.Height = Math.Max(480, bounds.Height);
        }
    }
    private void SaveConfiguration(Configuration next, string? expected)
    {
        CaptureBounds(next); ConfigJson.Validate(next);
        bool changeHotkey = hotkeyText != next.Preferences.Hotkey;
        bool changeStartup = config.Preferences.StartWithWindows != next.Preferences.StartWithWindows;
        string? previousHotkey = hotkeyText;
        try
        {
            if (changeHotkey) hotkey?.Register(next.Preferences.Hotkey);
            if (changeStartup) StartupRegistration.Set(next.Preferences.StartWithWindows);
            store.Save(next, expected);
        }
        catch
        {
            if (changeHotkey) { try { if (previousHotkey is null) hotkey?.Clear(); else hotkey?.Register(previousHotkey); } catch { } }
            if (changeStartup) { try { StartupRegistration.Set(config.Preferences.StartWithWindows); } catch { } }
            throw;
        }
        config = next; hotkeyText = next.Preferences.Hotkey; fingerprint = store.Fingerprint(); ApplyConfiguration();
    }
    private void Edit()
    {
        if (editor is not null) { editor.Activate(); return; }
        var expected = store.Fingerprint();
        editor = new EditorWindow(config, next => SaveConfiguration(next, expected)) { Owner = this };
        editor.ShowDialog(); editor = null;
        if (pendingReload) { pendingReload = false; Reload(); }
    }
    private void Launch(MenuItem item)
    {
        // Reject repeated activation in a single double-click, including across different buttons.
        if ((DateTime.UtcNow - lastLaunch).TotalMilliseconds < 400) return;
        lastLaunch = DateTime.UtcNow;
        try
        {
            var current = ConfigJson.Find(config.Items, item.Id) ?? throw new InvalidOperationException("The item was updated. Select it again.");
            var run = launcher.Launch(current);
            if (run is not null) ShowOutput(run); else Hide();
        }
        catch (Exception e) { status.Text = "Could not launch: " + e.Message; Ui.Error(this, e); }
    }
    private void ShowOutput(CommandRun? run)
    {
        if (output is null) { output = new OutputWindow(launcher); output.Closed += (_, _) => output = null; }
        if (run is not null) output.Select(run);
        output.Show(); if (output.WindowState == WindowState.Minimized) output.WindowState = WindowState.Normal; output.Activate();
    }
    private void QueueReload(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => { reloadTimer.Stop(); reloadTimer.Start(); });
    private void Reload()
    {
        if (editor is not null) { pendingReload = true; return; }
        try
        {
            var nextFingerprint = store.Fingerprint(); if (nextFingerprint == fingerprint) return;
            var next = ConfigJson.Parse(File.ReadAllText(store.FilePath));
            // External settings also go through the same integration validation, without rewriting the file.
            string? oldHotkey = hotkeyText;
            try
            {
                if (hotkeyText != next.Preferences.Hotkey) hotkey?.Register(next.Preferences.Hotkey);
                if (config.Preferences.StartWithWindows != next.Preferences.StartWithWindows) StartupRegistration.Set(next.Preferences.StartWithWindows);
            }
            catch
            {
                if (oldHotkey is null) hotkey?.Clear(); else hotkey?.Register(oldHotkey);
                throw;
            }
            config = next; hotkeyText = next.Preferences.Hotkey; fingerprint = nextFingerprint; ApplyConfiguration();
        }
        catch (Exception e) { status.Text = "Could not reload settings (keeping current menu): " + e.Message; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting) { e.Cancel = true; Hide(); return; }
        watcher.Dispose(); reloadTimer.Stop(); hotkey?.Dispose(); desktopGestures?.Dispose(); tray.Visible = false; tray.Dispose();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        scene.Dispose(); launcher.Dispose(); output?.Close();
    }
    internal void EndSession()
    {
        if (exiting) return;
        try { CaptureBounds(config); store.Save(config, fingerprint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        exiting = true; Close();
    }
    private void ExitApplication()
    {
        if (RequestExit()) Application.Current.Shutdown();
    }
    internal bool RequestExit()
    {
        if (editor is not null) { editor.Activate(); return false; }
        if (launcher.HasRunning && MessageBox.Show(this, "Stop running output commands and exit?", "ButtonFly", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        try { CaptureBounds(config); store.Save(config, fingerprint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notify("Could not save window position: " + e.Message); }
        exiting = true; Close(); return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}
