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

        if (Settings.Current.ElevateAtStartup && !IsElevated && RestartElevated(e.Args))
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // LDR_THEME=Light or Dark overrides the saved choice (handy for testing).
        // "Same as Windows" is already what App.xaml asks for, so only a fixed choice needs applying here.
        string theme = Environment.GetEnvironmentVariable("LDR_THEME") ?? Settings.Current.Theme;
        if (!string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)) ApplyTheme(theme);
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
