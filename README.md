# Tiam: tray icons on all monitors

Windows 11 shows your tray icons (Discord, Steam, volume apps and so on) only on the main monitor. Tiam adds them to the taskbar on your other monitors too. You can click them there, and their menus open on the monitor you clicked. This keeps working while a full-screen game covers your main monitor.

## Install

1. Download `Tiam.exe` from the [latest release](https://github.com/racclololz/Tiam/releases/latest).
2. Double-click it. Your icons appear next to the clock on your other monitors.
3. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. This appears because the app is new and not signed.
4. To start Tiam every time you sign in, double-click `Tiam.exe` again and choose **Start with Windows**.

You don't need to install anything else. Without step 4, Tiam runs from wherever you saved it and changes nothing on your PC.

## Use

- Click a mirrored icon the same way as the real one: left-click, right-click, or double-click.
- To open Tiam's own menu, double-click `Tiam.exe` again, or right-click its icon (two small blue screens) in the tray. Windows may hide that icon behind the **^** arrow.
- Icons hidden behind the **^** arrow are not mirrored. To show an icon, drag it from the **^** panel onto the taskbar. You can also turn it on in **Settings > Personalization > Taskbar > Other system tray icons**.

## Remove

Open Tiam's menu, untick **Start with Windows**, then choose **Quit**. If Tiam was set to start with Windows, this also deletes its copy in `%LOCALAPPDATA%\Programs\Tiam`.

## Requirements and limits

- Windows 11 and at least two monitors.
- **Show my taskbar on all displays** must be on: **Settings > Personalization > Taskbar > Taskbar behaviors**.
- After Tiam starts, a few apps cannot be right-clicked from another monitor while a full-screen game covers the main one. Windows Security is one of them. Left-click still works.
- Some apps open a normal window instead of a menu, and that window may still open on the main monitor.

## Build from source

Run `build.ps1` in PowerShell. It uses the C# compiler that comes with Windows and writes `bin\Tiam.exe`. [NOTES.md](NOTES.md) explains how the app works.
