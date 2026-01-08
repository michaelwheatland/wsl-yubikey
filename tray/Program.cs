using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;
using Svg;

namespace WslYubikeyTray;

enum DeviceState
{
    Attached,
    Shared,
    NotShared,
    Unknown,
}

enum UiStatus
{
    Attached,
    DetectedNotAttached,
    NotDetected,
    Error,
}

sealed record DeviceInfo(string BusId, DeviceState State, string Desc, string Line);

sealed record StatusSnapshot(
    UiStatus Status,
    List<DeviceInfo> Devices,
    List<DeviceInfo> Matching,
    DeviceInfo? Primary,
    string Message
);

sealed record DriveSnapshot(
    HashSet<char> Present,
    HashSet<char> Mounted
);

static class Program
{
    [STAThread]
    static void Main()
    {
        LogUtil.Log("start");
        Application.ThreadException += (_, e) => LogUtil.Log("ui-ex " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogUtil.Log("fatal " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogUtil.Log("task-ex " + e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new TrayAppContext());
        }
        catch (Exception ex)
        {
            LogUtil.Log("fatal " + ex);
        }
    }
}

sealed class TrayAppContext : ApplicationContext
{
    const int PollMs = 2000;
    const string AppTitle = "WSL 2FA Connector";
    const int DrivePollMsIdle = 20000;
    const int DrivePollMsBusy = 2000;
    const int WslMountPollMsIdle = 30000;
    const int WslMountPollMsBusy = 5000;
    const int DriveBurstSeconds = 20;

    readonly string[] _aliases =
    [
        "yubi",
        "smartcard",
        "ccid",
        "fido",
        "security key",
    ];

    readonly NotifyIcon _icon;
    readonly Timer _timer;
    readonly ToolStripMenuItem _attachItem;
    readonly ToolStripMenuItem _detachItem;
    readonly ToolStripMenuItem _autoAttachItem;
    readonly ToolStripMenuItem _refreshItem;
    readonly ToolStripMenuItem _aboutItem;

    readonly ToolStripMenuItem _driveHeaderItem;
    readonly ToolStripMenuItem _driveAutoMountItem;
    readonly ToolStripMenuItem _driveAutoUnmountItem;
    readonly ToolStripMenuItem _driveSettingsItem;
    readonly ToolStripMenuItem _driveRefreshItem;
    readonly ToolStripMenuItem _driveMenuItem;

    readonly Icon _greenIcon;
    readonly Icon _yellowIcon;
    readonly Icon _redIcon;
    readonly Icon _grayIcon;

    StatusSnapshot? _lastSnapshot;
    bool _autoAttachEnabled;
    bool _busy;

    readonly AppSettings _settings;
    DateTime _lastDrivePoll = DateTime.MinValue;
    DateTime _lastWslMountPoll = DateTime.MinValue;
    HashSet<char> _knownDrives = new();
    HashSet<char> _mountedDrives = new();
    bool _driveInitialized;
    DateTime _driveBurstUntil = DateTime.MinValue;

