# GameCloudSync（C# / WPF 版）

自动监控游戏存档、支持 OneDrive / WebDAV 双通道云备份的 Windows 桌面工具。

## 功能

- 自动监控存档变更并备份（存档停止变化后备份，备份中的新变化排队，内容未变则跳过）
- OneDrive / WebDAV 双通道备份与恢复
- 历史版本浏览、恢复、删除、批量下载
- 恢复前自动保存本地快照，可随时还原；备份写完才发布，失败不会留下半截备份
- 云端配置备份、新游戏检测、一键合并
- 托盘运行、开机自启（直接最小化到托盘）、单实例唤醒
- 备份失败弹出系统通知、托盘变红、界面写明原因；失败自动重试；完整日志写入 `logs` 文件夹
- 拒绝范围过大的存档目录（整盘、用户目录、文档等），保存前检查 WebDAV 地址
- WebDAV 密码 DPAPI 加密存储
- 中英双语界面（设置中切换）

## 项目结构

- `src/GameCloudSync.Core`：业务核心（配置、凭据、WebDAV、备份恢复、监控、云配置）
- `src/GameCloudSync.App`：WPF 界面
- `tests/`：xUnit 单元测试

## 构建 / 测试 / 发布

```powershell
dotnet build GameCloudSync.sln
dotnet test GameCloudSync.sln
dotnet publish src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

发布产物为 `publish/GameCloudSync.exe`（自包含单文件，目标机无需安装 .NET）。

## 兼容性

- 直接读取旧版 `config.json`，老用户凭据自动迁移
- 备份目录命名不变，新旧版备份互相可见
- Windows 10 / 11
