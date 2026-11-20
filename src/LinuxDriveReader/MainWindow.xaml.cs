using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LinuxFs.Core;
using LinuxFs.Core.Containers;
using LinuxFs.Core.Detection;
using LinuxFs.Core.Devices;
using LinuxFs.Core.Smb;
using Microsoft.Win32;

namespace LinuxDriveReader;

public partial class MainWindow : Window
{
    private readonly List<DiskItem> _imageItems = new();
    private IBlockDevice? _device;
    private IFileSystem? _fs;
    private VolumeItem? _volume;        // the volume whose files are shown
    private VolumeItem? _unlockTarget;  // the encrypted volume the unlock panel is asking about
    private VolumeItem? _current;       // whatever the right-hand side is currently about
    private List<FsNode> _trail = new();
    private readonly Stack<List<FsNode>> _history = new();
    private List<EntryRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _sortColumn = "Name";
    private bool _sortDescending;
    private int _loadVersion;

    public MainWindow()
    {
        InitializeComponent();
        var settings = Settings.Current;
        settings.ApplyTo(this);
        if (settings is { Sidebar: >= 220 and <= 600 }) SidebarColumn.Width = new GridLength(settings.Sidebar);
        ApplyColumnSetting();
        AboutTitle.Text = $"Linux Drive Reader {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        // LDR_DEMO=1 hides this PC's own disks and the administrator notice: used for website screenshots.
        AdminBar.Visibility = App.IsElevated || DemoMode ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                if (File.Exists(arg)) AddImage(arg);
            await Task.Run(RemoveStaleDrives);
            await RefreshDisksAsync();
            var first = _imageItems.SelectMany(i => i.Volumes.OfType<VolumeItem>()).SelectMany(v => v.Browsable()).FirstOrDefault();
            if (first != null) await OpenVolumeAsync(first);
        };
        Closing += (_, _) => SaveSettings();
        Closed += (_, _) => { EjectAll(); CloseVolume(); };
        UpdateButtons();
    }

    private void SaveSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var s = Settings.Current;
        s.Left = bounds.Left; s.Top = bounds.Top; s.Width = bounds.Width; s.Height = bounds.Height;
        s.Maximized = WindowState == WindowState.Maximized;
        s.Sidebar = SidebarColumn.ActualWidth;
        s.Save();
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => SaveSettings();

    // ---------------------------------------------------------------- disks

    private async Task RefreshDisksAsync()
    {
        SetStatus("Looking for disks…");
        var disks = await Task.Run(ScanPhysicalDisks);
        foreach (var image in _imageItems) await Task.Run(() => FillVolumes(image));
        DiskTree.ItemsSource = disks.Concat(_imageItems).ToList();
        RestoreDriveLetters();
        SetStatus(StatusForFolder());
    }

    private static bool DemoMode => Environment.GetEnvironmentVariable("LDR_DEMO") == "1";

    private static List<DiskItem> ScanPhysicalDisks()
    {
        var items = new List<DiskItem>();
        if (DemoMode) return items;
        List<DiskInfo> disks;
        try { disks = DiskEnumerator.Enumerate(); }
        catch (Exception) { return items; }
        foreach (var d in disks)
        {
            if (d.IsSystemDisk && !Settings.Current.ShowWindowsDisk) continue;
            string path = d.Path;
            var tags = new List<string> { Format.Size(d.Size) };
            if (d.Bus.Length > 0) tags.Add(d.Bus);
            if (d.Removable) tags.Add("removable");
            if (d.IsSystemDisk) tags.Add("Windows disk");
            var item = new DiskItem
            {
                Title = d.Model,
                Id = $"{d.Path}|{d.Serial}|{d.Size}",
                Detail = $"Disk {d.Number} · " + string.Join(" · ", tags),
                Removable = d.Removable || d.Bus == "USB",
                Open = () => new CachedDevice(new PhysicalDiskDevice(path)),
            };
            FillVolumes(item);
            items.Add(item);
        }
        return items;
    }

    private static void FillVolumes(DiskItem item)
    {
        var found = new List<object>();
        try
        {
            using var dev = item.Open();
            foreach (var v in VolumeScanner.Scan(dev))
            {
                long offset = v.Partition.Offset, length = v.Partition.Length;
                var volume = new VolumeItem
                {
                    Disk = item, Fs = v.FileSystem, Size = length,
                    Name = v.Partition.Number == 0 ? "Whole disk" : $"Partition {v.Partition.Number}",
                    Location = $"{item.Id}/{v.Partition.Number}@{offset}",
                    PartitionType = v.Partition.TypeName, PartitionName = v.Partition.Name,
                    Map = root => new SubDevice(root, offset, length),
                };
                Expand(volume, dev);
                found.Add(volume);
            }
            if (found.Count == 0) found.Add(new NoteItem { Text = "No partitions found." });
        }
        catch (UnauthorizedAccessException) { found.Add(new NoteItem { Text = "Needs administrator rights to read." }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { found.Add(new NoteItem { Text = "Cannot read this disk: " + ex.Message }); }
        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Volumes.Clear();
            foreach (var f in found) item.Volumes.Add(f);
        });
    }

    /// <summary>
    /// Lists what a container holds: the logical volumes of an LVM group, or the inside of an
    /// encrypted volume once it has a key. Containers can be nested, so this recurses.
    /// </summary>
    private static void Expand(VolumeItem item, IBlockDevice root)
    {
        var children = new List<object>();
        try
        {
            if (item.IsLvm)
            {
                var device = item.Map(root);
                var group = LvmGroup.Read(device);
                foreach (var lv in group.Volumes)
                {
                    var fs = lv.Supported ? FsDetector.Detect(lv.Open(device)) : new DetectedFs("LVM volume", null, false, lv.Note);
                    var child = new VolumeItem
                    {
                        Disk = item.Disk, Name = $"{group.Name}/{lv.Name}", Size = lv.Size, Fs = fs,
                        Location = $"{item.Location}/{group.Name}/{lv.Name}",
                        Map = r => lv.Open(item.Map(r)),
                    };
                    Expand(child, root);
                    children.Add(child);
                }
                if (children.Count == 0) children.Add(new NoteItem { Text = "This volume group has no volumes." });
            }
            else if (item.IsLuks && item.Key is { } key)
            {
                var device = key.Open(item.Map(root));
                var child = new VolumeItem
                {
                    Disk = item.Disk, Name = "Unlocked contents", Size = device.Length, Fs = FsDetector.Detect(device),
                    Location = item.Location + "/unlocked",
                    Map = r => key.Open(item.Map(r)),
                };
                Expand(child, root);
                children.Add(child);
            }
            else return;
        }
        catch (Exception ex) when (IsExpected(ex)) { children.Add(new NoteItem { Text = "Cannot read this: " + ex.Message }); }
        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Children.Clear();
            foreach (var c in children) item.Children.Add(c);
        });
    }

    private DiskItem? AddImage(string path)
    {
        string full = Path.GetFullPath(path);
        var existing = _imageItems.FirstOrDefault(i => string.Equals(i.ImagePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        long size;
        try { size = new FileInfo(full).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Cannot open " + full + ": " + ex.Message); return null; }
        var item = new DiskItem
        {
            Title = Path.GetFileName(full),
            Id = full.ToLowerInvariant(),
            Detail = $"Disk image · {Format.Size(size)}",
            ImagePath = full,
            Open = () => new CachedDevice(new ImageFileDevice(full)),
        };
        _imageItems.Add(item);
        return item;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDisksAsync();

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open disk image", Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.iso)|*.img;*.dd;*.raw;*.bin;*.iso|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        await OpenImageAsync(dlg.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(File.Exists)) await OpenImageAsync(f);
    }

    private async Task OpenImageAsync(string path)
    {
        var item = AddImage(path);
        if (item == null) return;
        await RefreshDisksAsync();
        var first = item.Volumes.OfType<VolumeItem>().SelectMany(v => v.Browsable()).FirstOrDefault()
            ?? item.Volumes.OfType<VolumeItem>().FirstOrDefault(v => v.IsLuks)
            ?? item.Volumes.OfType<VolumeItem>().FirstOrDefault();
        if (first != null) await OpenVolumeAsync(first);
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (App.RestartElevated(_imageItems.Select(i => i.ImagePath!))) Application.Current.Shutdown();
    }

    // -------------------------------------------------------------- volumes

    private async void DiskTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is VolumeItem v && v != _current && !_busy)
            await Dispatcher.InvokeAsync(() => OpenVolumeAsync(v)).Task.Unwrap();
    }

    private void CloseVolume()
    {
        _fs?.Dispose(); _fs = null;
        _device?.Dispose(); _device = null;
        _volume = null;
        _unlockTarget = null;
        _trail = new();
        _rows = new();
        _history.Clear();
    }

    /// <summary>Replaces the file list with a centred icon and message.</summary>
    private void ShowMessage(string icon, string title, string text)
    {
        FileList.ItemsSource = null;
        FileList.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Collapsed;
        EmptyIcon.Text = icon; EmptyTitle.Text = title; EmptyText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private async Task OpenVolumeAsync(VolumeItem v)
    {
        if (_busy) return;
        ShowSettings(false);
        CloseVolume();
        _current = v;
        v.IsSelected = true;
        WarningBar.Visibility = Visibility.Collapsed;
        FilterBox.Text = "";
        Breadcrumb.Children.Clear();
        UpdateButtons();
        SetStatus("Ready");

        if (v.IsLuks && v.Key == null) { ShowUnlock(v); return; }
        if (v.IsLuks || v.IsLvm)
        {
            if (v.Browsable().Any()) ShowMessage("", v.IsLvm ? "LVM volume group" : "Unlocked", "Choose one of the volumes listed under it on the left.");
            else ShowMessage("", "Nothing readable here", "None of the volumes inside can be read by this version.");
            return;
        }
        if (!v.Supported)
        {
            if (v.Fs.Type == FsDetector.Unknown) ShowMessage("", "No known filesystem", "Nothing recognisable was found here. It may be empty, or use a format this app does not know.");
            else if (v.Fs.Type == "swap") ShowMessage("", "Linux swap space", "Swap is scratch memory for Linux. It holds no files.");
            else ShowMessage("", $"{v.Fs.Type} cannot be read", $"{v.Fs.Note ?? "Not supported yet"}. This version reads ext2, ext3, ext4, btrfs and xfs.");
            return;
        }

        ShowMessage("", "Opening…", v.Title);
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var (device, fs) = await Task.Run(() =>
            {
                var dev = v.Disk.Open();
                try { return (dev, FileSystems.Open(v.Map(dev), new OpenOptions(Settings.Current.VerifyIntegrity, Settings.Current.ReplayJournal))); }
                catch { dev.Dispose(); throw; }
            });
            _device = device; _fs = fs; _volume = v;
            _fsVerified = Settings.Current.VerifyIntegrity;
            _fsReplayed = Settings.Current.ReplayJournal;
            if (fs.Warnings.Count > 0)
            {
                WarningText.Text = string.Join("\n", fs.Warnings);
                WarningBar.Visibility = Visibility.Visible;
            }
            await NavigateAsync(new List<FsNode> { fs.Root }, remember: false);
            // LDR_DEMO_PATH=some/folder opens that folder if it exists here: used for website screenshots.
            if (DemoMode && Environment.GetEnvironmentVariable("LDR_DEMO_PATH") is { Length: > 0 } demoPath
                && await Task.Run(() => FsPath.Resolve(fs, new List<FsNode> { fs.Root }, demoPath)) is { } demoTrail && demoTrail[^1].Kind == FsNodeKind.Directory)
                await NavigateAsync(demoTrail, remember: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            CloseVolume();
            ShowMessage("", "This could not be opened", ex.Message);
        }
        finally { LoadingBar.Visibility = Visibility.Hidden; }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException;

    // -------------------------------------------------------------- unlock

    private void ShowUnlock(VolumeItem v)
    {
        _unlockTarget = v;
        FileList.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        UnlockTitle.Text = $"{v.Title} is encrypted";
        UnlockPassword.Clear();
        UnlockError.Visibility = Visibility.Collapsed;
        UnlockBusy.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() => UnlockPassword.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e) => await UnlockAsync();

    private async void UnlockPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await UnlockAsync(); }
    }

    private async Task UnlockAsync()
    {
        var v = _unlockTarget;
        if (v == null || _busy) return;
        string password = UnlockPassword.Password;
        _busy = true;
        UnlockPassword.IsEnabled = UnlockButton.IsEnabled = false;
        UnlockError.Visibility = Visibility.Collapsed;
        UnlockBusy.Visibility = Visibility.Visible;
        LoadingBar.Visibility = Visibility.Visible;
        UpdateButtons();
        bool unlocked = false;
        try
        {
            await Task.Run(() =>
            {
                using var root = v.Disk.Open();
                var key = LuksVolume.Open(v.Map(root)).Unlock(password);
                Application.Current.Dispatcher.Invoke(() => v.Key = key);
                Expand(v, root);
            });
            unlocked = true;
        }
        catch (WrongPasswordException) { ShowUnlockError("That password is not correct. Check Caps Lock and the keyboard layout, then try again."); }
        catch (Exception ex) when (IsExpected(ex)) { ShowUnlockError(ex.Message); }
        finally
        {
            _busy = false;
            UnlockPassword.IsEnabled = UnlockButton.IsEnabled = true;
            UnlockBusy.Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Hidden;
            UpdateButtons();
        }
        if (!unlocked) { UnlockPassword.SelectAll(); UnlockPassword.Focus(); return; }

        UnlockPassword.Clear();
        var inside = v.Browsable().ToList();
        await OpenVolumeAsync(inside.Count == 1 ? inside[0] : v);
    }

    private void ShowUnlockError(string text)
    {
        UnlockError.Text = text;
        UnlockError.Visibility = Visibility.Visible;
    }

    // --------------------------------------------------------- drive letter

    /// <summary>A volume currently published as a Windows drive letter. It holds its own handle to the disk.</summary>
    private sealed record Mount(string Key, char Letter, string Share, IBlockDevice Device, IFileSystem Fs);

    private readonly List<Mount> _mounts = new();
    private readonly HashSet<string> _usedShares = new(StringComparer.OrdinalIgnoreCase);
    private SmbServer? _smb;

    private static string MountsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinuxDriveReader", "mounted.txt");

    /// <summary>Notes which letters are in use, so a later run can tidy up if this one ends without ejecting.</summary>
    private void RememberMounts()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MountsFile)!);
            File.WriteAllText(MountsFile, new string(_mounts.Select(m => m.Letter).ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes drive letters a previous run left behind (after a crash or power cut).</summary>
    private static void RemoveStaleDrives()
    {
        try
        {
            if (!File.Exists(MountsFile)) return;
            string letters = File.ReadAllText(MountsFile);
            foreach (char letter in letters) DriveMapper.RemoveIfStale(letter, App.IsElevated);
            File.Delete(MountsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async void MountDrive_Click(object sender, RoutedEventArgs e)
    {
        var v = _volume;
        if (v == null || _busy) return;
        if (!DriveMapper.IsSupported)
        {
            ShowError("Mounting as a drive letter needs Windows 11 version 24H2 or newer. On this version of Windows you can still browse and copy files here.");
            return;
        }
        if (DriveMapper.FindFreeLetter() is not { } letter) { ShowError("Every drive letter is already in use."); return; }

        _busy = true;
        LoadingBar.Visibility = Visibility.Visible;
        UpdateButtons();
        SetStatus($"Mounting as drive {letter}:…");
        try
        {
            bool elevated = App.IsElevated;
            var options = new OpenOptions(Settings.Current.VerifyIntegrity, Settings.Current.ReplayJournal);
            var mount = await Task.Run(() =>
            {
                var device = v.Disk.Open();
                try
                {
                    var fs = FileSystems.Open(v.Map(device), options);
                    _smb ??= SmbServer.ForCurrentUser();
                    string share = ShareName(fs.Label.Length > 0 ? fs.Label : v.Name);
                    _smb.AddShare(share, fs, v.Size);
                    string? error = DriveMapper.Map(letter, _smb, share, viaShell: elevated);
                    if (error != null) { _smb.RemoveShare(share); throw new IOException(error); }
                    return new Mount(v.Location, letter, share, device, fs);
                }
                catch { device.Dispose(); throw; }
            });
            _mounts.Add(mount);
            RememberMounts();
            v.DriveLetter = letter;
            if (Settings.Current.OpenExplorerAfterMount) OpenInExplorer(letter);
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowError("This volume could not be mounted as a drive: " + ex.Message); }
        finally
        {
            _busy = false;
            LoadingBar.Visibility = Visibility.Hidden;
            UpdateButtons();
            SetStatus(StatusForFolder());
        }
    }

    /// <summary>A share name Windows accepts, unique among the mounted volumes.</summary>
    private string ShareName(string wanted)
    {
        string clean = new(wanted.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray());
        if (clean.Length == 0) clean = "linux";
        // A name is never reused within one run, so Windows always makes a fresh connection to the new volume.
        string name = clean;
        for (int i = 2; !_usedShares.Add(name); i++) name = $"{clean}-{i}";
        return name;
    }

    private static void OpenInExplorer(char letter)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"{letter}:\\") { UseShellExecute = false }); }
        catch (Win32Exception) { }
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_volume != null && _mounts.FirstOrDefault(m => m.Key == _volume.Location) is { } mount) OpenInExplorer(mount.Letter);
    }

    private async void EjectDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_volume == null || _busy || _mounts.FirstOrDefault(m => m.Key == _volume.Location) is not { } mount) return;
        _busy = true;
        UpdateButtons();
        SetStatus($"Ejecting drive {mount.Letter}:…");
        bool elevated = App.IsElevated;
        await Task.Run(() => Eject(mount, elevated));
        _mounts.Remove(mount);
        RememberMounts();
        RestoreDriveLetters();
        _busy = false;
        UpdateButtons();
        SetStatus(StatusForFolder());
    }

    private void Eject(Mount mount, bool elevated)
    {
        DriveMapper.Unmap(mount.Letter, viaShell: elevated);
        Thread.Sleep(elevated ? 1500 : 200); // let Windows let go before the disk handle closes
        _smb?.RemoveShare(mount.Share);
        mount.Fs.Dispose();
        mount.Device.Dispose();
    }

    private void EjectAll()
    {
        bool elevated = App.IsElevated;
        foreach (var mount in _mounts.ToList())
        {
            try { Eject(mount, elevated); } catch (Exception ex) when (IsExpected(ex)) { }
        }
        _mounts.Clear();
        RememberMounts();
        _smb?.Dispose();
        _smb = null;
    }

    /// <summary>Re-marks mounted volumes after the disk list has been rebuilt or a container opened.</summary>
    private void RestoreDriveLetters()
    {
        IEnumerable<VolumeItem> All(IEnumerable<object> items) =>
            items.OfType<VolumeItem>().SelectMany(v => new[] { v }.Concat(All(v.Children)));
        if (DiskTree.ItemsSource is not IEnumerable<DiskItem> disks) return;
        foreach (var v in disks.SelectMany(d => All(d.Volumes)))
            v.DriveLetter = _mounts.FirstOrDefault(m => m.Key == v.Location)?.Letter;
    }

    // ------------------------------------------------------------- settings

    private bool _loadingSettings;
    private bool _fsVerified = true; // whether the open volume was opened with integrity checking
    private bool _fsReplayed = true; // ...and with its journal applied

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsView.Visibility != Visibility.Visible);

    private async void SettingsDone_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(false);
        // These are fixed when a volume is opened, so reopen it if either changed.
        if (_volume is { } v && (_fsVerified != Settings.Current.VerifyIntegrity || _fsReplayed != Settings.Current.ReplayJournal) && !_busy) await OpenVolumeAsync(v);
    }

    private void ShowSettings(bool show)
    {
        if (show)
        {
            if (_busy) return;
            var s = Settings.Current;
            _loadingSettings = true;
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme) ?? ThemeBox.Items[0];
            HiddenBox.IsChecked = s.ShowHiddenFiles;
            PermissionsBox.IsChecked = s.ShowPermissions;
            ElevateBox.IsChecked = s.ElevateAtStartup;
            WindowsDiskBox.IsChecked = s.ShowWindowsDisk;
            VerifyBox.IsChecked = s.VerifyIntegrity;
            JournalBox.IsChecked = s.ReplayJournal;
            DoubleClickBox.IsChecked = s.DoubleClickOpens;
            ExplorerBox.IsChecked = s.OpenExplorerAfterMount;
            DatesBox.IsChecked = s.KeepDates;
            OpenFolderBox.IsChecked = s.OfferOpenFolder;
            _loadingSettings = false;
        }
        SettingsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BrowserView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Any control on the settings page changed: store everything and apply what can change live.</summary>
    private async void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !IsLoaded) return;
        var s = Settings.Current;
        bool disksChanged = s.ShowWindowsDisk != (WindowsDiskBox.IsChecked == true);
        s.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        s.ShowHiddenFiles = HiddenBox.IsChecked == true;
        s.ShowPermissions = PermissionsBox.IsChecked == true;
        s.ElevateAtStartup = ElevateBox.IsChecked == true;
        s.ShowWindowsDisk = WindowsDiskBox.IsChecked == true;
        s.VerifyIntegrity = VerifyBox.IsChecked == true;
        s.ReplayJournal = JournalBox.IsChecked == true;
        s.DoubleClickOpens = DoubleClickBox.IsChecked == true;
        s.OpenExplorerAfterMount = ExplorerBox.IsChecked == true;
        s.KeepDates = DatesBox.IsChecked == true;
        s.OfferOpenFolder = OpenFolderBox.IsChecked == true;
        s.Save();

        ((App)Application.Current).ApplyTheme(Environment.GetEnvironmentVariable("LDR_THEME") ?? s.Theme);
        ApplyColumnSetting();
        if (_fs != null) { ApplyView(); SetStatus(StatusForFolder()); }
        if (disksChanged) await RefreshDisksAsync();
    }

    private void ApplyColumnSetting()
    {
        if (FileList.View is not GridView view) return;
        bool want = Settings.Current.ShowPermissions, have = view.Columns.Contains(PermissionsColumn);
        if (want && !have) view.Columns.Add(PermissionsColumn);
        else if (!want && have) view.Columns.Remove(PermissionsColumn);
        FileList_SizeChanged(this, null!);
    }

    // ----------------------------------------------------------- navigation

    private string CurrentPath => _trail.Count <= 1 ? "/" : "/" + string.Join("/", _trail.Skip(1).Select(n => n.Name));

    private async Task NavigateAsync(List<FsNode> trail, bool remember = true)
    {
        var fs = _fs;
        if (fs == null) return;
        int version = ++_loadVersion;
        LoadingBar.Visibility = Visibility.Visible;
        IReadOnlyList<FsNode> entries;
        try { entries = await Task.Run(() => fs.ListDirectory(trail[^1])); }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version == _loadVersion)
            {
                LoadingBar.Visibility = Visibility.Hidden;
                if (_trail.Count == 0) ShowMessage("", "This folder could not be read", ex.Message);
                else ShowError("This folder could not be read: " + ex.Message);
            }
            return;
        }
        if (version != _loadVersion || fs != _fs) return;
        LoadingBar.Visibility = Visibility.Hidden;

        if (remember && _trail.Count > 0) _history.Push(_trail);
        _trail = trail;
        _rows = entries.Select(n => new EntryRow(n)).ToList();
        if (FilterBox.Text.Length > 0) FilterBox.Text = ""; // also re-applies the list
        else ApplyView();
        BuildBreadcrumb();
        UpdateButtons();
        SetStatus(StatusForFolder());
        FileList.Focus();
    }

    /// <summary>The clickable path: one button per folder from the top of the volume down to here.</summary>
    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        for (int i = 0; i < _trail.Count; i++)
        {
            if (i > 0)
                Breadcrumb.Children.Add(new TextBlock
                {
                    Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"), FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 2, 0), Opacity = 0.6,
                });
            string label = i == 0 ? (_fs is { Label.Length: > 0 } f ? f.Label : _volume?.Name ?? "/") : _trail[i].Name;
            var prefix = _trail.Take(i + 1).ToList();
            bool current = i == _trail.Count - 1;
            var button = new Button
            {
                Content = label, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = i == 0 ? "Top of this volume" : null,
            };
            if (!current) button.Click += async (_, _) => { if (!_busy) await NavigateAsync(prefix); };
            Breadcrumb.Children.Add(button);
        }
        BreadcrumbScroll.ScrollToRightEnd();
    }

    /// <summary>Applies the filter text and the sort order to the current folder.</summary>
    private void ApplyView()
    {
        Func<EntryRow, IComparable> key = _sortColumn switch
        {
            "Size" => r => r.Node.Size,
            "Modified" => r => r.Node.ModifiedUtc ?? DateTime.MinValue,
            "Type" => r => r.TypeText,
            "Permissions" => r => r.Permissions,
            _ => r => r.Name.ToLowerInvariant(),
        };
        string filter = FilterBox.Text.Trim();
        var shown = Settings.Current.ShowHiddenFiles ? _rows : _rows.Where(r => !r.Name.StartsWith('.')).ToList();
        var visible = filter.Length == 0 ? shown : shown.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        IEnumerable<EntryRow> Order(IEnumerable<EntryRow> rows) =>
            _sortDescending ? rows.OrderByDescending(key).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                            : rows.OrderBy(key).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var list = Order(visible.Where(r => r.Node.Kind == FsNodeKind.Directory)).Concat(Order(visible.Where(r => r.Node.Kind != FsNodeKind.Directory))).ToList();

        if (_fs == null) return;
        if (list.Count == 0)
        {
            if (_rows.Count == 0) ShowMessage("", "This folder is empty", "");
            else if (shown.Count == 0) ShowMessage("", "Only hidden files here", "This folder holds only hidden items. Turn on 'Show hidden files' in Settings to see them.");
            else ShowMessage("", "No matches", $"Nothing in this folder has \"{filter}\" in its name.");
            return;
        }
        EmptyPanel.Visibility = UnlockPanel.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        FileList.ItemsSource = list;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_fs == null) return;
        ApplyView();
        SetStatus(StatusForFolder());
    }

    /// <summary>Lets the Name column take whatever width the other columns leave.</summary>
    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FileList.View is not GridView view) return;
        double others = view.Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
        NameColumn.Width = Math.Max(180, FileList.ActualWidth - others - 34);
    }

    private void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || FileList.View is not GridView view) return;
        string name = ((string)column.Header).TrimEnd(' ', '▲', '▼');
        if (_sortColumn == name) _sortDescending = !_sortDescending;
        else { _sortColumn = name; _sortDescending = false; }
        foreach (var c in view.Columns)
        {
            string plain = ((string)c.Header).TrimEnd(' ', '▲', '▼');
            c.Header = plain == _sortColumn ? plain + (_sortDescending ? "  ▼" : "  ▲") : plain;
        }
        ApplyView();
    }

    private string StatusForFolder()
    {
        if (_fs == null) return "Ready";
        string volume = $"{_fs.TypeName} · read-only";
        var selected = FileList.SelectedItems.Cast<EntryRow>().ToList();
        if (selected.Count > 0)
        {
            long bytes = selected.Where(r => r.Node.Kind == FsNodeKind.File).Sum(r => r.Node.Size);
            bool folders = selected.Any(r => r.Node.Kind == FsNodeKind.Directory);
            string size = bytes > 0 || !folders ? $" · {Format.Size(bytes)}{(folders ? " plus folders" : "")}" : "";
            return $"{selected.Count} selected{size}   —   {volume}";
        }
        int hidden = Settings.Current.ShowHiddenFiles ? 0 : _rows.Count(r => r.Name.StartsWith('.'));
        int total = _rows.Count - hidden, shown = FileList.Visibility == Visibility.Visible ? FileList.Items.Count : 0;
        string items = total == 1 ? "1 item" : $"{total} items";
        if (FilterBox.Text.Trim().Length > 0) items = $"{shown} of {items}";
        if (hidden > 0) items += $" ({hidden} hidden)";
        return $"{items}   —   {volume}";
    }

    private void UpdateButtons()
    {
        UpButton.IsEnabled = !_busy && _trail.Count > 1;
        BackButton.IsEnabled = !_busy && _history.Count > 0;
        SaveButton.IsEnabled = !_busy && _fs != null && _rows.Count > 0;
        FilterBox.IsEnabled = !_busy && _fs != null;
        var mount = _volume == null ? null : _mounts.FirstOrDefault(m => m.Key == _volume.Location);
        DriveButton.IsEnabled = !_busy && _fs != null && mount == null;
        DriveButton.Visibility = mount == null ? Visibility.Visible : Visibility.Collapsed;
        DriveBar.Visibility = mount != null && _fs != null ? Visibility.Visible : Visibility.Collapsed;
        if (mount != null) DriveBarText.Text = $"This volume is available in File Explorer and other programs as drive {mount.Letter}:  It is read-only.";
        int selected = FileList.SelectedItems.Count;
        SaveButtonText.Text = selected == 0 ? "Copy this folder to Windows" : selected == 1 ? "Copy to Windows" : $"Copy {selected} items to Windows";
        DiskTree.IsEnabled = !_busy;
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_trail.Count > 1 && !_busy) await NavigateAsync(_trail.Take(_trail.Count - 1).ToList());
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0 && !_busy) await NavigateAsync(_history.Pop(), remember: false);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (!_busy) SetStatus(StatusForFolder());
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(FileList, d) is not ListViewItem { Content: EntryRow row }) return;
        if (row.Node.Kind == FsNodeKind.File && !Settings.Current.DoubleClickOpens) return;
        await ActivateAsync(row);
    }

    private async void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is EntryRow row) { e.Handled = true; await ActivateAsync(row); }
        else if (e.Key == Key.Back) { e.Handled = true; Up_Click(sender, e); }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Alt && key == Key.Left) { e.Handled = true; Back_Click(sender, e); }
        else if (mods == ModifierKeys.Alt && key == Key.Up) { e.Handled = true; Up_Click(sender, e); }
        else if (mods == ModifierKeys.Control && key == Key.F && FilterBox.IsEnabled) { e.Handled = true; FilterBox.Focus(); FilterBox.SelectAll(); }
        else if (mods == ModifierKeys.Control && key == Key.O) { e.Handled = true; OpenImage_Click(sender, e); }
        else if (mods == ModifierKeys.None && key == Key.F5) { e.Handled = true; if (!_busy) await RefreshDisksAsync(); }
        else if (mods == ModifierKeys.None && key == Key.Escape && FilterBox.IsKeyboardFocusWithin) { e.Handled = true; FilterBox.Text = ""; FileList.Focus(); }
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is EntryRow row) await ActivateAsync(row);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = FileList.SelectedItems.Cast<EntryRow>().Select(r => CurrentPath.TrimEnd('/') + "/" + r.Name).ToList();
        if (paths.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy */ }
    }

    private async Task ActivateAsync(EntryRow row)
    {
        var fs = _fs;
        if (fs == null || _busy) return;
        var node = row.Node;
        switch (node.Kind)
        {
            case FsNodeKind.Directory:
                await NavigateAsync(_trail.Append(node).ToList());
                break;
            case FsNodeKind.Symlink:
                if (node.LinkTarget == null) { ShowError("This link could not be read."); break; }
                var trail = _trail;
                List<FsNode>? target;
                try { target = await Task.Run(() => FsPath.Resolve(fs, trail, node.LinkTarget)); }
                catch (Exception ex) when (IsExpected(ex)) { ShowError(ex.Message); break; }
                if (target == null) ShowError($"This link points to \"{node.LinkTarget}\", which does not exist on this volume.");
                else if (target[^1].Kind == FsNodeKind.Directory) await NavigateAsync(target);
                else if (target[^1].Kind == FsNodeKind.File) await OpenFileAsync(target[^1]);
                break;
            case FsNodeKind.File:
                await OpenFileAsync(node);
                break;
            default:
                ShowError(node.Error ?? $"\"{node.Name}\" is a {node.TypeDescription.ToLowerInvariant()}, not a file with content.");
                break;
        }
    }

    // -------------------------------------------------------------- copying

    /// <summary>Copies a file to a temporary folder and opens it with its Windows program.</summary>
    private async Task OpenFileAsync(FsNode node)
    {
        string folder = Path.Combine(Path.GetTempPath(), "LinuxDriveReader", Guid.NewGuid().ToString("N"));
        var result = await RunExportAsync(new[] { node }, folder, "Opening");
        if (result == null) return;
        if (result.Files != 1) { ShowError("This file could not be read: " + (result.Errors.FirstOrDefault() ?? "unknown error")); return; }
        string file = Directory.GetFiles(folder).First();
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Win32Exception ex) { ShowError("Windows could not open this file: " + ex.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_fs == null || _busy) return;
        var nodes = FileList.SelectedItems.Count > 0
            ? FileList.SelectedItems.Cast<EntryRow>().Select(r => r.Node).ToList()
            : _rows.Select(r => r.Node).ToList();
        if (nodes.Count == 0) return;

        var dlg = new OpenFolderDialog { Title = "Choose where to copy the files" };
        if (dlg.ShowDialog(this) != true) return;
        var result = await RunExportAsync(nodes, dlg.FolderName, "Copying", Settings.Current.KeepDates);
        if (result == null) return;

        var lines = new List<string> { $"Copied {result.Files} file(s) in {result.Folders} folder(s), {Format.Size(result.Bytes)}." };
        if (result.Renamed.Count > 0)
            lines.Add($"\n{result.Renamed.Count} name(s) were changed because Windows does not allow them:\n" + Sample(result.Renamed.Select(r => $"{r.Original} → {r.SavedAs}")));
        if (result.Skipped.Count > 0)
            lines.Add($"\n{result.Skipped.Count} item(s) were not copied because they are links or special files:\n" + Sample(result.Skipped));
        if (result.Errors.Count > 0)
            lines.Add($"\n{result.Errors.Count} item(s) FAILED to copy:\n" + Sample(result.Errors));
        bool offer = Settings.Current.OfferOpenFolder;
        if (offer) lines.Add("\nOpen the folder now?");
        var answer = MessageBox.Show(this, string.Join("\n", lines), result.Errors.Count > 0 ? "Copy finished with problems" : "Copy finished",
            offer ? MessageBoxButton.YesNo : MessageBoxButton.OK, result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(dlg.FolderName) { UseShellExecute = true }); }
            catch (Win32Exception) { }
        }
    }

    private static string Sample(IEnumerable<string> items)
    {
        var list = items.ToList();
        string text = string.Join("\n", list.Take(8).Select(s => "  " + s));
        return list.Count > 8 ? text + $"\n  …and {list.Count - 8} more" : text;
    }

    private async Task<ExportResult?> RunExportAsync(IReadOnlyList<FsNode> nodes, string destination, string verb, bool keepDates = true)
    {
        var fs = _fs;
        if (fs == null || _busy) return null;
        _busy = true;
        _cts = new CancellationTokenSource();
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        UpdateButtons();
        SetStatus($"{verb}…");
        var lastReport = Stopwatch.StartNew();
        var started = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            if (lastReport.ElapsedMilliseconds < 120 || !_busy) return;
            lastReport.Restart();
            double seconds = Math.Max(started.Elapsed.TotalSeconds, 0.5);
            SetStatus($"{verb} {p.CurrentPath}   —   {p.FilesCopied} file(s), {Format.Size(p.BytesCopied)}, {Format.Size((long)(p.BytesCopied / seconds))}/s");
        });
        try
        {
            var token = _cts.Token;
            return await Task.Run(() => Exporter.Export(fs, nodes, destination, progress, token, keepDates));
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "Cancelled. Files already copied were kept.", "Linux Drive Reader"); return null; }
        catch (Exception ex) when (IsExpected(ex)) { ShowError($"{verb} failed: {ex.Message}"); return null; }
        finally
        {
            _busy = false;
            _cts.Dispose(); _cts = null;
            Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            UpdateButtons();
            SetStatus(StatusForFolder());
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Linux Drive Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
}
