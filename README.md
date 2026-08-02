# 🎮 全自动多游戏云存档管家 (GameCloudSync)

> **新版（C# / WPF 重构版）已发布**：界面更现代、性能更好、无需安装 Python。源码与使用说明见 [GameCloudSync.CSharp/](GameCloudSync.CSharp/)，本目录为旧版 Python 实现。

> 一个基于 Python tkinter 的 Windows 桌面应用，自动监控游戏存档目录，支持 **OneDrive** 和 **WebDAV** 双通道云同步，并提供系统托盘后台运行、开机自启、历史版本管理、配置云端备份恢复等完整功能。

---

## 💬 前言

这个项目是我用 **Claude Code + DeepSeek V4 Pro** 编写的一个工具。起初的想法很简单——自己有多台电脑，游戏存档总是要手动搬来搬去，就想做个自动同步的小工具。结果一上手就掉进了 Claude Code 里越陷越深，从最初几百行的脚本，一路拆分模块、修线程安全 bug、加 WebDAV 支持、做云端配置同步……代码量翻了几倍，功能也越堆越多。

在这个过程中，我学到了不少东西：Python 的线程模型、tkinter 的自绘组件、WebDAV 协议细节、单实例互斥锁、开机自启注册表操作等等。回头看，这已经不止是一个工具，更是一段挺有意思的学习旅程。

由于目前测试的人比较少，估计还有很多 bug 潜伏着没被发现。如果你下载了这个项目并在使用中遇到任何问题，或者有功能建议，**非常欢迎提 Issue 或 PR**，多多交流，感谢使用！

> 📬 有问题或建议？欢迎在 GitHub 上提 Issue。

---

## 📋 目录