    public TrayAppContext()
    {
        LogUtil.Log("tray init");
        _settings = SettingsStore.Load();
        _autoAttachEnabled = _settings.AutoAttachUsb;
        _greenIcon = CreateStatusIcon("icon-connected.png", Color.FromArgb(0, 180, 0));
        _yellowIcon = CreateStatusIcon("icon-ready.png", Color.FromArgb(255, 185, 0));
        _redIcon = CreateStatusIcon("icon-none.png", Color.FromArgb(200, 0, 0));
        _grayIcon = CreateStatusIcon("icon-error.png", Color.FromArgb(120, 120, 120));

        _attachItem = new ToolStripMenuItem("Attach", null, (_, _) => RunAttach());
        _detachItem = new ToolStripMenuItem("Detach", null, (_, _) => RunDetach());
        _autoAttachItem = new ToolStripMenuItem("Auto-attach", null, (_, _) => ToggleAutoAttach()) { CheckOnClick = true };
        _refreshItem = new ToolStripMenuItem("Refresh", null, (_, _) => _ = RefreshStatusAsync());

        _driveHeaderItem = new ToolStripMenuItem("Drives") { Enabled = false };
        _driveAutoMountItem = new ToolStripMenuItem("Auto-mount new drives", null, (_, _) => ToggleAutoMountDrives()) { CheckOnClick = true };
        _driveAutoUnmountItem = new ToolStripMenuItem("Auto-unmount on removal", null, (_, _) => ToggleAutoUnmountDrives()) { CheckOnClick = true };
        _driveSettingsItem = new ToolStripMenuItem("Drive settings", null, (_, _) => ShowDriveSettings());
        _driveRefreshItem = new ToolStripMenuItem("Refresh drives", null, (_, _) => _ = RefreshDrivesAsync(force: true));
        _driveMenuItem = new ToolStripMenuItem("Drives");

        _aboutItem = new ToolStripMenuItem("About", null, (_, _) => ShowAbout());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            new ToolStripMenuItem(AppTitle) { Enabled = false },
            new ToolStripSeparator(),
            _attachItem,
            _detachItem,
            new ToolStripSeparator(),
            _autoAttachItem,
            _refreshItem,
            new ToolStripSeparator(),
            _driveHeaderItem,
            _driveAutoMountItem,
            _driveAutoUnmountItem,
            _driveSettingsItem,
            _driveRefreshItem,
            _driveMenuItem,
            new ToolStripSeparator(),
            _aboutItem,
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);

        _icon = new NotifyIcon
        {
            Text = AppTitle,
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _grayIcon,
        };
        _icon.DoubleClick += (_, _) => ToggleAttachDetach();
        if (_driveMenuItem.DropDown is ToolStripDropDownMenu driveMenu)
        {
            driveMenu.ShowCheckMargin = true;
        }

        _timer = new Timer { Interval = PollMs };
        _timer.Tick += async (_, _) => await OnTickAsync();
        _timer.Start();

        _autoAttachItem.Checked = _autoAttachEnabled;
        _driveAutoMountItem.Checked = _settings.AutoMountDrives;
        _driveAutoUnmountItem.Checked = _settings.AutoUnmountDrives;

        _ = RefreshStatusAsync();
        _ = RefreshDrivesAsync(force: true);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _greenIcon.Dispose();
        _yellowIcon.Dispose();
        _redIcon.Dispose();
        _grayIcon.Dispose();
        base.ExitThreadCore();
    }

    async Task OnTickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await RefreshStatusAsync();
            if (_autoAttachEnabled)
            {
                await TryAutoAttachAsync();
                await RefreshStatusAsync();
            }

            await RefreshDrivesAsync(force: false);
            await TryAutoMountUnmountAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    void ToggleAutoAttach()
    {
        _autoAttachEnabled = !_autoAttachEnabled;
        _autoAttachItem.Checked = _autoAttachEnabled;
        _settings.AutoAttachUsb = _autoAttachEnabled;
        SettingsStore.Save(_settings);
        LogUtil.Log($"auto-attach {( _autoAttachEnabled ? "on" : "off")}");
    }

    void ToggleAutoMountDrives()
    {
        _settings.AutoMountDrives = !_settings.AutoMountDrives;
        _driveAutoMountItem.Checked = _settings.AutoMountDrives;
        SettingsStore.Save(_settings);
        LogUtil.Log($"auto-mount drives {( _settings.AutoMountDrives ? "on" : "off")}");
    }

    void ToggleAutoUnmountDrives()
    {
        _settings.AutoUnmountDrives = !_settings.AutoUnmountDrives;
        _driveAutoUnmountItem.Checked = _settings.AutoUnmountDrives;
        SettingsStore.Save(_settings);
        LogUtil.Log($"auto-unmount drives {( _settings.AutoUnmountDrives ? "on" : "off")}");
    }

    void ToggleAttachDetach()
    {
        var snapshot = _lastSnapshot;
        if (snapshot == null) return;

        if (snapshot.Status == UiStatus.Attached)
        {
            RunDetach();
        }
        else
        {
            RunAttach();
        }
    }

