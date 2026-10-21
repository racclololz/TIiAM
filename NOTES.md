# Tiam handoff

Goal: show the Windows 11 tray icons on secondary taskbars, clickable, popups on the clicked monitor, and keep
working while a fullscreen app covers the primary monitor (the main use case).

Build: `build.ps1` (in-box .NET Framework csc, C# 5, no downloads). Output `bin\Tiam.exe` with
`assets\tiam.ico` (DIB entries; GDI+ and some loaders misread PNG entries below 256 px). Stop the running app
first or the build fails on the locked exe. Run with `-log` to write `%TEMP%\tiam.log`.

Distribution (`App.cs`): one portable exe, .NET Framework 4.8 ships with Windows 11. Nothing is written until the
user picks "Start with Windows": that copies the exe to `%LOCALAPPDATA%\Programs\Tiam\Tiam.exe` and sets
`HKCU\...\Run\Tiam`. Unticking removes the Run value (and the copy when not running from it); Quit from the
installed copy with autostart off deletes the folder via a short cmd. A hand-started newer exe refreshes the
installed copy. The app has its own tray icon (owner window `TiamWnd`/"Tiam"); Windows 11 hides new icons
behind "^", so starting the exe a second time posts `Tiam-ShowMenu` to the running copy, which opens the menu
at the cursor. First run shows a balloon (`HKCU\Software\Tiam\Welcomed`). Setting `IsPromoted` under
`HKCU\Control Panel\NotifyIconSettings` does not apply until Explorer restarts, so it is not used.
Gotcha: `DefWindowProc` must be the W import, or window titles are truncated to one character.

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

Click details learned later:
- NOTIFYICON_VERSION is sent once per icon (NIM_SETVERSION at creation). Apps running before Tiam that only send
  NIM_MODIFY never reveal it. Versions are saved in `HKCU\Software\Tiam\IconVersions` (process|uID); unknown with
  uID 0 is sent as version 4 (lParam is identical, wParam carries the anchor point). Sending v0 to a v4 app put
  its right-click menu at 0,0 and, in apps that swallow the duplicate LBUTTONUP/NIN_SELECT, ate every 2nd click.
- During a click the hidden Shell_TrayWnd stands on the clicked taskbar and ABM_GETTASKBARPOS from that app fails,
  so apps that place flyouts from FindWindow("Shell_TrayWnd") use the clicked monitor. Hidden flyout-sized windows
  of the app that sit by the primary taskbar are moved first (WPF shows a window at its old spot for a frame).
- Bar anchor: widest secondary clock seen (clock width changes with seconds). Re-raise on EVENT_OBJECT_REORDER /
  EVENT_SYSTEM_FOREGROUND when a taskbar covers the bar, plus a 250 ms check; never restack without need (flicker).

Wire facts (Win11 25H2): WINNOTIFYICONIDENTIFIER cbSize field = 32; message 1 returns top-left, message 2
returns width/height (not bottom-right). Monitor 2 is 100%, monitor 1 is 150%: keep everything PerMonitorV2.

Known limits: icons whose app never re-registers (NVIDIA Settings until next login, Windows Security has no
callback) cannot be right-clicked while the primary monitor is covered. EarTrumpet ignores the first click
after its flyout closed. KeePassXC's tray menu did not show even for a real click on this PC. Explorer restart
is handled in code (re-find tray, re-broadcast) but untested.

Main-tray drag (1.0.6): Explorer's own icon-drag code calls SetWindowPos(FindWindow("Shell_TrayWnd")) about every
16 ms and matches the drop against that window. A hidden twin with the taskbar's exact rect made drags fail. The
helper is now zero-size at the taskbar corner, forwards those SetWindowPos requests to Explorer's taskbar, and
steps below it while the left button is down on the main taskbar. UIA can report tray buttons stretching to the
screen edge; buttons are clipped at the next one (fixes hover/slot mapping).
