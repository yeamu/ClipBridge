using System.Windows;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipBridge.Windows;

public partial class MainWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private ClipboardSyncService? _sync;
    private HwndSource? _windowSource;
    private IntPtr _windowHandle;
    private readonly Forms.NotifyIcon _trayIcon;
    private System.Drawing.Size _trayIconSize;
    private bool _exiting;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ClipBridge";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipBridge",
        "pairing-code.bin");

    private static readonly string PhoneIpPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "phone-ip.txt");

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        PhoneIpBox.Text = LoadPhoneIp() ?? string.Empty;
        PairingCodeBox.Password = LoadPairingCode();
        UpdateAutoStartButton();
        Loaded += async (_, _) =>
        {
            if (!Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "--auto-start", StringComparison.OrdinalIgnoreCase)))
                return;

            if (PairingCodeBox.Password.Length < 4)
            {
                StatusText.Text = "未找到已保存的配对码，无法自动同步。";
                return;
            }

            if (await StartSyncAsync()) HideToTray();
        };
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        _trayIconSize = TrayIconImage.GetSize();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 ClipBridge", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        var icon = new Forms.NotifyIcon
        {
            Icon = TrayIconImage.Load(_trayIconSize),
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        return icon;
    }

    private void RefreshTrayIcon()
    {
        if (_exiting) return;
        var size = TrayIconImage.GetSize();
        if (size == _trayIconSize) return;
        var previous = _trayIcon.Icon;
        _trayIcon.Icon = TrayIconImage.Load(size);
        _trayIconSize = size;
        previous?.Dispose();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        AddClipboardFormatListener(_windowHandle);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate) _sync?.NotifyClipboardChanged();
        if (message == WmSettingChange || message == WmDisplayChange
            || message == WmDpiChanged || message == TaskbarCreatedMessage)
        {
            // Let Windows finish applying the display change before querying
            // the taskbar. This also works while the settings window is hidden.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(RefreshTrayIcon));
        }
        return IntPtr.Zero;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            if (_sync is not null)
            {
                await _sync.StopAsync();
                _sync = null;
                PhoneIpBox.IsEnabled = true;
                PairingCodeBox.IsEnabled = true;
                StartButton.Content = "开始同步";
                StatusText.Text = "已停止，可修改 IP 后重新开始同步。";
                return;
            }
            await StartSyncAsync();
        }
        finally { StartButton.IsEnabled = true; }
    }

    private bool TryGetPhoneAddress(out IPAddress address)
    {
        address = IPAddress.None;
        var input = PhoneIpBox.Text.Trim();
        if (input.Split('.').Length != 4 || !IPAddress.TryParse(input, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.Broadcast) || parsed.GetAddressBytes()[0] >= 224)
        {
            StatusText.Text = "请输入手机页面显示的 Wi-Fi IPv4 地址。";
            return false;
        }
        address = parsed;
        return true;
    }

    private static string? LoadPhoneIp()
    {
        try { return File.Exists(PhoneIpPath) ? File.ReadAllText(PhoneIpPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<bool> StartSyncAsync()
    {
        if (!TryGetPhoneAddress(out var address)) return false;
        if (PairingCodeBox.Password.Length < 4) { StatusText.Text = "配对码至少需要 4 位。"; return false; }
        var service = new ClipboardSyncService(PairingCodeBox.Password, s =>
            Dispatcher.BeginInvoke(() => StatusText.Text = s));
        try
        {
            await service.StartAsync(address);
            SavePairingCode(PairingCodeBox.Password);
            File.WriteAllText(PhoneIpPath, address.ToString());
            _sync = service;
            PhoneIpBox.Text = address.ToString();
            PhoneIpBox.IsEnabled = false;
            PairingCodeBox.IsEnabled = false;
            StartButton.Content = "停止同步";
            return true;
        }
        catch (Exception exception)
        {
            await service.StopAsync();
            StatusText.Text = $"启动失败：{exception.Message}";
            return false;
        }
    }

    private void AutoStartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的启动项。");
            if (IsAutoStartEnabled())
            {
                runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
                StatusText.Text = "已关闭开机自动启动。";
            }
            else
            {
                if (PairingCodeBox.Password.Length < 4)
                {
                    StatusText.Text = "请先输入至少 4 位配对码，再开启开机自动启动。";
                    return;
                }
                if (!TryGetPhoneAddress(out var address)) return;
                SavePairingCode(PairingCodeBox.Password);
                File.WriteAllText(PhoneIpPath, address.ToString());
                var executablePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定程序路径。");
                runKey.SetValue(StartupValueName, $"\"{executablePath}\" --auto-start", RegistryValueKind.String);
                StatusText.Text = "已开启开机自动启动；下次登录后会自动同步并隐藏到托盘。";
            }
            UpdateAutoStartButton();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"设置开机自动启动失败：{exception.Message}";
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
        return runKey?.GetValue(StartupValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private void UpdateAutoStartButton()
    {
        AutoStartButton.Content = IsAutoStartEnabled() ? "关闭开机自启动" : "开启开机自启动";
    }

    private static void SavePairingCode(string code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var protectedBytes = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(code),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SettingsPath, protectedBytes);
    }

    private static string LoadPairingCode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return string.Empty;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(SettingsPath), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) HideToTray();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _exiting = true;
        if (_windowHandle != IntPtr.Zero) RemoveClipboardFormatListener(_windowHandle);
        _windowSource?.RemoveHook(WindowMessageHook);
        _trayIcon.Visible = false;
        var image = _trayIcon.Icon;
        var menu = _trayIcon.ContextMenuStrip;
        _trayIcon.Dispose();
        image?.Dispose();
        menu?.Dispose();
        if (_sync is not null) await _sync.StopAsync();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
}
