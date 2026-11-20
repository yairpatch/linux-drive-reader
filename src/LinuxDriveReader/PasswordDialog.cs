using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LinuxDriveReader;

/// <summary>Asks for the password of an encrypted volume.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _box = new() { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
    public string Password => _box.Password;

    public PasswordDialog(Window owner, string volumeName, string? error)
    {
        Owner = owner;
        Title = "Unlock encrypted volume";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"{volumeName} is encrypted. Enter its password to read it.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        if (error != null)
            panel.Children.Add(new TextBlock { Text = error, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "Checking the password can take several seconds and up to about 1 GB of memory. The password is not saved.",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Unlock", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0) });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
