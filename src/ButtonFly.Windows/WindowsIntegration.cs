using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ButtonFly.Core;

namespace ButtonFly.Windows;

internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private ProcessJob(SafeFileHandle handle) => this.handle = handle;
    public static ProcessJob? TryAttach(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(handle, process.Handle)) { handle.Dispose(); return null; }
        return new(handle);
    }
    public void Terminate() { if (!handle.IsClosed) TerminateJobObject(handle, 1); }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcess, PerJob; public uint LimitFlags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}

internal sealed class HotkeyRegistration : IDisposable
{
    private readonly IntPtr window;
    private readonly HwndSource source;
    private readonly Action toggle;
    private int currentId;
    private int nextId = 100;
    private uint currentModifiers, currentKey;
    public HotkeyRegistration(Window owner, Action toggle)
    {
        this.toggle = toggle;
        window = new WindowInteropHelper(owner).EnsureHandle();
        source = HwndSource.FromHwnd(window)!;
        source.AddHook(Hook);
    }
    public void Register(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0x4000;
        if (parts.Length < 2) throw new ArgumentException("Use a hotkey with modifiers, such as Ctrl+Alt+B.");
        foreach (var part in parts[..^1]) modifiers |= part.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" => 8u,
            _ => throw new ArgumentException("Unknown modifier: " + part)
        };
        var key = (Key)new KeyConverter().ConvertFromInvariantString(parts[^1])!;
        if (key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) throw new ArgumentException("The hotkey is invalid.");
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (currentId != 0 && currentModifiers == modifiers && currentKey == virtualKey) return;
        int id = nextId++;
        if (!RegisterHotKey(window, id, modifiers, virtualKey)) throw new InvalidOperationException("Could not register the hotkey. Check for another app using it or a reserved key.");
        if (currentId != 0) UnregisterHotKey(window, currentId);
        currentId = id;
        currentModifiers = modifiers; currentKey = virtualKey;
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312 && wParam.ToInt32() == currentId) { handled = true; toggle(); }
        return IntPtr.Zero;
    }
    public void Clear() { if (currentId != 0) UnregisterHotKey(window, currentId); currentId = 0; }
    public void Dispose() { Clear(); source.RemoveHook(Hook); }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal sealed class DesktopGestureRegistration : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int GaRoot = 2;
    private const int LvmHitTest = 0x1012;
    private const uint LvhtOnItem = 0x000E;
    private readonly Action showLauncher;
    private readonly LowLevelMouseProc callback;
    private readonly IntPtr hook;
    private DesktopGesture gestures;
    private DateTime lastShown;

    public DesktopGestureRegistration(Action showLauncher)
    {
        this.showLauncher = showLauncher;
        callback = MouseHook;
        hook = SetWindowsHookEx(WhMouseLl, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not monitor desktop gestures.");
    }

    public void Configure(DesktopGesture value) => gestures = value;

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && gestures != DesktopGesture.None && data != IntPtr.Zero)
        {
            int eventId = message.ToInt32();
            if (eventId is WmLButtonUp or WmLButtonDblClk)
            {
                var mouse = Marshal.PtrToStructure<MouseHookData>(data);
                if ((mouse.Flags & 1) == 0 && IsDesktopBackground(mouse.Point) && Matches(eventId)) Show();
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private bool Matches(int eventId)
    {
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (shift && !ctrl && !alt) return eventId == WmLButtonUp ? gestures.HasFlag(DesktopGesture.ShiftClick) : gestures.HasFlag(DesktopGesture.ShiftDoubleClick);
        if (ctrl && !shift && !alt) return eventId == WmLButtonUp ? gestures.HasFlag(DesktopGesture.CtrlClick) : gestures.HasFlag(DesktopGesture.CtrlDoubleClick);
        return !shift && !ctrl && !alt && eventId == WmLButtonDblClk && gestures.HasFlag(DesktopGesture.DoubleClick);
    }

    private void Show()
    {
        if ((DateTime.UtcNow - lastShown).TotalMilliseconds < 300) return;
        lastShown = DateTime.UtcNow;
        Application.Current.Dispatcher.BeginInvoke(showLauncher);
    }

    private static bool IsDesktopBackground(Point point)
    {
        IntPtr target = WindowFromPoint(point);
        if (target == IntPtr.Zero) return false;
        if (ClassName(GetAncestor(target, GaRoot)) is not ("Progman" or "WorkerW")) return false;
        IntPtr icons = FindAncestor(target, "SysListView32");
        if (icons == IntPtr.Zero) return true;
        var hit = new ListViewHitTest { Point = point };
        if (!ScreenToClient(icons, ref hit.Point)) return false;
        return SendMessage(icons, LvmHitTest, IntPtr.Zero, ref hit) < 0 || (hit.Flags & LvhtOnItem) == 0;
    }

    private static IntPtr FindAncestor(IntPtr window, string className)
    {
        for (IntPtr current = window; current != IntPtr.Zero; current = GetParent(current))
            if (ClassName(current) == className) return current;
        return IntPtr.Zero;
    }

    private static string ClassName(IntPtr window)
    {
        var name = new System.Text.StringBuilder(256);
        return GetClassName(window, name, name.Capacity) == 0 ? "" : name.ToString();
    }

    public void Dispose() { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct ListViewHitTest { public Point Point; public uint Flags; public int Item, SubItem, Group; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, int flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ListViewHitTest lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

internal static class StartupRegistration
{
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("ButtonFly", "\"" + Environment.ProcessPath + "\" --background");
        else key.DeleteValue("ButtonFly", false);
    }
}
