using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LinuxDriveReader;

/// <summary>
/// Installs and removes the app without any separate installer program: the app's own exe copies itself
/// into place, adds a Start menu entry, and registers itself under "Installed apps".
/// </summary>
public static class Installer
{
    public const string AppName = "Linux Drive Reader";
    private const string ExeName = "LinuxDriveReader.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LinuxDriveReader";

    public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>All users: Program Files (needs administrator rights). One user: that user's own programs folder.</summary>
    public static string TargetFolder(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static RegistryKey Hive(bool allUsers) =>
        RegistryKey.OpenBaseKey(allUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    /// <summary>Where the app is installed, if it is, and whether that is for all users.</summary>
    public static string? InstalledFolder(out bool allUsers)
    {
        foreach (bool machine in new[] { true, false })
        {
            using var hive = Hive(machine);
            using var key = hive.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, ExeName)))
            {
                allUsers = machine;
                return folder;
            }
        }
        allUsers = false;
        return null;
    }

    private static string StartMenuLink(bool allUsers) => Path.Combine(
        Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string DesktopLink(bool allUsers) => Path.Combine(
        Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Copies this exe into place and registers it. Throws with a message fit to show if it cannot.</summary>
    public static string Install(bool allUsers, bool desktopShortcut)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the program file to install.");
        string folder = TargetFolder(allUsers), target = Path.Combine(folder, ExeName);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            if (IsRunning(target)) throw new IOException($"{AppName} is running. Close it, then try again.");
            Directory.CreateDirectory(folder);
            File.Copy(source, target, overwrite: true);
        }

        CreateShortcut(StartMenuLink(allUsers), target);
        if (desktopShortcut) CreateShortcut(DesktopLink(allUsers), target);

        using var hive = Hive(allUsers);
        using var key = hive.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", folder);
        key.SetValue("DisplayIcon", target);
        key.SetValue("UninstallString", $"\"{target}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{target}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return target;
    }

    /// <summary>Removes shortcuts, the "Installed apps" entry, this user's settings, and then the program folder.</summary>
    public static void Uninstall(bool allUsers, string folder)
    {
        foreach (string link in new[] { StartMenuLink(allUsers), DesktopLink(allUsers) })
            try { File.Delete(link); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        using (var hive = Hive(allUsers)) hive.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinuxDriveReader");
        try { if (Directory.Exists(data)) Directory.Delete(data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // A running program cannot delete its own file, so leave that to a command that outlives this process.
        if (File.Exists(Path.Combine(folder, ExeName)) && Path.GetFileName(folder).Equals(AppName, StringComparison.OrdinalIgnoreCase))
        {
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(cmd, $"/c \"\"{Environment.SystemDirectory}\\ping.exe\" -n 4 127.0.0.1 >nul & rmdir /s /q \"{folder}\"\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory });
        }
    }

    private static bool IsRunning(string exePath)
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (p)
            {
                if (p.Id == me) continue;
                try { if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return File.Exists(exePath); } // elevated copy: cannot tell, assume so
            }
        }
        return false;
    }

    private static void CreateShortcut(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.Description = "Read Linux disks from Windows";
        link.Save();
    }
}
