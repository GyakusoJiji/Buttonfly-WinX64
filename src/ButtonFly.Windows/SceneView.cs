using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ButtonFly.Core;
using MenuItem = ButtonFly.Core.MenuItem;

namespace ButtonFly.Windows;

public sealed class SceneView : Grid, IDisposable
{
    private readonly Viewport3D viewport = new();
    private readonly Viewport3D overlay = new() { IsHitTestVisible = false };
    private readonly Starfield stars = new();
    private readonly List<ButtonVisual> buttons = [];
    private readonly Dictionary<Model3D, ButtonVisual> hitMap = [];
    private readonly List<MenuItem> path = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Configuration config = Configuration.CreateDefault();
    private bool subscribed;
    private int selected = -1;
    private ButtonVisual? hovered;
    private Transition? transition;
    private double lastFrame;
    private Point pointer;
    private double flashUntil;
    private bool Modern => config.Preferences.Theme == "modern";
    private List<MenuItem> Current => path.Count == 0 ? config.Items : path[^1].Children!;
    public string Breadcrumb => "Home" + string.Concat(path.Select(p => "  /  " + p.Name.Replace('\n', ' ')));
    public bool CanGoBack => path.Count > 0;
    public bool IsTransitioning => transition is not null;
    public bool IsEmpty => Current.Count == 0;
    internal bool IsRendering => subscribed;
    public event Action<MenuItem>? Activated;
    public event Action? NavigationChanged;
    public event Action? HideRequested;
    private sealed record Transition(double Start, double Duration, Action<double> Tick, Action Finish);

