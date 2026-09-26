using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ButtonFly.Windows;

internal sealed class OutputWindow : Window
{
    private readonly LaunchService service;
    private readonly ListBox runs = new() { Background = Ui.Panel, Foreground = Brushes.WhiteSmoke, BorderThickness = new Thickness(0) };
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Background = Ui.Background, Foreground = Brushes.Gainsboro, BorderThickness = new Thickness(0), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    private readonly Button stop;
    private readonly TextBlock status = Ui.Text("", 12, Ui.Muted);
    private readonly DispatcherTimer timer;
    private Guid? selected;
    private string lastList = "";
    private string lastText = "";
    public OutputWindow(LaunchService service)
    {
        Ui.ConfigureWindow(this);
        this.service = service;
        Title = "ButtonFly — Command Output"; Width = 1050; Height = 650; MinWidth = 720; MinHeight = 440;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.Button("Copy", () => { try { Clipboard.SetText(output.SelectedText.Length > 0 ? output.SelectedText : output.Text); } catch (Exception e) { Ui.Error(this, e); } }));
        stop = Ui.Button("Stop", () => { if (selected is Guid id) service.Stop(id); }); actions.Children.Add(stop);
        DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions); bar.Children.Add(Ui.Text("Command Output", 23)); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(grid);
        grid.Children.Add(runs); Grid.SetColumn(output, 2); grid.Children.Add(output);
        runs.SelectionChanged += (_, _) => { if (runs.SelectedItem is CommandRun run) { selected = run.Id; lastText = ""; Refresh(); } };
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        Refresh();
    }
    public void Select(CommandRun run) { selected = run.Id; lastList = ""; Refresh(); }
    private void Refresh()
    {
        var history = service.History.Reverse().ToArray();
        string signature = string.Join("|", history.Select(r => r.Id + ":" + r));
        if (signature != lastList)
        {
            lastList = signature; runs.ItemsSource = history;
            runs.SelectedItem = history.FirstOrDefault(r => r.Id == selected) ?? history.FirstOrDefault();
        }
        var current = history.FirstOrDefault(r => r.Id == selected);
        stop.IsEnabled = current is { Finished: false };
        string text = current?.Output ?? "Run a command that captures output to view it here.";
        if (text != lastText)
        {
            bool atBottom = output.VerticalOffset >= output.ExtentHeight - output.ViewportHeight - 20;
            output.Text = lastText = text;
            if (atBottom) output.ScrollToEnd();
        }
        status.Text = current is null ? "The latest 20 runs are kept in memory." : current + "  ·  Non-interactive / 1 MiB output limit";
    }
}
