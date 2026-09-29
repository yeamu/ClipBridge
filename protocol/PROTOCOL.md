# ClipBridge v1.3 wire protocol (hello version 1)

连接是 TCP，单条 UTF-8 JSON 以 `\n` 分隔。接收端若实施单行限制，应至少允许 160 MiB，以容纳 100 MB 图片的 Base64 与 JSON 元数据。

v1.3 由 Windows 主动连接手机的 Wi-Fi IPv4:45837，Android 自动获取 Wi-Fi 地址并作为服务端；断线由 Windows 自动重试。此连接方向与 1.2.3 相反，需要同时更新两端，JSON/HMAC 格式不变。Android 换网后关闭旧监听和旧连接，在新 Wi-Fi IPv4 上重新监听并尝试同步当前剪贴板一次；无法在后台读取时需要用户点按通知。手机 IP 改变后需更新 Windows 配置。

1. 双方建立连接时先发送 `hello`：`deviceId`、`deviceName`、协议版本与 `proof`，用于状态展示与后续设备信任升级。
2. `proof = HMAC-SHA256(pairingCode, "hello|deviceId|version")` 的 Base64。
3. `clip` 可在连接建立后发送：`id`、`originDeviceId`、`text`、`sentAt`、`mac`。接收端会验证其 `mac`，无效消息不会写入剪贴板。
4. `mac = HMAC-SHA256(pairingCode, "clip|id|originDeviceId|text|sentAt")` 的 Base64。

`text` 仍用于普通文本。PNG 使用 `clipbridge:png:`，JPEG 使用 `clipbridge:jpeg:`，动画 GIF 使用 `clipbridge:gif:`；其他原始图片使用 `clipbridge:image:<extension>:`，各前缀后接 Base64 数据并沿用相同的 HMAC 字段。两端只处理原始二进制数据不超过 100 MB 的图片。

Android 将 HEIC/HEIF 在发送前转换为 PNG，使用 `clipbridge:png:`；Android 接收旧版 `clipbridge:image:heic:` / `heif:` 时也转换为 PNG。Windows 不接受原始 HEIC/HEIF 载荷，仅接收转换后的 PNG。原文件和转换后的 PNG 均不得超过 100 MB，不回退为 JPEG。

接收方仅应用 `originDeviceId != ownDeviceId` 的内容，并记录最近的 `id`；由远端写入剪贴板的数据不会再次广播。

## 接收确认（v1.3 新增）

双方仍发送 v1 hello/clip JSON，但新增 `ack` 控制消息：`Type="ack"`、`Id`、`Success`、`Proof`。`Proof = Base64(HMAC-SHA256(pairingCode, "ack|id|true"))`（失败为 `false`，小写）。只有完成 hello 验证且 ACK 签名匹配的连接才能确认队首项目；对端真正写入剪贴板后才返回成功 ACK，已成功处理的重复 ID 再次返回成功 ACK。

发送队列内只保存一份原始内容，不保存第二份完整 JSON。网络写入完成后仍保留，收到成功 ACK 后移除；失败 ACK 延迟 30 秒重试，等待 ACK 超过 120 秒或断线重连会重发。队列最多 20 项，常规内容按 UTF-16 估算限制为 64 MiB；超预算的单个大项目可在空队列中单独传输（估算上限 320 MiB），在释放前拒绝额外积压。满队列时拒绝新内容并提示用户，不覆盖未确认的旧项目。

所有待发/待写内容仅在内存中。停止同步、退出或进程终止会丢失未完成内容。近期已处理的 ID 最多保留 4096 个，并结合来源与内容指纹防止循环回写。这不是无限期历史记录。

v1.3 两端需要一起更新；旧版未实现 ACK，不能作为确认接收方使用。HMAC 的 `clip` 内容不包含额外 `Canonical` JSON 属性，以免重复发送图片载荷。现有 Base64/JSON 接收与图片解码仍会产生单张大图的瞬时内存峰值，尚未改为二进制分块协议。

Windows 当前的内存回收优化保持上述 JSON/ACK 格式不变，两端统一实现认证 ACK。Windows 以不超过 64 KiB 的缓冲连续写入同一个 JSON 字符串；接收使用自有 UTF-8 缓冲直接反序列化，避免完整 UTF-16 行与重复 JSON 解析。Windows 握手前单帧上限为 64 KiB，认证后的上限为 512 MiB，覆盖 100 MB 原图的 Base64 载荷。

移出发送队列代表不再保留待重试内容，并不代表进程物理内存立即归还。Windows 对估算至少 4 MiB 的大载荷/图片临时资源合并安排空闲回收：无处理活动约 2 秒后整理托管堆，再给终结器和 STA 消息循环留出时间，稍后进行第二次清理。持续复制或发送时会推迟；普通小文本不触发此策略。此策略仍不能消除运行时/图像组件缓存及当前剪贴板自身的占用。
