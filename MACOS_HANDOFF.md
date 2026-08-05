# ClipBridge macOS 26+ 开发交接

最后更新：2026-07-23  
工作区：仓库根目录

## 1. 交接状态

macOS 端尚未创建源码。Windows 与 Android 1.2.2 已完成文字、原始图片和 GIF 动画双向同步，可作为 macOS 端联调基准。

请在安装了 Xcode 26、运行 macOS 26 或更高版本的 Mac 上继续。当前 Windows 环境无法编译、签名或运行 macOS `.app`。

推荐技术栈：

- Swift 6.2
- SwiftUI + AppKit
- Network.framework
- CryptoKit
- 原生 macOS App 工程，不引入第三方依赖
- Deployment Target：macOS 26.0

Apple 的 PackageDescription 6.2 已提供 `.macOS(.v26)`：

- <https://developer.apple.com/documentation/packagedescription/supportedplatform/macosversion/v26>
- 也可以用 `.macOS("26.0")`：<https://developer.apple.com/documentation/packagedescription/supportedplatform/macos(_:)-9771f>

## 2. 当前可兼容版本

- Windows：`dist/ClipBridge-Windows-v1.2.2.zip`
- Android：`dist/ClipBridge-Android-v1.2.2.apk`
- Android application ID：`com.clipbridge`
- TCP 端口：`45837`
- 配对码：至少 4 位
- 当前握手协议版本：v1
- 当前应用版本：1.2.2
- 同步类型：纯文本与图片
- 图片原始二进制上限：20 MB

Windows 端现有能力：

- 作为 TCP 服务端监听 `0.0.0.0:45837`
- 托盘后台运行
- 原生 Windows 剪贴板写入
- `WM_CLIPBOARDUPDATE` 事件监听
- 双向 FIFO 网络发送
- 最多 20 条远端待写队列
- Windows `Win+V` 自动记录写入内容
- 原始图片文件直接发送；仅位图剪贴板使用 PNG/JPEG 兜底
- GIF 原始文件传输，保留完整动画

Android 端现有能力：

- 主动连接桌面端 IP 的 TCP 45837
- 前台服务维持网络连接
- 通知栏“一键同步当前剪贴板”
- Android → 桌面端最多缓存 20 条已捕获内容
- 桌面端 → Android 按接收顺序写入系统剪贴板
- Android 10+ 后台剪贴板限制仍然存在
- Gallery/FileProvider 提供 URI 时直接读取并发送原始图片
- 支持 PNG、JPG/JPEG/JFIF、BMP、GIF、TIFF、WebP、HEIC/HEIF、AVIF、ICO

重要限制：三星输入法显示的剪贴板历史是输入法私有数据。ClipBridge 普通应用只能读取 Android 当前 `primaryClip`，无法批量读取输入法历史。

## 3. macOS 产品目标

macOS 端应替代 Windows 桌面端，与现有 Android APK 直接兼容：

1. Mac 作为 TCP 服务端监听 45837。
2. Android 填写 Mac 的局域网 IPv4。
3. 两端填写相同的至少 4 位配对码。
4. Mac 复制纯文本或图片后发送到 Android。
5. Android 发来纯文本或图片后立即写入 Mac 当前剪贴板。
6. 远端写入不能被当成本地复制再次发送。
7. 连续消息按 FIFO 顺序处理，上限 20 条。
8. 应用关闭窗口后继续驻留菜单栏。
9. 菜单栏提供状态、显示窗口、开始/停止同步和退出。
10. 最低支持 macOS 26.0。

## 4. 线协议（必须逐字兼容）

协议文件：`protocol/PROTOCOL.md`

传输规则：

- TCP
- 每条消息是单行 UTF-8 JSON
- 每条 JSON 后追加 `\n`
- 接收缓冲区需要处理粘包、拆包与多行
- macOS 端单行上限至少设置为 32 MiB；20 MB 图片经 Base64 后约 26.7 MiB，再加 JSON 字段
- JSON 字段名区分大小写，必须保持下面的 PascalCase

### 4.1 Hello

双方连接后都发送：

