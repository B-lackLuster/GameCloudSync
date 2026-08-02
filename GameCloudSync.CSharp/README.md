# GameCloudSync — C# / WPF 重构版

这是原 Python + tkinter 版本（`dp4`）的 C# 重构。目标：更现代的界面观感、更好的性能，同时保持与旧版的数据和云端备份格式完全兼容。

## 技术栈

- .NET 9（LTS 前身，Windows 桌面）
- WPF + 自定义主题（圆角卡片、徽章、深色日志面板、现代按钮）
- 全程 `async/await`，UI 永不阻塞；文件复制与 WebDAV 传输并行化
- xUnit 单元测试（Core 层）

## 项目结构

```
GameCloudSync.CSharp/
├── GameCloudSync.sln
├── src/
│   ├── GameCloudSync.Core/        # 可测试的业务核心（无 UI 依赖）
│   │   ├── Models/                # AppConfig / GameConfig / SyncStatus ...
│   │   ├── Services/
│   │   │   ├── ConfigStore.cs     # config.json 原子读写 + 旧版 list 兼容
│   │   │   ├── SecretStore.cs     # DPAPI 凭据 + 旧 Fernet 凭据读取迁移
│   │   │   ├── WebDavClient.cs    # PROPFIND/MKCOL/PUT/GET/DELETE + 并行目录传输
│   │   │   ├── BackupService.cs   # 备份/恢复/清理/同步状态判断
│   │   │   ├── MonitoringService.cs # FileSystemWatcher + 冷却 + 原子任务提交
│   │   │   └── CloudConfigService.cs # 云端配置备份/检测/合并
│   │   └── Util/                  # 备份命名解析、目录复制、路径适配
│   └── GameCloudSync.App/         # WPF 界面（WinExe）
│       ├── Themes/Theme.xaml      # 统一设计系统（配色/圆角/字体）
│       ├── Views/                 # 主窗口 + 全部对话框
│       ├── ViewModels/            # MainViewModel / 绑定状态
│       └── Services/              # 托盘、单实例、注册表自启
└── tests/GameCloudSync.Core.Tests/
```

## 构建与运行

```powershell
# 需要 .NET SDK 9（winget install Microsoft.DotNet.SDK.9）
dotnet build GameCloudSync.sln

# 运行（调试）
dotnet run --project src/GameCloudSync.App

# 单元测试
dotnet test GameCloudSync.sln

# 发布自包含单文件 EXE（无需目标机安装 .NET）
dotnet publish src/GameCloudSync.App -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

发布产物：`publish/GameCloudSync.exe`（约 74 MB，自包含，可直接拷贝运行）。

## 功能对照（与 Python 版一致）

| 功能 | 说明 |
|------|------|
| 双通道备份 | OneDrive（本地云盘目录）+ WebDAV（远程服务器），可多选 |
| 自动监控 | FileSystemWatcher 监控存档目录，10 秒冷却 + mtime 去重 |
| 历史版本 | `save_时间戳` / `save_时间戳_mtime值` 命名，OneDrive/WebDAV 分页签管理 |
| 批量操作 | 批量上传、批量覆盖下载、批量删除、批量导出/导入配置 |
| 云配置同步 | 配置自动备份到云端、启动检测新游戏、「更新配置」一键合并 |
| 系统托盘 | 关闭窗口最小化到托盘，托盘菜单启停监控/退出 |
| 单实例 | Mutex 互斥，第二实例写入 wakeup 文件唤醒主窗口 |
| 开机自启 | 注册表 HKCU Run 项 |
| 凭据安全 | DPAPI 加密存储 WebDAV 密码，不写入 config.json |

## 与旧版的数据兼容

- `config.json`：字段名保持 snake_case，可直接读取旧版配置；旧版“纯列表”格式也能识别；老用户的明文 `webdav_password` 会在启动时自动迁移进 DPAPI 并清出文件。
- `.gcs_webdav.cred`：新版写入 `dpapi` 方案；旧 `fernet` 方案可读取并自动迁移（含 `.gcs_salt`）。
- 备份目录名：完全沿用 `save_yyyyMMdd_HHmmss` / `save_..._mtime<epoch>` / `config_...`，新旧版可以互相看到对方的备份。
- 配置路径：与旧版一致，`config.json` 和凭据文件都放在可执行文件所在目录。

## 与旧版的主要差异

1. UI 全面重做：卡片式游戏列表、状态徽章、圆角按钮、深色日志面板。
2. 所有 IO 改为异步 + 并行（目录复制 4 路、WebDAV 4 路并发），大存档备份更快，界面不卡。
3. 单文件 EXE 无需安装 Python 和第三方库；启动速度显著优于 PyInstaller。
4. 代码分层：业务逻辑全部在 Core 层，UI 只做展示和交互，并配有单元测试。

## 说明

- Windows 10/11 专用，无跨平台需求（与你的要求一致）。
- 若直接使用旧版 `dist/` 中的配置，把 `config.json` 拷贝到发布目录即可，凭据会自动迁移。
