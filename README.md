# ClipBridge

<p align="center">
  <img src="assets/icons/clipbridge-512.png" width="128" alt="ClipBridge icon">
</p>

[中文](#clipbridge) | [English](#english)

ClipBridge 是一个局域网剪贴板同步工具。目前支持 Windows 与 Android 双向同步文字和图片，macOS 26+ 版本的开发说明见 [MACOS_HANDOFF.md](MACOS_HANDOFF.md)。

当前稳定版本：**1.2.2**

## 功能

- Windows 与 Android 双向同步纯文本剪贴板
- Windows 与 Android 双向同步图片剪贴板，单张原文件最大 20 MB
- 常见输入格式：PNG、JPG/JPEG/JFIF、BMP、GIF、TIFF、WebP、HEIC/HEIF、AVIF、ICO
- 能取得原文件/URI 时直接传输原始图片，不解码、不转码；GIF 保留完整动画
- 只有剪贴板仅提供位图时才转成 PNG，超过 20 MB 时自动使用高质量 JPEG
- 使用同一条 TCP 连接完成双向通信
- HMAC-SHA256 配对认证，配对码至少 4 位
- Windows 使用 `WM_CLIPBOARDUPDATE` 监听，不定时轮询剪贴板
- Windows 收到文本后直接写入系统剪贴板；若已启用 `Win+V` 剪贴板历史，通常会被系统记录
- Windows 最小化或关闭窗口后驻留系统托盘
- Windows 可开启当前用户登录后的自动启动；配对码仅以 Windows 当前用户加密形式保存
- Android 使用前台服务维持连接
- Android 通知栏提供“同步当前剪贴板”按钮
- Android 已捕获的待发送文本、Windows 收到的待写文本均按 FIFO 顺序处理，队列最多保留 20 条
- 远端来源标记和消息 UUID 防止循环回写

## 下载

- [ClipBridge Windows 1.2.2](dist/ClipBridge-Windows-v1.2.2.zip)
- [ClipBridge Android 1.2.2](dist/ClipBridge-Android-v1.2.2.apk)

Windows 包是 .NET 8 框架依赖版本，需要安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

Android APK 使用 Release keystore 签名，可用于正式分发。签名私钥不在仓库中；必须妥善备份，后续版本需要使用同一把 keystore 才能覆盖升级。

若已安装此前的 Debug 签名测试包，首次安装此 Release 版前需要先卸载旧包；这是 Android 对不同签名证书的安全要求，卸载会清除应用内保存的 Windows IP 和配对码。

## 使用方法

### Windows

1. 解压 `ClipBridge-Windows-v1.2.2.zip`。
2. 运行 `ClipBridge.Windows.exe`。
3. 记下窗口中显示的 Windows 局域网 IPv4。
4. 输入至少 4 位配对码，点击“开始同步”。
5. 首次启动会弹出 Windows 管理员授权；选择“是”后，ClipBridge 会自动允许“专用网络”的 TCP 45837 入站连接。

最小化或点击关闭按钮后，程序会隐藏到系统托盘并继续同步。双击托盘图标恢复窗口，右键选择“退出”才会真正停止。

填写配对码后可点击“开启开机自启动”。之后 Windows 当前用户登录时，ClipBridge 会自动启动同步并隐藏到托盘；配对码仅以该 Windows 用户可解密的形式保存在本机。

### Android

1. 安装 `ClipBridge-Android-v1.2.2.apk`。
2. 填写 Windows 的局域网 IPv4 和相同配对码。
3. 点击“开始同步”并允许通知权限；通知栏“一键同步当前剪贴板”会默认启用。
4. 在其他应用复制文字后，下拉通知栏并点击“同步当前剪贴板”。

Android 10+ 不允许普通后台应用读取其他应用的剪贴板。ClipBridge 在获得输入焦点时可以读取当前剪贴板，因此也可以在复制后返回 ClipBridge 完成同步。

## Android 剪贴板限制

Android 普通应用只能读取当前 `primaryClip`，不能读取三星或其他输入法保存的私有剪贴板历史。

因此：

- 在其他应用连续复制 A、B、C，最后只点击一次“一键同步”，ClipBridge 只能读取当前的 C。
- 每次复制后都点击一次“一键同步”，A、B、C 会按顺序发送；若 Windows 已启用 `Win+V` 剪贴板历史，通常会被系统记录。
- Windows 发往 Android 的内容会依次写入 Android 系统剪贴板；输入法是否长期保留由输入法自身决定。

要在 Android 上做到完全后台自动捕获，需要把应用实现为并启用为默认输入法；1.2.2 版本不包含此模式。

## 网络要求

- Windows 与 Android 位于同一局域网
- 路由器未启用客户端隔离
- TCP 端口 `45837` 可访问
- Android 主动连接 Windows；Windows 作为 TCP 服务端
- 当前桌面端只保留一个活动 Android 连接

## 安全说明

每条消息带有 HMAC-SHA256，用于验证配对码并检测内容篡改。协议详情见 [protocol/PROTOCOL.md](protocol/PROTOCOL.md)。

4 位只是最低长度，抗猜测能力很弱，建议使用至少 8 位随机配对码。当前 1.2.2 版本的局域网内容仍以明文传输；HMAC 只提供认证与完整性校验，不提供加密，也不能抵御截获后的离线猜码。请只在可信局域网中使用，不要将 TCP 45837 暴露到公网。后续版本可使用 TLS/Noise 加密并保存已配对设备公钥。

## 从源码构建

### Windows

要求：

- Windows 10/11
- .NET 8 SDK

```powershell
dotnet build windows\ClipBridge.Windows\ClipBridge.Windows.csproj -c Release
dotnet publish windows\ClipBridge.Windows\ClipBridge.Windows.csproj `
  -c Release `
  -o artifacts\windows
```

### Android

要求：

- JDK 17
- Android SDK 35

Windows：

```powershell
cd android
.\gradlew.bat :app:assembleDebug
```

macOS/Linux：

```bash
cd android
./gradlew :app:assembleDebug
```

APK 输出：

```text
android/app/build/outputs/apk/debug/app-debug.apk
```

构建正式 Release APK 前，将 `android/keystore.properties.example` 复制为 `android/keystore.properties`，填入本机 Release keystore 路径、别名和密码；该文件和 keystore 均已被 Git 忽略。

```powershell
cd android
.\gradlew.bat :app:assembleRelease
```

Release APK 输出：

```text
android/app/build/outputs/apk/release/app-release.apk
```

## 项目结构

```text
android/                 Android Kotlin + Compose 源码
windows/                 Windows WPF 源码
protocol/                TCP/HMAC 线协议
assets/icons/            共用应用图标
dist/                    1.2.2 可安装产物
MACOS_HANDOFF.md         macOS 26+ 开发交接
CHANGELOG.md             版本说明
```

## macOS

macOS 端尚未提交实现。计划使用 SwiftUI、AppKit、Network.framework 与 CryptoKit，最低支持 macOS 26.0。完整协议、工程结构、权限和联调步骤见 [MACOS_HANDOFF.md](MACOS_HANDOFF.md)。

## 许可证

本项目以 [MIT License](LICENSE) 开源。

---

## English

ClipBridge is a LAN clipboard synchronization tool for bidirectional text and single-image sync between Windows and Android. The macOS 26+ implementation handoff is available in [MACOS_HANDOFF.md](MACOS_HANDOFF.md).

Current stable version: **1.2.2**

### Features

- Bidirectional plain-text and single-image clipboard sync between Windows and Android.
- Original image size limit: 20 MB. Supported formats: PNG, JPG/JPEG/JFIF, BMP, GIF, TIFF, WebP, HEIC/HEIF, AVIF, and ICO.
- When an original file or URI is available, its bytes are transferred directly without decoding or transcoding. Animated GIFs retain all frames.
- Only bitmap-only clipboard data is encoded as PNG; when that exceeds 20 MB, high-quality JPEG is attempted automatically.
- One TCP connection carries both directions, with HMAC-SHA256 pairing-code and message-integrity verification.
- Windows uses native clipboard events, stays in the system tray after minimizing or closing the window, and can auto-start for the current user.
- Android uses a foreground service and offers a notification action to sync the current clipboard.
- Messages are processed in FIFO order with a maximum of 20 pending items. Remote-origin markers and message UUIDs prevent feedback loops.

### Downloads

- [GitHub Releases](https://github.com/yeamu/ClipBridge/releases/latest)
- [Windows 1.2.2 ZIP](dist/ClipBridge-Windows-v1.2.2.zip)
- [Android 1.2.2 APK](dist/ClipBridge-Android-v1.2.2.apk)

The Windows package requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). The Android APK is signed with a Release keystore. The private key is not in this repository, and future updates must use the same keystore.

If you previously installed a Debug-signed build, uninstall it before installing the Release APK for the first time. Android does not allow an APK signed by another certificate to replace it, and uninstalling clears the saved Windows IP address and pairing code.

### Quick start

#### Windows

1. Extract `ClipBridge-Windows-v1.2.2.zip` and run `ClipBridge.Windows.exe`.
2. Note the LAN IPv4 address displayed in the window.
3. Enter a pairing code with at least four characters and click **Start sync**.
4. On the first start, approve the Windows administrator prompt. ClipBridge then automatically creates a private-network inbound TCP 45837 firewall rule.

Minimizing or closing the window keeps ClipBridge running in the system tray. Double-click the tray icon to restore the window; choose **Exit** in the tray menu to stop it. You can enable auto-start after entering a pairing code. The code is stored in a form decryptable only by the current Windows user.

#### Android

1. Install `ClipBridge-Android-v1.2.2.apk`.
2. Enter the Windows LAN IPv4 address and the same pairing code.
3. Tap **Start sync** and grant notification permission.
4. After copying content in another app, open the notification shade and tap **Sync current clipboard**. Returning to ClipBridge after copying can also trigger synchronization.

### Android clipboard limitations

On Android 10 and later, regular background apps cannot read other apps' clipboards. ClipBridge can read only the current system `primaryClip`; it cannot bulk-read private clipboard histories maintained by Samsung Keyboard or other IMEs.

- If you copy A, B, and C and synchronize only once, only the latest item, C, can be sent.
- Synchronize after each copy to send A, B, and C in order.
- Content received from Windows is written to the Android system clipboard in order. Whether an IME retains it is controlled by that IME.
- Fully automatic background capture requires implementing and enabling ClipBridge as the default keyboard; version 1.2.2 does not include that mode.

### Network and security

- Both devices must be on the same LAN and client isolation must be disabled.
- TCP port `45837` must be reachable. Android initiates the connection; the desktop side keeps one active Android connection.
- Every message is authenticated with HMAC-SHA256. See [protocol/PROTOCOL.md](protocol/PROTOCOL.md).
- Four characters are only the minimum. Use a random pairing code of at least eight characters.
- Payloads are not encrypted in version 1.2.2. HMAC provides authentication and integrity, not confidentiality. Use ClipBridge only on trusted LANs and never expose its TCP port to the public internet.

### Build from source

#### Windows

Requirements: Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet build windows\ClipBridge.Windows\ClipBridge.Windows.csproj -c Release
dotnet publish windows\ClipBridge.Windows\ClipBridge.Windows.csproj `
  -c Release `
  -o artifacts\windows
```

#### Android

Requirements: JDK 17 and Android SDK 35.

```powershell
cd android
.\gradlew.bat :app:assembleDebug
```

On macOS/Linux, use `./gradlew :app:assembleDebug`. The Debug APK is written to `android/app/build/outputs/apk/debug/app-debug.apk`.

Before building a Release APK, copy `android/keystore.properties.example` to `android/keystore.properties` and provide the local keystore path, alias, and passwords. Both that file and keystores are ignored by Git.

```powershell
cd android
.\gradlew.bat :app:assembleRelease
```

The Release APK is written to `android/app/build/outputs/apk/release/app-release.apk`.

### macOS and license

The macOS implementation has not been committed yet. It is planned as a macOS 26.0+ app using SwiftUI, AppKit, Network.framework, and CryptoKit. See [MACOS_HANDOFF.md](MACOS_HANDOFF.md) for the full handoff.

ClipBridge is released under the [MIT License](LICENSE).
