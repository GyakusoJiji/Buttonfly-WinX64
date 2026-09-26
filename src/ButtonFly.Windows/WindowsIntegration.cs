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

internal static class StartupRegistration
{
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("ButtonFly", "\"" + Environment.ProcessPath + "\" --background");
        else key.DeleteValue("ButtonFly", false);
    }
}
