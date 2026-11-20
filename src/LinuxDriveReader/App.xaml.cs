using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace LinuxDriveReader;

public partial class App : Application
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Linux Drive Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Installing and uninstalling are done by this same exe.
        bool quiet = e.Args.Contains("--quiet");
        if (e.Args.Contains("--install")) { Shutdown(RunInstall(e.Args, quiet)); return; }
        if (e.Args.Contains("--uninstall")) { Shutdown(RunUninstall(e.Args, quiet)); return; }
        bool setup = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

        if (!setup && Settings.Current.ElevateAtStartup && !IsElevated && RestartElevated(e.Args))
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        (setup ? (Window)new SetupWindow() : new MainWindow()).Show();
        // LDR_THEME=Light or Dark overrides the saved choice (handy for testing).
        // "Same as Windows" is already what App.xaml asks for, so only a fixed choice needs applying here.
        string theme = Environment.GetEnvironmentVariable("LDR_THEME") ?? Settings.Current.Theme;
        if (!string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)) ApplyTheme(theme);
    }

    /// <summary>"--install [--peruser] [--desktop] [--quiet]": used by the setup window's elevated helper and for scripted installs.</summary>
    private static int RunInstall(string[] args, bool quiet)
    {
        try
        {
            Installer.Install(allUsers: !args.Contains("--peruser"), desktopShortcut: args.Contains("--desktop"));
            return 0;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            if (!quiet) MessageBox.Show("Installation failed: " + ex.Message, Installer.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>"--uninstall [--quiet]": what Windows' "Installed apps" page runs.</summary>
    private static int RunUninstall(string[] args, bool quiet)
    {
        string? folder = Installer.InstalledFolder(out bool allUsers);
        if (folder == null)
        {
            if (!quiet) MessageBox.Show($"{Installer.AppName} is not installed.", Installer.AppName);
            return 1;
        }
        if (!quiet && MessageBox.Show($"Remove {Installer.AppName} and its settings from this PC?\n\nYour Linux disks are not touched.", "Uninstall " + Installer.AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 2;
        if (allUsers && !IsElevated)
            return RestartElevated(new[] { "--uninstall", "--quiet" }) ? 0 : 1; // already confirmed above
        try
        {
            Installer.Uninstall(allUsers, folder);
            return 0;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!quiet) MessageBox.Show("Uninstall failed: " + ex.Message, Installer.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    public void ApplyTheme(string name) => ThemeMode =
        string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Light :
        string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark : ThemeMode.System;

    /// <summary>Starts a second copy with administrator rights; false if the user said no at the Windows prompt.</summary>
    public static bool RestartElevated(IEnumerable<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception) { return false; }
    }
}
