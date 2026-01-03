# wsl-yubikey

Yubikey WSL Tool - Easily mount Fido2 USB devices into WSL

Tray app (Windows) in `tray/` provides a status icon + attach/detach + auto-attach using `usbipd`.

## Tray app (build/run)

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

## Commit + push

```
git status -sb
git add -A
git commit -m "feat: tray app"
git push -u origin feat/initial
```
