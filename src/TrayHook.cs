using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Our own hidden Shell_TrayWnd sits above Explorer's in z-order, so Shell_NotifyIcon calls reach us first
// (same technique as RetroBar/ManagedShell). Every message is forwarded to Explorer unchanged; we keep a copy
// of each icon's owner window, id, callback message and version so a click can be delivered without
// touching the real tray, even when a fullscreen app covers it.
static partial class Tiam
{
    class Rec { public IntPtr Hwnd; public uint Uid; public Guid Guid; public uint Callback; public uint Version; public string Tip = ""; }

    static readonly object recLock = new object();
    static readonly List<Rec> recs = new List<Rec>();
    static IntPtr hookWnd, explorerTray;
    static uint wmTaskbarCreated;
    static int rebroadcastAt;
    static Rec answerRec;
    static RECT answerRect, answerTaskbar;
    static int answerUntil;
    static uint answerPid;
    const int TRAY_MAGIC = 0x34753423;

    static IntPtr FindExplorerTray()
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            if (h != hookWnd) return h;
        return IntPtr.Zero;
    }

    static void StartTrayHook(IntPtr inst)
    {
        var wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.proc = wndProc;
        wc.hInstance = inst;
        wc.cls = "Shell_TrayWnd";
        RegisterClassEx(ref wc);
        explorerTray = FindExplorerTray();
        hookWnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Shell_TrayWnd", "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        KeepOnTop();
        SetTimer(hookWnd, new IntPtr(1), 100, IntPtr.Zero);
        Log("tray hook " + hookWnd + " explorer " + explorerTray);
        Broadcast();
    }

    // Apps re-register their icons on TaskbarCreated; that is how we learn icons added before we started.
    static void Broadcast()
    {
        broadcastUntil = Environment.TickCount + 5000;
        PostMessage(new IntPtr(0xFFFF), wmTaskbarCreated, IntPtr.Zero, IntPtr.Zero);
        Log("TaskbarCreated broadcast");
    }

    // Apps answer TaskbarCreated with NIM_DELETE + NIM_ADD (Electron) or a bare NIM_ADD. Explorer still has those
    // icons, so passing that through would reorder its tray and drop tooltips the app does not resend. During the
    // broadcast window: hold deletes, turn adds of icons Explorer already shows into modifies, then release deletes
    // that no add followed.
    static int broadcastUntil;
    static readonly Dictionary<long, byte[]> heldDeletes = new Dictionary<long, byte[]>();

    static long IconKey(IntPtr p) { return ((long)Marshal.ReadInt32(p, 12) << 32) | (uint)Marshal.ReadInt32(p, 16); }

    // Returns true when the message is fully handled here (not forwarded).
    static bool DuringBroadcast(COPYDATASTRUCT cds)
    {
        if (Environment.TickCount - broadcastUntil > 0) return false;
        uint msg = (uint)Marshal.ReadInt32(cds.lpData, 4);
        long key = IconKey(cds.lpData);
        if (msg == 2)
        {
            var copy = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, copy, 0, cds.cbData);
            heldDeletes[key] = copy;
            OnNotify(cds.lpData, cds.cbData);
            return true;
        }
        if (msg == 0)
        {
            var probe = new Rec(); probe.Hwnd = new IntPtr(Marshal.ReadInt32(cds.lpData, 12)); probe.Uid = (uint)Marshal.ReadInt32(cds.lpData, 16);
            RECT r;
            bool known = heldDeletes.Remove(key) | QueryExplorerRect(explorerTray, probe, out r);
            if (known) Marshal.WriteInt32(cds.lpData, 4, 1); // forwarded as NIM_MODIFY
        }
        return false;
    }

    static void ReleaseHeldDeletes()
    {
        if (heldDeletes.Count == 0 || Environment.TickCount - broadcastUntil <= 0) return;
        foreach (var data in heldDeletes.Values)
        {
            IntPtr buf = Marshal.AllocHGlobal(data.Length), pc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(COPYDATASTRUCT)));
            Marshal.Copy(data, 0, buf, data.Length);
            var cds = new COPYDATASTRUCT(); cds.dwData = new IntPtr(1); cds.cbData = data.Length; cds.lpData = buf;
            Marshal.StructureToPtr(cds, pc, false);
            IntPtr res;
            SendMessageTimeout(explorerTray, WM_COPYDATA, hookWnd, pc, 2, 500, out res);
            Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(pc);
        }
        Log("released " + heldDeletes.Count + " held deletes");
        heldDeletes.Clear();
    }

    static void KeepOnTop()
    {
        IntPtr ex = FindExplorerTray();
        if (ex != explorerTray)
        {
            Log("explorer tray changed " + explorerTray + " -> " + ex);
            explorerTray = ex;
            if (ex != IntPtr.Zero) rebroadcastAt = Environment.TickCount + 2000; // Explorer restarted: its own broadcast may have reached it first
        }
        var r = new RECT();
        if (Environment.TickCount - answerUntil <= 0) r = answerTaskbar; // see PostClick
        else if (ex != IntPtr.Zero) GetWindowRect(ex, out r);
        RECT mine; GetWindowRect(hookWnd, out mine);
        if (FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) != hookWnd || !Same(mine, r))
            SetWindowPos(hookWnd, HWND_TOPMOST, r.L, r.T, r.W, r.H, SWP_NOACTIVATE);
        if (IsWindowVisible(hookWnd))
            SetWindowPos(hookWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        if (rebroadcastAt != 0 && Environment.TickCount - rebroadcastAt >= 0) { rebroadcastAt = 0; Broadcast(); }
        ReleaseHeldDeletes();
    }

    static IntPtr HookProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_TIMER) { KeepOnTop(); return IntPtr.Zero; }
        if (m == WM_COPYDATA && l != IntPtr.Zero)
        {
            try
            {
                var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(l, typeof(COPYDATASTRUCT));
                long kind = cds.dwData.ToInt64();
                // ABM_GETTASKBARPOS from the app just clicked on an overlay: fail it, so the app falls back to
                // GetWindowRect(FindWindow("Shell_TrayWnd")), which is this window, now on the clicked taskbar.
                // EarTrumpet places its flyout this way.
                if (kind == 0 && cds.cbData >= 64 && Marshal.ReadInt32(cds.lpData, 40) == 5 &&
                    Environment.TickCount - answerUntil <= 0 && (uint)Marshal.ReadInt32(cds.lpData, 56) == answerPid)
                {
                    Log("failed ABM_GETTASKBARPOS for pid " + answerPid);
                    return IntPtr.Zero;
                }
                if (kind == 1 && cds.cbData >= 288)
                {
                    if (DuringBroadcast(cds)) return new IntPtr(1);
                    OnNotify(cds.lpData, cds.cbData);
                }
                else if (kind == 3 && cds.cbData >= 40)
                {
                    IntPtr res;
                    if (TryAnswerRect(cds.lpData, out res)) return res;
                }
            }
            catch (Exception e) { Log("copydata error " + e.Message); }
        }
        if (m == wmTaskbarCreated) return IntPtr.Zero; // our own broadcast also reaches this window
        if (m == WM_COPYDATA || m == WM_ACTIVATEAPP || m == WM_COMMAND || m >= WM_USER)
        {
            if (!IsWindow(explorerTray)) explorerTray = FindExplorerTray();
            if (explorerTray == IntPtr.Zero) return DefWindowProc(h, m, w, l);
            if (m == WM_USER + 372) { PostMessage(explorerTray, m, w, l); return DefWindowProc(h, m, w, l); }
            return SendMessage(explorerTray, m, w, l);
        }
        return DefWindowProc(h, m, w, l);
    }

    static Guid ReadGuid(IntPtr p, int off)
    {
        var b = new byte[16];
        Marshal.Copy(new IntPtr(p.ToInt64() + off), b, 0, 16);
        return new Guid(b);
    }

    static string ReadTip(IntPtr p, int off, int chars)
    {
        var c = new char[chars];
        Marshal.Copy(new IntPtr(p.ToInt64() + off), c, 0, chars);
        int n = Array.IndexOf(c, '\0');
        return new string(c, 0, n < 0 ? chars : n);
    }

    static Rec FindRec(IntPtr hwnd, uint uid, Guid g)
    {
        foreach (var r in recs)
            if ((g != Guid.Empty && r.Guid == g) || (r.Hwnd == hwnd && r.Uid == uid)) return r;
        return null;
    }

    // SHELLTRAYDATA: magic, message, then NOTIFYICONDATAW with 32-bit handle fields.
    static void OnNotify(IntPtr p, int size)
    {
        uint msg = (uint)Marshal.ReadInt32(p, 4);
        var hwnd = new IntPtr(Marshal.ReadInt32(p, 12));
        uint uid = (uint)Marshal.ReadInt32(p, 16), flags = (uint)Marshal.ReadInt32(p, 20), cb = (uint)Marshal.ReadInt32(p, 24);
        uint ver = size >= 812 ? (uint)Marshal.ReadInt32(p, 808) : 0;
        Guid g = (flags & 0x20) != 0 && size >= 960 ? ReadGuid(p, 944) : Guid.Empty;
        lock (recLock)
        {
            Rec r = FindRec(hwnd, uid, g);
            if (msg == 0 || msg == 1)
            {
                if (r == null)
                {
                    if (hwnd == IntPtr.Zero) return;
                    r = new Rec(); recs.Add(r);
                }
                if (hwnd != IntPtr.Zero) { r.Hwnd = hwnd; r.Uid = uid; }
                if (g != Guid.Empty) r.Guid = g;
                if ((flags & 1) != 0) r.Callback = cb;
                if ((flags & 4) != 0) r.Tip = ReadTip(p, 32, 128);
                // uVersion shares a union with the balloon timeout; without NIF_INFO a 3 or 4 there is the version
                // the app is about to set (Qt re-adds after TaskbarCreated without calling NIM_SETVERSION again).
                if ((flags & 0x10) == 0 && (ver == 3 || ver == 4)) r.Version = ver;
            }
            else if (msg == 2) { if (r != null) recs.Remove(r); }
            else if (msg == 4) { if (r != null) r.Version = ver; }
        }
    }

    // Shell_NotifyIconGetRect from the app whose icon was just clicked on an overlay: report the mirrored icon,
    // so flyouts that anchor to their icon open on the clicked monitor.
    static bool TryAnswerRect(IntPtr p, out IntPtr res)
    {
        res = IntPtr.Zero;
        Rec a = answerRec;
        if (a == null || Environment.TickCount - answerUntil > 0) return false;
        int msg = Marshal.ReadInt32(p, 4);
        var hwnd = new IntPtr(Marshal.ReadInt32(p, 16));
        uint uid = (uint)Marshal.ReadInt32(p, 20);
        Guid g = ReadGuid(p, 24);
        if (!((g != Guid.Empty && g == a.Guid) || (hwnd == a.Hwnd && uid == a.Uid))) return false;
        int x = msg == 1 ? answerRect.L : answerRect.W, y = msg == 1 ? answerRect.T : answerRect.H; // 1 = top-left, 2 = size
        res = new IntPtr((int)(((uint)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF)));
        Log("answered icon rect msg " + msg + " with " + x + "," + y);
        return true;
    }

    // Asks Explorer where an icon sits on the primary taskbar (worker thread).
    static bool QueryExplorerRect(IntPtr tray, Rec r, out RECT rect)
    {
        rect = new RECT();
        IntPtr buf = Marshal.AllocHGlobal(40), pc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(COPYDATASTRUCT)));
        try
        {
            for (int msg = 1; msg <= 2; msg++)
            {
                for (int i = 0; i < 40; i += 4) Marshal.WriteInt32(buf, i, 0);
                Marshal.WriteInt32(buf, 0, TRAY_MAGIC);
                Marshal.WriteInt32(buf, 4, msg);
                Marshal.WriteInt32(buf, 8, 32); // what shell32 sends (seen on the wire)
                Marshal.WriteInt32(buf, 16, r.Hwnd.ToInt32());
                Marshal.WriteInt32(buf, 20, (int)r.Uid);
                if (r.Guid != Guid.Empty) Marshal.Copy(r.Guid.ToByteArray(), 0, new IntPtr(buf.ToInt64() + 24), 16);
                var cds = new COPYDATASTRUCT(); cds.dwData = new IntPtr(3); cds.cbData = 40; cds.lpData = buf;
                Marshal.StructureToPtr(cds, pc, false);
                IntPtr res;
                if (SendMessageTimeout(tray, WM_COPYDATA, hookWnd, pc, 2, 300, out res) == IntPtr.Zero) return false;
                int v = res.ToInt32();
                if (v == 0) return false;
                int x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF);
                if (msg == 1) { rect.L = x; rect.T = y; } else { rect.R = rect.L + x; rect.B = rect.T + y; } // msg 2 returns the size
            }
            return rect.R > rect.L && rect.B > rect.T;
        }
        finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(pc); }
    }
}
