using System;
using System.Threading;
using System.Windows.Forms;

namespace Mochi.App;

internal static class Program
{
    public const string MutexName = @"Local\Mochi.DesktopPet.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
        if (Platform.SelfInstaller.WantsUninstall(args))
            return Platform.SelfInstaller.Uninstall(quiet: Array.Exists(args, a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
        if (Platform.SelfInstaller.WantsInstall(args))
            return Platform.SelfInstaller.Install();

        // One kitten per user session; a second launch just calls the existing one over.
        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Mochi.DesktopPet.Show");
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Platform.SelfInstaller.ExitEventName);
        using var ballSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Mochi.DesktopPet.Ball");
        bool wantsBall = Array.Exists(args, a => a.Equals("--ball", StringComparison.OrdinalIgnoreCase));
        if (!firstInstance)
        {
            (wantsBall ? ballSignal : showSignal).Set();
            return 0;
        }
        if (wantsBall) ballSignal.Set();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception (recovered)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Log.Info($"Starting Mochi {typeof(Program).Assembly.GetName().Version} ({string.Join(' ', args)})");
        try
        {
            bool atSignIn = Array.Exists(args, a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MochiAppContext(showSignal, exitSignal, ballSignal, atSignIn));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Mochi crashed during start-up", ex);
            MessageBox.Show($"Mochi could not start: {ex.Message}\n\nDetails were written to {Log.Directory}.", "Mochi",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}

/// <summary>
/// WPF (used only for the settings and welcome windows) is loaded on first use, so a kitten that is just
/// living on the desktop does not pay for it.
/// </summary>
internal static class WpfHost
{
    static System.Windows.Application? _app;

    public static void Ensure()
    {
        if (_app != null) return;
        _app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        _app.ThemeMode = System.Windows.ThemeMode.System; // Windows 11 Fluent look
        _app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled WPF exception (recovered)", e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Returns memory used by a closed window to the OS.</summary>
    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Platform.Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }
}