    async void RunAttach()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            LogUtil.Log("attach requested");
            var snapshot = await GetStatusSnapshotAsync();
            if (snapshot.Primary == null)
            {
                LogUtil.Log("attach failed: no matching device");
                ShowBalloon("No matching device found.");
                return;
            }

            var device = snapshot.Primary;
            LogDevices("attach devices", snapshot.Devices);
            if (device.State == DeviceState.NotShared)
            {
                LogUtil.Log($"bind {device.BusId}");
                var bindRes = await Task.Run(() => Usbipd.Run(["bind", $"--busid={device.BusId}"]));
                LogUtil.Log($"bind exit {bindRes.ExitCode} {bindRes.Output}");
            }

            LogUtil.Log($"attach {device.BusId}");
            var res = await Task.Run(() => Usbipd.Run(["attach", "--wsl", "--auto-attach", "--busid", device.BusId]));
            LogUtil.Log($"attach exit {res.ExitCode} {res.Output}");
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
        }
    }

    async void RunDetach()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            LogUtil.Log("detach requested");
            var snapshot = await GetStatusSnapshotAsync();
            var attached = snapshot.Matching.FirstOrDefault(d => d.State == DeviceState.Attached);
            LogDevices("detach devices", snapshot.Devices);
            if (attached == null)
            {
                LogUtil.Log("detach failed: no attached device");
                ShowBalloon("No attached device found.");
                return;
            }

            LogUtil.Log($"detach {attached.BusId}");
            var res = await Task.Run(() => Usbipd.Run(["detach", $"--busid={attached.BusId}"]));
            LogUtil.Log($"detach exit {res.ExitCode} {res.Output}");
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
        }
    }

    async Task TryAutoAttachAsync()
    {
        var snapshot = _lastSnapshot ?? await GetStatusSnapshotAsync();
        if (snapshot.Status != UiStatus.DetectedNotAttached || snapshot.Primary == null) return;

        var device = snapshot.Primary;
        LogDevices("auto devices", snapshot.Devices);
        if (device.State == DeviceState.NotShared)
        {
            LogUtil.Log($"auto bind {device.BusId}");
            var bindRes = await Task.Run(() => Usbipd.Run(["bind", $"--busid={device.BusId}"]));
            LogUtil.Log($"auto bind exit {bindRes.ExitCode} {bindRes.Output}");
        }

        LogUtil.Log($"auto attach {device.BusId}");
        var res = await Task.Run(() => Usbipd.Run(["attach", "--wsl", "--auto-attach", "--busid", device.BusId]));
        LogUtil.Log($"auto attach exit {res.ExitCode} {res.Output}");
    }

    async Task RefreshStatusAsync()
    {
        var snapshot = await GetStatusSnapshotAsync();
        _lastSnapshot = snapshot;

        _attachItem.Enabled = snapshot.Status != UiStatus.Attached && snapshot.Status != UiStatus.Error;
        _detachItem.Enabled = snapshot.Status == UiStatus.Attached;

        _icon.Icon = snapshot.Status switch
        {
            UiStatus.Attached => _greenIcon,
            UiStatus.DetectedNotAttached => _yellowIcon,
            UiStatus.NotDetected => _redIcon,
            _ => _grayIcon,
        };

        var tooltip = $"{AppTitle} - {snapshot.Message}";
        if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
        _icon.Text = tooltip;
    }

    async Task<StatusSnapshot> GetStatusSnapshotAsync()
    {
        return await Task.Run(() => Usbipd.GetStatus(_aliases));
    }

    async Task RefreshDrivesAsync(bool force)
    {
        var now = DateTime.UtcNow;
        var inBurst = now < _driveBurstUntil;
        var drivePollMs = inBurst ? DrivePollMsBusy : DrivePollMsIdle;
        if (!force && (now - _lastDrivePoll).TotalMilliseconds < drivePollMs) return;
        _lastDrivePoll = now;

        var present = DriveUtil.GetPresentDriveLetters();
        if (!_driveInitialized)
        {
            _knownDrives = new HashSet<char>(present);
            _driveInitialized = true;
        }

        var mountPollMs = inBurst ? WslMountPollMsBusy : WslMountPollMsIdle;
        if (force || (now - _lastWslMountPoll).TotalMilliseconds >= mountPollMs)
        {
            _lastWslMountPoll = now;
            _mountedDrives = await Task.Run(() => WslUtil.GetMountedDrvfsLetters(_settings));
        }

        UpdateDriveMenu(present, _mountedDrives);
    }

    async Task TryAutoMountUnmountAsync()
    {
        if (!_driveInitialized) return;
        var current = DriveUtil.GetPresentDriveLetters();
        var added = current.Except(_knownDrives).ToList();
        var removed = _knownDrives.Except(current).ToList();

        if (_settings.AutoMountDrives && added.Count > 0)
        {
            foreach (var letter in added)
            {
                await Task.Run(() => WslUtil.MountDrive(letter, _settings));
            }
            _mountedDrives = await Task.Run(() => WslUtil.GetMountedDrvfsLetters(_settings));
            _driveBurstUntil = DateTime.UtcNow.AddSeconds(DriveBurstSeconds);
        }

        if (_settings.AutoUnmountDrives && removed.Count > 0)
        {
            foreach (var letter in removed)
            {
                if (_mountedDrives.Contains(letter))
                {
                    await Task.Run(() => WslUtil.UnmountDrive(letter, _settings));
                }
            }
            _mountedDrives = await Task.Run(() => WslUtil.GetMountedDrvfsLetters(_settings));
            _driveBurstUntil = DateTime.UtcNow.AddSeconds(DriveBurstSeconds);
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            UpdateDriveMenu(current, _mountedDrives);
        }

        _knownDrives = new HashSet<char>(current);
    }

    void UpdateDriveMenu(HashSet<char> present, HashSet<char> mounted)
    {
        _driveMenuItem.DropDownItems.Clear();
        var letters = present.OrderBy(c => c).ToList();
        if (letters.Count == 0)
        {
            _driveMenuItem.DropDownItems.Add(new ToolStripMenuItem("(no drives)") { Enabled = false });
            return;
        }

        foreach (var letter in letters)
        {
            var driveItem = new ToolStripMenuItem($"{letter}:") { Checked = mounted.Contains(letter), CheckOnClick = false };
            driveItem.Click += (_, _) => ToggleDrive(letter);
            _driveMenuItem.DropDownItems.Add(driveItem);
        }
    }

    async void ToggleDrive(char letter)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var mounted = _mountedDrives.Contains(letter);
            if (mounted)
            {
                LogUtil.Log($"drive unmount {letter}:");
                await Task.Run(() => WslUtil.UnmountDrive(letter, _settings));
            }
            else
            {
                LogUtil.Log($"drive mount {letter}:");
                await Task.Run(() => WslUtil.MountDrive(letter, _settings));
            }

            _mountedDrives = await Task.Run(() => WslUtil.GetMountedDrvfsLetters(_settings));
            UpdateDriveMenu(DriveUtil.GetPresentDriveLetters(), _mountedDrives);
            _driveBurstUntil = DateTime.UtcNow.AddSeconds(DriveBurstSeconds);
        }
        finally
        {
            _busy = false;
        }
    }

    void ShowBalloon(string msg)
    {
        _icon.BalloonTipTitle = AppTitle;
        _icon.BalloonTipText = msg;
        _icon.ShowBalloonTip(2000);
    }

    static void LogDevices(string label, IEnumerable<DeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            LogUtil.Log($"{label}: {d.Line}");
        }
    }

    void ShowAbout()
    {
        var msg = "WSL 2FA Connector\n\n" +
                  "Michael Wheatland\n" +
                  "michael@wheatland.com.au\n" +
                  "https://www.wheatland.com.au\n\n" +
                  "Disclaimer: I'm not a programmer. Use at your own risk.";
        MessageBox.Show(msg, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ShowDriveSettings()
    {
        using var dialog = new DriveSettingsForm(_settings);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            SettingsStore.Save(_settings);
            _driveAutoMountItem.Checked = _settings.AutoMountDrives;
            _driveAutoUnmountItem.Checked = _settings.AutoUnmountDrives;
            _ = RefreshDrivesAsync(force: true);
        }
    }

    static Icon CreateStatusIcon(Color color)
    {
        return CreateStatusIcon(null, color);
    }

    static Icon CreateStatusIcon(string? pngName, Color color)
    {
        if (!string.IsNullOrWhiteSpace(pngName))
        {
            var png = ResolveAssetPath(pngName);
            if (png != null)
            {
                try
                {
                    using var raw = new Bitmap(png);
                    using var bmp = new Bitmap(16, 16);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.Transparent);
                        g.DrawImage(raw, 0, 0, 16, 16);
                    }
                    return IconFromBitmap(bmp);
                }
                catch (Exception ex)
                {
                    LogUtil.Log("png fail " + ex.Message);
                }
            }
            else
            {
                LogUtil.Log("png missing " + pngName);
            }
        }

        var svgPath = Path.Combine(AppContext.BaseDirectory, "key.svg");
        if (File.Exists(svgPath))
        {
            try
            {
                var doc = SvgDocument.Open(svgPath);
                ApplySvgColor(doc, color);
                using var bmp = doc.Draw(16, 16);
                return IconFromBitmap(bmp);
            }
            catch (Exception ex)
            {
                LogUtil.Log("svg fail " + ex.Message);
            }
        }

        return CreateDotIcon(color);
    }

    static string? ResolveAssetPath(string fileName)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(direct)) return direct;
        var img = Path.Combine(AppContext.BaseDirectory, "img", fileName);
        if (File.Exists(img)) return img;
        return null;
    }

    static Icon CreateDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(Color.FromArgb(110, 0, 0, 0));
        g.FillEllipse(brush, 2, 2, 12, 12);
        g.DrawEllipse(pen, 2, 2, 12, 12);
        return IconFromBitmap(bmp);
    }

    static Icon IconFromBitmap(Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        DestroyIcon(hIcon);
        return clone;
    }

    static void ApplySvgColor(SvgElement root, Color color)
    {
        var paint = new SvgColourServer(color);
        foreach (var el in root.Descendants().OfType<SvgVisualElement>())
        {
            el.Fill = paint;
            if (el.Stroke != null) el.Stroke = paint;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}

sealed class DriveSettingsForm : Form
{
    readonly TextBox _distroBox;
    readonly TextBox _mountBaseBox;
    readonly CheckBox _autoMountBox;
    readonly CheckBox _autoUnmountBox;
    readonly AppSettings _settings;

    public DriveSettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Drive Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 520;
        Height = 300;

        var distroLabel = new Label { Text = "WSL distro (blank = default):", Left = 12, Top = 16, Width = 360 };
        _distroBox = new TextBox { Left = 12, Top = 38, Width = 380, Text = _settings.WslDistro ?? string.Empty };

        var mountLabel = new Label { Text = "Mount base (default /mnt):", Left = 12, Top = 72, Width = 360 };
        _mountBaseBox = new TextBox { Left = 12, Top = 94, Width = 380, Text = _settings.MountBase };

        _autoMountBox = new CheckBox { Text = "Auto-mount new drives", Left = 12, Top = 130, Width = 200, Checked = _settings.AutoMountDrives };
        _autoUnmountBox = new CheckBox { Text = "Auto-unmount on removal", Left = 12, Top = 154, Width = 220, Checked = _settings.AutoUnmountDrives };

        var ok = new Button { Text = "OK", Left = 300, Width = 85, Top = 205, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 395, Width = 85, Top = 205, DialogResult = DialogResult.Cancel };

        ok.Click += (_, _) => ApplySettings();

        Controls.AddRange([distroLabel, _distroBox, mountLabel, _mountBaseBox, _autoMountBox, _autoUnmountBox, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void ApplySettings()
    {
        _settings.WslDistro = string.IsNullOrWhiteSpace(_distroBox.Text) ? null : _distroBox.Text.Trim();
        _settings.MountBase = SettingsStore.NormalizeMountBase(_mountBaseBox.Text);
        _settings.AutoMountDrives = _autoMountBox.Checked;
        _settings.AutoUnmountDrives = _autoUnmountBox.Checked;
    }
}

static class LogUtil
{
    const string LogFileName = "wsl-yubikey-tray.log";
    const long MaxBytes = 1_000_000;
    static string LogPath => Path.Combine(AppContext.BaseDirectory, LogFileName);

    public static void Log(string msg)
    {
        try
        {
            RotateIfNeeded();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch
        {
        }
    }

    static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backup = LogPath + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(LogPath, backup);
            }
        }
        catch
        {
        }
    }
}

sealed class AppSettings
{
    public string? WslDistro { get; set; }
    public string MountBase { get; set; } = "/mnt";
    public bool AutoMountDrives { get; set; } = false;
    public bool AutoUnmountDrives { get; set; } = true;
    public bool AutoAttachUsb { get; set; } = false;
}

static class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.MountBase = NormalizeMountBase(settings.MountBase);
            return settings;
        }
        catch (Exception ex)
        {
            LogUtil.Log("settings load fail " + ex.Message);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            settings.MountBase = NormalizeMountBase(settings.MountBase);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            LogUtil.Log("settings save fail " + ex.Message);
        }
    }

    public static string NormalizeMountBase(string? raw)
    {
        var basePath = string.IsNullOrWhiteSpace(raw) ? "/mnt" : raw.Trim();
        if (!basePath.StartsWith("/")) basePath = "/" + basePath;
        if (basePath.Length > 1 && basePath.EndsWith("/")) basePath = basePath.TrimEnd('/');
        return basePath;
    }
}

