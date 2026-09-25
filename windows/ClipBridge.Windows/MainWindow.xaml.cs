using System.ComponentModel;
using System.Diagnostics;
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
    private const int ClipboardPort = 45837;
    private const string FirewallRuleName = "ClipBridge Private LAN TCP 45837";
    private ClipboardSyncService? _sync;
    private HwndSource? _windowSource;
    private IntPtr _windowHandle;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _exiting;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ClipBridge";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipBridge",
        "pairing-code.bin");

    private static readonly string LocalIpPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "local-ip.txt");

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        LocalIpBox.Text = LoadLocalIp() ?? Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            ?.ToString() ?? "请用 ipconfig 查询";
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
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 ClipBridge", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        return icon;
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
                LocalIpBox.IsEnabled = true;
                PairingCodeBox.IsEnabled = true;
                StartButton.Content = "开始同步";
                StatusText.Text = "已停止，可修改 IP 后重新开始同步。";
                return;
            }
            if (!TryGetLocalAddress(out _)) return;
            if (PairingCodeBox.Password.Length < 4) { StatusText.Text = "配对码至少需要 4 位。"; return; }
            StatusText.Text = "正在检查 Windows 防火墙…";
            var firewallResult = await Task.Run(EnsurePrivateFirewallRule);
            if (firewallResult == FirewallRuleResult.Declined)
            {
                StatusText.Text = "未授予防火墙权限，Android 无法从局域网连接。再次点击“开始同步”可重试。";
                return;
            }
            if (firewallResult == FirewallRuleResult.Failed)
            {
                StatusText.Text = "无法创建防火墙规则。请以管理员身份运行，或允许专用网络 TCP 45837 入站连接。";
                return;
            }
            await StartSyncAsync();
        }
        finally { StartButton.IsEnabled = true; }
    }

    private static FirewallRuleResult EnsurePrivateFirewallRule()
    {
        if (HasPrivateFirewallRule()) return FirewallRuleResult.AlreadyAllowed;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments =
                    $"advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
                    $"dir=in action=allow protocol=TCP localport={ClipboardPort} profile=private enable=yes",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return FirewallRuleResult.Failed;
            process.WaitForExit();
            return process.ExitCode == 0 ? FirewallRuleResult.Added : FirewallRuleResult.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return FirewallRuleResult.Declined;
        }
        catch
        {
            return FirewallRuleResult.Failed;
        }
    }

    private static bool HasPrivateFirewallRule()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall show rule name=\"{FirewallRuleName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Contains(FirewallRuleName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private enum FirewallRuleResult
    {
        AlreadyAllowed,
        Added,
        Declined,
        Failed,
    }

    private bool TryGetLocalAddress(out IPAddress address)
    {
        address = IPAddress.None;
        var input = LocalIpBox.Text.Trim();
        if (input.Split('.').Length != 4 || !IPAddress.TryParse(input, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.Broadcast))
        {
            StatusText.Text = "请输入本机网卡的有效局域网 IPv4 地址。";
            return false;
        }
        if (!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(parsed)))
        {
            StatusText.Text = "该 IP 不属于本机网卡，请填写 Windows 当前的局域网 IPv4 地址。";
            return false;
        }
        address = parsed;
        return true;
    }

    private static string? LoadLocalIp()
    {
        try { return File.Exists(LocalIpPath) ? File.ReadAllText(LocalIpPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<bool> StartSyncAsync()
    {
        if (!TryGetLocalAddress(out var address)) return false;
        var service = new ClipboardSyncService(PairingCodeBox.Password, s =>
            Dispatcher.BeginInvoke(() => StatusText.Text = s));
        try
        {
            await service.StartAsync(address);
            SavePairingCode(PairingCodeBox.Password);
            File.WriteAllText(LocalIpPath, address.ToString());
            _sync = service;
            LocalIpBox.Text = address.ToString();
            LocalIpBox.IsEnabled = false;
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
                if (!TryGetLocalAddress(out var address)) return;
                SavePairingCode(PairingCodeBox.Password);
                File.WriteAllText(LocalIpPath, address.ToString());
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
        if (_windowHandle != IntPtr.Zero) RemoveClipboardFormatListener(_windowHandle);
        _windowSource?.RemoveHook(WindowMessageHook);
        if (_sync is not null) await _sync.StopAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
