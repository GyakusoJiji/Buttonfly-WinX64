using System.Diagnostics;
using System.Text;
using ButtonFly.Core;

namespace ButtonFly.Windows;

public sealed class CommandRun
{
    private readonly object gate = new();
    private readonly StringBuilder output = new();
    private int bytes;
    private bool truncated;
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTime Started { get; } = DateTime.Now;
    public int? ExitCode { get; internal set; }
    public bool Finished { get; internal set; }
    public bool Stopped { get; internal set; }
    internal Process? Process;
    internal ProcessJob? Job;
    public string Output { get { lock (gate) return output.ToString(); } }
    internal void Append(string text, bool error = false)
    {
        lock (gate)
        {
            if (truncated) return;
            int remaining = 1024 * 1024 - bytes;
            string value = error ? "[stderr] " + text : text;
            int count = Encoding.UTF8.GetByteCount(value);
            if (count > remaining)
            {
                // Respect UTF-16 surrogate boundaries when limiting the UTF-8 byte budget.
                int length = Math.Min(value.Length, remaining);
                while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > remaining) length--;
                if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
                output.Append(value.AsSpan(0, length));
                output.Append("\n[Output was truncated at 1 MiB. The process is still running.]\n");
                truncated = true;
            }
            else { output.Append(value); bytes += count; }
        }
    }
    public override string ToString() => $"{Started:HH:mm:ss}  {Name.Replace('\n', ' ')}  {(Finished ? Stopped ? "Stopped" : $"Exited {ExitCode}" : "Running")}";
}

public sealed class LaunchService : IDisposable
{
    private readonly List<CommandRun> history = [];
    private readonly object gate = new();
    public IReadOnlyList<CommandRun> History { get { lock (gate) return history.ToArray(); } }
    public bool HasRunning => History.Any(r => !r.Finished);
    public event Action? Changed;
    public LaunchService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ProcessStartInfo CreateStartInfo(LaunchAction action)
    {
        string target = Environment.ExpandEnvironmentVariables(action.Target);
        string working = string.IsNullOrWhiteSpace(action.WorkingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.ExpandEnvironmentVariables(action.WorkingDirectory);
        if (!Directory.Exists(working)) throw new DirectoryNotFoundException("The working folder does not exist: " + working);
        var start = new ProcessStartInfo { WorkingDirectory = working };
        if (action.Kind == ActionKind.Open)
        {
            start.FileName = target;
            start.UseShellExecute = true;
        }
        else if (action.Kind == ActionKind.Executable)
        {
            start.FileName = target;
            start.UseShellExecute = false;
            foreach (string arg in action.Arguments) start.ArgumentList.Add(Environment.ExpandEnvironmentVariables(arg));
        }
        else
        {
            bool capture = action.Output == OutputMode.Capture;
            start.UseShellExecute = !capture;
            start.CreateNoWindow = capture;
            if (action.Shell == CommandShell.Cmd)
            {
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                // cmd command text is deliberately a shell expression, unlike executable arguments.
                start.Arguments = "/d /s " + (capture ? "/c" : "/k") + " \"" + (capture ? "set /p BUTTONFLY_RUN_GATE= >nul & " : "") + action.Target + "\"";
            }
            else
            {
                start.FileName = action.Shell == CommandShell.PowerShell7 ? FindPwsh() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-OutputFormat");
                start.ArgumentList.Add("Text");
                if (capture) start.ArgumentList.Add("-NonInteractive"); else start.ArgumentList.Add("-NoExit");
                string script = action.Target;
                if (capture) script = "$null = [Console]::ReadLine(); $ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(" + (action.Encoding == "cp932" ? "932" : "65001") + "); $OutputEncoding = [Console]::OutputEncoding; " + script;
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            }
            if (capture)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.RedirectStandardInput = true;
                start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.GetEncoding(action.Encoding == "cp932" ? 932 : 65001);
            }
        }
        return start;
    }

    public CommandRun? Launch(MenuItem item)
    {
        if (item.Action is not { } action) throw new InvalidOperationException("This item has no launch settings.");
        var start = CreateStartInfo(action);
        if (action.Kind != ActionKind.Command || action.Output != OutputMode.Capture)
        {
            using var process = Process.Start(start);
            return null;
        }
        var run = new CommandRun { Name = item.Name };
        lock (gate)
        {
            if (history.Count(r => !r.Finished) >= 8) throw new InvalidOperationException("Up to 8 output-capturing commands can run at once.");
            while (history.Count >= 20) history.Remove(history.First(r => r.Finished));
            history.Add(run);
        }
        try
        {
            run.Process = Process.Start(start) ?? throw new IOException("Could not start the process.");
            run.Job = ProcessJob.TryAttach(run.Process);
            if (run.Job is null)
            {
                if (!run.Process.HasExited) run.Process.Kill(true);
                throw new IOException("Could not create the Windows Job used to stop this command. The command was not run.");
            }
            // The shell waits for this gate before executing any user command; all descendants inherit the job.
            run.Process.StandardInput.WriteLine("1");
            run.Process.StandardInput.Close();
        }
        catch
        {
            run.Job?.Dispose();
            run.Process?.Dispose();
            lock (gate) history.Remove(run);
            throw;
        }
        Changed?.Invoke();
        _ = CollectAsync(run);
        return run;
    }
    private async Task CollectAsync(CommandRun run)
    {
        var process = run.Process!;
        async Task Drain(StreamReader reader, bool error)
        {
            var buffer = new char[4096]; int read;
            while ((read = await reader.ReadAsync(buffer)) != 0) run.Append(new string(buffer, 0, read), error);
        }
        try
        {
            var stdout = Drain(process.StandardOutput, false);
            var stderr = Drain(process.StandardError, true);
            await process.WaitForExitAsync();
            run.ExitCode = process.ExitCode;
            // Close descendants of captured commands as well, so inherited output handles cannot hang forever.
            run.Job?.Dispose(); run.Job = null;
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception e) { run.Append("\n" + e.Message + "\n", true); }
        finally
        {
            run.Job?.Dispose(); run.Job = null;
            lock (gate) { run.Finished = true; run.Process = null; }
            process.Dispose();
            Changed?.Invoke();
        }
    }
    public void Stop(Guid id)
    {
        var run = History.FirstOrDefault(r => r.Id == id);
        if (run is null || run.Finished) return;
        run.Stopped = true;
        try { run.Job?.Terminate(); if (run.Process is { HasExited: false } process) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception e) { run.Append("\nCould not stop the process: " + e.Message, true); }
    }
    private static string FindPwsh()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("PowerShell 7 was not found. Select Windows PowerShell or install PowerShell 7.");
    }
    public void Dispose() { foreach (var run in History.Where(r => !r.Finished)) Stop(run.Id); }
}
