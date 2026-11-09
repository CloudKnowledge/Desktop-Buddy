using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mochi.App.Platform;

/// <summary>
/// Per-user install/uninstall without administrator rights: copies Mochi to %LOCALAPPDATA%\Programs\Mochi,
/// adds a Start-menu shortcut and an entry in Settings → Apps so it can be removed cleanly.
/// </summary>
internal static class SelfInstaller
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mochi";
    public const string ExitEventName = @"Local\Mochi.DesktopPet.Exit";

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mochi");
    static string InstalledExe => Path.Combine(InstallDir, "Mochi.exe");
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mochi.lnk");

    /// <summary>True when this launch should run the installer (explicit flag or the file is named *Setup*).</summary>
    public static bool WantsInstall(string[] args) =>
        Array.Exists(args, a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool WantsUninstall(string[] args) =>
        Array.Exists(args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

    public static int Install()
    {
        try
        {
            string source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            StopRunningMochi();
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                CopyWithRetry(source, InstalledExe);

            CreateShortcut(ShortcutPath, InstalledExe, "A fluffy golden tabby kitten for your desktop");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                var version = typeof(SelfInstaller).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
                key.SetValue("DisplayName", "Mochi – desktop kitten");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Mochi");
                key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
            Log.Info($"Installed to {InstallDir}");
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Install failed", ex);
            MessageBox.Show($"Mochi could not be installed: {ex.Message}", "Mochi setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    public static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove Mochi from this computer?", "Uninstall Mochi", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return 1;
        bool removeData = !quiet && MessageBox.Show("Also delete Mochi's settings and memories?", "Uninstall Mochi",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        StopRunningMochi();
        try { StartupRegistration.Set(false); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Error("Could not remove startup entry", ex); }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        TryDelete(ShortcutPath);
        if (removeData)
        {
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi"));
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mochi"));
        }

        // The running executable cannot delete itself; a short-lived shell removes the folder after we exit.
        if (Directory.Exists(InstallDir))
        {
            var cmd = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        if (!quiet) MessageBox.Show("Mochi has been removed. Bye bye! 🐾", "Uninstall Mochi", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>Asks a running Mochi to save and exit, then waits briefly for it.</summary>
    static void StopRunningMochi()
    {
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
        {
            using (exit) exit.Set();
            for (int i = 0; i < 40 && Mutex.TryOpenExisting(Program.MutexName, out var m); i++)
            {
                m.Dispose();
                Thread.Sleep(100);
            }
        }
        // The mutex is released before the process has fully exited; wait so its exe is no longer locked.
        foreach (var p in Process.GetProcessesByName("Mochi"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.WaitForExit(5000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    static void CopyWithRetry(string source, string dest)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(source, dest, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(300); } // e.g. a scanner still has the old exe open
        }
    }

    static void CreateShortcut(string path, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Could not delete {path}", ex); }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Could not delete {path}", ex); }
    }
}
