# v1.3 正式签名

正式签名 `clipbridge-release.jks` 仅保存在本机并被 Git 忽略，不上传 GitHub。该文件为 PKCS12，RSA 3072，别名 `clipbridge`；强随机密码保存在本机被忽略的 `android/keystore.properties`。

请离线备份签名文件和密码。只有签名文件无法重新签署版本；密码遗失后无法从 APK 恢复私钥。不要把密码写入本说明、示例配置、提交或 Release。

维护官方版本时，将 `android/keystore.properties.example` 复制为 `android/keystore.properties`，恢复备份的签名及密码。`storeFile=signing/clipbridge-release.jks` 相对 `android/` 目录解析。保持安装标识 `com.clipbridge.app` 并沿用此签名，后续版本可覆盖升级。

其他开发者 clone 项目后可直接构建 Debug，Gradle 自动使用本机测试签名；自行分发 Release 时应配置自己的 keystore，无需官方私钥。自己的签名不能覆盖官方 APK。

旧 v1.2.3 正式签名已遗失，因此 v1.3 无法覆盖它；用户需先卸载旧正式版，再安装并重新配置。Debug 包使用不同安装标识和测试签名，不作为正式发布包。

v1.3 官方签名证书的 SHA-256 指纹（公开校验信息）：`d344dfdb81fe9e437c489c40dc2f07d2dce74548421ea57c6c547077b522791c`。
