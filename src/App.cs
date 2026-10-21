using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

[assembly: AssemblyTitle("Tiam")]
[assembly: AssemblyProduct("Tiam - tray icons on all monitors")]
[assembly: AssemblyDescription("Shows the Windows 11 tray icons on every taskbar")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

// The app's own tray icon and menu, "Start with Windows" (installs a copy under %LOCALAPPDATA%\Programs\Tiam),
// and the first-run tip. The exe is portable: nothing is written until the user turns on Start with Windows.
static partial class Tiam
{
    const uint WM_TRAYICON = WM_APP + 1;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppKey = @"Software\Tiam";
    static IntPtr ownerWnd, appIcon;
    static uint wmShowMenu;
    static bool iconAdded;

    static string SelfPath { get { return Path.GetFullPath(Assembly.GetExecutingAssembly().Location); } }
    static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Tiam"); } }
    static string InstallExe { get { return Path.Combine(InstallDir, "Tiam.exe"); } }
    static bool RunningInstalled { get { return string.Equals(SelfPath, InstallExe, StringComparison.OrdinalIgnoreCase); } }
    static string Version { get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } }

    static void CreateOwnerWindow()
    {
        // a real (hidden) top-level window: receives TaskbarCreated broadcasts and can own a popup menu
        ownerWnd = CreateWindowEx(WS_EX_TOOLWINDOW, "TiamWnd", "Tiam", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        int size = GetSystemMetrics(49); // SM_CXSMICON at the system DPI
        appIcon = LoadImage(hInst, new IntPtr(32512), 1, size, size, 0);
        if (appIcon == IntPtr.Zero) appIcon = LoadImage(IntPtr.Zero, new IntPtr(32512), 1, size, size, 0x8000); // stock icon
    }

    static NOTIFYICONDATA IconData()
    {
        var d = new NOTIFYICONDATA();
        d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
        d.hWnd = ownerWnd; d.uID = 1;
        d.szTip = ""; d.szInfo = ""; d.szInfoTitle = "";
        return d;
    }

    static void AddAppIcon()
    {
        var d = IconData();
        d.uFlags = 0x1 | 0x2 | 0x4 | 0x80; // MESSAGE | ICON | TIP | SHOWTIP
        d.uCallbackMessage = WM_TRAYICON; d.hIcon = appIcon; d.szTip = "Tiam: tray icons on all monitors";
        iconAdded = Shell_NotifyIcon(0, ref d) || Shell_NotifyIcon(1, ref d);
    }

    static void RemoveAppIcon()
    {
        if (!iconAdded) return;
        var d = IconData();
        Shell_NotifyIcon(2, ref d);
        iconAdded = false;
    }

    static void ShowTip(string title, string text)
    {
        var d = IconData();
        d.uFlags = 0x10; // INFO
        d.szInfoTitle = title; d.szInfo = text; d.dwInfoFlags = 0x1 | 0x20; // NIIF_INFO | NIIF_LARGE_ICON
        Shell_NotifyIcon(1, ref d);
    }

    static bool welcomeChecked;

    // First run only: say where the app lives, or what to switch on when there is no second taskbar.
    static void WelcomeOnce()
    {
        if (welcomeChecked) return;
        welcomeChecked = true;
        using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            if (k.GetValue("Welcomed") != null) return;
            k.SetValue("Welcomed", 1);
        }
        if (cur.Secs.Count == 0)
            ShowTip("Tiam is running", "No second taskbar found. Open Settings > Personalization > Taskbar > Taskbar behaviors and turn on \"Show my taskbar on all displays\".");
        else
            ShowTip("Tiam is running", "Your tray icons now show on your other taskbars too. Open Tiam again to start it with Windows or to quit.");
    }

    static bool AutoStartOn()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("Tiam") != null;
    }

    static void SetAutoStart(bool on)
    {
        if (on)
        {
            if (!RunningInstalled) { Directory.CreateDirectory(InstallDir); File.Copy(SelfPath, InstallExe, true); }
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("Tiam", "\"" + InstallExe + "\"");
        }
        else
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("Tiam", false);
            if (!RunningInstalled) TryDeleteInstall();
        }
        Log("autostart " + on);
    }

    // A newer copy started by hand replaces the installed copy, so Start with Windows always runs the latest one.
    static void RefreshInstalledCopy()
    {
        try
        {
            if (AutoStartOn() && !RunningInstalled && File.Exists(InstallExe)) File.Copy(SelfPath, InstallExe, true);
        }
        catch (Exception e) { Log("refresh install failed " + e.Message); }
    }

    static void TryDeleteInstall()
    {
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch (Exception e) { Log("delete install failed " + e.Message); }
    }

    static void ShowAppMenu()
    {
        POINT p; GetCursorPos(out p);
        IntPtr m = CreatePopupMenu();
        AppendMenu(m, 0x1, IntPtr.Zero, "Tiam " + Version);                            // MF_GRAYED
        AppendMenu(m, 0x800, IntPtr.Zero, null);                                          // MF_SEPARATOR
        AppendMenu(m, AutoStartOn() ? 0x8u : 0u, new IntPtr(1), "Start with Windows");   // MF_CHECKED
        AppendMenu(m, 0, new IntPtr(2), "Quit");
        SetForegroundWindow(ownerWnd);
        int cmd = TrackPopupMenuEx(m, 0x100 | 0x2 | 0x10000, p.X, p.Y, ownerWnd, IntPtr.Zero); // RETURNCMD | RIGHTBUTTON | WORKAREA
        PostMessage(ownerWnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(m);
        Log("menu command " + cmd);
        try
        {
            if (cmd == 1) SetAutoStart(!AutoStartOn());
            else if (cmd == 2) Quit();
        }
        catch (Exception e)
        {
            Log("menu action failed " + e);
            MessageBox(ownerWnd, "Tiam could not change the setting:\n" + e.Message, "Tiam", 0x30);
        }
    }

    static void Quit()
    {
        RemoveAppIcon();
        foreach (var o in overlays.Values) if (o.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(o.Thumb);
        if (hookWnd != IntPtr.Zero) { DestroyWindow(hookWnd); hookWnd = IntPtr.Zero; } // apps talk to Explorer directly again
        if (RunningInstalled && !AutoStartOn())
        {
            // uninstall: the running exe cannot delete itself, so a short-lived cmd removes the folder after exit
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
            try { Process.Start(psi); } catch { }
        }
        PostQuitMessage(0);
    }

    static IntPtr OwnerProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_TRAYICON)
        {
            uint ev = (uint)((long)l & 0xFFFF);
            if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP) ShowAppMenu();
            return IntPtr.Zero;
        }
        if (m == wmTaskbarCreated && wmTaskbarCreated != 0) { AddAppIcon(); return IntPtr.Zero; }
        if (m == wmShowMenu && wmShowMenu != 0) { ShowAppMenu(); return IntPtr.Zero; }
        return DefWindowProc(h, m, w, l);
    }
}
