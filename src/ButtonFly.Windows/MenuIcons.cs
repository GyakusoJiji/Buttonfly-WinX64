using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ButtonFly.Core;

namespace ButtonFly.Windows;

internal static class MenuIcons
{
    private readonly record struct CacheKey(IconKind Kind, string Path, long Modified, long Length);
    private sealed record Result(BitmapSource? Image, string? Error);
    private static readonly Dictionary<CacheKey, Result> cache = [];
    private static readonly Queue<CacheKey> order = [];

    public static BitmapSource? Load(MenuItem item) => Load(item, out _);

    public static BitmapSource? Load(MenuItem item, out string? error)
    {
        error = null;
        if (item.Icon.Kind == IconKind.None) return null;
        try
        {
            string source = item.Icon.Path;
            if (item.Icon.Kind == IconKind.Application && string.IsNullOrWhiteSpace(source))
            {
                if (item.Action is not { Kind: ActionKind.Executable or ActionKind.Open } action)
                    throw new InvalidDataException("Choose an application to get its icon.");
                source = action.Target;
            }
            string path = ResolvePath(source, item.Action?.WorkingDirectory, item.Icon.Kind == IconKind.Application);
            var file = new FileInfo(path);
            bool directory = Directory.Exists(path);
            if (!file.Exists && !directory) throw new FileNotFoundException("The icon source was not found.");
            var key = new CacheKey(item.Icon.Kind, path.ToUpperInvariant(), directory ? Directory.GetLastWriteTimeUtc(path).Ticks : file.LastWriteTimeUtc.Ticks, directory ? 0 : file.Length);
            if (!cache.TryGetValue(key, out var result))
            {
                try
                {
                    BitmapSource image;
                    if (item.Icon.Kind == IconKind.File)
                    {
                        if (!path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose an .ico file.");
                        using var stream = File.OpenRead(path);
                        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        image = decoder.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).First();
                        image.Freeze();
                    }
                    else image = ExtractApplicationIcon(path);
                    result = new(image, null);
                }
                catch (Exception ex) when (IsLoadFailure(ex)) { result = new(null, "Could not load the icon. Choose a different file."); }
                while (cache.Count >= 128) cache.Remove(order.Dequeue());
                cache.Add(key, result); order.Enqueue(key);
            }
            error = result.Error;
            return result.Image;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            error = ex is FileNotFoundException or InvalidDataException ? ex.Message : "Could not load the icon. Check the source.";
            return null;
        }
    }

    private static bool IsLoadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or COMException or InvalidOperationException or FileFormatException;

    private static string ResolvePath(string source, string? workingDirectory, bool application)
    {
        source = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Choose an icon file.");
        string working = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.ExpandEnvironmentVariables(workingDirectory);
        if (Path.IsPathRooted(source)) return Path.GetFullPath(source);
        if (application)
        {
            // Match a launch target such as notepad.exe without ever starting it.
            var folders = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows), working }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
            foreach (var folder in folders)
            {
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), source);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                if (!Path.HasExtension(source) && File.Exists(candidate + ".exe")) return Path.GetFullPath(candidate + ".exe");
            }
        }
        return Path.GetFullPath(source, working);
    }

    private static BitmapSource ExtractApplicationIcon(string path)
    {
        const uint icon = 0x100; // SHGFI_ICON | SHGFI_LARGEICON
        var result = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), icon);
        try
        {
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) throw new InvalidDataException("Could not retrieve the application icon.");
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze(); return bitmap;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