```json
{
  "Type": "hello",
  "DeviceId": "UUID",
  "DeviceName": "MacBook Pro",
  "Proof": "BASE64_HMAC",
  "Version": 1
}
```

Proof 的精确输入：

```text
hello|{DeviceId}|1
```

算法：

```text
Base64(HMAC-SHA256(
  key = UTF8(pairingCode),
  data = UTF8("hello|{DeviceId}|1")
))
```

只有 Hello 验证通过后才允许处理或发送 `clip`。验证失败应关闭连接。

### 4.2 Clipboard

```json
{
  "Type": "clip",
  "Message": {
    "Id": "UUID",
    "OriginDeviceId": "UUID",
    "Text": "clipboard text",
    "SentAt": 1784780000000
  },
  "Mac": "BASE64_HMAC"
}
```

Canonical 字符串必须精确为：

```text
clip|{Id}|{OriginDeviceId}|{Text}|{SentAt}
```

Mac：

```text
Base64(HMAC-SHA256(
  key = UTF8(pairingCode),
  data = UTF8(canonicalString)
))
```

注意事项：

- `Text` 参与 HMAC 时使用原始 Swift `String` 的 UTF-8，不做换行替换或 Unicode 归一化。
- `SentAt` 是 Unix epoch 毫秒整数（Int64）。
- JSON 转义不影响 canonical；canonical 使用解码后的原始文本。
- Base64 使用标准 alphabet 和 padding，不用 URL-safe Base64。
- 先验证 MAC，再记录 Message ID。
- 忽略 `OriginDeviceId == ownDeviceId` 的消息。
- Message ID 应保存为有上限的近期集合，避免重复应用；建议 LRU 1000 条。

`Message.Text` 同时承载图片载荷，必须按以下前缀解析：

| 前缀 | 二进制内容 | 写入建议 |
| --- | --- | --- |
| `clipbridge:png:` | 原始 PNG | `.png` / `UTType.png` |
| `clipbridge:jpeg:` | 原始 JPEG/JFIF | `.jpg` / `UTType.jpeg` |
| `clipbridge:gif:` | 原始动画 GIF | `.gif` 文件 URL，必须保留全部帧 |
| `clipbridge:image:<extension>:` | 其他原始图片 | 按白名单扩展名保存 |

允许的 `<extension>`：`png`、`jpg`、`jpeg`、`jfif`、`bmp`、`gif`、`tif`、`tiff`、`webp`、`heic`、`heif`、`avif`、`ico`。

图片处理规则：

- Base64 解码后的原始二进制不得超过 20 MB。
- 扩展名必须先转小写并通过固定白名单，禁止直接拼接未经验证的路径。
- 收到原始图片后保存到应用缓存目录，再通过 `NSPasteboard` 写入文件 URL 和适用的 UTI。
- GIF 不得通过 `NSImage` 重编码，否则可能只剩第一帧。
- 本地剪贴板能取得文件 URL 时直接读取原始文件发送，不解码、不转码。
- 只有剪贴板仅提供位图时才编码为 PNG；若 PNG 超过 20 MB，再使用 JPEG 压缩到限制内。
- Base64 前缀字符串本身参与 HMAC，不能在验证前修改大小写、扩展名或内容。

### 4.3 心跳

Android 每约 2 秒发送：

```json
{"Type":"ping"}
```

Mac 回复：

```json
{"Type":"pong"}
```

### 4.4 连接模型

- Android 主动连接桌面端。
- Mac 用 `NWListener` 监听 45837。
- 当前产品只支持一个活动 Android 客户端。
- 新客户端连接时关闭旧连接。
- 对同一连接必须使用单一串行发送器，禁止多个 Task 同时写 socket。
- Hello、pong 与 clip 都通过同一个发送队列。

## 5. 推荐工程结构

建议创建 Xcode 26 的 macOS App 工程：

