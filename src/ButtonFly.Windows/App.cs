using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace ButtonFly.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, @"Local\ButtonFly-" + identity, out bool first);
        string eventName = @"Local\ButtonFly-Show-" + identity;
        if (!first)
        {
            for (int i = 0; i < 10; i++)
            {
                try { using var signal = EventWaitHandle.OpenExisting(eventName); signal.Set(); return 0; }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
            }
            return 1;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        using var installerMarker = new Mutex(false, @"Local\ButtonFly.App.Running");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.InstallStyles(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ButtonFly");
                Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "error.log"), DateTime.Now + "\n" + e.Exception + "\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            MessageBox.Show(e.Exception.Message, "ButtonFly", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var window = new MainWindow(args.Contains("--background"));
        app.MainWindow = window;
        app.SessionEnding += (_, _) => window.EndSession();
        var wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => app.Dispatcher.BeginInvoke(window.ShowLauncher), null, Timeout.Infinite, false);
        try { app.Run(window); }
        finally { wait.Unregister(null); mutex.ReleaseMutex(); }
        return 0;
    }
}
