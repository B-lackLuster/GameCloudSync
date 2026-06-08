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

# --- 常量 ---
CONFIG_FILE = os.path.join(_APP_DIR, "config.json")
COOLDOWN_SECONDS = 10
DEFAULT_MAX_BACKUPS = 10
WAKEUP_FILE = os.path.join(_APP_DIR, "wakeup.tmp")
# WebDAV 多文件上传使用的线程数
WEBDAV_WORKERS = 4
