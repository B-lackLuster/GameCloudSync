"""
全自动多游戏云存档管家 - 常量配置
"""

import os
import sys

# --- 应用目录检测 ---
# 始终以可执行文件/脚本所在目录为准，防止开机自启时工作目录不对导致找不到配置文件
if getattr(sys, 'frozen', False):
    _APP_DIR = os.path.dirname(sys.executable)
else:
    _APP_DIR = os.path.dirname(os.path.abspath(__file__))
os.chdir(_APP_DIR)  # 切换工作目录，确保所有相对路径正确

# --- 核心路径 / 配置 ---
CONFIG_FILE = os.path.join(_APP_DIR, "config.json")
COOLDOWN_SECONDS = 10
DEFAULT_MAX_BACKUPS = 10
WAKEUP_FILE = os.path.join(_APP_DIR, "wakeup.tmp")

# --- WebDAV ---
# WebDAV 多文件上传使用的线程数
WEBDAV_WORKERS = 4
# 云端游戏配置备份目录名（OneDrive 与 WebDAV 共用）
CLOUD_CONFIG_BACKUP_DIR = "云存档配置备份"
# 云端配置备份保留份数
MAX_CONFIG_BACKUPS = 10

# --- 时间 / 阈值（秒）---
# 本地 mtime 与云端 mtime 相差超过该值才判定为“不同步”
SYNC_THRESHOLD_SECONDS = 3
# WebDAV 配置列表请求的硬上限，避免拉取过多拖慢界面
WEBDAV_CONFIG_LIST_LIMIT = 10

# --- 备份方式 / 下载来源 字符串常量 ---
# 历史上以小写字面量 "onedrive"/"webdav"/"ask" 写入 config.json 与云端，
# 这里以常量集中声明，避免散落各处的拼写不一致。
METHOD_ONEDRIVE = "onedrive"
METHOD_WEBDAV = "webdav"
SOURCE_ASK = "ask"

# --- 颜色（统一颜色规范，UI 各处引用，避免硬编码漂移）---
COLOR_BG = "#F5F6FA"          # 窗口/对话框背景
COLOR_CARD = "#FFFFFF"        # 卡片背景
COLOR_PRIMARY = "#4A6CF7"     # 主色（选中、链接）
COLOR_SUCCESS = "#10B981"     # 成功 / 已同步 / 启动监控
COLOR_SUCCESS_HOVER = "#059669"
COLOR_DANGER = "#EF4444"      # 危险 / 未同步 / 停止监控
COLOR_DANGER_HOVER = "#DC2626"
COLOR_TEXT = "#1E293B"        # 主文本
COLOR_TEXT_SEC = "#64748B"    # 次要文本
COLOR_BORDER = "#E2E8F0"      # 边框 / 次级按钮底
COLOR_BORDER_HOVER = "#CBD5E1"
COLOR_LOG_BG = "#1E293B"      # 日志面板背景
COLOR_LOG_FG = "#E2E8F0"      # 日志文本
COLOR_MUTED = "#94A3B8"       # 暂停 / 未配置 灰色
COLOR_CLOUD_NEW_BG = "#EBF0FF"
COLOR_LOCAL_NEW_BG = "#FEE2E2"
COLOR_PAUSED_BG = "#F1F5F9"
# 托盘图标颜色
COLOR_TRAY_ACTIVE = "#32CD32"
COLOR_TRAY_INACTIVE = "#A9A9A9"

# --- 注册表自启动 ---
AUTOSTART_REG_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
AUTOSTART_REG_NAME = "GameCloudSync"

# --- 单实例互斥 ---
MUTEX_NAME = "Global\\GameCloudSync_Unique_Mutex"
