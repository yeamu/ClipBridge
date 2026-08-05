# Changelog

## 1.2.1

- 修复小于限制的 HEIC 照片转成 PNG 后被误判超限
- 剪贴板能提供原文件或 URI 时直接传输原始图片字节与格式
- 只有拿不到原文件、只能读取位图时才使用 PNG/JPEG 编码兜底
- 单张图片上限提高到 20 MB

## 1.2

- 常见静态图片格式输入后统一转为 PNG 同步
- GIF 使用原始文件载荷，保留完整动画
- 单张图片仍限制为 10 MB

## 1.1

- Windows 与 Android 双向 PNG 图片剪贴板同步
- 单张图片最大 10 MB
- Android 开始同步时默认启用通知栏一键同步
- Windows 当前用户开机自动启动与托盘自动同步

## 1.0

- Windows 与 Android 双向纯文本剪贴板同步
- 至少 4 位配对码与 HMAC-SHA256 消息认证
- Windows 原生剪贴板事件监听和低延迟写入
- Windows 系统托盘后台运行
- Android 前台服务保持网络连接
- Android 通知栏一键同步
- Android 已捕获待发送队列、Windows 远端待写队列按 FIFO 顺序处理，最多 20 条
- 写入 Windows 与 Android 当前剪贴板；历史是否保留取决于系统和输入法设置
- 统一 ClipBridge 应用图标
- macOS 26+ 开发交接文档
- Android Release keystore 正式签名
