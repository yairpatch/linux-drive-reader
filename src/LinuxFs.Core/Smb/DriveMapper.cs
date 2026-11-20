using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LinuxFs.Core.Smb;

/// <summary>Attaches a share of our local server to a Windows drive letter, using Windows' own "net use".</summary>
[SupportedOSPlatform("windows")]
public static class DriveMapper
{
    /// <summary>The custom-port option of "net use" first shipped in Windows 11 24H2 (build 26100).</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 26100;

    public static char? FindFreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (char c in "LMNOPQRSTUVWXYZKJIHGFED")
            if (!used.Contains(c) && !IsNetworkLetter(c)) return c;
        return null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, System.Text.StringBuilder remoteName, ref int length);

    /// <summary>True for a letter that is a network drive, including one that is currently disconnected and so not listed as a drive.</summary>
    private static bool IsNetworkLetter(char letter)
    {
        var buffer = new System.Text.StringBuilder(512);
        int length = buffer.Capacity;
        return WNetGetConnectionW(letter + ":", buffer, ref length) != 2250; // 2250 = "not a network connection"
    }

    /// <summary>
    /// Maps the drive. When the app runs as administrator the command is handed to Explorer to run,
    /// because a drive mapped by an elevated program is invisible to Explorer and ordinary programs.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Map(char letter, SmbServer server, string share, bool viaShell)
    {
        string arguments = $"use {letter}: \\\\{server.Address}\\{share} {server.Password} /user:{server.UserName} /TCPPORT:{server.Port} /persistent:no";
        string? error = Run(arguments, viaShell);
        if (error != null || !viaShell) return error; // run directly, net use itself reports success or failure

        // Handed to Explorer, the command's result is invisible; the server sees Windows arrive instead.
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 15000)
        {
            if (server.IsConnected(share)) return null;
            Thread.Sleep(100);
        }
        Unmap(letter, viaShell); // in case it half-worked
        return "Windows did not connect to the drive. Its file-sharing client may be turned off or blocked by policy on this PC.";
    }

    public static void Unmap(char letter, bool viaShell) => Run($"use {letter}: /delete /y", viaShell);

    /// <summary>
    /// Removes a drive letter left behind by a run that ended without ejecting (a crash, a power cut),
    /// but only if it still points at this computer's own server.
    /// </summary>
    public static void RemoveIfStale(char letter, bool viaShell)
    {
        if (!char.IsAsciiLetter(letter)) return;
        // Full paths: a "find" from elsewhere on PATH (Git, Cygwin) is a different program.
        string sys = Environment.SystemDirectory;
        string command = $"/c \"\"{sys}\\net.exe\" use {letter}: | \"{sys}\\find.exe\" \"127.0.0.1\" >nul && \"{sys}\\net.exe\" use {letter}: /delete /y\"";
        Run(command, viaShell, Path.Combine(sys, "cmd.exe"));
    }

    private static string? Run(string arguments, bool viaShell, string? program = null)
    {
        string net = program ?? Path.Combine(Environment.SystemDirectory, "net.exe");
        try
        {
            if (viaShell) { ShellExecuteUnelevated(net, arguments); return null; }
            var psi = new ProcessStartInfo(net, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (InvalidOperationException) { } return "Windows took too long to map the drive."; }
            return p.ExitCode == 0 ? null : output.Trim().Length > 0 ? output.Trim() : $"net use failed (code {p.ExitCode}).";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or COMException or InvalidCastException or InvalidOperationException)
        {
            return "Could not run Windows' drive-mapping command: " + ex.Message;
        }
    }

    // ---- asking Explorer to start a program for us (it runs without administrator rights) ----

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _VtblGap0_8();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In] ref object location, [In] ref object root, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _VtblGap0_12();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void _VtblGap0_12();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint item, ref Guid riid);
    }

    private static void ShellExecuteUnelevated(string file, string arguments)
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object desktop = 0, empty = Type.Missing; // CSIDL_DESKTOP
        var desktopWindow = (IServiceProvider)shellWindows.FindWindowSW(ref desktop, ref empty, 8, out _, 1);
        Guid topLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"), browserId = typeof(IShellBrowser).GUID, dispatch = new("00020400-0000-0000-C000-000000000046");
        var browser = (IShellBrowser)desktopWindow.QueryService(ref topLevelBrowser, ref browserId);
        var view = (IShellView)browser.QueryActiveShellView();
        dynamic folderView = view.GetItemObject(0, ref dispatch);
        dynamic shell = folderView.Application;
        shell.ShellExecute(file, arguments, "", "open", 0); // 0 = no window
    }
}
