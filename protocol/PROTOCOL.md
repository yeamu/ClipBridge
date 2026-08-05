# ClipBridge v1.2.1 wire protocol

连接是 TCP，单条 UTF-8 JSON 以 `\n` 分隔。接收端若实施单行限制，应至少允许 32 MiB，以容纳 20 MB 图片的 Base64 与 JSON 元数据。

1. 双方建立连接时先发送 `hello`：`deviceId`、`deviceName`、协议版本与 `proof`，用于状态展示与后续设备信任升级。
2. `proof = HMAC-SHA256(pairingCode, "hello|deviceId|version")` 的 Base64。
3. `clip` 可在连接建立后发送：`id`、`originDeviceId`、`text`、`sentAt`、`mac`。接收端会验证其 `mac`，无效消息不会写入剪贴板。
4. `mac = HMAC-SHA256(pairingCode, "clip|id|originDeviceId|text|sentAt")` 的 Base64。

`text` 仍用于普通文本。PNG 使用 `clipbridge:png:`，JPEG 使用 `clipbridge:jpeg:`，动画 GIF 使用 `clipbridge:gif:`；其他原始图片使用 `clipbridge:image:<extension>:`，各前缀后接 Base64 数据并沿用相同的 HMAC 字段。两端只处理原始二进制数据不超过 20 MB 的图片。

接收方仅应用 `originDeviceId != ownDeviceId` 的内容，并记录最近的 `id`；由远端写入剪贴板的数据不会再次广播。