    public SceneView()
    {
        Focusable = true;
        ClipToBounds = true;
        Children.Add(stars); Children.Add(viewport); Children.Add(overlay);
        SizeChanged += (_, _) => { CompleteTransition(); Rebuild(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CompleteTransition(); Unsubscribe(); } else { lastFrame = clock.Elapsed.TotalSeconds; UpdateLoop(); } };
        MouseMove += (_, e) =>
        {
            pointer = e.GetPosition(this);
            if (transition is not null) return;
            var next = Pick(pointer);
            if (next != hovered) { hovered = next; Cursor = next is null ? Cursors.Arrow : Cursors.Hand; UpdateLoop(); }
        };
        MouseLeave += (_, _) => { hovered = null; UpdateLoop(); };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (transition is not null) return;
            var hit = Pick(e.GetPosition(this));
            if (hit is not null) { selected = buttons.IndexOf(hit); Activate(hit); }
            else Back();
            e.Handled = true;
        };
        MouseRightButtonUp += (_, e) => { Back(); e.Handled = true; };
        KeyDown += OnKey;
        GotKeyboardFocus += (_, _) => UpdateLoop();
        LostKeyboardFocus += (_, _) => UpdateLoop();
        AutomationPropertiesSet();
    }
    private void AutomationPropertiesSet() => System.Windows.Automation.AutomationProperties.SetName(this, "3D menu. Use arrow keys to select, Enter to open, and Esc to go back.");
    public void SetConfiguration(Configuration value)
    {
        CompleteTransition();
        var ids = path.Select(p => p.Id).ToArray();
        config = value; path.Clear();
        var items = value.Items;
        foreach (var id in ids)
        {
            var node = items.FirstOrDefault(n => n.Id == id && n.IsFolder);
            if (node is null) break;
            path.Add(node); items = node.Children!;
        }
        Rebuild(); NavigationChanged?.Invoke();
    }
    public void Home() { if (transition is not null) return; path.Clear(); Rebuild(); NavigationChanged?.Invoke(); }
    private PerspectiveCamera Camera()
    {
        var fov = Modern ? 40d : 18d;
        var horizontalFov = 2 * Math.Atan(Math.Tan(fov * Math.PI / 360) * Math.Max(0.1, ActualWidth / Math.Max(1, ActualHeight))) * 180 / Math.PI;
        return new PerspectiveCamera(new Point3D(0, 0, Modern ? 14 : 32), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), horizontalFov) { NearPlaneDistance = 0.1, FarPlaneDistance = 1000 };
    }
    private Brush Backdrop() => Modern ? new SolidColorBrush(Color.FromRgb(7, 12, 27)) : new LinearGradientBrush(Color.FromRgb(34, 38, 81), Color.FromRgb(104, 66, 129), 90);
    private static void Lights(Viewport3D target, bool modern)
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(modern ? Color.FromRgb(150, 150, 164) : Color.FromRgb(104, 100, 114)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -3)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(55, 49, 70), new Vector3D(1, 0, -1)));
        target.Children.Add(new ModelVisual3D { Content = lights });
    }
    private void Rebuild()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        viewport.Children.Clear(); overlay.Children.Clear(); buttons.Clear(); hitMap.Clear(); hovered = null;
        Background = Backdrop(); stars.Visibility = Modern ? Visibility.Visible : Visibility.Collapsed;
        viewport.Camera = Camera(); overlay.Camera = Camera();
        Lights(viewport, Modern); Lights(overlay, Modern);
        var slots = Layout(Current.Count);
        for (int i = 0; i < Current.Count; i++)
        {
            var b = new ButtonVisual(Current[i], Modern, i);
            b.Set(slots[i]); buttons.Add(b);
            foreach (var mesh in b.HitModels) hitMap[mesh] = b;
        }
        if (Modern && path.Count > 0)
        {
            var header = new ButtonVisual(path[^1], true, -1) { IsHeader = true };
            header.Set(HeaderSlot()); buttons.Add(header);
            foreach (var mesh in header.HitModels) hitMap[mesh] = header;
        }
        foreach (var button in buttons) viewport.Children.Add(button.Visual);
        selected = Current.Count > 0 ? Math.Clamp(selected, 0, Current.Count - 1) : -1;
        UpdateLoop();
    }
    private (double Width, double Height) ViewSize(double z = 0)
    {
        double height = 2 * Math.Tan((Modern ? 40 : 18) * Math.PI / 360) * ((Modern ? 14 : 32) - z);
        return (height * ActualWidth / Math.Max(1, ActualHeight), height);
    }
    private Pose HeaderSlot()
    {
        var (_, h) = ViewSize();
        double scale = Math.Min(0.55, h / 12);
        return new(0, h / 2 - 1.5 * scale / 2 - 0.35, 0.3, 0, 180, scale, scale, scale);
    }
    private List<Pose> Layout(int count)
    {
        var result = new List<Pose>();
        if (count == 0) return result;
        var (w, h) = ViewSize();
        double bw = Modern ? 3.2 : 2.9, bh = Modern ? 1.5 : 2.2;
        double top = Modern && path.Count > 0 ? HeaderSlot().Y - 1.5 * HeaderSlot().SY / 2 - 0.5 : h / 2 - 0.5;
        double bottom = -h / 2 + 0.5, aw = w - 1, ah = top - bottom, cy = (top + bottom) / 2;
        Pose Slot(double x, double y, double s) => new(x, y, Modern ? -0.02 * x * x : 0, 0, Modern ? -0.035 * x * 180 / Math.PI : 0, s, s, s);
        int[][] dice = count switch
        {
            1 => [[1, 1]], 2 => [[0, 0], [2, 2]], 3 => [[0, 0], [1, 1], [2, 2]],
            4 => [[0, 0], [2, 0], [0, 2], [2, 2]], 5 => [[0, 0], [2, 0], [1, 1], [0, 2], [2, 2]],
            6 => [[0, 0], [2, 0], [0, 1], [2, 1], [0, 2], [2, 2]], _ => []
        };
        if (!Modern && dice.Length > 0)
        {
            var pairs = dice.SelectMany((a, i) => dice.Skip(i + 1).Select(b => (C: Math.Abs(a[0] - b[0]), R: Math.Abs(a[1] - b[1])))).ToArray();
            static double Min(IEnumerable<int> values) => values.Where(v => v > 0).Select(v => (double)v).DefaultIfEmpty(double.PositiveInfinity).Min();
            double dc = Min(pairs.Where(p => p.R == 0).Select(p => p.C));
            double dr = Min(pairs.Where(p => p.C == 0).Select(p => p.R));
            if (double.IsInfinity(dc)) dc = Min(pairs.Select(p => p.C));
            if (double.IsInfinity(dr)) dr = Min(pairs.Select(p => p.R));
            double px = double.IsInfinity(dc) ? bw + 0.135 : (bw + 0.135) / dc;
            double py = double.IsInfinity(dr) ? bh + 0.135 : (bh + 0.135) / dr;
            foreach (var p in pairs) if (p.R > 0 && p.C * px < bw + 0.135 - 1e-6) py = Math.Max(py, (bh + 0.135) / p.R);
            px = (px + bw + 0.27) / 2 * 1.15; py = (py + bh + 0.27) / 2;
            double cols = dice.Max(p => p[0]) - dice.Min(p => p[0]), rows = dice.Max(p => p[1]) - dice.Min(p => p[1]);
            double s = Math.Max(0.01, Math.Min(1, Math.Min(aw / (cols * px + bw), ah / (rows * py + bh))));
            double mx = (dice.Max(p => p[0]) + dice.Min(p => p[0])) / 2d, my = (dice.Max(p => p[1]) + dice.Min(p => p[1])) / 2d;
            return dice.Select(p => Slot((p[0] - mx) * px * s, cy - (p[1] - my) * py * s, s)).ToList();
        }
        int bestCols = 1; double scale = 0;
        for (int cols = 1; cols <= count; cols++)
        {
            int rows = (int)Math.Ceiling((double)count / cols);
            double s = Math.Min(1.15, Math.Min(aw / (cols * bw + (cols - 1) * 0.45), ah / (rows * bh + (rows - 1) * 0.45)));
            if (s >= scale - 1e-6) { bestCols = cols; scale = s; }
        }
        scale = Math.Max(0.01, scale);
        int rowCount = (int)Math.Ceiling((double)count / bestCols);
        for (int i = 0; i < count; i++)
        {
            int row = i / bestCols, col = i % bestCols, inRow = row == rowCount - 1 ? count - row * bestCols : bestCols;
            result.Add(Slot((col - (inRow - 1) / 2d) * (bw + 0.45) * scale, cy + ((rowCount - 1) / 2d - row) * (bh + 0.45) * scale, scale));
        }
        return result;
    }
    private ButtonVisual? Pick(Point point)
    {
        ButtonVisual? found = null;
        VisualTreeHelper.HitTest(viewport, null, hit =>
        {
            if (hit is RayMeshGeometry3DHitTestResult mesh && hitMap.TryGetValue(mesh.ModelHit, out var button)) { found = button; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return found;
    }
    private void Activate(ButtonVisual button)
    {
        if (button.IsHeader) { Back(); return; }
        if (button.Item.IsFolder) Navigate(button, false);
        else
        {
            button.PressStart = clock.Elapsed.TotalSeconds;
            flashUntil = clock.Elapsed.TotalSeconds + 0.65;
            UpdateLoop(); Activated?.Invoke(button.Item);
        }
    }
    public void Back()
    {
        if (transition is not null) return;
        if (path.Count == 0) { HideRequested?.Invoke(); return; }
        Navigate(null, true);
    }
    private void Navigate(ButtonVisual? chosen, bool back)
    {
        var oldPath = path.ToArray();
        var oldItems = Current;
        var oldPoses = buttons.Select(b => b.State).ToArray();
        var parent = back ? path[^1] : chosen!.Item;
        if (back) path.RemoveAt(path.Count - 1); else path.Add(parent);
        if (config.Preferences.ReduceMotion) { Rebuild(); NavigationChanged?.Invoke(); return; }
        if (!Modern)
        {
            BitmapSource snapshot;
            if (back)
            {
                // Capture the outgoing menu using its original path/layout.
                path.Clear(); path.AddRange(oldPath); snapshot = Snapshot(oldItems);
                path.RemoveAt(path.Count - 1);
                Rebuild();
                chosen = buttons.First(b => b.Item.Id == parent.Id);
            }
            else snapshot = Snapshot(Current);
            var flight = chosen!;
            var rest = flight.State;
            // The snapshot covers the full back face, including the bezel area.
            double faceW = 2.9, faceH = 2.2;
            var (width, height) = ViewSize(1.2);
            var portal = new Pose(0, 0, 1.2 - 0.284, -180, 0, width / faceW, height / faceH, 1);
            flight.SetBackImage(snapshot);
            viewport.Children.Remove(flight.Visual); overlay.Children.Add(flight.Visual);
            Pose from = back ? portal : rest, to = back ? rest : portal;
            flight.Set(from);
            transition = new(clock.Elapsed.TotalSeconds, 1.28, t => flight.Set(Pose.Lerp(from, to, Ease(t))), () => { Rebuild(); NavigationChanged?.Invoke(); });
        }
        else
        {
            var outgoing = buttons.ToArray();
            Rebuild();
            var incoming = buttons.ToArray();
            var destinations = incoming.Select(b => b.State).ToArray();
            foreach (var b in outgoing) { viewport.Children.Add(b.Visual); }
            for (int i = 0; i < incoming.Length; i++)
            {
                var d = destinations[i]; incoming[i].Set(d with { X = 0, Y = 0, Z = -4, RY = -180, SX = 0.01, SY = 0.01, SZ = 0.01 });
            }
            transition = new(clock.Elapsed.TotalSeconds, 1.0, t =>
            {
                for (int i = 0; i < outgoing.Length; i++)
                {
                    var origin = oldPoses[i];
                    var to = !back && outgoing[i] == chosen ? HeaderSlot() : origin with { Z = -12, SX = 0.01, SY = 0.01, SZ = 0.01 };
                    outgoing[i].Set(Pose.Lerp(origin, to, Ease(Math.Min(1, t / 0.9))));
                }
                for (int i = 0; i < incoming.Length; i++)
                {
                    if (!back && incoming[i].IsHeader) continue;
                    var to = destinations[i]; var from = to with { X = 0, Y = 0, Z = -4, RY = -180, SX = 0.01, SY = 0.01, SZ = 0.01 };
                    incoming[i].Set(Pose.Lerp(from, to, Ease(Math.Clamp((t - 0.25) / 0.75, 0, 1))));
                }
            }, () => { Rebuild(); NavigationChanged?.Invoke(); });
        }
        hovered = null; Cursor = Cursors.Arrow; UpdateLoop();
    }
    private BitmapSource Snapshot(List<MenuItem> items)
    {
        var root = new Grid { Width = ActualWidth, Height = ActualHeight, Background = Backdrop() };
        var view = new Viewport3D { Camera = Camera() }; root.Children.Add(view); Lights(view, Modern);
        var slots = Layout(items.Count);
        for (int i = 0; i < items.Count; i++) { var b = new ButtonVisual(items[i], Modern, i); b.Set(slots[i]); view.Children.Add(b.Visual); }
        root.Measure(new Size(ActualWidth, ActualHeight)); root.Arrange(new Rect(0, 0, ActualWidth, ActualHeight)); root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = Math.Min(dpi.DpiScaleX, 2048 / Math.Max(ActualWidth, ActualHeight));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)(ActualWidth * scale)), Math.Max(1, (int)(ActualHeight * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze(); return bitmap;
    }
    private void CompleteTransition()
    {
        if (transition is not { } current) return;
        transition = null; current.Tick(1); current.Finish();
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Back(); e.Handled = true; return; }
        if (transition is not null || Current.Count == 0) return;
        if (e.Key == Key.Enter && selected >= 0) { Activate(buttons[selected]); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        int next = -1; double score = double.MaxValue;
        var origin = buttons[Math.Max(0, selected)].State;
        for (int i = 0; i < Current.Count; i++)
        {
            if (i == selected) continue;
            var candidate = buttons[i].State;
            double dx = candidate.X - origin.X, dy = candidate.Y - origin.Y;
            double forward = e.Key switch { Key.Left => -dx, Key.Right => dx, Key.Up => dy, _ => -dy };
            if (forward < 0.01) continue;
            double lateral = e.Key is Key.Left or Key.Right ? Math.Abs(dy) : Math.Abs(dx);
            double distance = forward + lateral * 3;
            if (distance < score) { score = distance; next = i; }
        }
        if (next >= 0) selected = next;
        else selected = (selected + (e.Key is Key.Left or Key.Up ? -1 : 1) + Current.Count) % Current.Count;
        hovered = null; UpdateLoop();
        System.Windows.Automation.AutomationProperties.SetHelpText(this, buttons[selected].Item.Name);
        e.Handled = true;
    }
    private void UpdateLoop()
    {
        if (!IsVisible) { Unsubscribe(); return; }
        if (!subscribed) { CompositionTarget.Rendering += OnFrame; subscribed = true; }
    }
    private void Unsubscribe() { if (subscribed) CompositionTarget.Rendering -= OnFrame; subscribed = false; }
    private void OnFrame(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - lastFrame, 0, 0.05); lastFrame = now;
        if (transition is { } active)
        {
            double t = Math.Clamp((now - active.Start) / active.Duration, 0, 1); active.Tick(t);
            if (t >= 1) { transition = null; active.Finish(); }
        }
        bool settling = false;
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            double target = transition is null && (b == hovered || IsKeyboardFocusWithin && i == selected) ? 1 : 0;
            settling |= b.Tick(dt, now, target, !config.Preferences.ReduceMotion && transition is null);
        }
        if (Modern && !config.Preferences.ReduceMotion)
        {
            double px = (pointer.X / Math.Max(1, ActualWidth) - 0.5), py = (pointer.Y / Math.Max(1, ActualHeight) - 0.5);
            stars.Offset = new Point(px * 10, py * 10);
            if (viewport.Camera is PerspectiveCamera camera) camera.Position = new Point3D(px * 0.09, -py * 0.09, 14);
        }
        if (transition is null && !settling && now > flashUntil && (!Modern || config.Preferences.ReduceMotion)) Unsubscribe();
    }
    private static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public void Dispose() { transition = null; Unsubscribe(); viewport.Children.Clear(); overlay.Children.Clear(); buttons.Clear(); hitMap.Clear(); }
}