```text
macos/ClipBridgeMac/
├── ClipBridgeMac.xcodeproj
├── ClipBridgeMac/
│   ├── ClipBridgeMacApp.swift
│   ├── AppModel.swift
│   ├── ContentView.swift
│   ├── MenuBarView.swift
│   ├── ClipboardMonitor.swift
│   ├── ClipboardWriter.swift
│   ├── TCPServer.swift
│   ├── ProtocolModels.swift
│   ├── ProtocolCrypto.swift
│   ├── LocalIPv4.swift
│   ├── Info.plist
│   ├── ClipBridgeMac.entitlements
│   └── Assets.xcassets/
└── README.md
```

建议职责：

### `ClipBridgeMacApp.swift`

- `WindowGroup`：设置/状态窗口
- `MenuBarExtra`：后台菜单栏入口
- AppModel 作为共享状态
- 用户关闭窗口时不退出应用

### `AppModel.swift`

- `@MainActor`
- 保存配对码、运行状态、设备名、Mac IP、连接状态
- 配对码保存在 Keychain；非敏感启动偏好保存在 `UserDefaults`
- 负责协调 TCPServer 与 ClipboardMonitor
- 配对码少于 4 位时不启动

### `TCPServer.swift`

- `NWListener(using: .tcp, on: 45837)`
- 新连接到达时关闭旧连接
- 按行解码 UTF-8 JSON
- 握手认证门控
- 单 writer actor/queue
- 断开后更新 UI，但继续监听

### `ProtocolModels.swift`

Swift Codable 模型需要明确 CodingKeys，保证字段大小写：

```swift
struct Hello: Codable {
    let type: String
    let deviceId: String
    let deviceName: String
    let proof: String
    let version: Int

    enum CodingKeys: String, CodingKey {
        case type = "Type"
        case deviceId = "DeviceId"
        case deviceName = "DeviceName"
        case proof = "Proof"
        case version = "Version"
    }
}
```

Clip/SignedClip 同理。

### `ProtocolCrypto.swift`

使用 CryptoKit：

```swift
import CryptoKit

func proof(code: String, value: String) -> String {
    let key = SymmetricKey(data: Data(code.utf8))
    let digest = HMAC<SHA256>.authenticationCode(
        for: Data(value.utf8),
        using: key
    )
    return Data(digest).base64EncodedString()
}
```

### `ClipboardMonitor.swift`

macOS AppKit 没有通用的全局剪贴板变更通知。正确做法是轻量检查 `NSPasteboard.general.changeCount`；只有计数变化时才读取文本。

Apple 文档：

- <https://developer.apple.com/documentation/appkit/nspasteboard/changecount>

推荐：

- 同步运行时启动 `DispatchSourceTimer`
- 间隔 150–250 ms
- 每次只比较整数 `changeCount`
- 计数未变化时不读取剪贴板
- 停止同步时取消 Timer
- 计数变化后按“文件 URL 图片 → 原始图片 Data → 普通字符串”的顺序读取
- 空文本不发送；图片执行 20 MB 上限检查
- 相同文本是否再次发送应根据 changeCount/来源标记决定，不能只按文本永久去重

虽然形式上有 Timer，但它不是反复读取剪贴板，只检查 AppKit 提供的 changeCount。macOS 没有与 Windows `WM_CLIPBOARDUPDATE` 等价的公开全局事件。

图片读取建议使用 `UniformTypeIdentifiers`：

- Finder 复制单张受支持图片：优先读取 `.fileURL`，直接发送文件 Data。
- 其他应用复制图片：依次尝试 `UTType.png`、`UTType.jpeg`、`UTType.gif`、`UTType.heic` 等原始 Data。
- 只存在 `NSImage`/TIFF 表示时才进入 PNG/JPEG 兜底编码。
- 一次只同步一张图片；多个文件暂不进入 v1.2.2 协议。

### `ClipboardWriter.swift`

远端写入时附加私有类型：

```swift
let originType = NSPasteboard.PasteboardType(
    "com.clipbridge.origin.v1"
)
```

写入建议：

```swift
let pasteboard = NSPasteboard.general
pasteboard.clearContents()
pasteboard.declareTypes([.string, originType], owner: nil)
pasteboard.setString(text, forType: .string)
pasteboard.setString(messageId, forType: originType)
```

记录写入后的 `changeCount`。ClipboardMonitor 遇到同一个 changeCount 和私有标记时跳过发送；用户之后重新复制相同文本会产生新的 changeCount，应作为本地复制发送。

