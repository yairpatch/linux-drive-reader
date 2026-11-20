using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinuxDriveReader;

/// <summary>The window shown when the exe is run under a name containing "Setup".</summary>
public sealed class SetupWindow : Window
{
    private readonly RadioButton _everyone = new() { Content = "For everyone on this PC (recommended)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _justMe = new() { Content = "Just for me (no administrator rights needed)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _desktop = new() { Content = "Add a shortcut to the desktop", Margin = new Thickness(0, 14, 0, 0) };
    private readonly CheckBox _launch = new() { Content = "Start Linux Drive Reader when finished", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _install = new() { IsDefault = true, MinWidth = 110 };
    private readonly Button _close = new() { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

    public SetupWindow()
    {
        Title = Installer.AppName + " Setup";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        string? existing = Installer.InstalledFolder(out bool existingAllUsers);
        if (existing != null) { _everyone.IsChecked = existingAllUsers; _justMe.IsChecked = !existingAllUsers; }
        _install.Content = existing != null ? "Update" : "Install";
        _install.SetResourceReference(StyleProperty, "AccentButtonStyle");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = (existing != null ? "Update " : "Install ") + Installer.AppName, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {Installer.Version}. Reads Linux disks (ext2/3/4, btrfs, xfs, LUKS, LVM) from Windows without changing them." +
                   (existing != null ? $"\n\nIt is already installed in {existing} and will be replaced." : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14), Opacity = 0.8,
        });
        panel.Children.Add(new TextBlock { Text = "Install", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_everyone);
        panel.Children.Add(_justMe);
        panel.Children.Add(_desktop);
        panel.Children.Add(_launch);
        panel.Children.Add(_message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(_install);
        buttons.Children.Add(_close);
        panel.Children.Add(buttons);
        Content = panel;

        _install.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
    }

    private void Say(string text, bool error)
    {
        _message.Text = text;
        _message.Foreground = error ? Brushes.Firebrick : (Brush)FindResource(SystemColors.ControlTextBrushKey);
        _message.Visibility = Visibility.Visible;
    }

    private async Task InstallAsync()
    {
        bool allUsers = _everyone.IsChecked == true, desktop = _desktop.IsChecked == true;
        _install.IsEnabled = _everyone.IsEnabled = _justMe.IsEnabled = _desktop.IsEnabled = false;
        Say("Installing…", false);
        string? error = await Task.Run(() =>
        {
            try
            {
                if (allUsers && !App.IsElevated)
                {
                    // Program Files needs administrator rights: let a second, elevated copy do the copying.
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    psi.ArgumentList.Add("--install");
                    psi.ArgumentList.Add("--quiet");
                    if (desktop) psi.ArgumentList.Add("--desktop");
                    using var p = Process.Start(psi)!;
                    p.WaitForExit();
                    return p.ExitCode == 0 ? null : "Installation did not finish. If the app is open, close it and try again.";
                }
                Installer.Install(allUsers, desktop);
                return null;
            }
            catch (System.ComponentModel.Win32Exception) { return "Installing for everyone needs administrator rights, and the Windows prompt was declined. Choose 'Just for me' to install without them."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return ex.Message; }
        });

        if (error != null)
        {
            Say(error, true);
            _install.IsEnabled = _everyone.IsEnabled = _justMe.IsEnabled = _desktop.IsEnabled = true;
            return;
        }
        string exe = Path.Combine(Installer.TargetFolder(allUsers), "LinuxDriveReader.exe");
        if (_launch.IsChecked == true)
        {
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
            Close();
            return;
        }
        Say($"{Installer.AppName} is installed. You can find it in the Start menu.", false);
        _install.Visibility = Visibility.Collapsed;
        _launch.IsEnabled = false;
        _close.Content = "Close";
    }
}
