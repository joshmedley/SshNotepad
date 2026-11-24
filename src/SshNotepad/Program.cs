using System.Security.Cryptography;
using System.Text;

namespace SshNotepad;

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int process);

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Theme.Start();
        // One instance per data folder; starting it again opens a new window (or the named files) in it.
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalSettings.Home.ToLowerInvariant())))[..16];
        var mutex = new Mutex(true, "SshNotepad-" + suffix, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "SshNotepadShow-" + suffix);
        Shell.RequestOpen(args, !first);
        if (!first)
        {
            AllowSetForegroundWindow(-1); // any process: lets the running instance come to the front
            show.Set();
            return;
        }
        var shell = new Shell();
        var wait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => shell.Post(() => shell.OpenRequested(true)), null, -1, false);
        Application.Run(shell);
        wait.Unregister(null);
        mutex.Dispose();
        if (shell.RestartRequested) System.Diagnostics.Process.Start(Application.ExecutablePath);
    }
}
