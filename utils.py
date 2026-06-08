"""
工具函数：文件操作、字体检测、窗口居中、mtime 处理等
"""

import os
import re
import shutil
import time
import tkinter.font as tkfont
from datetime import datetime

from PIL import Image, ImageDraw, ImageTk


def center_window(win):
    """将窗口居中显示，无闪烁"""
    win.withdraw()
    win.update_idletasks()
    w = win.winfo_width()
    h = win.winfo_height()
    sw = win.winfo_screenwidth()
    sh = win.winfo_screenheight()
    x = (sw - w) // 2
    y = (sh - h) // 2
    win.geometry(f"+{x}+{y}")
    win.deiconify()


def detect_font():
    """检测可用的中文字体，优先思源黑体"""
    available = set(tkfont.families())
    for name in ("Source Han Sans SC", "Noto Sans SC", "Noto Sans CJK SC",
                  "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "simsun"):
        if name in available:
            return name
    return "Microsoft YaHei UI"


def rmtree_safe(path):
    """安全删除目录，自动处理 Windows 只读文件属性"""
    if not os.path.exists(path):
        return

    def _on_error(func, fp, _exc):
        os.chmod(fp, 0o777)
        try:
            func(fp)
        except Exception:
            pass

    try:
        shutil.rmtree(path, onexc=_on_error)
    except TypeError:
        shutil.rmtree(path, onerror=_on_error)


def make_checkbox_images():
    """生成复选框图标（16x16），返回 (checked, unchecked) PhotoImage"""
    size = 16
    img_u = Image.new('RGBA', (size, size), (255, 255, 255, 0))
    draw_u = ImageDraw.Draw(img_u)
    draw_u.rounded_rectangle((1, 1, size-2, size-2), radius=3, outline='#94A3B8', width=1)
    img_c = Image.new('RGBA', (size, size), (255, 255, 255, 0))
    draw_c = ImageDraw.Draw(img_c)
    draw_c.rounded_rectangle((1, 1, size-2, size-2), radius=3, fill='#4A6CF7', outline='#4A6CF7', width=1)
    draw_c.line((3, 8, 6, 12), fill='white', width=2)
    draw_c.line((6, 12, 12, 4), fill='white', width=2)
    return ImageTk.PhotoImage(img_c), ImageTk.PhotoImage(img_u)


def fix_save_path(save_dir):
    """自动替换存档路径中的用户名为当前用户名"""
    current_user = os.environ.get('USERNAME', '')
    if not current_user:
        return save_dir
    # 匹配 C:\Users\xxx\ 模式，替换为当前用户
    pattern = re.compile(r'C:\\Users\\([^\\]+)\\', re.IGNORECASE)
    return pattern.sub(f'C:\\\\Users\\\\{current_user}\\\\', save_dir)


def get_latest_mtime(folder_path):
    """获取目录中最新的文件修改时间（递归）"""
    latest_time = 0
    if not os.path.exists(folder_path):
        return 0
    for walk_root, _, files in os.walk(folder_path):
        for file in files:
            try:
                mtime = os.path.getmtime(os.path.join(walk_root, file))
                if mtime > latest_time:
                    latest_time = mtime
            except Exception:
                pass
    if latest_time == 0:
        try:
            latest_time = os.path.getmtime(folder_path)
        except Exception:
            pass
    return latest_time


def get_folder_size(folder):
    """计算目录总大小（字节）"""
    total = 0
    for dirpath, dirnames, filenames in os.walk(folder):
        for f in filenames:
            fp = os.path.join(dirpath, f)
            try:
                total += os.path.getsize(fp)
            except Exception:
                pass
    return total


def apply_backup_mtime(backup_name, target_dir):
    """从 WebDAV 备份目录名提取 mtime 并回写到下载后的所有文件，
    避免下载时间干扰跨设备同步判断"""
    try:
        # 目录名格式: save_20260512_143000_mtime1715500200
        # 旧格式: save_20260512_143000（无 mtime，跳过）
        parts = backup_name.split("_mtime")
        if len(parts) < 2:
            return
        mtime = int(parts[-1])
        for walk_root, _, files in os.walk(target_dir):
            for fname in files:
                fp = os.path.join(walk_root, fname)
                try:
                    os.utime(fp, (mtime, mtime))
                except Exception:
                    pass
    except Exception:
        pass


def parse_backup_name(name):
    """解析备份目录名，返回 (时间戳字符串, mtime 或 0)

    支持格式:
    - 新格式: save_20260512_143000_mtime1715500200 → ('20260512_143000', 1715500200)
    - 旧格式: save_20260512_143000 → ('20260512_143000', 0)
    """
    try:
        ts_str = name[5:]  # 去掉 "save_" 前缀
        if "_mtime" in ts_str:
            parts = ts_str.split("_mtime")
            return parts[0], int(parts[1])
        else:
            return ts_str, 0
    except Exception:
        return "00000000_000000", 0
