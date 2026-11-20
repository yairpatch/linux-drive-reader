using System.IO;
using System.Text.Json;
using System.Windows;

namespace LinuxDriveReader;

/// <summary>Remembers the window's size and layout between runs.</summary>
public sealed class Settings
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public bool Maximized { get; set; }
    public double Sidebar { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public bool ShowHiddenFiles { get; set; } = true;
    public bool ShowPermissions { get; set; } = true;
    public bool ShowWindowsDisk { get; set; } = true;
    public bool ElevateAtStartup { get; set; }
    public bool VerifyIntegrity { get; set; } = true;
    public bool KeepDates { get; set; } = true;
    public bool OfferOpenFolder { get; set; } = true;
    public bool DoubleClickOpens { get; set; } = true;

    /// <summary>The settings in use; loaded once, saved whenever something changes.</summary>
    public static Settings Current { get; } = Load() ?? new Settings();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinuxDriveReader", "settings.json");

    private static Settings? Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Applies the saved bounds if they still fit on a connected screen.</summary>
    public void ApplyTo(Window window)
    {
        if (Width < 400 || Height < 300) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.Contains(new Rect(Left, Top, Math.Min(Width, screen.Width), Math.Min(Height, screen.Height)))) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Left; window.Top = Top; window.Width = Width; window.Height = Height;
        if (Maximized) window.WindowState = WindowState.Maximized;
    }
}
