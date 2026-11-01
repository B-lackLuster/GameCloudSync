# GameCloudSync 云存档管家

[![CI](https://github.com/B-lackLuster/GameCloudSync/actions/workflows/ci.yml/badge.svg)](https://github.com/B-lackLuster/GameCloudSync/actions/workflows/ci.yml)

自动监控游戏存档目录、支持 OneDrive / WebDAV 双通道云备份的 Windows 桌面工具（C# / WPF，.NET 9）。

## 功能

- 自动监控：存档停止变化 5 秒后自动备份；备份进行中的新存档会排队，不会丢失；内容未变则跳过
- 双通道备份：OneDrive / WebDAV 可单独或同时启用
- 多台电脑同步：每个游戏记住本机上次同步时的存档时间；本机和云端在那之后都改过时，先问你保留哪一份，而不是“较新的直接覆盖”
- 历史版本：浏览、恢复、删除、批量下载云端备份；恢复前显示本地和备份各自的时间
- 安全恢复：覆盖本地存档前自动保存快照（保留最近 5 份），可随时还原；备份写完才会出现在列表里，失败或中断的备份不会被当成最新版本
- 云配置同步：配置自动备份到云端，启动时检测新游戏并一键合并
- 批量操作：批量上传、覆盖下载、删除、导出 / 导入配置；处理时显示当前游戏，可随时取消
- 系统集成：托盘运行、开机自启（直接最小化到托盘）、单实例唤醒
- 失败可见：备份失败会弹出系统通知，托盘图标变红，仪表板和游戏卡片写明原因（认证失败、路径不存在、WebDAV 连不上等）；失败的备份会自动重试，WebDAV 请求遇到网络抖动也会自动重试
- 运行日志：完整日志写入 `logs` 文件夹（约保留最近 2 MB），可在「活动日志」页一键打开
- 输入校验：拒绝整个磁盘、用户目录、文档等范围过大的存档目录；保存设置前检查 WebDAV 地址；游戏名称边输边检查
- 存档路径可以写成 `%APPDATA%\游戏\Saves` 这样的形式；从别的电脑导入的配置会自动换成本机的用户目录
- 安全存储：WebDAV 密码 DPAPI 加密，不写入配置文件
- 中英双语：设置中可随时切换界面语言
- 快捷键：F5 刷新，Ctrl+N 添加游戏，Del 删除选中的游戏

## 使用

1. 下载 [Release](https://github.com/B-lackLuster/GameCloudSync/releases) 中的 `GameCloudSync.exe` 直接运行，无需安装 .NET。
2. 旧版用户：把原来的 `config.json` 放到 exe 同目录即可，凭据自动迁移。
3. 在「设置」中配置 OneDrive 云盘目录（可点「自动检测」）和 / 或 WebDAV 服务器，添加游戏后开启监控。

配置、日志和快照默认放在 exe 同目录（便携）。如果这个目录不能写入（比如放在 Program Files 里），会改存到 `%APPDATA%\GameCloudSync`，并把 exe 旁已有的配置一起带过去。

## 项目结构

- `GameCloudSync.CSharp/src/GameCloudSync.Core`：业务核心（配置、凭据、WebDAV、备份恢复、监控、云配置）
- `GameCloudSync.CSharp/src/GameCloudSync.App`：WPF 界面
- `GameCloudSync.CSharp/tests`：xUnit 单元测试，每次推送由 GitHub Actions 在 Windows 上运行

## 构建

需要 .NET SDK 9：

```powershell
dotnet build GameCloudSync.CSharp/GameCloudSync.sln
dotnet test GameCloudSync.CSharp/GameCloudSync.sln
dotnet publish GameCloudSync.CSharp/src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o GameCloudSync.CSharp/publish
```

发布产物为 `GameCloudSync.CSharp/publish/GameCloudSync.exe`（自包含单文件，目标机无需安装 .NET）。

## 兼容性

- `config.json` 字段与旧版（Python）完全兼容，可直接沿用
- 备份目录命名不变，新旧版备份互相可见
- 早期的 Python 版本已从仓库移除，需要时可在 git 历史中找到（最后包含它的提交是 `a8bb92f`）
- 系统要求：Windows 10 / 11
