# 连接方向与换网回归检查

自动检查：

```powershell
dotnet run --project windows/ClipBridge.ConnectionChecks/ConnectionChecks.csproj -c Release
dotnet run --project windows/ClipBridge.MemoryChecks/MemoryChecks.csproj -c Release
dotnet run --project windows/ClipBridge.ClipboardMemoryChecks/ClipboardMemoryChecks.csproj -c Release
cd android
.\gradlew.bat testDebugUnitTest assembleDebug
```

Windows 检查在 loopback:45837 启动模拟手机，使用生产连接实现及内存剪贴板替身，不改动系统剪贴板。覆盖延迟启动、双向认证消息、未认证/错误 MAC 拒绝、去重、心跳、断开重连、错误配对码和取消连接。

Android 网络回调测试覆盖重复回调、相同 IP 的不同网络、旧网络延迟丢失、断开重连、DHCP 地址变化、IPv6/无有效 IPv4 和停止后的迟到回调。

Android 监听测试使用 Robolectric 与真实 loopback TCP，检查认证后发送、切换地址关闭旧连接、新地址重建监听、换网发送一次、重复事件不重发及停止时关闭连接。它不替代真机 Wi-Fi、Android 后台限制及厂商省电策略验证。

真机联调（两端必须一起更新）：

1. 手机连接 Wi-Fi、输入配对码并启动同步；页面显示的 IPv4 应与手机 Wi-Fi 设置一致。Windows 填这个 IP 和相同配对码，启动后双向复制文本、PNG 和 GIF。
2. 手机切换到另一个与 PC 可达的 Wi-Fi。IP 更新，旧连接关闭；若 IP 变化，在 Windows 停止同步、更新 IP、重新开始。手机在前台时当前内容自动发送一次，重复网络回调不重复发送。
3. 手机在后台切换 Wi-Fi。已有队列在重新配对后发出；Android 若禁止读取新剪贴板，点按“点击同步到电脑”通知。通知只显示标题，详细状态在应用内显示；点通知后文字/图片应正常发送。
4. 关闭再开启 Wi-Fi；在无外网的局域网重复测试。断网暂停，获得有效 Wi-Fi IPv4 后恢复。只有移动数据或只有 IPv6 时不监听移动网络。
5. 在 Windows 先启动、手机稍后启动；强制停止手机进程后重新打开并启动同步；复制较大图片时切换 Wi-Fi。Windows 应自动重连，手机不能卡死在旧连接。
6. 两端分别停止同步，确认没有重连或换网自动发送；重新启动后恢复。修改配对码并验证错误配对码不写入剪贴板。
7. Windows 重启/自启动应读取手机 IP，不能沿用旧版本保存的 Windows 本机 IP。

Android 普通后台应用不能保证读取剪贴板；自动换网请求不等于已成功读取或发送。使用测试 APK 时先停止原安装版的服务，避免两个版本同时占用端口。

内存与缓存检查覆盖：队列字节/条数上限、超限拒绝且不覆盖队首、成功 ACK 后释放、未确认时跨连接重发、停止清空、有限去重历史、分块 UTF-8/HMAC 兼容性、200 次重试复用同一缓存路径、缓存计数/大小/过期与当前引用保护。缓存回收和逻辑队列测试不等同于长时间堆内存/句柄分析；100 MB 单图的 Base64/JSON 解码峰值仍需真机验证。

新增检查：Base64 无子串复制的编解码、JSON 分块写入与 Unicode/转义边界、UTF-8 字节读取/CRLF/EOF/帧大小限制、小文本不主动回收、传输期间推迟回收、大载荷 ACK 后在没有后续剪贴板事件时仍能回收。真实 STA/WIC 测试不写系统剪贴板，通过工作线程编码两批共 10 张随机 3840×2160 图片，检查空闲消息处理、载荷弱引用释放、托管缓冲回收和两批之间的原生内存增长。测量范围及结果见 [内存验证记录](MEMORY_RECLAMATION.md)。
