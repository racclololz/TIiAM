// Tiam: mirrors the primary taskbar tray icons onto secondary taskbars (Windows 11).
// Compiled with the in-box .NET Framework csc.exe (C# 5): no ?., no $"", no =>.
// Layout: UI Automation lists the primary tray buttons. Pixels: a live DWM thumbnail of Explorer's taskbar,
// which keeps rendering while a fullscreen app covers the primary monitor. Clicks: the icon's own callback
// message (learned by TrayHook) is posted to its owner window; SendInput on the real icon is the fallback.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

static partial class Tiam
{
    class Item { public string Name; public RECT Btn; public Rec Rec; public AutomationElement El; }
    class Sec { public IntPtr Hwnd; public RECT Rect; public int ClockLeft; public bool Visible; }
    class Snap { public IntPtr TrayHwnd; public RECT Tray; public List<Item> Items = new List<Item>(); public List<Sec> Secs = new List<Sec>(); }
    class Overlay
    {
        public IntPtr Hwnd, HoverWnd, Thumb, ThumbSrc;
        public THUMBPROPS ThumbProps; public bool ThumbSet;
        public int MaxClockW, ClockForR;
        public Sec Sec; public RECT Pos; public int Hover = -1; public bool Shown, Tracking, IgnoreNextLeftUp; public float Scale = 1;
    }
    enum Kind { Left, Right, Middle, Double }

    static readonly object gate = new object();
    static Snap pending;
    static Snap cur = new Snap();
    static readonly Dictionary<IntPtr, Overlay> overlays = new Dictionary<IntPtr, Overlay>();
    static IntPtr msgWnd, hInst;
    static WndProcDelegate wndProc = WndProcImpl;
    static WndProcDelegate defProc = DefWindowProc;
    static WinEventProc zorderProc = delegate (IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        foreach (var o in overlays.Values) if (o.Shown && TaskbarAbove(o)) ApplyVisibility(o);
    };
    static bool log;
    static string logPath;

    static void Log(string s)
    {
        if (!log) return;
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch { }
    }

    static int Main(string[] args)
    {
        bool created;
        var mtx = new Mutex(true, "Local\\TIiAM-single-instance", out created);
        if (!created)
        {
            // starting it again opens the running copy's menu: Windows 11 often hides new tray icons behind "^"
            IntPtr other = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "TiamWnd", "Tiam");
            if (other != IntPtr.Zero)
            {
                uint pid; GetWindowThreadProcessId(other, out pid);
                AllowSetForegroundWindow(pid);
                PostMessage(other, RegisterWindowMessage("Tiam-ShowMenu"), IntPtr.Zero, IntPtr.Zero);
            }
            else MessageBox(IntPtr.Zero, "Tiam is already running.", "Tiam", 0x40);
            return 0;
        }
        wmShowMenu = RegisterWindowMessage("Tiam-ShowMenu");
        foreach (var a in args) if (a == "-log") log = true;
        logPath = Path.Combine(Path.GetTempPath(), "tiam.log");
        Log("start");

