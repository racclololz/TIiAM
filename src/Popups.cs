using System;
using System.Collections.Generic;

// After a click on an overlay, for 4 s: popups that still open on the primary monitor are moved to the clicked
// monitor, and popups that open on the clicked monitor but overlap its taskbar are lifted above it.
// Polling, not WinEvents: some XAML/WPF flyouts reuse an existing window, so no show event fires.
static partial class Tiam
{
    static int relocUntil, relocSecTop, baseDx, baseDy, menuDx, menuDy;
    static bool menuDeltaSet;
    static RECT relocPriMon, relocSecMon, relocPriTray;
    static readonly Dictionary<IntPtr, RECT> seen = new Dictionary<IntPtr, RECT>();
    static readonly Dictionary<IntPtr, RECT> pendRect = new Dictionary<IntPtr, RECT>();
    static readonly Dictionary<IntPtr, int> pendStable = new Dictionary<IntPtr, int>();
    static readonly Dictionary<IntPtr, RECT> placed = new Dictionary<IntPtr, RECT>(); // windows we moved, and where we put them

    static void StartPopupWatch(Overlay ov, RECT realBtn, int mirrorCx)
    {
        relocPriTray = cur.Tray;
        relocPriMon = MonitorRect(cur.Tray, false);
        relocSecMon = MonitorRect(ov.Sec.Rect, false);
        relocSecTop = ov.Sec.Rect.T;
        baseDx = mirrorCx - (realBtn.L + realBtn.R) / 2;
        baseDy = ov.Sec.Rect.T - cur.Tray.T;
        menuDeltaSet = false;
        relocUntil = Environment.TickCount + 4000;
        seen.Clear(); pendRect.Clear(); pendStable.Clear(); placed.Clear();
        EnumWindows(delegate (IntPtr h, IntPtr l) { if (IsWindowVisible(h)) { RECT wr; GetWindowRect(h, out wr); seen[h] = wr; } return true; }, IntPtr.Zero);
        SetTimer(msgWnd, new IntPtr(2), 30, IntPtr.Zero);
    }

    static bool OnPrimary(RECT r) { return r.R > relocPriMon.L && r.L < relocPriMon.R && r.B > relocPriMon.T && r.T < relocPriMon.B; }
    static bool OverlapsSecTaskbar(RECT r) { return r.W < 1000 && r.H >= 40 && r.L < relocSecMon.R && r.R > relocSecMon.L && r.T < relocSecTop && r.B > relocSecTop; }

    static void PollPopups()
    {
        POINT cp; GetCursorPos(out cp);
        // stop once time is up or the mouse has left the clicked monitor (popups from there are not ours to move)
        if (Environment.TickCount - relocUntil > 0 || !Contains(relocSecMon, cp.X, cp.Y)) { relocUntil = Environment.TickCount - 1; KillTimer(msgWnd, new IntPtr(2)); return; }
        var wins = new List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l) { if (IsWindowVisible(h)) wins.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in wins)
        {
            if (h == msgWnd || h == hookWnd || IsOurs(h)) continue;
            RECT r; GetWindowRect(h, out r);
            try
            {
                RECT where;
                if (placed.TryGetValue(h, out where))
                {
                    // the app moved or resized its popup again (animation, relayout): follow it
                    if (Same(where, r)) continue;
                    if (OnPrimary(r)) MovePopup(h, r, ClassOf(h));
                    else if (OverlapsSecTaskbar(r)) Place(h, r.L, relocSecTop - r.H, r);
                    else if (ClassOf(h) != "#32768" && Math.Abs(r.B - (relocSecTop - FlyoutGap())) > 8) Place(h, r.L, relocSecTop - FlyoutGap() - r.H, r); // resized (DPI change) or re-laid out: keep it on the taskbar edge
                    else placed[h] = r;
                    continue;
                }
                RECT old;
                if (seen.TryGetValue(h, out old) && Same(old, r) && !pendRect.ContainsKey(h)) continue;
                Consider(h, r);
            }
            catch (Exception e) { Log("reloc error " + e.Message); }
        }
    }

    static void Consider(IntPtr hwnd, RECT r)
    {
        string c = ClassOf(hwnd);
        if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "tooltips_class32") return;
        if (OverlapsSecTaskbar(r))
        {
            Place(hwnd, r.L, relocSecTop - r.H, r);
            Log("lifted " + c);
            return;
        }
        if (!OnPrimary(r)) return;
        if (c == "#32768") { MovePopup(hwnd, r, c); return; }
        if (r.W >= 1000 || r.H < 40) return;
        RECT last; int n;
        if (pendRect.TryGetValue(hwnd, out last) && Same(last, r)) { pendStable.TryGetValue(hwnd, out n); pendStable[hwnd] = ++n; }
        else { if (!pendRect.ContainsKey(hwnd)) Log("popup candidate " + c + " " + r.L + "," + r.T + "," + r.R + "," + r.B); n = 0; pendStable[hwnd] = 0; }
        pendRect[hwnd] = r;
        seen[hwnd] = r;
        // a flyout sits on the taskbar edge; one that has not finished animating in (paused while covered) is lower
        if (n >= 3 && r.B >= relocPriTray.T - 140 && r.B <= relocPriMon.B + 400) MovePopup(hwnd, r, c);
    }

    static void MovePopup(IntPtr hwnd, RECT r, string c)
    {
        int nx, ny;
        if (c == "#32768")
        {
            // one shared offset, so submenus stay attached to their parent menu
            if (!menuDeltaSet)
            {
                menuDx = baseDx; menuDy = baseDy;
                int x0 = r.L + menuDx, y0 = r.T + menuDy;
                if (x0 + r.W > relocSecMon.R) menuDx -= x0 + r.W - relocSecMon.R;
                if (x0 < relocSecMon.L) menuDx += relocSecMon.L - x0;
                if (y0 + r.H > relocSecTop) menuDy -= y0 + r.H - relocSecTop;
                if (y0 < relocSecMon.T) menuDy += relocSecMon.T - y0;
                menuDeltaSet = true;
            }
            nx = r.L + menuDx; ny = r.T + menuDy;
        }
        else
        {
            // only windows that sit on the primary taskbar edge get here: put them on the secondary taskbar edge
            nx = Math.Max(relocSecMon.L, Math.Min(r.L + baseDx, relocSecMon.R - r.W));
            ny = Math.Max(relocSecMon.T, relocSecTop - FlyoutGap() - r.H);
        }
        Place(hwnd, nx, ny, r);
        Log("moved " + c + " to " + nx + "," + ny);
    }

    static int FlyoutGap() { return (int)(12 * DpiOf(relocSecMon) / 96); }

    static void Place(IntPtr hwnd, int x, int y, RECT r)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        RECT now; GetWindowRect(hwnd, out now);
        if (now.H != r.H && now.T == y)
        {
            // crossing to a monitor with another DPI rescaled it during the move: keep the intended bottom edge
            SetWindowPos(hwnd, IntPtr.Zero, now.L, y + r.H - now.H, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            GetWindowRect(hwnd, out now);
        }
        placed[hwnd] = now;
    }
}
