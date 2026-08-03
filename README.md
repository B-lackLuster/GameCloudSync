# GameCloudSync 云存档管家

自动监控游戏存档目录、支持 OneDrive / WebDAV 双通道云备份的 Windows 桌面工具（C# / WPF，.NET 9）。

## 功能

- 自动监控：存档文件变更后自动备份（10 秒冷却 + mtime 去重）
- 双通道备份：OneDrive / WebDAV 可单独或同时启用
- 历史版本：浏览、恢复、删除、批量下载云端备份
- 云配置同步：配置自动备份到云端，启动时检测新游戏并一键合并
- 批量操作：批量上传、覆盖下载、删除、导出 / 导入配置
- 系统集成：托盘运行、开机自启、单实例唤醒
- 安全存储：WebDAV 密码 DPAPI 加密，不写入配置文件
- 中英双语：设置中可随时切换界面语言

## 使用

1. 下载 [Release](https://github.com/B-lackLuster/GameCloudSync/releases) 中的 `GameCloudSync.exe` 直接运行，无需安装 .NET。
2. 旧版用户：把原来的 `config.json` 放到 exe 同目录即可，凭据自动迁移。
3. 在「设置」中配置 OneDrive 云盘目录和 / 或 WebDAV 服务器，添加游戏后开启监控。

## 构建

需要 .NET SDK 9：

```powershell
dotnet build GameCloudSync.sln
dotnet test GameCloudSync.sln
dotnet publish src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## 兼容性

- `config.json` 字段与旧版（Python）完全兼容，可直接沿用
- 备份目录命名不变，新旧版备份互相可见
- 系统要求：Windows 10 / 11