图片写入：

- 将解码后的原始 Data 保存到 `FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)` 下的 ClipBridge 专用目录。
- 文件名使用随机 UUID；扩展名只能来自协议白名单。
- 在 pasteboard item 中写入文件 URL；PNG/JPEG 等可同时写对应 UTI Data，提高在聊天软件和图像软件中的粘贴兼容性。
- GIF 必须至少写文件 URL 或 `UTType.gif` 原始 Data，不得用单帧 `NSImage` 替换。
- 私有来源标记同样写进图片 pasteboard item，并记录最终 `changeCount`。

远端待写队列：

- FIFO
- 最大 20 条
- 队满时丢弃最旧内容
- 每条按顺序写入
- 写入必须在 MainActor 或主线程完成

## 6. UI 建议

主窗口保持与 Windows 端一致的最小功能：

- 标题：ClipBridge
- 显示 Mac 局域网 IPv4（只读）
- 配对码输入框（至少 4 位）
- 开始同步 / 停止同步
- 当前状态
- “关闭窗口后仍在菜单栏运行”说明

菜单栏建议：

- 图标：剪贴板 + 双向同步
- 状态：等待 Android / 验证中 / 已连接
- 显示窗口
- 开始或停止同步
- 退出 ClipBridge

项目已有图标源：

- `assets/icons/clipbridge-512.png`
- `windows/ClipBridge.Windows/Assets/clipbridge.ico`

在 Xcode 中把 PNG 导入 `Assets.xcassets/AppIcon.appiconset`。菜单栏应使用单色 template image，不能直接显示彩色方形 AppIcon。

## 7. macOS 权限与配置

建议 Info.plist：

```xml
<key>NSLocalNetworkUsageDescription</key>
<string>ClipBridge 需要在局域网内与 Android 设备同步剪贴板。</string>
<key>LSUIElement</key>
<true/>
```

说明：

- 若使用 `LSUIElement = true`，应用不会常驻 Dock；窗口仍可由菜单栏显示。
- 若希望首次运行时出现在 Dock，可先不设置 LSUIElement，再通过 App activation policy 控制。

建议 entitlements：

```xml
<key>com.apple.security.app-sandbox</key>
<true/>
<key>com.apple.security.network.server</key>
<true/>
```

若需要主动连接其他服务，再加：

```xml
<key>com.apple.security.network.client</key>
<true/>
```

当前 Mac 端只作为服务端，理论上只需要 network server。

首次启动时：

- 允许“本地网络”
- 若系统防火墙询问，允许传入连接

## 8. 在 Mac 上的创建与构建步骤

1. 安装 Xcode 26。
2. 打开 Xcode，新建 `macOS > App`。
3. Product Name：`ClipBridgeMac`
4. Interface：SwiftUI
5. Language：Swift
6. Deployment Target：macOS 26.0
7. Bundle Identifier 示例：`com.clipbridge.mac`
8. 将工程保存到仓库的 `macos/ClipBridgeMac/`。
9. 按第 5 节创建文件。
10. 导入 `assets/icons/clipbridge-512.png`。
11. 设置 Signing Team。
12. 添加 App Sandbox 与 Incoming Connections 权限。
13. Build & Run。

命令行构建示例：

```sh
xcodebuild \
  -project macos/ClipBridgeMac/ClipBridgeMac.xcodeproj \
  -scheme ClipBridgeMac \
  -configuration Release \
  -derivedDataPath .build/macos
```

未签名本地 `.app` 通常位于：

```text
.build/macos/Build/Products/Release/ClipBridgeMac.app
```

正式分发还需要：

- Developer ID Application 签名
- Hardened Runtime
- Notarization
- stapler

## 9. 联调步骤

1. Mac 与 Android 连接同一 Wi-Fi。
2. Mac 启动 ClipBridge，填写至少 4 位配对码并开始同步。
3. 记下 Mac 界面显示的 IPv4。
4. Android 安装 `ClipBridge-Android-v1.2.2.apk`。
5. Android 填入 Mac IPv4 和相同配对码。
6. Android 点击开始同步并允许通知权限。
7. 确认双方显示握手验证成功。

