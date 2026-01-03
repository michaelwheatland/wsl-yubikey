using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

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
    DeviceInfo? Primary,
    string Message
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

    readonly Icon _greenIcon;
    readonly Icon _yellowIcon;
    readonly Icon _redIcon;
    readonly Icon _grayIcon;

    StatusSnapshot? _lastSnapshot;
    bool _autoAttachEnabled;
    bool _busy;

    public TrayAppContext()
    {
        LogUtil.Log("tray init");
        _greenIcon = CreateStatusIcon(Color.FromArgb(0, 180, 0));
        _yellowIcon = CreateStatusIcon(Color.FromArgb(255, 185, 0));
        _redIcon = CreateStatusIcon(Color.FromArgb(200, 0, 0));
        _grayIcon = CreateStatusIcon(Color.FromArgb(120, 120, 120));

        _attachItem = new ToolStripMenuItem("Attach", null, (_, _) => RunAttach());
        _detachItem = new ToolStripMenuItem("Detach", null, (_, _) => RunDetach());
        _autoAttachItem = new ToolStripMenuItem("Auto-attach", null, (_, _) => ToggleAutoAttach()) { CheckOnClick = true };
        _refreshItem = new ToolStripMenuItem("Refresh", null, (_, _) => _ = RefreshStatusAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _attachItem,
            _detachItem,
            new ToolStripSeparator(),
            _autoAttachItem,
            _refreshItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);

        _icon = new NotifyIcon
        {
            Text = "WSL YubiKey",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _grayIcon,
        };
        _icon.DoubleClick += (_, _) => ToggleAttachDetach();

        _timer = new Timer { Interval = PollMs };
        _timer.Tick += async (_, _) => await OnTickAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
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
        LogUtil.Log($"auto-attach {( _autoAttachEnabled ? "on" : "off")}");
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
            var snapshot = await GetStatusSnapshotAsync();
            if (snapshot.Primary == null)
            {
                ShowBalloon("No matching device found.");
                return;
            }

            var device = snapshot.Primary;
            if (device.State == DeviceState.NotShared)
            {
                LogUtil.Log($"bind {device.BusId}");
                _ = await Task.Run(() => Usbipd.Run(["bind", $"--busid={device.BusId}"]));
            }

            LogUtil.Log($"attach {device.BusId}");
            var res = await Task.Run(() => Usbipd.Run(["attach", "--wsl", "--auto-attach", "--busid", device.BusId]));
            LogUtil.Log(res.Output);
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
            var snapshot = await GetStatusSnapshotAsync();
            var attached = snapshot.Devices.FirstOrDefault(d => d.State == DeviceState.Attached);
            if (attached == null)
            {
                ShowBalloon("No attached device found.");
                return;
            }

            LogUtil.Log($"detach {attached.BusId}");
            var res = await Task.Run(() => Usbipd.Run(["detach", $"--busid={attached.BusId}"]));
            LogUtil.Log(res.Output);
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
        if (device.State == DeviceState.NotShared)
        {
            LogUtil.Log($"auto bind {device.BusId}");
            _ = await Task.Run(() => Usbipd.Run(["bind", $"--busid={device.BusId}"]));
        }

        LogUtil.Log($"auto attach {device.BusId}");
        var res = await Task.Run(() => Usbipd.Run(["attach", "--wsl", "--auto-attach", "--busid", device.BusId]));
        LogUtil.Log(res.Output);
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

        var tooltip = snapshot.Message;
        if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
        _icon.Text = tooltip;
    }

    async Task<StatusSnapshot> GetStatusSnapshotAsync()
    {
        return await Task.Run(() => Usbipd.GetStatus(_aliases));
    }

    void ShowBalloon(string msg)
    {
        _icon.BalloonTipTitle = "WSL YubiKey";
        _icon.BalloonTipText = msg;
        _icon.ShowBalloonTip(2000);
    }

    static Icon CreateStatusIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(Color.FromArgb(110, 0, 0, 0));
        g.FillEllipse(brush, 2, 2, 12, 12);
        g.DrawEllipse(pen, 2, 2, 12, 12);
        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        DestroyIcon(hIcon);
        return clone;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}

static class LogUtil
{
    const string LogFileName = "wsl-yubikey-tray.log";
    static string LogPath => Path.Combine(AppContext.BaseDirectory, LogFileName);

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch
        {
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
            return new StatusSnapshot(UiStatus.Error, [], null, "usbipd missing");
        }

        var devices = ParseDevices(list.Output);
        var aliasList = aliases.Select(a => a.ToLowerInvariant()).ToArray();

        var matching = devices
            .Where(d => aliasList.Any(a => d.Desc.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matching.Count == 0)
        {
            return new StatusSnapshot(UiStatus.NotDetected, devices, null, "2FA device not detected");
        }

        var attached = matching.FirstOrDefault(d => d.State == DeviceState.Attached);
        if (attached != null)
        {
            return new StatusSnapshot(UiStatus.Attached, devices, attached, $"Attached {attached.BusId}");
        }

        var primary = matching[0];
        var stateLabel = primary.State switch
        {
            DeviceState.NotShared => "not shared",
            DeviceState.Shared => "shared",
            _ => "detected",
        };
        return new StatusSnapshot(UiStatus.DetectedNotAttached, devices, primary, $"Detected {primary.BusId} ({stateLabel})");
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
            proc.WaitForExit(5000);

            var combined = string.Concat(stdout, stderr);
            return (proc.ExitCode, combined.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