static class DriveUtil
{
    public static HashSet<char> GetPresentDriveLetters()
    {
        var letters = new HashSet<char>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var name = drive.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var letter = char.ToUpperInvariant(name[0]);
                if (letter >= 'A' && letter <= 'Z') letters.Add(letter);
            }
        }
        catch (Exception ex)
        {
            LogUtil.Log("drive list fail " + ex.Message);
        }

        return letters;
    }
}

static class WslUtil
{
    public static HashSet<char> GetMountedDrvfsLetters(AppSettings settings)
    {
        var result = new HashSet<char>();
        var mountBase = SettingsStore.NormalizeMountBase(settings.MountBase);
        var res = RunWsl(settings, ["sh", "-lc", "findmnt -rn -t drvfs -o TARGET"]);
        if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.Output))
        {
            var targets = res.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var target in targets)
            {
                var mountPoint = target.Trim();
                if (!mountPoint.StartsWith(mountBase + "/", StringComparison.OrdinalIgnoreCase)) continue;
                var letter = char.ToUpperInvariant(mountPoint.Substring(mountBase.Length + 1).FirstOrDefault());
                if (letter >= 'A' && letter <= 'Z') result.Add(letter);
            }
            return result;
        }

        var fallback = RunWsl(settings, ["sh", "-lc", "grep -i ' drvfs ' /proc/mounts"]);
        if (fallback.ExitCode != 0 || string.IsNullOrWhiteSpace(fallback.Output)) return result;

        var lines = fallback.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;
            var mountPoint = parts[1];
            if (!mountPoint.StartsWith(mountBase + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var letter = char.ToUpperInvariant(mountPoint.Substring(mountBase.Length + 1).FirstOrDefault());
            if (letter >= 'A' && letter <= 'Z') result.Add(letter);
        }

        return result;
    }

    public static void MountDrive(char letter, AppSettings settings)
    {
        var mountBase = SettingsStore.NormalizeMountBase(settings.MountBase);
        var mountPoint = $"{mountBase}/{char.ToLowerInvariant(letter)}";
        var mountPointEsc = EscapeSh(mountPoint);
        var drive = $"{char.ToUpperInvariant(letter)}:";
        var driveEsc = EscapeSh(drive);
        var prep = RunWsl(settings, ["sh", "-lc", $"mkdir -p {mountPointEsc}"], runAsRoot: true);
        if (prep.ExitCode != 0)
        {
            LogUtil.Log($"mkdir {letter}: exit {prep.ExitCode} {prep.Output}");
            return;
        }

        var cmd = $"mount -t drvfs {driveEsc} {mountPointEsc}";
        var res = RunWsl(settings, ["sh", "-lc", cmd], runAsRoot: true);
        LogUtil.Log($"mount {letter}: exit {res.ExitCode} {res.Output}");
    }

    public static void UnmountDrive(char letter, AppSettings settings)
    {
        var mountBase = SettingsStore.NormalizeMountBase(settings.MountBase);
        var mountPoint = $"{mountBase}/{char.ToLowerInvariant(letter)}";
        var mountPointEsc = EscapeSh(mountPoint);
        var res = RunWsl(settings, ["sh", "-lc", $"umount {mountPointEsc}"], runAsRoot: true);
        LogUtil.Log($"umount {letter}: exit {res.ExitCode} {res.Output}");
        var cleanup = RunWsl(settings, ["sh", "-lc", $"rmdir {mountPointEsc}"], runAsRoot: true);
        if (cleanup.ExitCode != 0 && !string.IsNullOrWhiteSpace(cleanup.Output))
        {
            LogUtil.Log($"rmdir {letter}: exit {cleanup.ExitCode} {cleanup.Output}");
        }
    }

    static string EscapeSh(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    public static (int ExitCode, string Output) RunWsl(AppSettings settings, string[] args, bool runAsRoot = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (runAsRoot)
            {
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("root");
            }

            if (!string.IsNullOrWhiteSpace(settings.WslDistro))
            {
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(settings.WslDistro.Trim());
                psi.ArgumentList.Add("--");
            }

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "wsl failed to start");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(true); } catch { }
                return (-1, "wsl timed out");
            }

            var combined = string.Concat(stdout, stderr);
            return (proc.ExitCode, combined.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

static class Usbipd
{
    static readonly Regex BusIdRegex = new(@"^(\d+-\d+)\b", RegexOptions.Compiled);

    public static StatusSnapshot GetStatus(IEnumerable<string> aliases)
    {
        var list = Run(["list"]);
        if (list.ExitCode != 0 && list.Output.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return new StatusSnapshot(UiStatus.Error, [], [], null, "usbipd missing");
        }

        var devices = ParseDevices(list.Output);
        var aliasList = aliases.Select(a => a.ToLowerInvariant()).ToArray();

        var matching = devices
            .Where(d => aliasList.Any(a => d.Desc.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matching.Count == 0)
        {
            return new StatusSnapshot(UiStatus.NotDetected, devices, matching, null, "2FA device not detected");
        }

        var attached = matching.FirstOrDefault(d => d.State == DeviceState.Attached);
        if (attached != null)
        {
            return new StatusSnapshot(UiStatus.Attached, devices, matching, attached, $"Attached {attached.BusId}");
        }

        var primary = matching[0];
        var stateLabel = primary.State switch
        {
            DeviceState.NotShared => "not shared",
            DeviceState.Shared => "shared",
            _ => "detected",
        };
        return new StatusSnapshot(UiStatus.DetectedNotAttached, devices, matching, primary, $"Detected {primary.BusId} ({stateLabel})");
    }

    public static List<DeviceInfo> ParseDevices(string output)
    {
        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var devices = new List<DeviceInfo>();
        foreach (var line in lines)
        {
            if (!BusIdRegex.IsMatch(line)) continue;
            var busId = BusIdRegex.Match(line).Groups[1].Value;
            var desc = BusIdRegex.Replace(line, string.Empty).Trim();
            var state = ParseState(line);
            devices.Add(new DeviceInfo(busId, state, desc.ToLowerInvariant(), line));
        }

        return devices;
    }

    static DeviceState ParseState(string line)
    {
        if (line.Contains("Attached", StringComparison.OrdinalIgnoreCase)) return DeviceState.Attached;
        if (line.Contains("Not shared", StringComparison.OrdinalIgnoreCase)) return DeviceState.NotShared;
        if (line.Contains("Shared", StringComparison.OrdinalIgnoreCase)) return DeviceState.Shared;
        return DeviceState.Unknown;
    }

    public static (int ExitCode, string Output) Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "usbipd",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "usbipd failed to start");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                return (-1, "usbipd timed out");
            }

            var combined = string.Concat(stdout, stderr);
            return (proc.ExitCode, combined.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
