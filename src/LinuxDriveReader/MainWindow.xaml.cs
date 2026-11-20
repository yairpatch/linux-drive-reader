using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxFs.Core;
using LinuxFs.Core.Devices;
using Microsoft.Win32;

namespace LinuxDriveReader;

public partial class MainWindow : Window
{
    private readonly List<DiskItem> _imageItems = new();
    private IBlockDevice? _device;
    private IFileSystem? _fs;
    private VolumeItem? _volume;
    private List<FsNode> _trail = new();
    private readonly Stack<List<FsNode>> _history = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _sortColumn = "Name";
    private bool _sortDescending;
    private int _loadVersion;

    public MainWindow()
    {
        InitializeComponent();
        using (var id = WindowsIdentity.GetCurrent())
            AdminBanner.Visibility = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                if (File.Exists(arg)) AddImage(arg);
            await RefreshDisksAsync();
            var first = _imageItems.SelectMany(i => i.Volumes.OfType<VolumeItem>()).FirstOrDefault(v => v.Supported);
            if (first != null) await OpenVolumeAsync(first);
        };
        Closed += (_, _) => CloseVolume();
    }

    // ---------------------------------------------------------------- disks

    private async Task RefreshDisksAsync()
    {
        SetStatus("Looking for disks…");
        var disks = await Task.Run(ScanPhysicalDisks);
        foreach (var image in _imageItems) await Task.Run(() => FillVolumes(image));
        DiskTree.ItemsSource = disks.Concat(_imageItems).ToList();
        SetStatus(_fs == null ? "Ready" : StatusForFolder());
    }

    private static List<DiskItem> ScanPhysicalDisks()
    {
        var items = new List<DiskItem>();
        List<DiskInfo> disks;
        try { disks = DiskEnumerator.Enumerate(); }
        catch (Exception) { return items; }
        foreach (var d in disks)
        {
            string path = d.Path;
            var tags = new List<string> { Format.Size(d.Size) };
            if (d.Bus.Length > 0) tags.Add(d.Bus);
            if (d.Removable) tags.Add("removable");
            if (d.IsSystemDisk) tags.Add("Windows disk");
            var item = new DiskItem
            {
                Title = $"Disk {d.Number} — {d.Model}",
                Detail = string.Join(" · ", tags),
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
            foreach (var v in VolumeScanner.Scan(dev)) found.Add(new VolumeItem { Disk = item, Info = v });
            if (found.Count == 0) found.Add(new NoteItem { Text = "No partitions found." });
        }
        catch (UnauthorizedAccessException) { found.Add(new NoteItem { Text = "Administrator rights are needed to read this disk." }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { found.Add(new NoteItem { Text = "Cannot read this disk: " + ex.Message }); }
        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Volumes.Clear();
            foreach (var f in found) item.Volumes.Add(f);
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
        var first = item.Volumes.OfType<VolumeItem>().FirstOrDefault(v => v.Supported);
        if (first != null) await OpenVolumeAsync(first);
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var image in _imageItems) psi.ArgumentList.Add(image.ImagePath!);
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* the user said no at the Windows prompt */ }
    }

    // -------------------------------------------------------------- volumes

    private async void DiskTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is VolumeItem v && v != _volume && !_busy) await OpenVolumeAsync(v);
    }

    private void CloseVolume()
    {
        _fs?.Dispose(); _fs = null;
        _device?.Dispose(); _device = null;
        _volume = null;
        _trail = new();
        _history.Clear();
    }

    private async Task OpenVolumeAsync(VolumeItem v)
    {
        if (_busy) return;
        CloseVolume();
        FileList.ItemsSource = null;
        WarningBar.Visibility = Visibility.Collapsed;
        PathBox.Text = "";
        UpdateButtons();

        if (!v.Supported)
        {
            EmptyHint.Text = v.Info.FileSystem.Type == LinuxFs.Core.Detection.FsDetector.Unknown
                ? "No known filesystem was found on this partition."
                : $"This partition contains {v.Info.FileSystem.Type}, which this version cannot read yet. It currently reads ext2, ext3, ext4, btrfs and xfs.";
            EmptyHint.Visibility = Visibility.Visible;
            SetStatus("Ready");
            return;
        }

        SetStatus("Opening…");
        try
        {
            var (device, fs) = await Task.Run(() =>
            {
                var dev = v.Disk.Open();
                try
                {
                    var part = new SubDevice(dev, v.Info.Partition.Offset, v.Info.Partition.Length);
                    return (dev, FileSystems.Open(part));
                }
                catch { dev.Dispose(); throw; }
            });
            _device = device; _fs = fs; _volume = v;
            if (fs.Warnings.Count > 0)
            {
                WarningText.Text = string.Join("\n", fs.Warnings);
                WarningBar.Visibility = Visibility.Visible;
            }
            await NavigateAsync(new List<FsNode> { fs.Root }, remember: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            CloseVolume();
            EmptyHint.Text = "This partition could not be opened: " + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            SetStatus("Ready");
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException;

    // ----------------------------------------------------------- navigation

    private string CurrentPath => _trail.Count <= 1 ? "/" : "/" + string.Join("/", _trail.Skip(1).Select(n => n.Name));

    private async Task NavigateAsync(List<FsNode> trail, bool remember = true)
    {
        var fs = _fs;
        if (fs == null) return;
        int version = ++_loadVersion;
        SetStatus("Reading folder…");
        IReadOnlyList<FsNode> entries;
        try { entries = await Task.Run(() => fs.ListDirectory(trail[^1])); }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version == _loadVersion) { ShowError("This folder could not be read: " + ex.Message); SetStatus(StatusForFolder()); }
            return;
        }
        if (version != _loadVersion || fs != _fs) return;

        if (remember && _trail.Count > 0) _history.Push(_trail);
        _trail = trail;
        _rows = entries.Select(n => new EntryRow(n)).ToList();
        ApplySort();
        PathBox.Text = CurrentPath;
        EmptyHint.Text = "This folder is empty.";
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
        SetStatus(StatusForFolder());
    }

    private List<EntryRow> _rows = new();

    private void ApplySort()
    {
        Func<EntryRow, IComparable> key = _sortColumn switch
        {
            "Size" => r => r.Node.Size,
            "Modified" => r => r.Node.ModifiedUtc ?? DateTime.MinValue,
            "Type" => r => r.TypeText,
            "Permissions" => r => r.Permissions,
            _ => r => r.Name.ToLowerInvariant(),
        };
        var folders = _rows.Where(r => r.Node.Kind == FsNodeKind.Directory);
        var others = _rows.Where(r => r.Node.Kind != FsNodeKind.Directory);
        IEnumerable<EntryRow> Order(IEnumerable<EntryRow> rows) =>
            _sortDescending ? rows.OrderByDescending(key).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                            : rows.OrderBy(key).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        FileList.ItemsSource = Order(folders).Concat(Order(others)).ToList();
    }

    private void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: string header }) return;
        if (_sortColumn == header) _sortDescending = !_sortDescending;
        else { _sortColumn = header; _sortDescending = false; }
        ApplySort();
    }

    private string StatusForFolder()
    {
        if (_fs == null) return "Ready";
        int selected = FileList.SelectedItems.Count;
        string items = _rows.Count == 1 ? "1 item" : $"{_rows.Count} items";
        string label = _fs.Label.Length > 0 ? $" \"{_fs.Label}\"" : "";
        return selected > 0 ? $"{items}, {selected} selected — {_fs.TypeName}{label}, read-only" : $"{items} — {_fs.TypeName}{label}, read-only";
    }

    private void UpdateButtons()
    {
        UpButton.IsEnabled = !_busy && _trail.Count > 1;
        BackButton.IsEnabled = !_busy && _history.Count > 0;
        SaveButton.IsEnabled = !_busy && _fs != null && _rows.Count > 0;
        SaveButton.Content = FileList.SelectedItems.Count > 0 ? "Copy selected to Windows…" : "Copy this folder to Windows…";
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
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem { Content: EntryRow row })
            await ActivateAsync(row);
    }

    private async void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is EntryRow row) { e.Handled = true; await ActivateAsync(row); }
        else if (e.Key == Key.Back) { e.Handled = true; Up_Click(sender, e); }
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is EntryRow row) await ActivateAsync(row);
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
                if (target == null) ShowError($"This link points to \"{node.LinkTarget}\", which does not exist on this partition.");
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
        var result = await RunExportAsync(new[] { node }, folder);
        if (result == null || result.Files != 1) return;
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
        var result = await RunExportAsync(nodes, dlg.FolderName);
        if (result == null) return;

        var lines = new List<string> { $"Copied {result.Files} file(s) in {result.Folders} folder(s), {Format.Size(result.Bytes)}." };
        if (result.Renamed.Count > 0)
            lines.Add($"\n{result.Renamed.Count} name(s) were changed because Windows does not allow them:\n" + Sample(result.Renamed.Select(r => $"{r.Original} → {r.SavedAs}")));
        if (result.Skipped.Count > 0)
            lines.Add($"\n{result.Skipped.Count} item(s) were not copied because they are links or special files:\n" + Sample(result.Skipped));
        if (result.Errors.Count > 0)
            lines.Add($"\n{result.Errors.Count} item(s) FAILED to copy:\n" + Sample(result.Errors));
        MessageBox.Show(this, string.Join("\n", lines), "Copy finished", MessageBoxButton.OK,
            result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static string Sample(IEnumerable<string> items)
    {
        var list = items.ToList();
        string text = string.Join("\n", list.Take(8).Select(s => "  " + s));
        return list.Count > 8 ? text + $"\n  …and {list.Count - 8} more" : text;
    }

    private async Task<ExportResult?> RunExportAsync(IReadOnlyList<FsNode> nodes, string destination)
    {
        var fs = _fs;
        if (fs == null || _busy) return null;
        _busy = true;
        _cts = new CancellationTokenSource();
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        UpdateButtons();
        var lastReport = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            if (lastReport.ElapsedMilliseconds < 100 || !_busy) return;
            lastReport.Restart();
            SetStatus($"Copying {p.CurrentPath} — {p.FilesCopied} file(s), {Format.Size(p.BytesCopied)}");
        });
        try
        {
            var token = _cts.Token;
            return await Task.Run(() => Exporter.Export(fs, nodes, destination, progress, token));
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "Copy cancelled. Files already copied were kept.", "Linux Drive Reader"); return null; }
        catch (Exception ex) when (IsExpected(ex)) { ShowError("Copy failed: " + ex.Message); return null; }
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