        hInst = Marshal.GetHINSTANCE(typeof(Tiam).Module);
        var wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.style = 0x8; // CS_DBLCLKS
        wc.proc = wndProc;
        wc.hInstance = hInst;
        wc.hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512));
        wc.hbrBackground = CreateSolidBrush(0);
        wc.cls = "TiamWnd";
        RegisterClassEx(ref wc);
        var hc = new WNDCLASSEX();
        hc.cbSize = wc.cbSize; hc.proc = defProc; hc.hInstance = hInst; hc.hbrBackground = CreateSolidBrush(0xFFFFFF); hc.cls = "TiamHover";
        RegisterClassEx(ref hc);
        msgWnd = CreateWindowEx(0, "TiamWnd", "TiamMsg", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInst, IntPtr.Zero);
        CreateOwnerWindow();
        RefreshInstalledCopy();

        SetTimer(msgWnd, new IntPtr(1), 250, IntPtr.Zero);
        // a click on a secondary taskbar brings it to the front: re-raise the bar on the z-order change, not on the next tick
        SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, zorderProc, 0, 0, 0);  // EVENT_SYSTEM_FOREGROUND
        SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, zorderProc, 0, 0, 0);  // EVENT_OBJECT_REORDER
        var t = new Thread(Worker);
        t.IsBackground = true;
        t.Start();

        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        GC.KeepAlive(mtx);
        return 0;
    }

    #region Worker (UI Automation scan, icon-to-record matching)
    static void Worker()
    {
        while (true)
        {
            try
            {
                var s = Scan();
                lock (gate) pending = s;
                PostMessage(msgWnd, WM_APP, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception e) { Log("scan error " + e.Message); }
            Thread.Sleep(700);
        }
    }

    static RECT ToRect(System.Windows.Rect r)
    {
        var o = new RECT();
        o.L = (int)Math.Round(r.Left); o.T = (int)Math.Round(r.Top);
        o.R = (int)Math.Round(r.Right); o.B = (int)Math.Round(r.Bottom);
        return o;
    }

    static Snap Scan()
    {
        var s = new Snap();
        IntPtr tray = FindExplorerTray();
        s.TrayHwnd = tray;
        if (tray != IntPtr.Zero)
        {
            GetWindowRect(tray, out s.Tray);
            var found = AutomationElement.FromHandle(tray).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon"));
            foreach (AutomationElement e in found)
            {
                var r = ToRect(e.Current.BoundingRectangle);
                if (r.W <= 0 || r.H <= 0) continue;
                var it = new Item(); it.Name = e.Current.Name; it.Btn = r; it.El = e;
                s.Items.Add(it);
            }
            s.Items.Sort(delegate (Item a, Item b) { return a.Btn.L.CompareTo(b.Btn.L); });
            if (hookWnd != IntPtr.Zero) MatchRecords(tray, s.Items);
        }
        var secHwnds = new List<IntPtr>();
        EnumWindows(delegate (IntPtr h, IntPtr l) { if (ClassOf(h) == "Shell_SecondaryTrayWnd") secHwnds.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in secHwnds)
        {
            var sec = new Sec(); sec.Hwnd = h; sec.Visible = IsWindowVisible(h);
            GetWindowRect(h, out sec.Rect);
            sec.ClockLeft = sec.Rect.R - 12 - 216 * sec.Rect.H / 72;
            try
            {
                var clock = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButton"));
                if (clock != null) sec.ClockLeft = ToRect(clock.Current.BoundingRectangle).L;
            }
            catch { }
            s.Secs.Add(sec);
        }
        return s;
    }

    static int lastMatchLog;

    static void MatchRecords(IntPtr tray, List<Item> items)
    {
        List<Rec> copy;
        lock (recLock) copy = new List<Rec>(recs);
        foreach (var r in copy)
        {
            if (!IsWindow(r.Hwnd)) { lock (recLock) recs.Remove(r); continue; }
            RECT er;
            if (!QueryExplorerRect(tray, r, out er)) continue;
            int cx = (er.L + er.R) / 2, cy = (er.T + er.B) / 2;
            foreach (var it in items) if (Contains(it.Btn, cx, cy)) { it.Rec = r; break; }
        }
        if (log && Environment.TickCount - lastMatchLog > 10000)
        {
            lastMatchLog = Environment.TickCount;
            Log("records " + copy.Count);
            foreach (var r in copy)
            {
                RECT er; bool ok = QueryExplorerRect(tray, r, out er);
                Log("  rec hwnd " + r.Hwnd + " uid " + r.Uid + " guid " + r.Guid + " cb 0x" + r.Callback.ToString("x") + " tip '" + r.Tip + "' rect " + (ok ? er.L + "," + er.T + "," + er.R + "," + er.B : "none"));
            }
            foreach (var it in items)
                Log("item '" + it.Name.Replace("\n", " ") + "' -> " + (it.Rec == null ? "no record" : "hwnd " + it.Rec.Hwnd + " uid " + it.Rec.Uid + " cb 0x" + it.Rec.Callback.ToString("x") + " v" + it.Rec.Version));
        }
    }
    #endregion

    #region UI thread
    static void OnScan()
    {
        Snap s;
        lock (gate) { s = pending; pending = null; }
        if (s == null) return;
        if (hookWnd == IntPtr.Zero && s.TrayHwnd != IntPtr.Zero) { StartTrayHook(hInst); AddAppIcon(); } // only once Explorer's tray exists
        if (s.Items.Count == 0 && cur.Items.Count > 0 && s.TrayHwnd == cur.TrayHwnd) s.Items = cur.Items; // transient UIA miss
        cur = s;
        Layout();
        if (iconAdded) WelcomeOnce();
    }

    static void Layout()
    {
        var alive = new HashSet<IntPtr>();
        foreach (var sec in cur.Secs)
        {
            alive.Add(sec.Hwnd);
            Overlay o = null;
            foreach (var ov in overlays.Values) if (ov.Sec.Hwnd == sec.Hwnd) o = ov;
            if (o == null)
            {
                o = new Overlay(); o.Sec = sec;
                o.Hwnd = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, "TiamWnd", "TiamBar", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                o.HoverWnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, "TiamHover", "", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                SetLayeredWindowAttributes(o.HoverWnd, 0, 30, 2);
                overlays[o.Hwnd] = o;
                Log("overlay " + o.Hwnd + " for " + sec.Hwnd);
            }
            o.Sec = sec;
            o.Scale = (float)DpiOf(sec.Rect) / (cur.TrayHwnd != IntPtr.Zero ? DpiOf(cur.Tray) : 96u);
            int w = cur.Items.Count == 0 ? 0 : (int)Math.Round((cur.Items[cur.Items.Count - 1].Btn.R - cur.Items[0].Btn.L) * o.Scale);
            // the clock's width changes with the time text (seconds): anchor to the widest clock seen, so the bar holds still
            int clockW = sec.Rect.R - sec.ClockLeft;
            if (o.ClockForR != sec.Rect.R) { o.ClockForR = sec.Rect.R; o.MaxClockW = 0; }
            if (clockW > o.MaxClockW) o.MaxClockW = clockW;
            int anchor = sec.Rect.R - o.MaxClockW;
            var pos = new RECT(); pos.L = anchor - w; pos.T = sec.Rect.T; pos.R = anchor; pos.B = sec.Rect.B;
            if (!Same(pos, o.Pos)) Log("layout scale=" + o.Scale + " pos=" + pos.L + "," + pos.T + "," + pos.R + "," + pos.B + " items=" + cur.Items.Count);
            bool moved = !Same(pos, o.Pos);
            o.Pos = pos;
            if (moved && log)
                for (int i = 0; i < cur.Items.Count; i++) { RECT sr = SlotRect(o, i); Log("  slot " + i + " x=" + (sr.L + sr.R) / 2 + " '" + cur.Items[i].Name.Replace("\n", " ") + "'"); }
            UpdateThumb(o);
            ApplyVisibility(o);
        }
        var dead = new List<IntPtr>();
        foreach (var kv in overlays) if (!alive.Contains(kv.Value.Sec.Hwnd)) dead.Add(kv.Key);
        foreach (var h in dead)
        {
            var o = overlays[h];
            if (o.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(o.Thumb);
            DestroyWindow(o.HoverWnd); DestroyWindow(h); overlays.Remove(h);
        }
    }

    static void UpdateThumb(Overlay o)
    {
        if (cur.TrayHwnd == IntPtr.Zero || cur.Items.Count == 0) return;
        if (o.Thumb == IntPtr.Zero || o.ThumbSrc != cur.TrayHwnd)
        {
            if (o.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(o.Thumb);
            IntPtr id;
            if (DwmRegisterThumbnail(o.Hwnd, cur.TrayHwnd, out id) != 0) { o.Thumb = IntPtr.Zero; return; }
            o.Thumb = id; o.ThumbSrc = cur.TrayHwnd; o.ThumbSet = false;
        }
        RECT first = cur.Items[0].Btn, last = cur.Items[cur.Items.Count - 1].Btn;
        var p = new THUMBPROPS();
        p.dwFlags = 0x1 | 0x2 | 0x8 | 0x10; // RECTDESTINATION | RECTSOURCE | VISIBLE | SOURCECLIENTAREAONLY
        p.dst.R = o.Pos.W; p.dst.B = o.Pos.H;
        p.src.L = first.L - cur.Tray.L; p.src.T = first.T - cur.Tray.T; p.src.R = last.R - cur.Tray.L; p.src.B = last.B - cur.Tray.T;
        p.visible = 1;
        if (o.ThumbSet && Same(p.src, o.ThumbProps.src) && Same(p.dst, o.ThumbProps.dst)) return;
        DwmUpdateThumbnailProperties(o.Thumb, ref p);
        o.ThumbProps = p; o.ThumbSet = true;
    }

    // A fullscreen app on the secondary monitor hides its taskbar: probe the clock, which the overlay never covers.
    static bool TaskbarHiddenBehindApp(Overlay o)
    {
        var p = new POINT(); p.X = o.Pos.R + 6; p.Y = (o.Pos.T + o.Pos.B) / 2;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return true;
        IntPtr root = GetAncestor(h, 2);
        if (ClassOf(root) == "Shell_SecondaryTrayWnd") return false;
        RECT r, mon = MonitorRect(o.Sec.Rect, false);
        GetWindowRect(root, out r);
        return r.L <= mon.L && r.T <= mon.T && r.R >= mon.R && r.B >= mon.B;
    }

    static void ApplyVisibility(Overlay o)
    {
        // an auto-hidden taskbar slides off the screen: follow its live position, not the last scan
        RECT live; GetWindowRect(o.Sec.Hwnd, out live);
        bool inPlace = live.T == o.Sec.Rect.T && live.B <= MonitorRect(live, false).B;
        bool show = o.Sec.Visible && inPlace && cur.Items.Count > 0 && o.Pos.W > 0 && !TaskbarHiddenBehindApp(o);
        if (show)
        {
            // touch the windows only when something changed: re-stacking them every tick made the hover highlight flicker
            RECT now; GetWindowRect(o.Hwnd, out now);
            bool moved = !o.Shown || !Same(now, o.Pos);
            if (moved || TaskbarAbove(o))
            {
                SetWindowPos(o.Hwnd, HWND_TOPMOST, o.Pos.L, o.Pos.T, o.Pos.W, o.Pos.H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Log("raised bar (" + (moved ? "moved" : "taskbar was above") + ")");
                o.Shown = true;
                ShowHover(o);
            }
        }
        else if (o.Shown)
        {
            SetWindowPos(o.Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            SetWindowPos(o.HoverWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }
        if (show != o.Shown) Log("bar " + (show ? "shown" : "hidden") + " (visible=" + o.Sec.Visible + " inPlace=" + inPlace + " items=" + cur.Items.Count + ")");
        o.Shown = show;
    }

    static bool TaskbarAbove(Overlay o)
    {
        for (IntPtr h = GetWindow(o.Hwnd, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) // GW_HWNDPREV: windows above ours
            if (h == o.Sec.Hwnd) return true;
        return false;
    }

    // Screen rect of item i on overlay o.
    static RECT SlotRect(Overlay o, int i)
    {
        int x0 = cur.Items[0].Btn.L;
        var r = new RECT();
        r.L = o.Pos.L + (int)Math.Round((cur.Items[i].Btn.L - x0) * o.Scale);
        r.R = o.Pos.L + (int)Math.Round((cur.Items[i].Btn.R - x0) * o.Scale);
        r.T = o.Pos.T; r.B = o.Pos.B;
        return r;
    }

    static int SlotAt(Overlay o, int x)
    {
        if (cur.Items.Count == 0) return -1;
        float sx = cur.Items[0].Btn.L + x / o.Scale;
        for (int i = 0; i < cur.Items.Count; i++) if (sx >= cur.Items[i].Btn.L && sx < cur.Items[i].Btn.R) return i;
        return -1;
    }

    static void ShowHover(Overlay o)
    {
        if (o.Hover < 0 || o.Hover >= cur.Items.Count || !o.Shown)
        {
            SetWindowPos(o.HoverWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }
        RECT r = SlotRect(o, o.Hover);
        int m = Math.Max(2, (int)(4 * o.Scale));
        SetWindowPos(o.HoverWnd, HWND_TOPMOST, r.L + m, r.T + m, r.W - 2 * m, r.H - 2 * m, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    static bool IsOurs(IntPtr h)
    {
        foreach (var o in overlays.Values) if (o.Hwnd == h || o.HoverWnd == h) return true;
        return false;
    }

    static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (h == hookWnd && hookWnd != IntPtr.Zero) return HookProc(h, m, w, l);
        if (h == ownerWnd && ownerWnd != IntPtr.Zero) return OwnerProc(h, m, w, l);
        if (h == msgWnd)
        {
            if (m == WM_APP)
            {
                try { OnScan(); } catch (Exception e) { Log("OnScan error " + e); }
                return IntPtr.Zero;
            }
            if (m == WM_TIMER)
            {
                if ((long)w == 2) { PollPopups(); return IntPtr.Zero; }
                foreach (var o in new List<Overlay>(overlays.Values)) ApplyVisibility(o);
                return IntPtr.Zero;
            }
            return DefWindowProc(h, m, w, l);
        }
        Overlay ov;
        if (!overlays.TryGetValue(h, out ov)) return DefWindowProc(h, m, w, l);
        int x = (short)((long)l & 0xFFFF);
        switch (m)
        {
            case WM_MOUSEACTIVATE: return new IntPtr(3); // MA_NOACTIVATE
            case WM_MOUSEMOVE:
                {
                    if (!ov.Tracking)
                    {
                        var tme = new TRACKMOUSEEVENT(); tme.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)); tme.flags = 2; tme.hwnd = h;
                        TrackMouseEvent(ref tme); ov.Tracking = true;
                    }
                    int slot = SlotAt(ov, x);
                    if (slot != ov.Hover) { ov.Hover = slot; ShowHover(ov); }
                    return IntPtr.Zero;
                }
            case WM_MOUSELEAVE:
                ov.Tracking = false; ov.Hover = -1; ShowHover(ov);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (ov.IgnoreNextLeftUp) ov.IgnoreNextLeftUp = false; else Click(ov, x, Kind.Left);
                return IntPtr.Zero;
            case WM_LBUTTONDBLCLK: ov.IgnoreNextLeftUp = true; Click(ov, x, Kind.Double); return IntPtr.Zero;
            case WM_RBUTTONUP: Click(ov, x, Kind.Right); return IntPtr.Zero;
            case WM_MBUTTONUP: Click(ov, x, Kind.Middle); return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }
    #endregion

    #region Clicks
    static void Click(Overlay ov, int x, Kind kind)
    {
        int slot = SlotAt(ov, x);
        if (slot < 0) return;
        var it = cur.Items[slot];
        RECT mirror = SlotRect(ov, slot);
        StartPopupWatch(ov, it.Btn, (mirror.L + mirror.R) / 2);
        Rec r = it.Rec ?? RecByTip(it.Name);
        if (r != null && r.Callback != 0 && IsWindow(r.Hwnd))
        {
            PostClick(r, kind, mirror, ov);
            Log("click " + kind + " '" + it.Name.Replace("\n", " ") + "' via callback v" + (r.VersionKnown ? r.Version.ToString() : "? (sent as v" + EffectiveVersion(r) + ")"));
            return;
        }
        if ((kind == Kind.Left || kind == Kind.Double) && it.El != null)
        {
            // Explorer's own button: it sends the app a left click, and works while the tray is covered
            try
            {
                var ip = (InvokePattern)it.El.GetCurrentPattern(InvokePattern.Pattern);
                ip.Invoke();
                if (kind == Kind.Double) ip.Invoke();
                Log("click " + kind + " '" + it.Name.Replace("\n", " ") + "' via UIA Invoke");
                return;
            }
            catch (Exception e) { Log("invoke failed " + e.Message); }
        }
        if (TrayVisibleAt(it.Btn))
        {
            InputClick(it.Btn, kind);
            Log("click " + kind + " '" + it.Name.Replace("\n", " ") + "' via SendInput");
            return;
        }
        Log("click " + kind + " '" + it.Name.Replace("\n", " ") + "' dropped: no callback known and the real tray is covered");
    }

    // Unknown version: with uID 0 both formats put the event in the low word of lParam and differ only in wParam
    // (uID 0 vs cursor point), so version 4 is the safer guess; version-4 apps then get their anchor point and
    // NIN_SELECT/WM_CONTEXTMENU. With another uID the old format is the only one that carries the id.
    static uint EffectiveVersion(Rec r) { return r.VersionKnown ? r.Version : (r.Uid == 0 ? 4u : 0u); }

    // The UIA scan can miss the record match for a moment; the tooltip still identifies the icon.
    static Rec RecByTip(string name)
    {
        string n = name.Trim();
        if (n.Length == 0) return null;
        lock (recLock)
            foreach (var r in recs)
                if (r.Callback != 0 && r.Tip.Trim().Length > 0 && n.Replace("\n", " ") == r.Tip.Trim().Replace("\n", " ")) return r;
        return null;
    }

    // Same message sequence Explorer sends for a mouse click on a notification icon.
    static void PostClick(Rec r, Kind kind, RECT mirror, Overlay ov)
    {
        uint pid; GetWindowThreadProcessId(r.Hwnd, out pid);
        AllowSetForegroundWindow(pid);
        // for 3 s the hidden Shell_TrayWnd stands on the clicked taskbar, for apps that place popups from it
        answerRec = r; answerRect = mirror; answerPid = pid; answerTaskbar = ov.Sec.Rect; answerUntil = Environment.TickCount + 3000;
        SetWindowPos(hookWnd, HWND_TOPMOST, answerTaskbar.L, answerTaskbar.T, answerTaskbar.W, answerTaskbar.H, SWP_NOACTIVATE);
        PreMoveHiddenPopups(pid, ov);
        uint v = EffectiveVersion(r);
        POINT c; GetCursorPos(out c);
        c.Y = Math.Min(c.Y, ov.Sec.Rect.T - 1); // version-4 anchor point: apps open menus upward from it, so keep it above the taskbar
        var seq = new List<uint>();
        switch (kind)
        {
            case Kind.Left: seq.Add(WM_MOUSEMOVE); seq.Add(WM_LBUTTONDOWN); seq.Add(WM_LBUTTONUP); if (v >= 3) seq.Add(NIN_SELECT); break;
            case Kind.Right: seq.Add(WM_MOUSEMOVE); seq.Add(WM_RBUTTONDOWN); seq.Add(WM_RBUTTONUP); if (v >= 3) seq.Add(WM_CONTEXTMENU); break;
            case Kind.Middle: seq.Add(WM_MBUTTONDOWN); seq.Add(WM_MBUTTONUP); break;
            case Kind.Double: seq.Add(WM_LBUTTONDBLCLK); seq.Add(WM_LBUTTONUP); break;
        }
        foreach (uint msg in seq)
        {
            IntPtr w, l;
            if (v >= 4)
            {
                w = new IntPtr((int)(((uint)(c.Y & 0xFFFF) << 16) | (uint)(c.X & 0xFFFF)));
                l = new IntPtr((int)(((r.Uid & 0xFFFF) << 16) | msg));
            }
            else { w = new IntPtr((int)r.Uid); l = new IntPtr((int)msg); }
            PostMessage(r.Hwnd, r.Callback, w, l);
        }
    }

    // Apps like EarTrumpet show their flyout window at its last position for a frame before placing it. Move the
    // app's hidden flyout-sized windows that last sat by the primary taskbar to the same spot by the clicked one.
    static void PreMoveHiddenPopups(uint pid, Overlay ov)
    {
        RECT priMon = MonitorRect(cur.Tray, false), secMon = MonitorRect(ov.Sec.Rect, false);
        int dx = secMon.R - priMon.R, dy = ov.Sec.Rect.T - cur.Tray.T;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            bool flyoutSized = r.W >= 40 && r.H >= 40 && r.W < 1000 && r.H < priMon.H;
            bool byPrimaryTaskbar = r.R > priMon.L && r.L < priMon.R && r.B >= cur.Tray.T - 140 && r.B <= priMon.B + 400;
            if (flyoutSized && byPrimaryTaskbar)
            {
                SetWindowPos(h, IntPtr.Zero, Math.Max(secMon.L, r.L + dx), r.T + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                Log("pre-moved hidden " + ClassOf(h));
            }
            return true;
        }, IntPtr.Zero);
    }

    static bool TrayVisibleAt(RECT btn)
    {
        var p = new POINT(); p.X = (btn.L + btn.R) / 2; p.Y = (btn.T + btn.B) / 2;
        IntPtr h = WindowFromPoint(p);
        return h != IntPtr.Zero && GetAncestor(h, 2) == cur.TrayHwnd;
    }

    static INPUT MouseAbs(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var i = new INPUT();
        i.mi.dx = (int)((long)(x - vx) * 65535 / (vw - 1));
        i.mi.dy = (int)((long)(y - vy) * 65535 / (vh - 1));
        i.mi.dwFlags = 0x8000 | 0x4000 | 0x1; // ABSOLUTE | VIRTUALDESK | MOVE
        return i;
    }

    static INPUT Button(uint flag) { var i = new INPUT(); i.mi.dwFlags = flag; return i; }

    // Fallback for icons whose callback is unknown: click the real icon and put the cursor straight back.
    static void InputClick(RECT btn, Kind kind)
    {
        POINT orig; GetCursorPos(out orig);
        int size = Marshal.SizeOf(typeof(INPUT));
        SendInput(1, new[] { MouseAbs((btn.L + btn.R) / 2, (btn.T + btn.B) / 2) }, size);
        Thread.Sleep(15);
        var seq = new List<INPUT>();
        switch (kind)
        {
            case Kind.Left: seq.Add(Button(0x2)); seq.Add(Button(0x4)); break;
            case Kind.Double: seq.Add(Button(0x2)); seq.Add(Button(0x4)); seq.Add(Button(0x2)); seq.Add(Button(0x4)); break;
            case Kind.Right: seq.Add(Button(0x8)); seq.Add(Button(0x10)); break;
            case Kind.Middle: seq.Add(Button(0x20)); seq.Add(Button(0x40)); break;
        }
        seq.Add(MouseAbs(orig.X, orig.Y));
        SendInput((uint)seq.Count, seq.ToArray(), size);
    }
    #endregion
}
