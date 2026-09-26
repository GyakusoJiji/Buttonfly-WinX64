using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;

namespace ButtonFly.Windows;

internal static class Ui
{
    public static readonly Brush Background = new SolidColorBrush(Color.FromRgb(17, 21, 34));
    public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(27, 32, 49));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(165, 174, 195));
    public static void ConfigureWindow(Window window) => window.Style = (Style)Application.Current.FindResource(typeof(Window));
    public static void InstallStyles(Application app)
    {
        const string xaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Window"><Setter Property="FontFamily" Value="Yu Gothic UI"/><Setter Property="FontSize" Value="14"/><Setter Property="Background" Value="#111522"/><Setter Property="Foreground" Value="#EFF1F8"/></Style>
          <Style TargetType="Button">
            <Setter Property="Background" Value="#303951"/><Setter Property="Foreground" Value="#F0F2FF"/><Setter Property="Padding" Value="14,8"/><Setter Property="Margin" Value="3"/><Setter Property="Cursor" Value="Hand"/><Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#536082"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#536082"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox"><Setter Property="Padding" Value="8,6"/><Setter Property="Margin" Value="0,3,0,12"/><Setter Property="Background" Value="#F2F4F9"/><Setter Property="Foreground" Value="#171C2A"/><Setter Property="BorderBrush" Value="#555F7A"/><Setter Property="BorderThickness" Value="1"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="ComboBox"><Setter Property="Margin" Value="0,3,0,12"/><Setter Property="Padding" Value="8,5"/><Setter Property="Foreground" Value="#171C2A"/><Setter Property="MinHeight" Value="32"/></Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#EFF1F8"/><Setter Property="Margin" Value="0,8"/></Style>
          <Style TargetType="TreeView"><Setter Property="Background" Value="#1B2031"/><Setter Property="Foreground" Value="#EFF1F8"/><Setter Property="BorderThickness" Value="0"/></Style>
          <Style TargetType="TreeViewItem"><Setter Property="Padding" Value="5"/><Setter Property="Foreground" Value="#EFF1F8"/></Style>
          <Style TargetType="ToolTip"><Setter Property="Foreground" Value="#171C2A"/></Style>
        </ResourceDictionary>
        """;
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
    }
    public static Button Button(string text, Action click, string? tooltip = null)
    {
        var button = new Button { Content = text, ToolTip = tooltip };
        button.Click += (_, _) => click(); return button;
    }
    public static TextBlock Text(string text, double size = 14, Brush? brush = null) => new() { Text = text, FontSize = size, Foreground = brush ?? Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    public static void Label(Panel parent, string text) => parent.Children.Add(Text(text, 12, Muted));
    public static void Error(Window owner, Exception exception) => MessageBox.Show(owner, exception.Message, "ButtonFly", MessageBoxButton.OK, MessageBoxImage.Warning);
}