必须验证：

- Mac 复制文本 → Android 当前剪贴板更新。
- Android 复制文本 → 点通知栏“一键同步” → Mac 更新。
- Mac 连续复制 A、B、C → Android 按顺序收到。
- Android 每次捕获并同步 A、B、C → Mac 按顺序写入。
- Android 复制 1–20 MB 的 HEIC → Mac 收到同大小原始文件，不转 PNG。
- Android 复制 GIF → Mac 保存并粘贴后仍包含完整动画。
- Mac 从 Finder 复制 PNG、JPEG、WebP、HEIC、AVIF → Android 收到原始格式。
- 超过 20 MB 的图片在发送端明确提示并且不入队。
- 只提供位图的截图/画布 → PNG 或 JPEG 兜底后可双向粘贴。
- 配对码错误时连接立即关闭，不传剪贴板。
- Android 断网后重新连接。
- Mac 关闭窗口后菜单栏仍在、连接仍在。
- 菜单栏退出后端口释放。
- 远端写入不会形成回写循环。
- Unicode、中文、emoji、多行文本和包含 `|` 的文本 HMAC 均正确。

## 10. 已知限制与安全边界

- 支持纯文本和单张图片；不支持任意文件、富文本及一次多张图片。
- 局域网内容仍是明文；HMAC 只提供认证与完整性，不提供加密。
- 后续生产版应改为 TLS/Noise，并安全保存配对设备公钥。
- Android 10+ 普通后台应用不能读取其他应用产生的剪贴板。
- Android 输入法历史不能通过标准 ClipboardManager 批量读取。
- 当前桌面服务只保留一个 Android 连接。
- macOS 没有 Windows 式全局剪贴板更新消息，因此需要检查 `NSPasteboard.changeCount`。

## 11. 推荐验收标准

- Xcode Release 构建 0 error。
- macOS deployment target 确认为 26.0。
- 不引入第三方依赖。
- 空闲时 changeCount 检查不造成可见 CPU 占用。
- Android 与 Mac 保持连接 30 分钟无周期性断开。
- 单条文本在正常局域网下端到端延迟小于 300 ms。
- 连续 20 条消息保持顺序且无重复。
- 1–20 MB 原始 HEIC 往返后文件格式和字节保持不变。
- 动画 GIF 往返后帧数、时长和原始字节保持不变。
- PNG、JPG/JPEG/JFIF、BMP、GIF、TIFF、WebP、HEIC/HEIF、AVIF、ICO 均完成至少一次单向原始文件测试。
- 20 MB 图片可以成功同步，20 MB + 1 byte 被拒绝。
- 错误配对码无法收到任何剪贴板明文。
- 关闭主窗口不停止同步，明确退出才停止。

## 12. 给 Mac 上 Codex 的直接任务说明

可把下面这段直接交给 Mac 上的 Codex：

```text
请先完整阅读仓库根目录 MACOS_HANDOFF.md 和 protocol/PROTOCOL.md。
在 macos/ClipBridgeMac/ 创建原生 SwiftUI macOS App，最低支持 macOS 26.0。
严格复用现有 ClipBridge v1 TCP/HMAC 协议，Mac 作为 45837 TCP 服务端，
与当前 Android v1.2.2 APK 兼容。实现菜单栏常驻、主设置窗口、
至少 4 位配对码、单客户端握手认证、单 writer、FIFO 20 条、
NSPasteboard.changeCount 监听、远端来源标记与防回写循环。
实现文字与单张图片双向同步：原始图片上限 20 MB，支持
PNG/JPEG/JFIF/BMP/GIF/TIFF/WebP/HEIC/HEIF/AVIF/ICO；能读取原始文件时
必须直接传输，GIF 保留完整动画，只有位图剪贴板才做 PNG/JPEG 兜底。
单行网络缓冲上限至少 32 MiB，并严格校验图片扩展名白名单。
使用 Network.framework、CryptoKit、SwiftUI、AppKit，不引入第三方依赖。
完成后在 Mac 上用 Xcode 26 Release 构建并实际联调 Android。
```
