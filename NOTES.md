# TIiAM handoff

Goal: show the Windows 11 tray icons on secondary taskbars, clickable, popups on the clicked monitor, and keep
working while a fullscreen app covers the primary monitor (the main use case).

Build: `build.ps1` (in-box .NET Framework csc, C# 5, no downloads). Output `bin\Tiam.exe`. Stop the running
app first or the build fails on the locked exe. Run with `-log` to write `%TEMP%\tiam.log`.
Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `TIiAM`.

Design (src/):
- `TrayHook.cs`: a hidden window of class `Shell_TrayWnd`, kept above Explorer's in z-order (100 ms timer), so
  `Shell_NotifyIcon` calls reach it first (RetroBar/ManagedShell technique). Everything is forwarded to Explorer.
  It records hwnd, uID, GUID, callback message and version per icon. On start it broadcasts `TaskbarCreated`
  so apps re-register; during the 5 s after a broadcast, deletes are held and adds of icons Explorer already
  shows are forwarded as modifies (otherwise Electron apps lose their tooltips and the tray reorders).
- `Tiam.cs`: worker thread every 700 ms lists the primary tray buttons by UIA (`NotifyItemIcon`), matches each
  to a record by asking Explorer for the record's icon rect (WM_COPYDATA kind 3), and finds secondary taskbars.
  Each secondary taskbar gets a topmost no-activate overlay left of its clock showing a DWM thumbnail of the
  tray strip (live while covered). Hover = separate layered window.
- Click: post the icon's callback messages like Explorer (v4: wParam = cursor x/y, lParam = msg | uID<<16),
  with AllowSetForegroundWindow. Shell_NotifyIconGetRect from that app for 3 s is answered with the mirrored
  rect. No record: left click = UIA Invoke on Explorer's button; otherwise SendInput on the real icon if visible.
- `Popups.cs`: for 4 s after a click, move popups that still open on the primary monitor (menus at once,
  flyouts once stable) and lift popups that overlap the secondary taskbar.

Wire facts (Win11 25H2): WINNOTIFYICONIDENTIFIER cbSize field = 32; message 1 returns top-left, message 2
returns width/height (not bottom-right). Monitor 2 is 100%, monitor 1 is 150%: keep everything PerMonitorV2.

Known limits: icons whose app never re-registers (NVIDIA Settings until next login, Windows Security has no
callback) cannot be right-clicked while the primary monitor is covered. EarTrumpet ignores the first click
after its flyout closed. KeePassXC's tray menu did not show even for a real click on this PC. Explorer restart
is handled in code (re-find tray, re-broadcast) but untested.