- [功能特性](#-功能特性)
- [系统要求](#-系统要求)
- [安装与运行](#-安装与运行)
- [项目结构](#-项目结构)
- [使用指南](#-使用指南)
  - [基本工作流](#基本工作流)
  - [添加游戏](#添加游戏)
  - [监控与备份](#监控与备份)
  - [恢复存档](#恢复存档)
  - [历史版本管理](#历史版本管理)
  - [全局设置](#全局设置)
  - [云端配置同步](#云端配置同步)
  - [批量导入/导出配置](#批量导入导出配置)
  - [系统托盘](#系统托盘)
- [打包为 EXE](#-打包为-exe)
- [技术架构](#-技术架构)
- [线程安全设计](#-线程安全设计)
- [常见问题](#-常见问题)
- [License](#-license)

---

## ✨ 功能特性

| 功能 | 说明 |
|------|------|
| 🔍 **自动监控** | 基于 watchdog 实时监听存档目录的文件变更，自动触发备份 |
| ☁️ **双云备份** | 同时支持 **OneDrive**（本地文件夹同步）和 **WebDAV**（远程服务器） |
| 🕐 **历史版本** | 每次备份保留时间戳版本，支持浏览、下载、删除历史备份 |
| ⚡ **并行传输** | WebDAV 上传/下载使用多线程并行（默认 4 线程），大幅提升速度 |
| 🖥️ **系统托盘** | 关闭窗口后最小化到托盘，后台静默运行 |
| 🚀 **开机自启** | 通过 Windows 注册表实现开机自动启动 |
| 📦 **批量操作** | 批量添加配置、批量导入/导出、批量下载/删除备份 |
| 📋 **云端配置** | 自动备份游戏配置到云端，启动时检测并合并新游戏（仅新增，不修改已有） |
| 🔒 **单实例** | Windows Mutex 保证只运行一个实例，重复启动会唤醒已有窗口 |
| 🎨 **定制 UI** | 基于 Canvas 的自绘圆角按钮，思源黑体优先显示，复选框图标自定义绘制 |
| 🛡️ **线程安全** | `games_config` 全局加锁保护，TOCTOU 竞态条件已修复 |
| ⏱️ **冷却机制** | 10 秒冷却防止频繁备份，mtime 去重避免无变化重复上传 |

---

## 💻 系统要求

| 组件 | 要求 |
|------|------|
| 操作系统 | Windows 10 / 11（托盘和开机自启依赖 Windows API） |
| Python | 3.10+（使用 `tkinter`、`http.client` 标准库） |
| OneDrive | 已安装并登录（使用 OneDrive 功能时需要） |
| WebDAV | 任意 WebDAV 服务器（如 Nextcloud、ownCloud、坚果云等） |

### Python 依赖

```bash
pip install watchdog pystray Pillow
```

> **注意**：WebDAV 客户端完全基于 Python 标准库（`http.client` + `urllib` + `xml.etree`），无需额外安装第三方库。

---

## 🚀 安装与运行

### 方式一：源码运行

```bash
# 1. 克隆或下载项目
cd dp4

# 2. 安装依赖
pip install watchdog pystray Pillow

# 3. 运行
python main.py
```

### 方式二：打包后的 EXE

直接双击 `dist/GameCloudSync.exe` 运行，无需安装 Python 环境。

---

## 📁 项目结构

```
dp4/
├── main.py              # 程序入口：单实例互斥锁 + Tk 主窗口启动
├── app.py               # 主应用类 CloudBackupApp（~2500 行）
│                        #   包含所有 UI 布局、对话框、业务逻辑
├── constants.py         # 常量定义 & 应用目录检测
│                        #   CONFIG_FILE, COOLDOWN_SECONDS, WAKEUP_FILE...
├── webdav_client.py     # WebDAV 客户端（纯标准库实现）
│                        #   PROPFIND / MKCOL / PUT / GET / DELETE
│                        #   并行上传/下载目录
├── worker.py            # 后台任务队列 + 文件监控 Handler
│                        #   BackgroundWorker (TOCTOU 修复版)
│                        #   SaveHandler (watchdog 事件处理)
├── utils.py             # 工具函数集（10 个独立函数）
│                        #   窗口居中、字体检测、mtime 处理...
├── ui_widgets.py        # 自定义 UI 组件
│                        #   RoundedButton (Canvas 圆角按钮)
│                        #   create_button (按钮工厂)
├── icon.ico             # 应用图标
├── GameCloudSync.spec   # PyInstaller 打包配置
├── config.json          # 用户配置文件（程序自动生成）
├── dist/                # 打包输出目录
│   └── GameCloudSync.exe
└── build/               # 构建临时文件
```

### 模块依赖关系

```
main.py
  └─ app.py
       ├─ constants.py
       ├─ webdav_client.py
       ├─ worker.py
       ├─ utils.py
       └─ ui_widgets.py
```

---

## 📖 使用指南

### 基本工作流

1. **设置云存储**：打开「全局设置」，配置 OneDrive 云存档根目录和/或 WebDAV 服务器信息
2. **添加游戏**：点击「添加游戏」，填写游戏名称、存档目录
3. **开始监控**：点击「启动监控」，程序开始监听文件变化
4. **玩游戏**：正常游戏，存档变化会自动备份到云端
5. **恢复存档**：需要时点击「恢复存档」从云端下载最新备份

### 添加游戏

点击「➕ 添加游戏」按钮，在弹出的对话框中填写：

| 字段 | 说明 | 示例 |
|------|------|------|
| 游戏名称 | 自定义名称 | `星露谷物语` |
| 存档目录 | 选择游戏的本地存档文件夹 | `C:\Users\xxx\AppData\Roaming\StardewValley\Saves` |
| 备份方式 | OneDrive / WebDAV（可多选） | ☑ OneDrive  ☑ WebDAV |
| 最大备份数 | 该游戏保留的历史备份数量 | `10`（默认） |

> 💡 **存档目录支持自动替换用户名**：粘贴的路径中包含 `C:\Users\旧用户名\` 会自动替换为当前用户名。

### 监控与备份

- **启动监控**后，程序通过 watchdog 监听所有已添加游戏存档目录的文件变更
- 文件发生 **修改** 或 **创建** 时，在满足以下条件后自动备份：
  - 距上次备份已超过 **10 秒**（冷却时间）
  - 存档目录的最新 mtime 确实有变化（防止重复备份）
- 备份时自动重试最多 3 次，处理文件被占用的瞬时冲突
- **暂停监控**：可对单个游戏暂停自动备份（右键菜单 → 暂停/恢复监控）

### 恢复存档

点击「📥 恢复存档」按钮，程序会：

1. 列出该游戏在 OneDrive 和 WebDAV 上的所有备份
2. 自动选择**最新备份**（比较所有源的时间）
3. 弹窗确认后下载到本地存档目录
4. 恢复前备份当前存档为 `_restore_backup`，支持撤销

也可以手动指定下载来源（OneDrive 或 WebDAV）。

### 历史版本管理

右键点击游戏 →「📜 历史备份管理」：

- **On

**OneDrive 历史**：树形显示所有备份文件夹（含时间、大小、文件数）
- **WebDAV 历史**：WebDAV 上的备份列表
- 操作：双击下载、右键删除、批量选择下载/删除

### 全局设置

点击「⚙️ 全局设置」，可配置：

| 设置项 | 说明 |
|--------|------|
| OneDrive 云存档根目录 | 本地 OneDrive 文件夹中用于存储云存档的目录 |
| WebDAV 服务器地址 | 如 `https://dav.example.com/remote.php/dav/files/user/存档` |
| WebDAV 用户名 / 密码 | 登录凭据，支持测试连接按钮 |
| 默认最大备份数 | 新添加游戏的默认备份保留数量（默认 10） |
| 默认下载来源 | 恢复存档时的默认来源：询问 / OneDrive / WebDAV |
| 最小化到系统托盘 | 关闭窗口时隐藏到托盘而非退出 |
| 启动时自动同步 | 软件启动后自动对所有游戏执行一次同步 |
| 自动保存游戏配置到云端 | 增删改游戏时自动备份配置；启动时检测云端更新并提示合并 |

### 云端配置同步

#### 自动备份

勾选「自动保存游戏配置到云端」后，每次：
- 添加游戏
- 删除游戏
- 编辑游戏
- 批量导入游戏配置

都会自动将当前游戏列表配置 `config_时间戳.json` 上传到 OneDrive 和 WebDAV 的 `云存档配置备份/` 目录，保留最近 **10 条**。

#### 启动时检测

程序启动时（若开启该选项），后台自动：
1. 扫描 OneDrive + WebDAV 上的 `云存档配置备份/` 目录
2. 找到最新的配置备份文件
3. 与本地游戏列表对比
4. 若云端有本地不存在的新游戏 → 弹出合并对话框
5. 用户确认后 **仅新增**云端的游戏，**不修改**本地已有游戏配置

#### 手动恢复

点击主界面「☁ 从云端恢复配置」按钮：
1. 列出所有云端配置备份（含时间、来源、游戏数量）
2. 选择一个备份后显示与本地配置的对比
3. 绿色标记将新增的游戏，灰色标记已存在的游戏
4. 若云端无新游戏则提示无需下载

### 批量导入/导出配置

- **📤 导出配置**：将游戏列表导出为 `.json` 文件，可选择导出单个或全部
- **📥 批量导入配置**：从 `.json` 文件批量导入游戏，**仅添加新游戏**（名称不重复的），不影响已有配置

### 系统托盘

- 关闭窗口 → 最小化到系统托盘
- 托盘图标颜色：🟢 绿色 = 监控运行中 / ⚫ 灰色 = 未监控
- 右键托盘图标可：显示主面板、启动/停止监控、完全退出

---

## 📦 打包为 EXE

```bash
pip install pyinstaller
pyinstaller GameCloudSync.spec
```

输出：`dist/GameCloudSync.exe`（约 18-19 MB）

`.spec` 中已配置：
- `hiddenimports`: `pystray._win32`, `pystray._util`（托盘依赖）
- `icon`: 使用 `icon.ico`
- `console`: `False`（无控制台窗口）

---

## 🏗️ 技术架构

### 线程模型

```
┌─────────────────────────────────────┐
│         主线程 (tkinter GUI)          │
│  - 所有 UI 更新通过 root.after()     │
│  - log() 消息通过 after() 回调       │
│  - TOCTOU 修复: try_submit() 原子   │
└──────────────┬──────────────────────┘
               │
    ┌──────────┴──────────┐
    │                     │
    ▼                     ▼
┌──────────────┐   ┌──────────────────┐
│ 后台工作线程  │   │   watchdog 线程   │
│ (单线程队列)  │   │ (Observer 内部)   │
│              │   │                  │
│ - I/O 任务    │   │ - 文件变更事件    │
│ - 备份/恢复   │   │ - 自动触发备份    │
│ - 逐任务执行   │   │ - COOLDOWN 控制  │
└──────────────┘   └──────────────────┘
         │
         ├── WebDAV 并行线程池 (4 workers)
         │   - upload_dir: 并行 PUT
         │   - download_dir: 并行 GET
         │
         └── 托盘线程 (pystray)
             - 独立消息循环
             - 通过 root.after() 操作 GUI
```

### 数据结构

```python
# config.json 结构
{
    "cloud_root": "D:/OneDrive/云存档",        # OneDrive 根目录
    "webdav_url": "https://...",              # WebDAV 服务器地址
    "webdav_username": "",
    "webdav_password": "",
    "default_max_backups": 10,                # 默认最大备份数
    "default_download_source": "ask",          # 默认下载来源
    "background_mode": true,                  # 最小化到托盘
    "auto_sync_on_startup": false,            # 启动时自动同步
    "auto_backup_config_to_cloud": false,     # 自动备份配置到云端
    "games": [
        {
            "name": "星露谷物语",
            "save": "C:\\Users\\...\\Saves",
            "paused": false,
            "backup_methods": ["onedrive", "webdav"],
            "max_backups": 10
        }
    ]
}
```

### WebDAV 通信

完全基于 Python 标准库，无第三方依赖：

- `PROPFIND` — 列出远程目录
- `MKCOL` — 创建目录（递归）
- `PUT` — 上传文件
- `GET` — 下载文件
- `DELETE` — 删除远程文件/目录

URL 编码：对中文等非 ASCII 字符逐段 URL-encode，确保兼容性。
并行传输：`ThreadPoolExecutor` 多线程并发上传/下载（`WEBDAV_WORKERS=4`）。

---

## 🔒 线程安全设计

项目拆分时重点修复了以下 3 个线程安全问题：

### 1. `games_config` 全局加锁

```python
# ❌ 原代码
game = next((g for g in self.games_config if g["name"] == name), None)

# ✅ 修复后
def _get_game(self, name):
    with self._config_lock:
        return next((g for g in self.games_config if g["name"] == name), None)
```

**后果**：原代码在后台线程遍历 `games_config` 时，主线程可能同时修改列表，导致迭代器异常或崩溃。

### 2. TOCTOU 竞态条件修复

```python
# ❌ 原问题：检查 busy 和设置 busy 之间有空窗期，两个线程可能同时通过检查
# if self._busy:
#     return
# self._busy = True

# ✅ 修复后：try_submit() 在锁内原子性地检查并预留
def try_submit(self, fn, on_done=None):
    with self._lock:
        if self._busy:
            return False
        self._busy = True  # 提前预约
    self._queue.put(wrapped)
    return True
```

**后果**：原代码在高频存档变化时可能提交重复备份任务，造成云端冗余版本。

### 3. 提取重复的 game 查找模式

将 `next((g for g in self.games_config ...), None)` 提取为线程安全的 `_get_game(name)` / `_get_game_names()` / `_get_games_snapshot()` 方法，消除 20+ 处重复代码。

---

## ❓ 常见问题

### Q: 为什么监控没有自动备份？

1. 检查是否点击了「启动监控」
2. 检查游戏是否被暂停（列表中「状态」列显示「已暂停」）
3. 存档目录路径是否正确（可能用户名不同，程序会自动修复常见路径）
4. 查看日志面板是否有错误信息

### Q: WebDAV 连接失败？

1. 在全局设置中点击「测试连接」验证
2. 确认 URL 格式正确，如 `https://server.com/remote.php/dav/files/user/存档`
3. 确认用户名密码正确
4. 某些服务器可能需要先在网页端创建目标目录

### Q: 重复打开软件提示已运行？

这是单实例保护机制。第二次启动会自动唤醒已有的隐藏窗口。

### Q: 如何更换 OneDrive 目录？

在全局设置中修改 OneDrive 云存档根目录，保存后立即生效。

### Q: 配置文件在哪里？

`config.json` 位于程序所在目录（与 `main.py` 或 `GameCloudSync.exe` 同级）。

---

## 📝 License

MIT License

---

<p align="center">
  <sub>Made with ❤️ for gamers who care about their save files</sub>
</p>