internal readonly record struct Pose(double X, double Y, double Z, double RX, double RY, double SX, double SY, double SZ)
{
    public static Pose Lerp(Pose a, Pose b, double t)
    {
        double L(double x, double y) => x + (y - x) * t;
        return new(L(a.X, b.X), L(a.Y, b.Y), L(a.Z, b.Z), L(a.RX, b.RX), L(a.RY, b.RY), L(a.SX, b.SX), L(a.SY, b.SY), L(a.SZ, b.SZ));
    }
}

internal sealed class ButtonVisual
{
    private static readonly Dictionary<bool, MeshGeometry3D[]> slabs = [];
    private static readonly Dictionary<(string, Color, bool, BitmapSource?), BitmapSource> textures = [];
    private static readonly Queue<(string, Color, bool, BitmapSource?)> textureOrder = [];
    private readonly ScaleTransform3D scale = new();
    private readonly AxisAngleRotation3D rx = new(new Vector3D(1, 0, 0), 0), ry = new(new Vector3D(0, 1, 0), 0);
    private readonly TranslateTransform3D translate = new(), innerMove = new();
    private readonly AxisAngleRotation3D innerX = new(new Vector3D(1, 0, 0), 0), innerY = new(new Vector3D(0, 1, 0), 0);
    private readonly GeometryModel3D back;
    private readonly SolidColorBrush glow = new(Colors.Transparent);
    private readonly bool modern;
    private readonly int index;
    private double hover;
    public MenuItem Item { get; }
    public ModelVisual3D Visual { get; }
    public List<Model3D> HitModels { get; } = [];
    public Pose State { get; private set; }
    public bool IsHeader { get; init; }
    public double PressStart { get; set; } = -10;
    public ButtonVisual(MenuItem item, bool modern, int index)
    {
        Item = item; this.modern = modern; this.index = index;
        double bw = modern ? 3.2 : 2.9, bh = modern ? 1.5 : 2.2, bevel = modern ? 0.03 : 0.18, depth = modern ? 0.06 : 0.2;
        var color = modern ? (Color)ColorConverter.ConvertFromString(item.Color) : item.IsFolder ? Color.FromRgb(158, 87, 153) : Color.FromRgb(77, 69, 189);
        var group = new Model3DGroup();
        if (!slabs.TryGetValue(modern, out var slab)) { slab = Slab(bw, bh, bevel, depth); slabs[modern] = slab; }
        if (modern)
        {
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(110, 110, 110)), 90));
            material.Children.Add(new EmissiveMaterial(glow));
            var body = new GeometryModel3D(slab[0], material) { BackMaterial = material }; group.Children.Add(body); HitModels.Add(body);
        }
        else
        {
            var panelColor = ScaleColor(color, 0.58);
            // Bottom, right, top, left: sampled from the reference bezel (94,50,94) / (109,60,110) / (169,111,164) / (154,94,149).
            double[] brightness = [1.03, 1.20, 1.84, 1.67];
            foreach (var (mesh, factor) in slab.Zip(brightness))
            {
                // Emission alone is additive in WPF, so the bezel would blend with whatever is behind it.
                var material = new MaterialGroup();
                material.Children.Add(new DiffuseMaterial(Brushes.Black));
                material.Children.Add(new EmissiveMaterial(new SolidColorBrush(ScaleColor(panelColor, factor))));
                var body = new GeometryModel3D(mesh, material) { BackMaterial = material };
                group.Children.Add(body); HitModels.Add(body);
            }
        }
        double z = depth / 2 + bevel + 0.004;
        var root = new Model3DGroup();
        var texture = FaceTexture(item.Name, color, modern, MenuIcons.Load(item));
        var frontMat = new MaterialGroup();
        if (modern) { frontMat.Children.Add(new DiffuseMaterial(new ImageBrush(texture))); frontMat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(90, 90, 105)), 90)); }
        // Classic faces are unlit so they keep the exact texture color, like the bezel.
        else { frontMat.Children.Add(new DiffuseMaterial(Brushes.Black)); frontMat.Children.Add(new EmissiveMaterial(new ImageBrush(texture))); }
        frontMat.Children.Add(new EmissiveMaterial(glow));
        var front = new GeometryModel3D(Plane(bw - 2 * bevel, bh - 2 * bevel, z, false, modern), frontMat);
        back = new GeometryModel3D(Plane(bw, bh, z, true, modern), frontMat);
        group.Children.Add(front); group.Children.Add(back); HitModels.Add(front); HitModels.Add(back);
        var inside = new Transform3DGroup(); inside.Children.Add(new RotateTransform3D(innerX)); inside.Children.Add(new RotateTransform3D(innerY)); inside.Children.Add(innerMove); group.Transform = inside;
        var outer = new Transform3DGroup(); outer.Children.Add(scale); outer.Children.Add(new RotateTransform3D(rx)); outer.Children.Add(new RotateTransform3D(ry)); outer.Children.Add(translate);
        root.Children.Add(group);
        Visual = new ModelVisual3D { Content = root, Transform = outer };
    }
    public void Set(Pose pose)
    {
        State = pose;
        scale.ScaleX = pose.SX; scale.ScaleY = pose.SY; scale.ScaleZ = pose.SZ;
        rx.Angle = pose.RX; ry.Angle = pose.RY;
        translate.OffsetX = pose.X; translate.OffsetY = pose.Y; translate.OffsetZ = pose.Z;
    }
    public void SetBackImage(BitmapSource bitmap)
    {
        // Emissive-only preserves the snapshot's colors independently of scene lighting.
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(new ImageBrush(bitmap)));
        back.Material = material;
        hover = 0;
        innerMove.OffsetZ = 0; innerX.Angle = innerY.Angle = 0;
    }
    public bool Tick(double dt, double time, double target, bool animate)
    {
        hover += (target - hover) * Math.Min(1, dt * 12);
        if (Math.Abs(target - hover) < 0.001) hover = target;
        double press = animate ? Math.Clamp((time - PressStart) / 0.6, 0, 1) : 1;
        innerMove.OffsetZ = hover * 0.35 - Math.Sin(press * Math.PI) * 0.35;
        innerMove.OffsetY = modern && animate ? Math.Sin(time * 0.9 + index) * 0.035 : 0;
        innerX.Angle = modern && animate ? Math.Sin(time * 0.6 + index) * 5.15 * (1 - hover * 0.7) : 0;
        innerY.Angle = modern && animate ? Math.Sin(time * 0.43 + index * 1.7) * 9.17 * (1 - hover * 0.7) + (press < 1 ? press * 360 : 0) : 0;
        glow.Color = Color.FromArgb((byte)(hover * 60), 180, 170, 220);
        return hover != target || press < 1;
    }
    private static MeshGeometry3D[] Slab(double w, double h, double bevel, double depth)
    {
        var meshes = Enumerable.Range(0, 4).Select(_ => new MeshGeometry3D()).ToArray();
        Point3D[] Ring(double width, double height, double z) => [new(-width / 2, -height / 2, z), new(width / 2, -height / 2, z), new(width / 2, height / 2, z), new(-width / 2, height / 2, z)];
        var rings = new[] { Ring(w - 2 * bevel, h - 2 * bevel, depth / 2 + bevel), Ring(w, h, depth / 2), Ring(w, h, -depth / 2), Ring(w - 2 * bevel, h - 2 * bevel, -depth / 2 - bevel) };
        for (int ring = 0; ring < 3; ring++) for (int i = 0; i < 4; i++) AddQuad(meshes[i], rings[ring][i], rings[ring + 1][i], rings[ring + 1][(i + 1) % 4], rings[ring][(i + 1) % 4]);
        foreach (var mesh in meshes) mesh.Freeze();
        return meshes;
    }
    private static Color ScaleColor(Color color, double factor) => Color.FromRgb((byte)Math.Clamp(Math.Round(color.R * factor), 0, 255), (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255), (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
    private static MeshGeometry3D Plane(double w, double h, double z, bool back, bool modern)
    {
        Point3D P(double x, double y) => back ? modern ? new(-x, y, -z) : new(x, -y, -z) : new(x, y, z);
        var mesh = new MeshGeometry3D(); AddQuad(mesh, P(-w / 2, -h / 2), P(w / 2, -h / 2), P(w / 2, h / 2), P(-w / 2, h / 2));
        mesh.TextureCoordinates = new PointCollection([new(0, 1), new(1, 1), new(1, 0), new(0, 0)]); mesh.Freeze(); return mesh;
    }
    private static void AddQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        int n = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        var normal = Vector3D.CrossProduct(b - a, c - a); normal.Normalize();
        for (int i = 0; i < 4; i++) mesh.Normals.Add(normal);
        foreach (int i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(n + i);
    }
    private static BitmapSource FaceTexture(string label, Color color, bool modern, BitmapSource? icon)
    {
        var key = (label, color, modern, icon);
        if (textures.TryGetValue(key, out var cached)) return cached;
        const int w = 768;
        int h = modern ? 352 : 556;
        var visual = new DrawingVisual();
        Color Shade(double k) => Color.FromRgb((byte)Math.Min(255, color.R * k), (byte)Math.Min(255, color.G * k), (byte)Math.Min(255, color.B * k));
        // Muted face matching the reference: (158,87,153) -> (111,91,110).
        Color Face() => Color.FromRgb((byte)(color.R * 0.28 + 67), (byte)(color.G * 0.28 + 67), (byte)(color.B * 0.28 + 67));
        using (var dc = visual.RenderOpen())
        {
            Brush background = modern ? new LinearGradientBrush(Shade(1.25), Shade(0.6), 90) : new SolidColorBrush(Face());
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
            if (modern) dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 5), new Rect(3, 3, w - 6, h - 6));
            double textLeft = w * 0.07, textWidth = w * 0.86;
            double iconBottom = 0;
            if (icon is not null && modern)
            {
                double iconSize = 176;
                double fit = iconSize / Math.Max(icon.PixelWidth, icon.PixelHeight);
                double iw = icon.PixelWidth * fit, ih = icon.PixelHeight * fit;
                dc.DrawImage(icon, new Rect(textLeft + (iconSize - iw) / 2, (h - ih) / 2, iw, ih));
                textLeft += iconSize + 28;
                textWidth = w * 0.93 - textLeft;
            }
            else if (icon is not null)
            {
                // Classic is deliberately stacked: icon, then its title below it.
                double iconSize = 178;
                double fit = iconSize / Math.Max(icon.PixelWidth, icon.PixelHeight);
                double iw = icon.PixelWidth * fit, ih = icon.PixelHeight * fit;
                double y = 64;
                dc.DrawImage(icon, new Rect((w - iw) / 2, y, iw, ih));
                iconBottom = y + ih + 22;
            }
            double size = modern ? 100 : 110;
            var typeface = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, modern ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            if (icon is not null && modern)
            {
                // Preserve intentional line breaks; make room for the icon before wrapping.
                var measure = new FormattedText(label, System.Globalization.CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, size, Brushes.WhiteSmoke, 1);
                size = Math.Max(18, Math.Min(size, size * textWidth / Math.Max(1, measure.Width)));
            }
            FormattedText Format(Brush brush) => new(label, System.Globalization.CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                typeface, size, brush, 1) { TextAlignment = TextAlignment.Center, MaxTextWidth = textWidth, Trimming = TextTrimming.CharacterEllipsis };
            var text = Format(Brushes.WhiteSmoke);
            double availableHeight = iconBottom > 0 ? h - iconBottom - 34 : h * 0.8;
            while ((text.Height > availableHeight || text.Width > textWidth) && size > 18) { size -= 3; text = Format(Brushes.WhiteSmoke); }
            var point = new Point(textLeft, iconBottom > 0 ? iconBottom + (h - iconBottom - text.Height) / 2 : (h - text.Height) / 2);
            dc.DrawText(Format(new SolidColorBrush(Color.FromArgb(190, 12, 0, 25))), point + new Vector(modern ? 4 : 2, modern ? 5 : 2));
            if (modern) dc.DrawText(Format(Brushes.White), point + new Vector(-2, -2));
            dc.DrawText(text, point);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        while (textures.Count >= 128) textures.Remove(textureOrder.Dequeue());
        textures[key] = bitmap; textureOrder.Enqueue(key); return bitmap;
    }
}

internal sealed class Starfield : FrameworkElement
{
    private readonly (double X, double Y, double Size)[] points;
    private Point offset;
    public Point Offset { set { if ((value - offset).Length > 0.2) { offset = value; InvalidateVisual(); } } }
    public Starfield()
    {
        IsHitTestVisible = false;
        var random = new Random(42);
        points = Enumerable.Range(0, 180).Select(_ => (random.NextDouble(), random.NextDouble(), 0.4 + random.NextDouble() * 1.1)).ToArray();
    }
    protected override void OnRender(DrawingContext dc)
    {
        foreach (var p in points) dc.DrawEllipse(Brushes.LightSteelBlue, null, new Point(p.X * ActualWidth + offset.X * p.Size, p.Y * ActualHeight + offset.Y * p.Size), p.Size, p.Size);
    }
}
