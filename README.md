# wsl-yubikey

A simple tray program that monitors, connects and disconnects a Yubikey or Fido2 Two factor authentication device from a windows PC to Windows Subsystem for Linux (WSL) image. So that you can use your 2FA within WSL for SSH login.

You must install usbipd-win to use this tool.
Once USBIPD is installed, just copy the exe binary to wherever you want and run it. (It's fully portable)

Yubikey WSL Tool - Easily mount Fido2 USB devices into WSL.
Tray app (Windows) in `tray/` provides a status icon + attach/detach + auto-attach using `usbipd`.

Feel free to contribute to this project by submitting a pull request. But I am not a programmer, so I am not maintining this actively.

## DIY Build (recommended)

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

Build + copy exe to repo root (small, requires .NET 8 Desktop Runtime):
```
.\build.ps1
```
Build + copy exe to repo root (self-contained, larger):
```
.\build.ps1 -SelfContained
```
Root exe:
```
WslYubikeyTray.exe
```
Note: GitHub rejects blobs >100MB. Use the small build for GitHub mirrors.
Icon svg copied to repo root as `key.svg`. If missing, app falls back to dot icon.

## Download (prebuilt)
Download: `www.wheatland.com.au/code/wsl-yubi`

Run: double-click exe. Tray icon uses:
- Green: attached
- Yellow: detected not attached
- Red: not detected
- Gray: usbipd error

Auto-attach uses `usbipd bind` then `usbipd attach --wsl --auto-attach`.
Log file sits beside exe: `wsl-yubikey-tray.log` (rotates at ~1MB to `.1`)
