# wsl-yubikey

A simple tray program that monitors, connects and disconnects a Yubikey or Fido2 Two factor authentication device from a windows PC to Windows Subsystem for Linux (WSL) image. So that you can use your 2FA within WSL for SSH login.

You must install [usbipd-win](https://github.com/dorssel/usbipd-win) to use this tool.
Once ISBIPD is installed, just copy the exe binary to wherever you want and run it. (It's fully portable)

Yubikey WSL Tool - Easily mount Fido2 USB devices into WSL.
Tray app (Windows) in `tray/` provides a status icon + attach/detach + auto-attach using `usbipd`.

Feel free to contribute to this project by submitting a pull request. But I am not a programmer, so I am not maintining this actively.

## Build Instructions

Prereqs: Windows 11, usbipd-win installed, .NET 8 SDK.

Build (portable exe):
```
cd tray
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```
Exe output:
```
tray/bin/Release/net8.0-windows/win-x64/publish/WslYubikeyTray.exe
```

Build + copy exe to repo root:
```
.\build.ps1
```
Root exe:
```
WslYubikeyTray.exe
```
Icon svg copied to repo root as `key.svg`. If missing, app falls back to dot icon.

Run: double-click exe. Tray icon uses:
- Green: attached
- Yellow: detected not attached
- Red: not detected
- Gray: usbipd error

Auto-attach uses `usbipd bind` then `usbipd attach --wsl --auto-attach`.
Log file sits beside exe: `wsl-yubikey-tray.log` (rotates at ~1MB to `.1`)
