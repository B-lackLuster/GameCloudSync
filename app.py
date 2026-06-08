"""
全自动多游戏云存档管家 - 主应用类 + 所有对话框
"""

import os
import json
import shutil
import time
import threading
import sys
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
from datetime import datetime

import pystray
from pystray import MenuItem as item
from PIL import Image, ImageDraw, ImageTk
import winreg
from watchdog.observers import Observer

from constants import (
    CONFIG_FILE, COOLDOWN_SECONDS, DEFAULT_MAX_BACKUPS,
    WAKEUP_FILE, WEBDAV_WORKERS
)
from webdav_client import WebDAVClient
from worker import BackgroundWorker, SaveHandler
from ui_widgets import RoundedButton, create_button
from utils import (
    center_window, detect_font, rmtree_safe, make_checkbox_images,
    fix_save_path, get_latest_mtime, get_folder_size, apply_backup_mtime,
    parse_backup_name,
)


# --- GUI 与 主程序逻辑 ---
class CloudBackupApp:
    def __init__(self, root):
        self.root = root
        self.root.title("全自动多游戏云存档管家")
        self.root.geometry("1050x580")

        # ---- 线程安全锁 ----
        # 保护 games_config 及 cloud_root/webdav_*/autostart/default_max_backups 等配置字段
        self._config_lock = threading.Lock()

        config = self.load_config()
        self.cloud_root = config.get("cloud_root", "")
        self.autostart = config.get("autostart", False)
        self.webdav_url = config.get("webdav_url", "")
        self.webdav_username = config.get("webdav_username", "")
        self.webdav_password = config.get("webdav_password", "")
        self.default_max_backups = config.get("default_max_backups", DEFAULT_MAX_BACKUPS)
        self.default_download_source = config.get("default_download_source", "ask")
        self.background_mode = config.get("background_mode", True)
        self.auto_sync_on_startup = config.get("auto_sync_on_startup", False)
        self.auto_backup_config_to_cloud = config.get("auto_backup_config_to_cloud", False)
        self.games_config = config.get("games", [])

        # 向后兼容
        if isinstance(config, list):
            self.cloud_root = ""
            self.autostart = False
            self.webdav_url = ""
            self.webdav_username = ""
            self.webdav_password = ""
            self.default_max_backups = DEFAULT_MAX_BACKUPS
            self.default_download_source = "ask"
            self.background_mode = True
            self.auto_sync_on_startup = False
            self.auto_backup_config_to_cloud = False
            self.games_config = config

        # 确保每个游戏都有 backup_methods 和 max_backups 字段
        for g in self.games_config:
            if "backup_methods" not in g:
                g["backup_methods"] = ["onedrive"]
            if "max_backups" not in g:
                g["max_backups"] = self.default_max_backups

        self.observers = []
        self.is_monitoring = False
        # WebDAV 客户端每次操作时新建，避免全局 cookie 过期导致 404
        self._selected_game_name = None  # 右键点击时暂存
        self._btn_upload = None       # 在 setup_ui 中赋值
        self._btn_download = None

        # 后台工作线程，避免 I/O 阻塞 GUI
        self.worker = BackgroundWorker(root)
        self.worker.start()

        # 检测可用中文字体，优先思源黑体
        self._font_family = detect_font()
        RoundedButton.font_family = self._font_family

        # 生成复选框图标
        self._cb_checked, self._cb_unchecked = make_checkbox_images()

        self.setup_ui()
        self.setup_tray()
        self.sync_autostart_status()
        if self.background_mode:
            self.root.protocol('WM_DELETE_WINDOW', self.hide_window)
        else:
            self.root.protocol('WM_DELETE_WINDOW', self.quit_app)
        center_window(self.root)
        self.root.after(1000, self.auto_start)
        self.check_wakeup_signal()

    # ================= 线程安全的游戏配置查询 =================
    def _get_game(self, name):
        """线程安全地根据名称查找游戏配置，返回 dict 或 None"""
        with self._config_lock:
            return next((g for g in self.games_config if g["name"] == name), None)

    def _get_game_names(self):
        """线程安全地获取所有游戏名称列表"""
        with self._config_lock:
            return [g["name"] for g in self.games_config]

    def _get_games_snapshot(self):
        """线程安全地获取 games_config 的快照副本（用于后台线程遍历）"""
        with self._config_lock:
            return list(self.games_config)

    # ================= 配置读写 =================
    def load_config(self):
        if os.path.exists(CONFIG_FILE):
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, list):
                return {"cloud_root": "", "autostart": False,
                        "webdav_url": "", "webdav_username": "", "webdav_password": "",
                        "games": data}
            # 向后兼容: 确保有 webdav 字段和新配置项
            for key in ("webdav_url", "webdav_username", "webdav_password"):
                if key not in data:
                    data[key] = ""
            if "background_mode" not in data:
                data["background_mode"] = True
            if "auto_sync_on_startup" not in data:
                data["auto_sync_on_startup"] = False
            if "auto_backup_config_to_cloud" not in data:
                data["auto_backup_config_to_cloud"] = False
            return data
        return {"cloud_root": "", "autostart": False,
                "webdav_url": "", "webdav_username": "", "webdav_password": "",
                "background_mode": True, "auto_sync_on_startup": False,
                "auto_backup_config_to_cloud": False,
                "games": []}

    def save_config(self):
        with self._config_lock:
            config = {
                "cloud_root": self.cloud_root,
                "autostart": self.autostart,
                "webdav_url": self.webdav_url,
                "webdav_username": self.webdav_username,
                "webdav_password": self.webdav_password,
                "default_max_backups": self.default_max_backups,
                "default_download_source": self.default_download_source,
                "background_mode": self.background_mode,
                "auto_sync_on_startup": self.auto_sync_on_startup,
                "auto_backup_config_to_cloud": self.auto_backup_config_to_cloud,
                "games": list(self.games_config)
            }
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(config, f, indent=4, ensure_ascii=False)

    def backup_config_to_cloud(self):
        """自动备份游戏配置到云端（OneDrive 和 WebDAV），保留最近 10 条"""
        with self._config_lock:
            if not self.games_config:
                return
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            backup_name = f"config_{timestamp}.json"
            config_data = [{
                "name": g["name"], "save": g["save"],
                "paused": g.get("paused", False),
                "backup_methods": g.get("backup_methods", ["onedrive"]),
                "max_backups": g.get("max_backups", self.default_max_backups)
            } for g in self.games_config]
            cloud_root = self.cloud_root
            webdav_url = self.webdav_url

        # OneDrive 备份
        if cloud_root:
            cf_dir = os.path.join(cloud_root, "云存档配置备份")
            try:
                os.makedirs(cf_dir, exist_ok=True)
                fp = os.path.join(cf_dir, backup_name)
                with open(fp, "w", encoding="utf-8") as f:
                    json.dump(config_data, f, indent=4, ensure_ascii=False)
                # 清理旧备份
                existing = [os.path.join(cf_dir, fn) for fn in os.listdir(cf_dir)
                           if fn.startswith("config_") and fn.endswith(".json")]
                existing.sort(key=os.path.getmtime)
                for old in existing[:-10]:
                    try:
                        os.remove(old)
                    except Exception:
                        pass
            except Exception:
                pass

        # WebDAV 备份
        if webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    import tempfile
                    tmpdir = tempfile.mkdtemp()
                    try:
                        tmp_fp = os.path.join(tmpdir, backup_name)
                        with open(tmp_fp, "w", encoding="utf-8") as f:
                            json.dump(config_data, f, indent=4, ensure_ascii=False)
                        remote_dir = "云存档配置备份"
                        wc.mkcol(remote_dir)
                        if wc.put_file(tmp_fp, f"{remote_dir}/{backup_name}"):
                            # 清理旧备份
                            items = wc.propfind(remote_dir)
                            cf_files = [(n, h) for h, n in items if n.startswith("config_")]
                            cf_files.sort(key=lambda x: x[0], reverse=True)
                            for name, _ in cf_files[10:]:
                                try:
                                    wc.delete_path(f"{remote_dir}/{name}")
                                except Exception:
                                    pass
                    finally:
                        rmtree_safe(tmpdir)
                except Exception:
                    pass

    # ================= WebDAV 客户端 =================
    def get_webdav_client(self):
        """创建 WebDAV 客户端（每次新建，避免复用全局 cookie 等状态导致 404）；未配置则返回 None"""
        if not self.webdav_url:
            return None
        return WebDAVClient(
            self.webdav_url, self.webdav_username, self.webdav_password,
            log_cb=self.log
        )

    # ================= 唤醒机制 =================
    def check_wakeup_signal(self):
        if os.path.exists(WAKEUP_FILE):
            try:
                os.remove(WAKEUP_FILE)
                self.show_window()
            except Exception:
                pass
        self.root.after(500, self.check_wakeup_signal)

    # ================= 系统托盘 =================
    def create_icon_image(self, color_name):
        image = Image.new('RGBA', (64, 64), (255, 255, 255, 0))
        draw = ImageDraw.Draw(image)
        draw.ellipse((8, 8, 56, 56), fill=color_name, outline="black")
        draw.ellipse((20, 15, 44, 30), fill=(255, 255, 255, 80))
        return image

    def setup_tray(self):
        self.icon_active = self.create_icon_image("#32CD32")
        self.icon_inactive = self.create_icon_image("#A9A9A9")
        menu = pystray.Menu(
            item('显示主面板', self.show_window, default=True),
            item('启动监控', self.start_monitoring),
            item('停止监控', self.stop_monitoring),
            pystray.Menu.SEPARATOR,
            item('完全退出', self.quit_app)
        )
        self.tray_icon = pystray.Icon("GameCloudSync", self.icon_inactive, "云存档管家 (未监控)", menu)
        threading.Thread(target=self.tray_icon.run, daemon=True).start()

    def hide_window(self):
        self.root.withdraw()
        self.log("面板已隐藏，程序在托盘继续运行...")

    def show_window(self, icon=None, item=None):
        self.root.after(0, self.root.deiconify)
        self.root.after(0, self.root.lift)
        self.root.after(0, self.root.focus_force)

    def quit_app(self, icon=None, item=None):
        self.stop_monitoring()
        self.tray_icon.stop()
        self.root.quit()

    # ================= 工具函数 =================
    def browse_dir(self, entry_widget):
        folder = filedialog.askdirectory()
        if folder:
            entry_widget.delete(0, tk.END)
            entry_widget.insert(0, folder.replace("/", "\\"))

    def log(self, message):
        def _write():
            self.log_text.config(state="normal")
            self.log_text.insert(tk.END, message + "\n")
            self.log_text.see(tk.END)
            self.log_text.config(state="disabled")
        self.root.after(0, _write)

    def _disable_action_buttons(self, status_text="处理中…"):
        """禁用上传/下载按钮，显示状态文字"""
        def _do():
            if self._btn_upload:
                self._btn_upload.configure(state="disabled", text=f"⏳ {status_text}")
            if self._btn_download:
                self._btn_download.configure(state="disabled")
        self.root.after(0, _do)

    def _enable_action_buttons(self):
        """恢复上传/下载按钮"""
        def _do():
            if self._btn_upload:
                self._btn_upload.configure(state="normal", text="⬆ 手动上传存档")
            if self._btn_download:
                self._btn_download.configure(state="normal", text="⬇ 覆盖下载存档")
        self.root.after(0, _do)

    # ================= 自启动管理 =================
    def sync_autostart_status(self):
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                                 r"Software\Microsoft\Windows\CurrentVersion\Run",
                                 0, winreg.KEY_READ)
            try:
                winreg.QueryValueEx(key, "GameCloudSync")
                self.autostart = True
            except FileNotFoundError:
                self.autostart = False
            winreg.CloseKey(key)
        except Exception:
            self.autostart = False

    def set_autostart(self, enable):
        try:
            if getattr(sys, 'frozen', False):
                target = sys.executable
            else:
                python_dir = os.path.dirname(sys.executable)
                pythonw_path = os.path.join(python_dir, "pythonw.exe")
                if not os.path.exists(pythonw_path):
                    pythonw_path = sys.executable
                script_path = os.path.abspath(__file__)
                target = f'"{pythonw_path}" "{script_path}"'

            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                                 r"Software\Microsoft\Windows\CurrentVersion\Run",
                                 0, winreg.KEY_SET_VALUE)
            if enable:
                winreg.SetValueEx(key, "GameCloudSync", 0, winreg.REG_SZ, target)
                self.log("已添加开机自启动")
            else:
                try:
                    winreg.DeleteValue(key, "GameCloudSync")
                    self.log("已取消开机自启动")
                except FileNotFoundError:
                    pass
            winreg.CloseKey(key)
            self.autostart = enable
        except Exception as e:
            messagebox.showerror("自启动设置失败", f"无法修改注册表：{e}")

    # ================= UI 样式 =================
    def _setup_styles(self):
        """配置现代化 ttk 主题样式"""
        style = ttk.Style()
        # 使用 vista 主题确保 Checkbutton 显示 ✓ 而非 ❌
        available = style.theme_names()
        if 'vista' in available:
            style.theme_use('vista')
        elif 'winnative' in available:
            style.theme_use('winnative')
        elif 'clam' in available:
            style.theme_use('clam')

        # 颜色常量
        BG = "#F5F6FA"
        CARD = "#FFFFFF"
        PRIMARY = "#4A6CF7"
        SUCCESS = "#10B981"
        DANGER = "#EF4444"
        TEXT = "#1E293B"
        TEXT_SEC = "#64748B"
        BORDER = "#E2E8F0"

        self.root.configure(bg=BG)

        # 通用框架样式
        style.configure('TFrame', background=BG)
        style.configure('Card.TFrame', background=CARD, relief='solid', borderwidth=1)
        style.configure('TLabelframe', background=BG, bordercolor=BORDER)
        style.configure('TLabelframe.Label', background=BG, foreground=TEXT, font=(self._font_family, 9, 'bold'))

        # Label 样式
        style.configure('TLabel', background=BG, foreground=TEXT, font=(self._font_family, 9))
        style.configure('Hint.TLabel', foreground=TEXT_SEC, font=('Microsoft YaHei UI', 8))
        style.configure('Status.TLabel', font=(self._font_family, 9))
        style.configure('Dialog.TLabel', font=('Microsoft YaHei UI', 10))

        # Entry 样式
        style.configure('TEntry', fieldbackground=CARD, bordercolor=BORDER, padding=4)

        # 按钮样式
        style.configure('TButton', font=(self._font_family, 9), padding=(12, 5), borderwidth=0)
        style.map('TButton',
                  background=[('active', PRIMARY), ('!active', '#E2E8F0')],
                  foreground=[('active', '#FFFFFF')])

        # 主要操作按钮（添加/保存）
        style.configure('Success.TButton', background=SUCCESS, foreground='#FFFFFF',
                        font=(self._font_family, 9, 'bold'), borderwidth=0, padding=(12, 6))
        style.map('Success.TButton',
                  background=[('active', '#059669'), ('!active', SUCCESS)],
                  foreground=[('active', '#FFFFFF')])

        # 启动/停止按钮
        style.configure('Start.TButton', background=SUCCESS, foreground='#FFFFFF',
                        font=(self._font_family, 9, 'bold'), padding=(12, 6), borderwidth=0)
        style.map('Start.TButton',
                  background=[('active', '#059669'), ('!active', SUCCESS)],
                  foreground=[('active', '#FFFFFF')])

        # 次级按钮
        style.configure('Secondary.TButton', font=(self._font_family, 9), padding=(12, 5))

        # 危险操作按钮
        style.configure('Danger.TButton', background=DANGER, foreground='#FFFFFF',
                        font=(self._font_family, 9, 'bold'), borderwidth=0, padding=(12, 6))
        style.map('Danger.TButton',
                  background=[('active', '#DC2626'), ('!active', DANGER)],
                  foreground=[('active', '#FFFFFF')])

        # Checkbutton 样式
        style.configure('TCheckbutton', background=BG, font=(self._font_family, 9))

        # Treeview 样式
        style.configure('Treeview', background=CARD, fieldbackground=CARD,
                        foreground=TEXT, bordercolor=BORDER, rowheight=28)
        style.configure('Treeview.Heading', font=(self._font_family, 9, 'bold'),
                        background=BG, foreground=TEXT, bordercolor=BORDER, padding=(8, 4))
        style.map('Treeview', background=[('selected', PRIMARY)], foreground=[('selected', '#FFFFFF')])

    # ================= UI 构建 =================
    def setup_ui(self):
        self._setup_styles()

        # ---------- 顶部按钮栏 ----------
        frame_top = ttk.Frame(self.root, padding=(15, 12))
        frame_top.pack(fill="x")

        create_button(frame_top, text="＋ 添加游戏", style='Success.TButton',
                   command=self.open_add_game_dialog, width=110,
                   pack_side="left", pack_padx=(0, 8))

        create_button(frame_top, text="⚙ 全局设置", style='Secondary.TButton',
                   command=self.open_settings, width=100,
                   pack_side="left", pack_padx=(0, 20))

        # 状态摘要
        self.cloud_status_label = ttk.Label(frame_top, text="", style='Status.TLabel')
        self.cloud_status_label.pack(side="left", padx=20)
        self.update_cloud_status_label()

        # ---------- 游戏列表 ----------
        frame_list = ttk.LabelFrame(self.root, text="受监控的游戏列表（右键可唤出高级菜单）", padding=(15, 12))
        frame_list.pack(fill="both", expand=True, padx=15, pady=(5, 8))

        columns = ("name", "save", "backup", "status")
        self.tree = ttk.Treeview(frame_list, columns=columns, show="tree headings", height=7, selectmode="extended")
        self.tree.heading("#0", text="")
        self.tree.heading("name", text="游戏名称")
        self.tree.heading("save", text="本地存档路径")
        self.tree.heading("backup", text="备份方式 / 云端路径")
        self.tree.heading("status", text="同步状态")

        self.tree.column("#0", width=30, stretch=False, anchor="center")
        self.tree.column("name", width=100)
        self.tree.column("save", width=220)
        self.tree.column("backup", width=240)
        self.tree.column("status", width=150, anchor="center")
        self.tree.pack(fill="both", expand=True, side="left")

        # 复选框列：点击切换选中, 选择变化时同步图标
        self.tree.bind("<Button-1>", self._on_tree_checkbox_click)
        self.tree.bind("<<TreeviewSelect>>", lambda e: self._sync_checkbox_images(self.tree))

        # 标记样式
        self.tree.tag_configure("synced", foreground="#10B981")
        self.tree.tag_configure("cloud_new", foreground="#4A6CF7", background="#EBF0FF")
        self.tree.tag_configure("local_new", foreground="#EF4444", background="#FEE2E2")
        self.tree.tag_configure("none", foreground="#94A3B8")
        self.tree.tag_configure("paused", foreground="#94A3B8", background="#F1F5F9")

        # ---------- 右键菜单 ----------
        self.backup_onedrive_var = tk.BooleanVar()
        self.backup_webdav_var = tk.BooleanVar()

        # 单选右键菜单（完整）
        self.context_menu = tk.Menu(self.root, tearoff=0)
        self.context_menu.add_command(label="⬆ 手动上传", command=self.force_backup)
        self.context_menu.add_command(label="⬇ 覆盖下载", command=self.restore_game)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="📂 打开本地存档位置", command=self.open_local_folder)
        self.context_menu.add_command(label="☁ 打开云端存档位置", command=self.open_cloud_folder)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="⏸ 暂停同步", command=self.toggle_pause)
        self.context_menu.add_separator()
        self.context_menu.add_checkbutton(label="备份到 OneDrive",
                                          variable=self.backup_onedrive_var,
                                          command=lambda: self._toggle_backup_method("onedrive"))
        self.context_menu.add_checkbutton(label="备份到 WebDAV",
                                          variable=self.backup_webdav_var,
                                          command=lambda: self._toggle_backup_method("webdav"))
        self.context_menu.add_separator()
        self.context_menu.add_command(label="📜 查看历史备份", command=self.show_history_backup)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="✏ 修改游戏配置", command=self.edit_game)
        self.context_menu.add_command(label="📤 单独导出配置", command=self.export_single_config)
        self.context_menu.add_command(label="🗑 删除", command=self.delete_game)

        # 多选右键菜单（精简）
        self.context_menu_multi = tk.Menu(self.root, tearoff=0)
        self.context_menu_multi.add_command(label="⬆ 手动上传所选游戏", command=self.force_backup)
        self.context_menu_multi.add_command(label="⬇ 覆盖下载所选游戏", command=self.restore_game)
        self.context_menu_multi.add_separator()
        self.context_menu_multi.add_command(label="⏸ 暂停所选游戏同步", command=self.toggle_pause)
        self.context_menu_multi.add_command(label="📤 导出所选游戏配置", command=self.export_single_config)
        self.context_menu_multi.add_separator()
        self.context_menu_multi.add_command(label="🗑 删除所选游戏", command=self.delete_game)

        self.tree.bind("<Button-3>", self.show_context_menu)

        # ---------- 控制按钮 ----------
        frame_ctrl = ttk.Frame(self.root, padding=(15, 8))
        frame_ctrl.pack(fill="x")

        self.btn_toggle = create_button(frame_ctrl, text="▶ 启动监控", style='Start.TButton',
                                      command=self.toggle_monitoring, width=110,
                                      pack_side="left", pack_padx=(0, 8))

        create_button(frame_ctrl, text="🔄 刷新状态", style='Secondary.TButton',
                   command=self.refresh_list, width=100,
                   pack_side="left", pack_padx=(0, 8))

        self._btn_upload = create_button(frame_ctrl, text="⬆ 手动上传存档", style='Secondary.TButton',
                   command=self.force_backup, width=120,
                   pack_side="left", pack_padx=(0, 8))

        self._btn_download = create_button(frame_ctrl, text="⬇ 覆盖下载存档", style='Secondary.TButton',
                   command=self.restore_game, width=120,
                   pack_side="left", pack_padx=(0, 8))

        create_button(frame_ctrl, text="📤 导出全部配置", style='Secondary.TButton',
                   command=self.export_config, width=120,
                   pack_side="right", pack_padx=(8, 0))
        create_button(frame_ctrl, text="☁ 从云端恢复配置", style='Secondary.TButton',
                   command=self.restore_config_from_cloud, width=140,
                   pack_side="right", pack_padx=(8, 0))
        create_button(frame_ctrl, text="📥 批量导入配置", style='Secondary.TButton',
                   command=self.import_config, width=120,
                   pack_side="right", pack_padx=(8, 0))

        # ---------- 日志 ----------
        self.log_text = tk.Text(self.root, height=6, state="disabled", bg="#1E293B", fg="#E2E8F0",
                                font=("Consolas", 9), relief="flat", borderwidth=0,
                                insertbackground="white", selectbackground="#4A6CF7")
        self.log_text.pack(fill="x", padx=15, pady=(0, 12))

        self.refresh_list()

    def update_cloud_status_label(self):
        parts = []
        if self.cloud_root:
            parts.append(f"OneDrive: {self.cloud_root}")
        else:
            parts.append("OneDrive: 未设置")
        if self.webdav_url:
            parts.append(f"WebDAV: {self.webdav_url}")
        configured = bool(self.cloud_root or self.webdav_url)
        self.cloud_status_label.config(text=" | ".join(parts),
                                       foreground="#10B981" if configured else "#EF4444")

    # ================= 添加游戏对话框（二级窗口） =================
    def open_add_game_dialog(self):
        dialog = tk.Toplevel(self.root)
        dialog.title("添加新游戏")
        dialog.geometry("520x260")
        dialog.resizable(False, False)
        dialog.grab_set()
        dialog.configure(bg="#F5F6FA")
        center_window(dialog)

        content = ttk.Frame(dialog, padding=15)
        content.pack(fill="both", expand=True)

        # 游戏名称
        ttk.Label(content, text="游戏名称：", style='Dialog.TLabel').grid(row=0, column=0, padx=(5, 10), pady=(5, 10), sticky="e")
        entry_name = ttk.Entry(content, width=35, font=(self._font_family, 10))
        entry_name.grid(row=0, column=1, padx=5, pady=(5, 10), sticky="w")

        # 本地存档
        ttk.Label(content, text="本地存档路径：", style='Dialog.TLabel').grid(row=1, column=0, padx=(5, 10), pady=5, sticky="e")
        entry_save = ttk.Entry(content, width=35, font=(self._font_family, 10))
        entry_save.grid(row=1, column=1, padx=5, pady=5, sticky="w")
        create_button(content, text="浏览...", style='Secondary.TButton',
                   command=lambda: self.browse_dir(entry_save), width=60,
                   grid_row=1, grid_col=2, grid_padx=5)

        # 备份方式
        ttk.Label(content, text="备份方式：", style='Dialog.TLabel').grid(row=2, column=0, padx=(5, 10), pady=(10, 5), sticky="e")

        frame_methods = ttk.Frame(content)
        frame_methods.grid(row=2, column=1, padx=5, pady=(10, 5), sticky="w")

        var_onedrive = tk.BooleanVar(value=bool(self.cloud_root))
        var_webdav = tk.BooleanVar(value=bool(self.webdav_url))

        cb_onedrive = ttk.Checkbutton(frame_methods, text="OneDrive", variable=var_onedrive)
        cb_onedrive.pack(side="left", padx=(0, 20))
        cb_webdav = ttk.Checkbutton(frame_methods, text="WebDAV", variable=var_webdav)
        cb_webdav.pack(side="left")

        # 提示（如果未配置）
        hint_text = ""
        if not self.cloud_root and not self.webdav_url:
            hint_text = "请先在全局设置中配置 OneDrive 或 WebDAV"
        elif not self.cloud_root:
            hint_text = "OneDrive 未配置，请在全局设置中设置"
        elif not self.webdav_url:
            hint_text = "WebDAV 未配置，请在全局设置中设置"
        if hint_text:
            ttk.Label(content, text=hint_text, style='Hint.TLabel').grid(
                row=3, column=1, padx=5, pady=(0, 5), sticky="w")

        # 按钮
        frame_btn = ttk.Frame(content)
        frame_btn.grid(row=4, column=0, columnspan=3, pady=(10, 5))

        def do_add():
            name = entry_name.get().strip()
            save_dir = entry_save.get().strip()
            if not name or not save_dir:
                messagebox.showwarning("错误", "请填写游戏名称和本地存档路径！", parent=dialog)
                return
            with self._config_lock:
                if any(g["name"] == name for g in self.games_config):
                    messagebox.showwarning("错误", f"游戏【{name}】已存在！", parent=dialog)
                    return

            methods = []
            if var_onedrive.get():
                if not self.cloud_root:
                    messagebox.showwarning("提示", "OneDrive 云盘目录未设置，请在全局设置中配置。", parent=dialog)
                    return
                methods.append("onedrive")
            if var_webdav.get():
                if not self.webdav_url:
                    messagebox.showwarning("提示", "WebDAV 未配置，请在全局设置中设置。", parent=dialog)
                    return
                methods.append("webdav")
            if not methods:
                messagebox.showwarning("错误", "请至少选择一种备份方式！", parent=dialog)
                return

            with self._config_lock:
                self.games_config.append({
                    "name": name, "save": save_dir, "paused": False,
                    "backup_methods": methods,
                    "max_backups": self.default_max_backups
                })
            self.save_config()
            if self.auto_backup_config_to_cloud:
                self.backup_config_to_cloud()
            self.refresh_list()
            if self.is_monitoring:
                self.stop_monitoring()
                self.start_monitoring()
                # 自动上传第一次存档
                if os.path.isdir(save_dir) and os.listdir(save_dir):
                    self.root.after(500, lambda: self._monitor_backup(name, save_dir))
            self.log(f"➕ 已添加游戏【{name}】，备份方式: {', '.join(methods)}")
            dialog.destroy()

        create_button(frame_btn, text="添加", style='Success.TButton',
                   command=do_add, width=100,
                   pack_side="left", pack_padx=(0, 10))
        create_button(frame_btn, text="取消", style='Secondary.TButton',
                   command=dialog.destroy, width=100,
                   pack_side="left")

        entry_name.focus_set()

    # ================= 全局设置窗口 =================
    def open_settings(self):
        settings_win = tk.Toplevel(self.root)
        settings_win.title("全局设置")
        settings_win.geometry("600x600")
        settings_win.resizable(False, False)
        settings_win.grab_set()
        settings_win.configure(bg="#F5F6FA")
        center_window(settings_win)

        content = ttk.Frame(settings_win, padding=15)
        content.pack(fill="both", expand=True)

        # ---- OneDrive 设置 ----
        frame_od = ttk.LabelFrame(content, text="OneDrive 设置", padding=(12, 10))
        frame_od.pack(fill="x", pady=(0, 8))

        row_od = ttk.Frame(frame_od)
        row_od.pack(fill="x")
        ttk.Label(row_od, text="云盘根目录:", width=12, anchor="e").pack(side="left")
        cloud_entry = ttk.Entry(row_od, width=40)
        cloud_entry.pack(side="left", padx=5)
        cloud_entry.insert(0, self.cloud_root)
        create_button(row_od, text="浏览", style='Secondary.TButton',
                   command=lambda: self.browse_dir(cloud_entry), width=60,
                   pack_side="left")

        # ---- WebDAV 设置 ----
        frame_wd = ttk.LabelFrame(content, text="WebDAV 设置", padding=(12, 10))
        frame_wd.pack(fill="x", pady=8)

        row_frame = ttk.Frame(frame_wd)
        row_frame.pack(fill="x", pady=3)
        ttk.Label(row_frame, text="服务器地址:", width=12, anchor="e").pack(side="left")
        webdav_url_entry = ttk.Entry(row_frame, width=50)
        webdav_url_entry.pack(side="left", padx=5)
        webdav_url_entry.insert(0, self.webdav_url)

        row_frame2 = ttk.Frame(frame_wd)
        row_frame2.pack(fill="x", pady=3)
        ttk.Label(row_frame2, text="用户名:", width=12, anchor="e").pack(side="left")
        webdav_user_entry = ttk.Entry(row_frame2, width=25)
        webdav_user_entry.pack(side="left", padx=5)
        webdav_user_entry.insert(0, self.webdav_username)
        ttk.Label(row_frame2, text="密码:", width=6, anchor="e").pack(side="left")
        webdav_pass_entry = ttk.Entry(row_frame2, width=25, show="*")
        webdav_pass_entry.pack(side="left", padx=5)
        webdav_pass_entry.insert(0, self.webdav_password)

        # 测试连接按钮
        def test_webdav():
            url = webdav_url_entry.get().strip()
            user = webdav_user_entry.get().strip()
            pwd = webdav_pass_entry.get().strip()
            if not url:
                messagebox.showwarning("提示", "请先输入 WebDAV 服务器地址", parent=settings_win)
                return
            try:
                client = WebDAVClient(url, user, pwd)
                if client.test_connection():
                    messagebox.showinfo("成功", "WebDAV 连接测试成功！", parent=settings_win)
                else:
                    messagebox.showerror("失败", "WebDAV 连接测试失败，请检查地址和账号密码。", parent=settings_win)
            except Exception as e:
                messagebox.showerror("错误", f"WebDAV 连接失败: {e}", parent=settings_win)

        row_frame3 = ttk.Frame(frame_wd)
        row_frame3.pack(fill="x", pady=(8, 0))
        ttk.Label(row_frame3, text="", width=12).pack(side="left")
        create_button(row_frame3, text="测试 WebDAV 连接", style='Secondary.TButton',
                   command=test_webdav, width=130,
                   pack_side="left", pack_padx=5)

        # ---- 默认备份数量 ----
        frame_mb = ttk.LabelFrame(content, text="备份保留设置", padding=(12, 10))
        frame_mb.pack(fill="x", pady=8)

        row_mb = ttk.Frame(frame_mb)
        row_mb.pack(fill="x")
        ttk.Label(row_mb, text="默认备份数量:", width=12, anchor="e").pack(side="left")
        max_backups_var = tk.IntVar(value=self.default_max_backups)
        ttk.Spinbox(row_mb, from_=1, to=99, width=8, textvariable=max_backups_var).pack(side="left", padx=5)
        ttk.Label(row_mb, text="（每个游戏可单独配置保留数量，修改后新游戏生效）", style='Hint.TLabel').pack(side="left")

        # ---- 默认下载来源 ----
        frame_dl = ttk.LabelFrame(content, text="下载偏好", padding=(12, 10))
        frame_dl.pack(fill="x", pady=8)

        row_dl = ttk.Frame(frame_dl)
        row_dl.pack(fill="x")
        ttk.Label(row_dl, text="默认下载来源:", width=12, anchor="e").pack(side="left")
        dl_display = ["OneDrive 优先", "WebDAV 优先", "每次询问我"]
        dl_keys = ["onedrive", "webdav", "ask"]
        dl_display_to_key = dict(zip(dl_display, dl_keys))
        dl_key_to_display = dict(zip(dl_keys, dl_display))
        dl_var = tk.StringVar(value=dl_key_to_display.get(self.default_download_source, dl_display[2]))
        dl_combo = ttk.Combobox(row_dl, textvariable=dl_var, width=22,
                                values=dl_display, state="readonly")
        dl_combo.pack(side="left", padx=5)

        # ---- 开机自启 ----
        frame_auto = ttk.Frame(content)
        frame_auto.pack(fill="x", pady=10)
        autostart_var = tk.BooleanVar(value=self.autostart)
        ttk.Checkbutton(frame_auto, text="开机自动启动（添加到系统启动项）",
                       variable=autostart_var).pack(anchor="w")

        # ---- 后台运行 ----
        frame_bg = ttk.Frame(content)
        frame_bg.pack(fill="x", pady=4)
        background_var = tk.BooleanVar(value=self.background_mode)
        ttk.Checkbutton(frame_bg, text="后台运行（关闭窗口后最小化到托盘继续监控）",
                       variable=background_var).pack(anchor="w")

        # ---- 启动时自动同步 ----
        frame_as = ttk.Frame(content)
        frame_as.pack(fill="x", pady=4)
        autosync_var = tk.BooleanVar(value=self.auto_sync_on_startup)
        ttk.Checkbutton(frame_as, text="打开时自动备份或下载（启动时若存档有变更则自动上传/下载）",
                       variable=autosync_var).pack(anchor="w")

        # ---- 自动备份配置到云端 ----
        frame_acc = ttk.Frame(content)
        frame_acc.pack(fill="x", pady=4)
        autocfg_var = tk.BooleanVar(value=self.auto_backup_config_to_cloud)
        ttk.Checkbutton(frame_acc, text="自动保存游戏配置到云端（增删改游戏时自动备份配置；启动时检测云端更新）",
                       variable=autocfg_var).pack(anchor="w")

        # ---- 保存按钮 ----
        def save_settings():
            new_cloud = cloud_entry.get().strip()
            new_webdav_url = webdav_url_entry.get().strip()
            new_webdav_user = webdav_user_entry.get().strip()
            new_webdav_pass = webdav_pass_entry.get().strip()
            new_max_backups = max_backups_var.get()
            new_dl_source = dl_display_to_key.get(dl_var.get(), "ask")

            need_restart = False
            with self._config_lock:
                if new_cloud != self.cloud_root:
                    self.cloud_root = new_cloud
                    need_restart = True
                if (new_webdav_url != self.webdav_url or
                    new_webdav_user != self.webdav_username or
                    new_webdav_pass != self.webdav_password):
                    self.webdav_url = new_webdav_url
                    self.webdav_username = new_webdav_user
                    self.webdav_password = new_webdav_pass
                    need_restart = True
                self.default_max_backups = new_max_backups
                self.default_download_source = new_dl_source

            # 后台运行
            self.background_mode = background_var.get()
            if self.background_mode:
                self.root.protocol('WM_DELETE_WINDOW', self.hide_window)
            else:
                self.root.protocol('WM_DELETE_WINDOW', self.quit_app)

            # 启动时自动同步
            self.auto_sync_on_startup = autosync_var.get()

            # 自动备份配置到云端
            self.auto_backup_config_to_cloud = autocfg_var.get()

            # 自启动
            autostart_enable = autostart_var.get()
            if autostart_enable != self.autostart:
                self.set_autostart(autostart_enable)

            self.save_config()
            self.update_cloud_status_label()
            # 监控重启延迟到窗口关闭后执行，避免关闭卡顿
            if need_restart and self.is_monitoring:
                self.root.after(50, lambda: (self.stop_monitoring(), self.start_monitoring()))
            self.log("⚙ 全局设置已保存。")
            settings_win.destroy()

        # 点击窗口 X 按钮也自动保存
        settings_win.protocol('WM_DELETE_WINDOW', save_settings)

    # ================= 游戏列表与状态 =================
    def get_sync_status(self, game):
        if game.get("paused", False):
            return "⏸ 已暂停", "paused"
        methods = game.get("backup_methods", ["onedrive"])
        if not methods:
            return "未设置备份方式", "none"

        save_dir = game["save"]
        has_onedrive = "onedrive" in methods and self.cloud_root
        has_webdav = "webdav" in methods and self.webdav_url

        if not has_onedrive and not has_webdav:
            return "备份目标未配置", "none"

        local_time = get_latest_mtime(save_dir)

        # 检查 OneDrive 云端（基于文件实际 mtime，准确可靠）
        od_cloud_time = 0
        if has_onedrive:
            game_cloud_dir = os.path.join(self.cloud_root, game["name"])
            if os.path.exists(game_cloud_dir):
                backups = [os.path.join(game_cloud_dir, d) for d in os.listdir(game_cloud_dir) if d.startswith("save_")]
                if backups:
                    backups.sort(key=os.path.getmtime, reverse=True)
                    od_cloud_time = get_latest_mtime(backups[0])

        # 检查 WebDAV 云端
        #   新格式目录名: save_20260512_143000_mtime1715500200
        #     其中 1715500200 = 备份时本地文件的最新 mtime，用于跨设备准确判断
        #   旧格式目录名: save_20260512_143000（兼容，仅能判断"本地较新"）
        wd_backup_mtime = 0  # 新格式：备份时本地文件的最新 mtime
        wd_dir_ts = 0         # 旧格式后备：目录名中的时间戳
        if has_webdav and self.webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    items = wc.propfind(game["name"])
                    save_names = [n for _, n in items if n.startswith("save_")]
                    if save_names:
                        save_names.sort(reverse=True)
                        try:
                            latest_name = save_names[0]
                            ts_str, bk_mtime = parse_backup_name(latest_name)
                            if bk_mtime > 0:
                                wd_backup_mtime = bk_mtime
                                wd_dir_ts = time.mktime(datetime.strptime(ts_str, "%Y%m%d_%H%M%S").timetuple())
                            else:
                                wd_dir_ts = time.mktime(datetime.strptime(ts_str, "%Y%m%d_%H%M%S").timetuple())
                        except Exception:
                            pass
                except Exception:
                    pass

        # 确定有效的云端时间
        # OneDrive 文件 mtime 最准确，优先使用
        # WebDAV 新格式：用备份时本地文件 mtime（和 OneDrive 同等准确）
        # WebDAV 旧格式：用目录名时间戳作为后备
        if od_cloud_time > 0:
            cloud_time = od_cloud_time
        elif wd_backup_mtime > 0:
            cloud_time = wd_backup_mtime
        else:
            cloud_time = wd_dir_ts

        if local_time == 0 and cloud_time == 0:
            return "无存档记录", "none"
        if cloud_time == 0:
            return "等待首次备份...", "local_new"
        if local_time == 0:
            return "☁ 本地无档 (可下载)", "cloud_new"

        # 本地较新：本地 mtime 晚于云端，说明有未备份的改动
        if local_time - cloud_time > 3:
            return "⚠ 本地较新 (未备份)", "local_new"

        # 云端较新：
        # - OneDrive 或 WebDAV 新格式：cloud_time 是文件实际 mtime，可准确判断
        # - WebDAV 旧格式：目录名时间戳无法区分"刚备份了旧文件"和"其他设备上传了新存档"
        if od_cloud_time > 0 or wd_backup_mtime > 0:
            if cloud_time - local_time > 3:
                return "✨ 云端较新 (请下载)", "cloud_new"

        return "✅ 已完全同步", "synced"

    def get_backup_display(self, game):
        """获取备份方式的显示文本"""
        methods = game.get("backup_methods", ["onedrive"])
        labels = []
        for m in methods:
            if m == "onedrive":
                labels.append("OneDrive" if self.cloud_root else "OneDrive(未配置)")
            elif m == "webdav":
                labels.append("WebDAV" if self.webdav_url else "WebDAV(未配置)")
        return " | ".join(labels) if labels else "无"

    def refresh_list(self):
        for item in self.tree.get_children():
            self.tree.delete(item)
        with self._config_lock:
            games = list(self.games_config)
        for game in games:
            status_text, tag = self.get_sync_status(game)
            backup_display = self.get_backup_display(game)
            self.tree.insert("", "end",
                             values=(game["name"], game["save"], backup_display, status_text),
                             tags=(tag,), image=self._cb_unchecked)
        self._sync_checkbox_images(self.tree)

    def _sync_checkbox_images(self, tree_widget):
        """同步复选框图标与选中状态"""
        selected = set(tree_widget.selection())
        for iid in tree_widget.get_children():
            img = self._cb_checked if iid in selected else self._cb_unchecked
            tree_widget.item(iid, image=img)

    def _on_tree_checkbox_click(self, event):
        """点击复选框列切换选中状态"""
        tree_widget = event.widget
        col = tree_widget.identify_column(event.x)
        if col != '#0':
            return
        iid = tree_widget.identify_row(event.y)
        if not iid:
            return
        cur_sel = set(tree_widget.selection())
        if iid in cur_sel:
            tree_widget.selection_remove(iid)
        else:
            tree_widget.selection_add(iid)
        self._sync_checkbox_images(tree_widget)
        return "break"

    def trigger_refresh_ui(self):
        self.root.after(0, self.refresh_list)

    def _monitor_backup(self, game_name, save_dir):
        """监控触发的备份：原子性 try_submit，繁忙时跳过（防止积压 + TOCTOU）"""
        def task():
            self.perform_backup(game_name, save_dir)
        self.worker.try_submit(task)  # 原子 check-and-submit，不再有竞态窗口

    # ================= 备份执行（核心） =================
    def perform_backup(self, game_name, save_dir):
        """对指定游戏执行备份（到所有启用的目标），由监控或手动触发"""
        game = self._get_game(game_name)
        if not game:
            return
        methods = game.get("backup_methods", ["onedrive"])
        max_backups = game.get("max_backups", self.default_max_backups)
        cloud_root = self.cloud_root
        webdav_url = self.webdav_url
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        success = []

        # --- OneDrive 备份 ---
        if "onedrive" in methods and cloud_root:
            backup_folder = f"save_{timestamp}"
            target_path = os.path.join(cloud_root, game_name, backup_folder)
            try:
                os.makedirs(os.path.dirname(target_path), exist_ok=True)
                shutil.copytree(save_dir, target_path)
                self._cleanup_onedrive_backups(os.path.join(cloud_root, game_name), max_backups)
                success.append("OneDrive")
            except Exception as e:
                self.log(f"[{datetime.now().strftime('%H:%M:%S')}] ❌ {game_name}: OneDrive 备份失败 ({e})")

        # --- WebDAV 备份 ---
        if "webdav" in methods and webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    # 将备份时本地文件的最新 mtime 编码到目录名，供跨设备同步状态判断
                    local_mtime = int(get_latest_mtime(save_dir))
                    backup_folder = f"save_{timestamp}_mtime{local_mtime}"
                    remote_dir = f"{game_name}/{backup_folder}"
                    if wc.upload_dir(save_dir, remote_dir):
                        self._cleanup_webdav_backups(wc, game_name, max_backups)
                        success.append("WebDAV")
                    else:
                        self.log(f"[{datetime.now().strftime('%H:%M:%S')}] ❌ {game_name}: WebDAV 备份上传失败")
                except Exception as e:
                    self.log(f"[{datetime.now().strftime('%H:%M:%S')}] ❌ {game_name}: WebDAV 备份失败 ({e})")

        if success:
            self.log(f"[{datetime.now().strftime('%H:%M:%S')}] ✅ {game_name}: 自动备份成功 ({', '.join(success)})")
            self.trigger_refresh_ui()

    def _cleanup_onedrive_backups(self, game_cloud_dir, max_backups=None):
        """清理 OneDrive 旧备份，保留最近 max_backups 个"""
        if max_backups is None:
            max_backups = DEFAULT_MAX_BACKUPS
        if not os.path.exists(game_cloud_dir):
            return
        all_backups = [os.path.join(game_cloud_dir, d) for d in os.listdir(game_cloud_dir) if d.startswith("save_")]
        all_backups.sort(key=os.path.getmtime)
        if len(all_backups) > max_backups:
            for old_backup in all_backups[:-max_backups]:
                try:
                    rmtree_safe(old_backup)
                except Exception:
                    pass

    def _cleanup_webdav_backups(self, wc, game_name, max_backups=None):
        """清理 WebDAV 旧备份，保留最近 max_backups 个"""
        if max_backups is None:
            max_backups = DEFAULT_MAX_BACKUPS
        try:
            items = wc.propfind(game_name)
            backups = []
            for href, name in items:
                if name.startswith("save_"):
                    backups.append((name, href))
            backups.sort(key=lambda x: x[0], reverse=True)
            if len(backups) > max_backups:
                for name, _ in backups[max_backups:]:
                    try:
                        wc.delete_path(f"{game_name}/{name}")
                    except Exception:
                        pass
        except Exception:
            pass

    # ================= 游戏管理 =================
    def add_game(self):
        """保留兼容旧代码的空方法，实际通过 open_add_game_dialog 添加"""
        self.open_add_game_dialog()

    def delete_game(self):
        selected = self.tree.selection()
        if not selected:
            return
        if messagebox.askyesno("确认", "确定删除该游戏配置吗？"):
            for item in selected:
                values = self.tree.item(item, 'values')
                name = values[0]
                with self._config_lock:
                    self.games_config = [g for g in self.games_config if g['name'] != name]
                self.tree.delete(item)
            self.save_config()
            if self.auto_backup_config_to_cloud:
                self.backup_config_to_cloud()
            if self.is_monitoring:
                self.stop_monitoring()
                self.start_monitoring()

    def edit_game(self):
        selected = self.tree.selection()
        if not selected:
            return
        item = selected[0]
        old_name = self.tree.item(item, 'values')[0]
        game = self._get_game(old_name)
        if not game:
            return

        edit_win = tk.Toplevel(self.root)
        edit_win.title(f"修改配置 - {old_name}")
        edit_win.geometry("500x300")
        edit_win.resizable(False, False)
        edit_win.grab_set()
        edit_win.configure(bg="#F5F6FA")
        center_window(edit_win)

        content = ttk.Frame(edit_win, padding=15)
        content.pack(fill="both", expand=True)

        ttk.Label(content, text="游戏名称:", style='Dialog.TLabel').grid(row=0, column=0, padx=(5, 10), pady=(5, 10), sticky="e")
        ent_name = ttk.Entry(content, width=35, font=(self._font_family, 10))
        ent_name.insert(0, game["name"])
        ent_name.grid(row=0, column=1, padx=5, pady=(5, 10))

        ttk.Label(content, text="本地存档:", style='Dialog.TLabel').grid(row=1, column=0, padx=(5, 10), pady=5, sticky="e")
        ent_save = ttk.Entry(content, width=35, font=(self._font_family, 10))
        ent_save.insert(0, game["save"])
        ent_save.grid(row=1, column=1, padx=5, pady=5)
        create_button(content, text="浏览", style='Secondary.TButton',
                   command=lambda: self.browse_dir(ent_save), width=60,
                   grid_row=1, grid_col=2, grid_padx=5)

        # 备份方式
        ttk.Label(content, text="备份方式:", style='Dialog.TLabel').grid(row=2, column=0, padx=(5, 10), pady=(10, 5), sticky="e")
        frame_m = ttk.Frame(content)
        frame_m.grid(row=2, column=1, padx=5, pady=(10, 5), sticky="w")
        cur_methods = game.get("backup_methods", ["onedrive"])
        var_od = tk.BooleanVar(value="onedrive" in cur_methods)
        var_wd = tk.BooleanVar(value="webdav" in cur_methods)
        ttk.Checkbutton(frame_m, text="OneDrive", variable=var_od).pack(side="left", padx=(0, 15))
        ttk.Checkbutton(frame_m, text="WebDAV", variable=var_wd).pack(side="left")

        # 备份保留数量
        ttk.Label(content, text="备份保留数量:", style='Dialog.TLabel').grid(row=3, column=0, padx=(5, 10), pady=(10, 5), sticky="e")
        var_max = tk.IntVar(value=game.get("max_backups", self.default_max_backups))
        ttk.Spinbox(content, from_=1, to=99, width=8, textvariable=var_max).grid(row=3, column=1, padx=5, pady=(10, 5), sticky="w")

        def save_edit():
            new_name = ent_name.get().strip()
            new_save = ent_save.get().strip()
            if not new_name or not new_save:
                messagebox.showwarning("错误", "请填写完整！", parent=edit_win)
                return
            new_methods = []
            if var_od.get():
                new_methods.append("onedrive")
            if var_wd.get():
                new_methods.append("webdav")
            if not new_methods:
                messagebox.showwarning("错误", "请至少选择一种备份方式！", parent=edit_win)
                return
            game["name"] = new_name
            game["save"] = new_save
            game["backup_methods"] = new_methods
            game["max_backups"] = var_max.get()
            self.save_config()
            if self.auto_backup_config_to_cloud:
                self.backup_config_to_cloud()
            self.refresh_list()
            if self.is_monitoring:
                self.stop_monitoring()
                self.start_monitoring()
            self.log(f"✏ 游戏【{new_name}】配置已修改。")
            edit_win.destroy()

        create_button(content, text="💾 保存修改", style='Success.TButton',
                  command=save_edit, width=120,
                  grid_row=4, grid_col=1, grid_pady=(15, 5))

    # ================= 右键菜单功能 =================
    def show_context_menu(self, event):
        item_id = self.tree.identify_row(event.y)
        if not item_id:
            return
        if item_id not in self.tree.selection():
            self.tree.selection_set(item_id)
        sel_count = len(self.tree.selection())

        if sel_count > 1:
            # 多选：精简菜单
            self.context_menu_multi.post(event.x_root, event.y_root)
        else:
            # 单选：完整菜单
            game_name = self.tree.item(item_id, 'values')[0]
            self._selected_game_name = game_name
            game = self._get_game(game_name)
            if game:
                if game.get("paused", False):
                    self.context_menu.entryconfig(6, label="▶ 恢复同步")
                else:
                    self.context_menu.entryconfig(6, label="⏸ 暂停同步")
                methods = game.get("backup_methods", ["onedrive"])
                self.backup_onedrive_var.set("onedrive" in methods)
                self.backup_webdav_var.set("webdav" in methods)
            self.context_menu.post(event.x_root, event.y_root)

    def _toggle_backup_method(self, method):
        """切换某游戏的备份方式（从右键菜单触发）"""
        if not self._selected_game_name:
            return
        game = self._get_game(self._selected_game_name)
        if not game:
            return
        methods = game.get("backup_methods", ["onedrive"])

        if method == "onedrive":
            if "onedrive" in methods:
                methods.remove("onedrive")
            else:
                if not self.cloud_root:
                    messagebox.showwarning("提示", "OneDrive 云盘目录未设置，请先在全局设置中配置。")
                    self.backup_onedrive_var.set(False)
                    return
                methods.append("onedrive")
        elif method == "webdav":
            if "webdav" in methods:
                methods.remove("webdav")
            else:
                if not self.webdav_url:
                    messagebox.showwarning("提示", "WebDAV 未配置，请先在全局设置中配置。")
                    self.backup_webdav_var.set(False)
                    return
                methods.append("webdav")

        game["backup_methods"] = methods
        self.save_config()
        self.refresh_list()
        if self.is_monitoring:
            self.stop_monitoring()
            self.start_monitoring()
        self.log(f"⚙ 游戏【{self._selected_game_name}】备份方式已更新: {', '.join(methods) if methods else '无'}")

    def toggle_pause(self):
        selected = self.tree.selection()
        if not selected:
            return
        # 多选时，根据第一个游戏的状态统一切换
        first_game = self._get_game(self.tree.item(selected[0], 'values')[0])
        new_paused = not first_game.get("paused", False) if first_game else True
        state_label = "暂停" if new_paused else "恢复"
        for item in selected:
            game_name = self.tree.item(item, 'values')[0]
            game = self._get_game(game_name)
            if game:
                game["paused"] = new_paused
                self.log(f"⚙ 游戏【{game_name}】已{state_label}同步。")
        self.save_config()
        self.refresh_list()
        if self.is_monitoring:
            self.stop_monitoring()
            self.start_monitoring()

    def open_local_folder(self):
        """打开选中的游戏本地存档文件夹"""
        selected = self.tree.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中一个游戏！")
        game_name = self.tree.item(selected[0], 'values')[0]
        game = self._get_game(game_name)
        if not game:
            return
        save_dir = game["save"]
        if os.path.isdir(save_dir):
            os.startfile(save_dir)
        else:
            messagebox.showwarning("路径不存在", f"本地存档路径不存在：\n{save_dir}\n\n可能尚未创建或已被删除。")

    def open_cloud_folder(self):
        """打开选中的游戏云端存档位置（OneDrive 文件夹或 WebDAV 网址）"""
        selected = self.tree.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中一个游戏！")
        game_name = self.tree.item(selected[0], 'values')[0]
        game = self._get_game(game_name)
        if not game:
            return
        methods = game.get("backup_methods", ["onedrive"])

        # 优先打开 OneDrive 本地目录（更直观），否则打开 WebDAV 网址
        if "onedrive" in methods and self.cloud_root:
            cloud_dir = os.path.join(self.cloud_root, game_name)
            if not os.path.exists(cloud_dir):
                os.makedirs(cloud_dir, exist_ok=True)
            os.startfile(cloud_dir)
        elif "webdav" in methods and self.webdav_url:
            wd_url = self.webdav_url.rstrip('/')
            import urllib.parse
            encoded_name = urllib.parse.quote(game_name, safe='')
            import webbrowser
            webbrowser.open(f"{wd_url}/{encoded_name}")
        else:
            messagebox.showinfo("提示", "该游戏未配置可用的云端目标。请在全局设置中配置或在右键菜单中切换备份方式。")

    def force_backup(self):
        selected = self.tree.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中需要上传的游戏！")
        if len(selected) > 1:
            if not messagebox.askyesno("批量上传", f"确定要为选中的 {len(selected)} 个游戏执行手动上传吗？"):
                return

        # 提前收集游戏信息（主线程安全读取 Treeview）
        tasks = []
        for item in selected:
            values = self.tree.item(item, 'values')
            game_name, save_dir = values[0], values[1]
            game = self._get_game(game_name)
            if not game:
                continue
            methods = game.get("backup_methods", ["onedrive"])
            if not methods:
                continue
            if not os.path.isdir(save_dir) or not os.listdir(save_dir):
                continue
            tasks.append((game_name, save_dir))

        if not tasks:
            return

        self._disable_action_buttons("上传中…")
        def run():
            for game_name, save_dir in tasks:
                self.perform_backup(game_name, save_dir)
        self.worker.submit(run, on_done=lambda: (self.refresh_list(), self._enable_action_buttons()))

    def restore_game(self):
        selected = self.tree.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中需要下载覆盖的游戏！")

        # 多选：批量模式，尊重下载偏好
        if len(selected) > 1:
            # 询问偏好
            batch_pref = self.default_download_source
            if batch_pref == "ask":
                batch_pref = self._ask_batch_download_source()
                if not batch_pref:
                    return

            if not messagebox.askyesno("批量覆盖下载", f"确定要用云端最新备份覆盖选中的 {len(selected)} 个游戏的本地存档吗？"):
                return

            # 提前收集所有恢复任务
            batch_tasks = []
            old_pref = self.default_download_source
            self.default_download_source = batch_pref
            try:
                for item in selected:
                    values = self.tree.item(item, 'values')
                    game_name, save_dir = values[0], values[1]
                    game = self._get_game(game_name)
                    if not game:
                        continue
                    methods = game.get("backup_methods", ["onedrive"])
                    source = self._find_latest_backup(game_name, methods)
                    if not source:
                        self.log(f"🔄 ⚠ {game_name}: 未找到任何云端备份，跳过")
                        continue
                    batch_tasks.append((game_name, save_dir, source))
            finally:
                self.default_download_source = old_pref

            if not batch_tasks:
                return

            self._disable_action_buttons("下载中…")
            def run():
                for game_name, save_dir, source in batch_tasks:
                    self._do_restore(game_name, save_dir, source)
            self.worker.submit(run, on_done=lambda: (self.refresh_list(), self._enable_action_buttons()))
            return

        # 单选：完整流程（确认对话框 → 主线程，实际下载 → Worker）
        values = self.tree.item(selected[0], 'values')
        game_name, save_dir = values[0], values[1]
        game = self._get_game(game_name)
        if not game:
            return
        methods = game.get("backup_methods", ["onedrive"])

        # 收集可用源
        sources = []
        if "onedrive" in methods and self.cloud_root:
            game_cloud_dir = os.path.join(self.cloud_root, game_name)
            if os.path.exists(game_cloud_dir):
                backups = [os.path.join(game_cloud_dir, d) for d in os.listdir(game_cloud_dir) if d.startswith("save_")]
                if backups:
                    backups.sort(key=os.path.getmtime, reverse=True)
                    sources.append(("OneDrive", "onedrive", backups[0]))
        if "webdav" in methods and self.webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    items = wc.propfind(game_name)
                    save_names = [(n, h) for h, n in items if n.startswith("save_")]
                    if save_names:
                        save_names.sort(key=lambda x: x[0], reverse=True)
                        sources.append(("WebDAV", "webdav", save_names[0][0]))
                except Exception:
                    pass

        if not sources:
            return messagebox.showinfo("提示", "未找到任何云端备份！")

        # 选择恢复源
        if len(sources) == 1:
            chosen_source = sources[0]
        elif self.default_download_source == "ask":
            chosen_source = self._ask_download_source(game_name, sources)
            if not chosen_source:
                return
        else:
            chosen_source = self._find_latest_backup(game_name, methods)

        source_label, source_key, source_path = chosen_source
        local_ts = get_latest_mtime(save_dir)
        cloud_ts = get_latest_mtime(source_path) if source_key == "onedrive" else 0
        if source_key == "webdav":
            try:
                ts_str, bk_mtime = parse_backup_name(source_path)
                cloud_ts = time.mktime(datetime.strptime(ts_str, "%Y%m%d_%H%M%S").timetuple())
            except Exception:
                cloud_ts = 0

        local_str = datetime.fromtimestamp(local_ts).strftime("%Y-%m-%d %H:%M:%S") if local_ts else "无本地"
        cloud_str = datetime.fromtimestamp(cloud_ts).strftime("%Y-%m-%d %H:%M:%S") if cloud_ts else "未知"

        if local_ts == 0:
            msg = f"本地无存档。\n\n来源: {source_label}\n[云端]: {cloud_str}\n\n确定下载？"
        elif cloud_ts > local_ts:
            msg = f"☁ 云端较新\n\n来源: {source_label}\n[云端]: {cloud_str}\n[本地]: {local_str}\n\n确定覆盖？"
        else:
            msg = f"⚠ 本地较新或相同！\n\n来源: {source_label}\n[本地]: {local_str} (较新)\n[云端]: {cloud_str}\n\n确定覆盖？"

        if not messagebox.askyesno("覆盖确认", msg):
            return

        self._disable_action_buttons("下载中…")
        def run():
            self._do_restore(game_name, save_dir, chosen_source)
        self.worker.submit(run, on_done=lambda: (self.refresh_list(), self._enable_action_buttons()))

    def _ask_batch_download_source(self):
        """批量下载前询问用户偏好来源，返回 "onedrive" / "webdav" 或 None（取消）"""
        choice_win = tk.Toplevel(self.root)
        choice_win.title("选择下载来源")
        choice_win.geometry("360x150")
        choice_win.resizable(False, False)
        choice_win.grab_set()
        choice_win.configure(bg="#F5F6FA")
        choice_win.result = None

        content = ttk.Frame(choice_win, padding=20)
        content.pack(fill="both", expand=True)
        ttk.Label(content, text="批量下载：请选择优先使用的备份来源",
                 font=(self._font_family, 10)).pack(pady=(0, 15))

        btn_frame = ttk.Frame(content)
        btn_frame.pack()
        def choose(key):
            choice_win.result = key
            choice_win.destroy()

        create_button(btn_frame, text="OneDrive 优先", style='Success.TButton',
                   command=lambda: choose("onedrive"), width=120,
                   pack_side="left", pack_padx=(0, 10))
        create_button(btn_frame, text="WebDAV 优先", style='Secondary.TButton',
                   command=lambda: choose("webdav"), width=120,
                   pack_side="left")

        center_window(choice_win)
        self.root.wait_window(choice_win)
        return getattr(choice_win, 'result', None)

    def _ask_download_source(self, game_name, sources):
        """弹出窗口让用户选择下载来源"""
        choice_win = tk.Toplevel(self.root)
        choice_win.title("选择下载来源")
        choice_win.geometry("380x160")
        choice_win.resizable(False, False)
        choice_win.grab_set()
        choice_win.configure(bg="#F5F6FA")
        center_window(choice_win)
        choice_win.result = None

        content = ttk.Frame(choice_win, padding=20)
        content.pack(fill="both", expand=True)
        ttk.Label(content, text=f"游戏【{game_name}】有多个备份源：",
                 font=(self._font_family, 10)).pack(pady=(0, 12))

        def choose(src):
            choice_win.result = src
            choice_win.destroy()

        for label, key, path in sources:
            info = path if key == "onedrive" else f"WebDAV: {path}"
            create_button(content, text=f"从 {label} 恢复", style='Secondary.TButton',
                       command=lambda s=(label, key, path): choose(s), width=140).pack(pady=3)

        self.root.wait_window(choice_win)
        return getattr(choice_win, 'result', None)

    def _do_restore(self, game_name, save_dir, source):
        """执行实际的恢复操作（在后台线程中运行，不做 GUI 更新）"""
        source_label, source_key, source_path = source
        try:
            if source_key == "onedrive":
                if os.path.exists(save_dir):
                    rmtree_safe(save_dir)
                shutil.copytree(source_path, save_dir)
            else:
                wc = self.get_webdav_client()
                if not wc:
                    raise Exception("无法连接 WebDAV")
                remote_dir = f"{game_name}/{source_path}"
                # 先下载到临时目录，成功后再原子替换，避免下载失败清空本地存档
                tmp_dir = save_dir + ".tmp"
                if os.path.exists(tmp_dir):
                    rmtree_safe(tmp_dir)
                file_count = wc.download_dir(remote_dir, tmp_dir)
                if file_count == 0:
                    rmtree_safe(tmp_dir)
                    raise Exception("WebDAV 下载失败：未获取到任何文件")
                if os.path.exists(save_dir):
                    rmtree_safe(save_dir)
                os.rename(tmp_dir, save_dir)
                apply_backup_mtime(source_path, save_dir)

            self.log(f"🔄 ✅ {game_name}: 从 {source_label} 恢复成功！")
        except Exception as e:
            self.log(f"🔄 ❌ {game_name}: 恢复失败 ({e})")

    def _find_latest_backup(self, game_name, methods):
        """查找游戏的最新云端备份，返回 (label, key, path) 或 None，尊重下载偏好"""
        pref = self.default_download_source

        od_best = None
        od_ts = 0
        wd_best = None
        wd_ts = 0

        if "onedrive" in methods and self.cloud_root:
            game_cloud_dir = os.path.join(self.cloud_root, game_name)
            if os.path.exists(game_cloud_dir):
                backups = [os.path.join(game_cloud_dir, d) for d in os.listdir(game_cloud_dir) if d.startswith("save_")]
                if backups:
                    backups.sort(key=os.path.getmtime, reverse=True)
                    od_ts = get_latest_mtime(backups[0])
                    od_best = ("OneDrive", "onedrive", backups[0])

        if "webdav" in methods and self.webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    items = wc.propfind(game_name)
                    save_names = [(n, h) for h, n in items if n.startswith("save_")]
                    if save_names:
                        save_names.sort(key=lambda x: x[0], reverse=True)
                        try:
                            ts_str, bk_mtime = parse_backup_name(save_names[0][0])
                            if bk_mtime > 0:
                                wd_ts = bk_mtime
                            else:
                                wd_ts = time.mktime(datetime.strptime(ts_str, "%Y%m%d_%H%M%S").timetuple())
                            wd_best = ("WebDAV", "webdav", save_names[0][0])
                        except Exception:
                            pass
                except Exception:
                    pass

        if pref == "onedrive" and od_best:
            return od_best
        if pref == "webdav" and wd_best:
            return wd_best
        if od_ts > wd_ts:
            return od_best
        return wd_best or od_best

    # ================= 历史备份窗口 =================
    def show_history_backup(self):
        selected = self.tree.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中一个游戏！")
        game_name = self.tree.item(selected[0], 'values')[0]
        game = self._get_game(game_name)
        if not game:
            return
        methods = game.get("backup_methods", ["onedrive"])

        hist_win = tk.Toplevel(self.root)
        hist_win.title(f"历史备份 - {game_name}")
        hist_win.geometry("700x420")
        hist_win.grab_set()
        hist_win.configure(bg="#F5F6FA")
        center_window(hist_win)

        # 用 Notebook 分 Tab 显示 OneDrive 和 WebDAV 备份
        notebook = ttk.Notebook(hist_win)
        notebook.pack(fill="both", expand=True, padx=10, pady=10)

        # ==== OneDrive Tab ====
        has_od = "onedrive" in methods and self.cloud_root
        if has_od:
            game_cloud_dir = os.path.join(self.cloud_root, game_name)
            od_backups = [d for d in os.listdir(game_cloud_dir) if d.startswith("save_")] if os.path.exists(game_cloud_dir) else []
            od_backups.sort(reverse=True)
        else:
            od_backups = []
            game_cloud_dir = ""

        frame_od = ttk.Frame(notebook)
        notebook.add(frame_od, text="OneDrive")

        tree_od = self._build_history_tree(frame_od, game_cloud_dir, od_backups, "onedrive")
        self._bind_history_menu(tree_od, hist_win, game_name, game_cloud_dir, "onedrive")

        # ==== WebDAV Tab ====
        has_wd = "webdav" in methods and self.webdav_url
        wd_backups = []
        if has_wd:
            wc = self.get_webdav_client()
            if wc:
                try:
                    items = wc.propfind(game_name)
                    wd_backups = [(n, h) for h, n in items if n.startswith("save_")]
                    wd_backups.sort(key=lambda x: x[0], reverse=True)
                except Exception:
                    pass

        frame_wd = ttk.Frame(notebook)
        notebook.add(frame_wd, text="WebDAV")

        tree_wd = self._build_history_tree_webdav(frame_wd, wd_backups)
        # WebDAV 历史右键
        # 单选菜单
        menu_wd_single = tk.Menu(hist_win, tearoff=0)
        menu_wd_single.add_command(label="恢复此版本",
                            command=lambda: self._restore_webdav_backup(game_name, tree_wd, wd_backups, hist_win))
        menu_wd_single.add_command(label="删除此备份",
                            command=lambda: self._delete_webdav_backup(game_name, tree_wd, wd_backups))

        # 多选菜单
        menu_wd_multi = tk.Menu(hist_win, tearoff=0)
        menu_wd_multi.add_command(label="删除选中的备份",
                            command=lambda: self._batch_delete_webdav_backups(game_name, tree_wd, wd_backups))
        menu_wd_multi.add_command(label="下载选中的备份",
                            command=lambda: self._batch_download_webdav_backups(game_name, tree_wd, wd_backups, hist_win))

        def on_right_wd(event):
            iid = tree_wd.identify_row(event.y)
            if iid:
                if iid not in tree_wd.selection():
                    tree_wd.selection_set(iid)
            if len(tree_wd.selection()) > 1:
                menu_wd_multi.post(event.x_root, event.y_root)
            else:
                menu_wd_single.post(event.x_root, event.y_root)

        tree_wd.bind("<Button-3>", on_right_wd)

        # 如果只有一个 Tab 有内容，默认切到空的 Tab 也没影响
        if not has_od and has_wd:
            notebook.select(1)

    def _build_history_tree(self, parent, game_cloud_dir, backups, source):
        """构建 OneDrive 历史备份 treeview"""
        columns = ("name", "date", "size")
        tree = ttk.Treeview(parent, columns=columns, show="tree headings", selectmode="extended")
        tree.heading("#0", text="")
        tree.heading("name", text="备份文件夹")
        tree.heading("date", text="备份时间")
        tree.heading("size", text="大小")
        tree.column("#0", width=30, stretch=False, anchor="center")
        tree.column("name", width=200)
        tree.column("date", width=180)
        tree.column("size", width=100)
        tree.pack(fill="both", expand=True, padx=5, pady=5)

        # 复选框绑定
        tree.bind("<Button-1>", self._on_tree_checkbox_click)
        tree.bind("<<TreeviewSelect>>", lambda e, t=tree: self._sync_checkbox_images(t))

        for backup_name in backups:
            try:
                ts_str, _ = parse_backup_name(backup_name)
                dt = datetime.strptime(ts_str, "%Y%m%d_%H%M%S")
                date_str = dt.strftime("%Y-%m-%d %H:%M:%S")
            except Exception:
                date_str = "未知时间"
            backup_path = os.path.join(game_cloud_dir, backup_name) if game_cloud_dir else ""
            size_str = ""
            if backup_path and os.path.exists(backup_path):
                size_bytes = get_folder_size(backup_path)
                if size_bytes > 1024 * 1024:
                    size_str = f"{size_bytes / 1024 / 1024:.2f} MB"
                elif size_bytes > 1024:
                    size_str = f"{size_bytes / 1024:.2f} KB"
                else:
                    size_str = f"{size_bytes} B"
            tree.insert("", "end", values=(backup_name, date_str, size_str), iid=backup_name,
                       image=self._cb_unchecked)

        return tree

    def _build_history_tree_webdav(self, parent, backups):
        """构建 WebDAV 历史备份 treeview"""
        columns = ("name", "date")
        tree = ttk.Treeview(parent, columns=columns, show="tree headings", selectmode="extended")
        tree.heading("#0", text="")
        tree.heading("name", text="备份文件夹")
        tree.heading("date", text="备份时间")
        tree.column("#0", width=30, stretch=False, anchor="center")
        tree.column("name", width=280)
        tree.column("date", width=200)
        tree.pack(fill="both", expand=True, padx=5, pady=5)

        # 复选框绑定
        tree.bind("<Button-1>", self._on_tree_checkbox_click)
        tree.bind("<<TreeviewSelect>>", lambda e, t=tree: self._sync_checkbox_images(t))

        for name, href in backups:
            try:
                ts_str, _ = parse_backup_name(name)
                dt = datetime.strptime(ts_str, "%Y%m%d_%H%M%S")
                date_str = dt.strftime("%Y-%m-%d %H:%M:%S")
            except Exception:
                date_str = "未知时间"
            tree.insert("", "end", values=(name, date_str), iid=name,
                       image=self._cb_unchecked)

        return tree

    def _bind_history_menu(self, tree_hist, hist_win, game_name, game_cloud_dir, source):
        # 单选菜单
        menu_single = tk.Menu(hist_win, tearoff=0)
        menu_single.add_command(label="恢复此版本",
                              command=lambda: self.restore_specific_backup(game_name, game_cloud_dir, tree_hist, hist_win))
        menu_single.add_command(label="删除此备份",
                              command=lambda: self.delete_single_backup(game_name, game_cloud_dir, tree_hist))
        menu_single.add_separator()
        menu_single.add_command(label="删除所有备份",
                              command=lambda: self.delete_all_backups(game_name, game_cloud_dir, hist_win))

        # 多选菜单
        menu_multi = tk.Menu(hist_win, tearoff=0)
        menu_multi.add_command(label="删除选中的备份",
                              command=lambda: self.batch_delete_backups(game_name, game_cloud_dir, tree_hist))
        menu_multi.add_command(label="下载选中的备份",
                              command=lambda: self.batch_download_backups(game_name, game_cloud_dir, tree_hist, hist_win))

        def on_right_click(event):
            iid = tree_hist.identify_row(event.y)
            if iid:
                if iid not in tree_hist.selection():
                    tree_hist.selection_set(iid)
            if len(tree_hist.selection()) > 1:
                menu_multi.post(event.x_root, event.y_root)
            else:
                menu_single.post(event.x_root, event.y_root)

        tree_hist.bind("<Button-3>", on_right_click)

        def on_double_click(event):
            iid = tree_hist.identify_row(event.y)
            if iid:
                self.restore_specific_backup(game_name, game_cloud_dir, tree_hist, hist_win)

        tree_hist.bind("<Double-1>", on_double_click)

    def _restore_webdav_backup(self, game_name, tree_wd, wd_backups, parent_win=None):
        selected = tree_wd.selection()
        if not selected:
            return
        backup_name = selected[0]
        game = self._get_game(game_name)
        if not game:
            return messagebox.showerror("错误", "未找到游戏配置！")
        save_dir = game["save"]

        if not messagebox.askyesno("确认恢复", f"确定要用 WebDAV 备份【{backup_name}】覆盖本地存档吗？\n当前本地存档将被删除！"):
            return

        self._disable_action_buttons("下载中…")
        def run():
            wc = self.get_webdav_client()
            if not wc:
                raise Exception("无法连接 WebDAV")
            remote_dir = f"{game_name}/{backup_name}"
            tmp_dir = save_dir + ".tmp"
            if os.path.exists(tmp_dir):
                rmtree_safe(tmp_dir)
            file_count = wc.download_dir(remote_dir, tmp_dir)
            if file_count == 0:
                rmtree_safe(tmp_dir)
                raise Exception("WebDAV 下载失败：未获取到任何文件")
            if os.path.exists(save_dir):
                rmtree_safe(save_dir)
            os.rename(tmp_dir, save_dir)
            apply_backup_mtime(backup_name, save_dir)
            self._last_restore_msg = f"游戏【{game_name}】已恢复至 WebDAV 备份版本 {backup_name}"

        def on_done():
            self._enable_action_buttons()
            self.log(f"🔄 ✅ {game_name}: 已从 WebDAV 备份【{backup_name}】恢复")
            self.refresh_list()
            if parent_win:
                parent_win.destroy()
            msg = getattr(self, '_last_restore_msg', '恢复成功')
            self._last_restore_msg = None
            messagebox.showinfo("恢复成功", msg)

        def on_error():
            self._enable_action_buttons()
            self.log(f"🔄 ❌ {game_name}: WebDAV 恢复失败")

        def task():
            try:
                run()
                self.root.after(0, on_done)
            except Exception as e:
                self.log(f"🔄 ❌ {game_name}: WebDAV 恢复失败 ({e})")
                self.root.after(0, lambda: messagebox.showerror("错误", str(e)))
                self.root.after(0, on_error)

        self.worker.submit(task)

    def _delete_webdav_backup(self, game_name, tree_wd, wd_backups):
        selected = tree_wd.selection()
        if not selected:
            return
        backup_name = selected[0]
        if messagebox.askyesno("确认删除", f"确定要删除 WebDAV 备份【{backup_name}】吗？"):
            try:
                wc = self.get_webdav_client()
                if wc:
                    wc.delete_path(f"{game_name}/{backup_name}")
                    tree_wd.delete(selected)
                    self.log(f"🗑 {game_name}: 已删除 WebDAV 备份 {backup_name}")
            except Exception as e:
                messagebox.showerror("错误", str(e))

    def restore_specific_backup(self, game_name, game_cloud_dir, tree_hist, parent_win=None):
        selected = tree_hist.selection()
        if not selected:
            return
        backup_name = selected[0]
        backup_path = os.path.join(game_cloud_dir, backup_name)
        game = self._get_game(game_name)
        if not game:
            return messagebox.showerror("错误", "未找到游戏配置！")
        save_dir = game["save"]

        if not messagebox.askyesno("确认恢复", f"确定要用备份【{backup_name}】覆盖本地存档吗？\n当前本地存档将被删除！"):
            return

        self._disable_action_buttons("恢复中…")
        def task():
            try:
                if os.path.exists(save_dir):
                    rmtree_safe(save_dir)
                shutil.copytree(backup_path, save_dir)

                def on_done():
                    self._enable_action_buttons()
                    self.log(f"🔄 ✅ {game_name}: 已从历史备份【{backup_name}】恢复")
                    self.refresh_list()
                    if parent_win:
                        parent_win.destroy()
                    messagebox.showinfo("恢复成功", f"游戏【{game_name}】已恢复至备份版本 {backup_name}")
                self.root.after(0, on_done)
            except Exception as e:
                self.log(f"🔄 ❌ {game_name}: 恢复失败 ({e})")
                self.root.after(0, lambda: (self._enable_action_buttons(), messagebox.showerror("错误", str(e))))

        self.worker.submit(task)

    def delete_single_backup(self, game_name, game_cloud_dir, tree_hist):
        selected = tree_hist.selection()
        if not selected:
            return
        backup_name = selected[0]
        backup_path = os.path.join(game_cloud_dir, backup_name)
        if messagebox.askyesno("确认删除", f"确定要删除备份【{backup_name}】吗？"):
            try:
                rmtree_safe(backup_path)
                tree_hist.delete(selected)
                self.log(f"🗑 {game_name}: 已删除备份 {backup_name}")
            except Exception as e:
                messagebox.showerror("错误", str(e))

    def delete_all_backups(self, game_name, game_cloud_dir, hist_win):
        if messagebox.askyesno("危险操作", f"确定要删除游戏【{game_name}】的所有云端备份吗？此操作不可恢复！"):
            try:
                rmtree_safe(game_cloud_dir)
                self.log(f"🗑 {game_name}: 已清空所有云端备份")
                hist_win.destroy()
                self.refresh_list()
            except Exception as e:
                messagebox.showerror("错误", str(e))

    def batch_delete_backups(self, game_name, game_cloud_dir, tree_hist):
        """批量删除选中的 OneDrive 备份"""
        selected = tree_hist.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中要删除的备份（可多选）！")
        count = len(selected)
        if not messagebox.askyesno("确认删除", f"确定要删除选中的 {count} 个备份吗？"):
            return
        deleted = 0
        for backup_name in selected:
            backup_path = os.path.join(game_cloud_dir, backup_name)
            try:
                if os.path.exists(backup_path):
                    rmtree_safe(backup_path)
                tree_hist.delete(backup_name)
                deleted += 1
            except Exception as e:
                self.log(f"🗑 ❌ {game_name}: 删除备份 {backup_name} 失败 ({e})")
        self.log(f"🗑 {game_name}: 已批量删除 {deleted} 个备份")
        self.refresh_list()

    def batch_download_backups(self, game_name, game_cloud_dir, tree_hist, hist_win):
        """批量下载选中的 OneDrive 备份到本地指定目录"""
        selected = tree_hist.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中要下载的备份（可多选）！")
        target_dir = filedialog.askdirectory(title="选择下载保存目录")
        if not target_dir:
            return
        count = 0
        for backup_name in selected:
            backup_path = os.path.join(game_cloud_dir, backup_name)
            try:
                dest = os.path.join(target_dir, backup_name)
                if os.path.exists(dest):
                    rmtree_safe(dest)
                shutil.copytree(backup_path, dest)
                count += 1
            except Exception as e:
                self.log(f"⬇ ❌ {game_name}: 下载备份 {backup_name} 失败 ({e})")
        self.log(f"⬇ {game_name}: 已批量下载 {count} 个备份到 {target_dir}")
        messagebox.showinfo("完成", f"已下载 {count} 个备份。")

    def _batch_delete_webdav_backups(self, game_name, tree_wd, wd_backups):
        """批量删除选中的 WebDAV 备份"""
        selected = tree_wd.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中要删除的备份（可多选）！")
        count = len(selected)
        if not messagebox.askyesno("确认删除", f"确定要删除选中的 {count} 个 WebDAV 备份吗？"):
            return
        deleted = 0
        wc = self.get_webdav_client()
        if not wc:
            return
        for backup_name in selected:
            try:
                wc.delete_path(f"{game_name}/{backup_name}")
                tree_wd.delete(backup_name)
                deleted += 1
            except Exception as e:
                self.log(f"🗑 ❌ {game_name}: 删除 WebDAV 备份 {backup_name} 失败 ({e})")
        self.log(f"🗑 {game_name}: 已批量删除 {deleted} 个 WebDAV 备份")

    def _batch_download_webdav_backups(self, game_name, tree_wd, wd_backups, hist_win):
        """批量下载选中的 WebDAV 备份到本地指定目录"""
        selected = tree_wd.selection()
        if not selected:
            return messagebox.showinfo("提示", "请先选中要下载的备份（可多选）！")
        target_dir = filedialog.askdirectory(title="选择下载保存目录")
        if not target_dir:
            return
        wc = self.get_webdav_client()
        if not wc:
            return
        count = 0
        for backup_name in selected:
            try:
                dest = os.path.join(target_dir, backup_name)
                os.makedirs(dest, exist_ok=True)
                wc.download_dir(f"{game_name}/{backup_name}", dest)
                count += 1
            except Exception as e:
                self.log(f"⬇ ❌ {game_name}: 下载 WebDAV 备份 {backup_name} 失败 ({e})")
        self.log(f"⬇ {game_name}: 已批量下载 {count} 个 WebDAV 备份到 {target_dir}")
        messagebox.showinfo("完成", f"已下载 {count} 个备份。")

    # ================= 导入/导出 =================
    def export_single_config(self):
        selected = self.tree.selection()
        if not selected:
            return
        if len(selected) == 1:
            game_name = self.tree.item(selected[0], 'values')[0]
            game = self._get_game(game_name)
            if not game:
                return
            file_path = filedialog.asksaveasfilename(defaultextension=".json",
                                                     filetypes=[("JSON Config", "*.json")],
                                                     initialfile=f"GameCloudSync_{game_name}.json",
                                                     title="导出单个游戏配置")
            if file_path:
                try:
                    export_data = [{
                        "name": game["name"],
                        "save": game["save"],
                        "paused": game.get("paused", False),
                        "backup_methods": game.get("backup_methods", ["onedrive"]),
                        "max_backups": game.get("max_backups", self.default_max_backups)
                    }]
                    with open(file_path, "w", encoding="utf-8") as f:
                        json.dump(export_data, f, indent=4, ensure_ascii=False)
                    self.log(f"📦 单独配置已导出至: {file_path}")
                    messagebox.showinfo("成功", f"【{game_name}】配置导出成功！")
                except Exception as e:
                    messagebox.showerror("错误", f"导出失败: {e}")
        else:
            # 多选导出：合并为一个配置文件
            file_path = filedialog.asksaveasfilename(defaultextension=".json",
                                                     filetypes=[("JSON Config", "*.json")],
                                                     initialfile="GameCloudSync_Selected.json",
                                                     title="导出所选游戏配置")
            if not file_path:
                return
            count = 0
            export_list = []
            for item in selected:
                game_name = self.tree.item(item, 'values')[0]
                game = self._get_game(game_name)
                if not game:
                    continue
                export_list.append({
                    "name": game["name"],
                    "save": game["save"],
                    "paused": game.get("paused", False),
                    "backup_methods": game.get("backup_methods", ["onedrive"]),
                    "max_backups": game.get("max_backups", self.default_max_backups)
                })
                count += 1
            try:
                with open(file_path, "w", encoding="utf-8") as f:
                    json.dump(export_list, f, indent=4, ensure_ascii=False)
                self.log(f"📦 已合并导出 {count} 个游戏配置到: {file_path}")
                messagebox.showinfo("完成", f"已导出 {count} 个游戏配置。")
            except Exception as e:
                messagebox.showerror("错误", f"导出失败: {e}")

    def export_config(self):
        if not self.games_config:
            return messagebox.showwarning("提示", "当前列表为空！")
        file_path = filedialog.asksaveasfilename(defaultextension=".json",
                                                 filetypes=[("JSON Config", "*.json")],
                                                 initialfile="GameCloudSync_Config.json",
                                                 title="导出配置文件")
        if file_path:
            try:
                with self._config_lock:
                    data = [{
                        "name": g["name"],
                        "save": g["save"],
                        "paused": g.get("paused", False),
                        "backup_methods": g.get("backup_methods", ["onedrive"])
                    } for g in self.games_config]
                with open(file_path, "w", encoding="utf-8") as f:
                    json.dump(data, f, indent=4, ensure_ascii=False)
                self.log(f"📦 全部配置已导出至: {file_path}")
                messagebox.showinfo("成功", "配置导出成功！")
            except Exception as e:
                messagebox.showerror("错误", f"导出失败: {e}")

    def import_config(self):
        file_path = filedialog.askopenfilename(filetypes=[("JSON Config", "*.json")],
                                               title="选择要导入的配置文件")
        if not file_path:
            return
        try:
            with open(file_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict):
                imported_games = data.get("games", [])
                # 也尝试加载 webdav 配置和默认备份数量
                if data.get("webdav_url") and not self.webdav_url:
                    if messagebox.askyesno("导入 WebDAV 配置", "配置文件中包含 WebDAV 设置，是否一同导入？"):
                        self.webdav_url = data.get("webdav_url", "")
                        self.webdav_username = data.get("webdav_username", "")
                        self.webdav_password = data.get("webdav_password", "")
                if data.get("default_max_backups"):
                    self.default_max_backups = data.get("default_max_backups", DEFAULT_MAX_BACKUPS)
                if data.get("default_download_source"):
                    self.default_download_source = data.get("default_download_source", "ask")
            elif isinstance(data, list):
                imported_games = data
            else:
                return messagebox.showerror("错误", "配置文件格式不正确！")
            if not imported_games:
                return messagebox.showerror("错误", "配置文件为空！")

            # 询问云盘根目录
            if self.cloud_root:
                if messagebox.askyesno("更新云盘目录", f"当前 OneDrive 云盘根目录为：\n{self.cloud_root}\n\n是否替换为新目录？"):
                    new_cloud = filedialog.askdirectory(title="选择新的 OneDrive 云盘总目录")
                    if new_cloud:
                        self.cloud_root = new_cloud
            else:
                new_cloud = filedialog.askdirectory(title="选择 OneDrive 云盘总目录")
                if new_cloud:
                    self.cloud_root = new_cloud

            imported_count = 0
            fixed_paths = 0
            for ig in imported_games:
                name = ig.get("name")
                save_dir = ig.get("save")
                if not name or not save_dir:
                    continue
                # 自动适配当前用户名
                fixed_save = fix_save_path(save_dir)
                if fixed_save != save_dir:
                    fixed_paths += 1
                save_dir = fixed_save
                with self._config_lock:
                    if any(g["name"] == name for g in self.games_config):
                        if not messagebox.askyesno("重复游戏", f"游戏【{name}】已存在，是否覆盖？"):
                            continue
                        self.games_config = [g for g in self.games_config if g["name"] != name]
                    self.games_config.append({
                        "name": name,
                        "save": save_dir,
                        "paused": ig.get("paused", False),
                        "backup_methods": ig.get("backup_methods", ["onedrive"]),
                        "max_backups": ig.get("max_backups", self.default_max_backups)
                    })
                imported_count += 1

            self.save_config()
            if self.auto_backup_config_to_cloud:
                self.backup_config_to_cloud()
            self.update_cloud_status_label()
            self.refresh_list()
            if self.is_monitoring:
                self.stop_monitoring()
                self.start_monitoring()
            if fixed_paths:
                self.log(f"📥 已自动适配 {fixed_paths} 个路径中的用户名，共导入 {imported_count} 个游戏配置。")
            else:
                self.log(f"📥 成功导入 {imported_count} 个游戏配置。")
            messagebox.showinfo("导入成功", f"已导入 {imported_count} 个游戏。")
        except Exception as e:
            messagebox.showerror("错误", f"导入失败: {e}")

    # ================= 从云端恢复配置 =================
    def restore_config_from_cloud(self):
        """从云端配置备份文件中选择并恢复游戏配置（仅添加新游戏，不修改已有）"""
        cloud_configs = self._list_cloud_configs()
        if not cloud_configs:
            return messagebox.showinfo("提示", "未在云端找到任何配置备份。\n请先在全局设置中开启\"自动保存游戏配置到云端\"。")

        # 弹出选择窗口
        choice_win = tk.Toplevel(self.root)
        choice_win.title("选择云端配置备份")
        choice_win.geometry("520x380")
        choice_win.resizable(False, False)
        choice_win.grab_set()
        choice_win.configure(bg="#F5F6FA")
        center_window(choice_win)
        choice_win.result = None

        content = ttk.Frame(choice_win, padding=15)
        content.pack(fill="both", expand=True)
        ttk.Label(content, text="请选择要恢复的云端配置备份：",
                 font=(self._font_family, 10, 'bold')).pack(anchor="w", pady=(0, 10))

        # 构建列表
        list_frame = ttk.Frame(content)
        list_frame.pack(fill="both", expand=True)

        tree = ttk.Treeview(list_frame, columns=("source", "date", "count"), show="headings",
                           selectmode="browse", height=8)
        tree.heading("source", text="来源")
        tree.heading("date", text="备份时间")
        tree.heading("count", text="游戏数量")
        tree.column("source", width=80, anchor="center")
        tree.column("date", width=280)
        tree.column("count", width=80, anchor="center")
        tree.pack(side="left", fill="both", expand=True)

        scrollbar = ttk.Scrollbar(list_frame, orient="vertical", command=tree.yview)
        scrollbar.pack(side="right", fill="y")
        tree.configure(yscrollcommand=scrollbar.set)

        for source, fname, games in cloud_configs:
            try:
                ts_str = fname[7:]  # "config_" 之后
                if ts_str.endswith(".json"):
                    ts_str = ts_str[:-5]
                dt = datetime.strptime(ts_str, "%Y%m%d_%H%M%S")
                date_str = dt.strftime("%Y-%m-%d %H:%M:%S")
            except Exception:
                date_str = fname
            tree.insert("", "end", values=(source, date_str, f"{len(games)} 个"), iid=fname)

        # 双击选择
        def on_select():
            sel = tree.selection()
            if not sel:
                return
            fname = sel[0]
            for s, fn, gs in cloud_configs:
                if fn == fname:
                    choice_win.result = (s, fn, gs)
                    break
            choice_win.destroy()

        tree.bind("<Double-1>", lambda e: on_select())

        # 按钮
        btn_frame = ttk.Frame(content)
        btn_frame.pack(pady=(10, 0))
        create_button(btn_frame, text="选择此备份", style='Success.TButton',
                   command=on_select, width=120,
                   pack_side="left", pack_padx=(0, 10))
        create_button(btn_frame, text="取消", style='Secondary.TButton',
                   command=choice_win.destroy, width=100,
                   pack_side="left")

        self.root.wait_window(choice_win)
        result = getattr(choice_win, 'result', None)
        if not result:
            return

        source_label, filename, cloud_games = result
        local_names = set(self._get_game_names())
        cloud_names = {g["name"] for g in cloud_games}
        new_names = cloud_names - local_names
        existing_names = cloud_names & local_names

        if not new_names:
            return messagebox.showinfo("无需下载",
                f"云端配置 ({source_label}) 中未包含新游戏。\n\n"
                f"云端共 {len(cloud_games)} 个游戏，本地已有 {len(existing_names)} 个匹配。\n"
                f"无需下载。")

        new_games = [g for g in cloud_games if g["name"] in new_names]
        existing = [g for g in cloud_games if g["name"] in existing_names]

        # 显示对比
        diff = f"来源: {source_label}  —  {filename}\n\n"
        diff += f"📋 云端配置中共有 {len(cloud_games)} 个游戏\n\n"
        diff += f"🆕 将新增 {len(new_games)} 个游戏:\n"
        for g in new_games:
            diff += f"   ➕ {g['name']}\n"
            diff += f"      存档: {g.get('save', '无')}\n"
        if existing:
            diff += f"\n✅ {len(existing)} 个已存在 (保持不变):\n"
            for g in existing[:5]:
                diff += f"   ✓ {g['name']}\n"
            if len(existing) > 5:
                diff += f"   ... 等共 {len(existing)} 个\n"
        diff += "\n⚠ 本次只添加新游戏，不会修改任何已有游戏配置。"

        if not messagebox.askyesno("云端配置对比", diff):
            return

        # 执行合并
        imported = 0
        for ng in new_games:
            with self._config_lock:
                if not any(g["name"] == ng["name"] for g in self.games_config):
                    self.games_config.append({
                        "name": ng["name"],
                        "save": fix_save_path(ng.get("save", "")),
                        "paused": ng.get("paused", False),
                        "backup_methods": ng.get("backup_methods", ["onedrive"]),
                        "max_backups": ng.get("max_backups", self.default_max_backups)
                    })
                    imported += 1

        if imported > 0:
            self.save_config()
            self.refresh_list()
            if self.is_monitoring:
                self.stop_monitoring()
                self.start_monitoring()
            self.log(f"☁ 从云端配置 ({source_label}) 导入 {imported} 个新游戏。")
            messagebox.showinfo("完成", f"已从云端配置导入 {imported} 个新游戏。")

    # ================= 监控控制 =================
    def auto_start(self):
        # 自动测试 WebDAV 连接
        if self.webdav_url:
            threading.Thread(target=self._auto_test_webdav, daemon=True).start()
        if self.games_config:
            self.start_monitoring()
            if self.auto_sync_on_startup:
                self.log("🔍 正在检查各游戏存档同步状态...")
                threading.Thread(target=self._auto_sync_all_games, daemon=True).start()
        else:
            self.log("💡 欢迎！列表为空，请先添加游戏或导入配置。")

        # 启动时检测云端是否有更新的游戏配置
        if self.auto_backup_config_to_cloud:
            self.log("☁ 正在检查云端游戏配置...")
            threading.Thread(target=self._check_cloud_config, daemon=True).start()

    def _auto_sync_all_games(self):
        """启动时检查所有游戏，本地较新则上传，云端较新则下载"""
        games = self._get_games_snapshot()
        for game in games:
            if game.get("paused", False):
                continue
            game_name = game["name"]
            save_dir = game["save"]
            methods = game.get("backup_methods", ["onedrive"])
            if not methods:
                continue
            if not os.path.isdir(save_dir):
                continue

            local_time = get_latest_mtime(save_dir)
            if local_time == 0:
                continue

            # 确定最佳下载来源
            source = self._find_latest_backup(game_name, methods)
            cloud_time = 0
            if source:
                _, source_key, source_path = source
                # 从目录名解析云端的 mtime
                if source_key == "onedrive":
                    cloud_time = get_latest_mtime(source_path)
                else:
                    # WebDAV: 从目录名提取 mtime
                    try:
                        _, bk_mtime = parse_backup_name(source_path)
                        cloud_time = bk_mtime
                    except Exception:
                        pass

            # 本地较新 → 上传
            if local_time - cloud_time > 3:
                self.log(f"📤 启动同步: {game_name} 本地较新，自动备份...")
                self.worker.submit(lambda gn=game_name, sd=save_dir: self.perform_backup(gn, sd))
                time.sleep(1)  # 错开多个游戏的备份时机
            # 云端较新 → 下载
            elif cloud_time - local_time > 3 and source:
                self.log(f"📥 启动同步: {game_name} 云端较新，自动下载...")
                self.worker.submit(lambda gn=game_name, sd=save_dir, src=source: self._do_restore(gn, sd, src))
                time.sleep(1)
            else:
                self.log(f"✅ 启动同步: {game_name} 已完全同步，跳过")

    def _check_cloud_config(self):
        """后台线程：检测云端游戏配置文件，若有新游戏则提示用户下载"""
        local_names = set(self._get_game_names())
        cloud_configs = self._list_cloud_configs()
        if not cloud_configs:
            self.log("☁ 云端暂无游戏配置备份，跳过检查（添加游戏后会自动备份）。")
            return

        # 取最新的云端备份
        source_label, filename, cloud_games = cloud_configs[0]
        cloud_names = {g["name"] for g in cloud_games}
        new_names = cloud_names - local_names

        if not new_names:
            self.log(f"☁ 云端游戏配置 ({source_label}, {len(cloud_games)} 个游戏) 与本地一致，无需更新。")
            return

        new_games = [g for g in cloud_games if g["name"] in new_names]
        self.root.after(500, lambda: self._prompt_cloud_config_merge(
            source_label, filename, cloud_games, new_games))

    def _prompt_cloud_config_merge(self, source_label, filename, cloud_games, new_games):
        """主线程弹窗：提示用户合并云端游戏配置（仅添加新游戏）"""
        local_names = set(self._get_game_names())
        existing = [g for g in cloud_games if g["name"] in local_names]

        msg = f"☁ 云端 ({source_label}) 有新的游戏配置\n"
        msg += f"备份文件: {filename}\n\n"
        msg += f"🆕 将新增 {len(new_games)} 个游戏:\n"
        for g in new_games:
            msg += f"   ➕ {g['name']}\n"
        if existing:
            msg += f"\n✅ {len(existing)} 个已存在 (保持不变)\n"
        msg += "\n⚠ 只添加新游戏，不会修改已有配置。\n是否下载？"

        if messagebox.askyesno("云端配置更新", msg):
            imported = 0
            for ng in new_games:
                with self._config_lock:
                    if not any(g["name"] == ng["name"] for g in self.games_config):
                        self.games_config.append({
                            "name": ng["name"],
                            "save": fix_save_path(ng.get("save", "")),
                            "paused": ng.get("paused", False),
                            "backup_methods": ng.get("backup_methods", ["onedrive"]),
                            "max_backups": ng.get("max_backups", self.default_max_backups)
                        })
                        imported += 1
            if imported > 0:
                self.save_config()
                self.refresh_list()
                if self.is_monitoring:
                    self.stop_monitoring()
                    self.start_monitoring()
                self.log(f"☁ 从云端配置导入 {imported} 个新游戏。")
                messagebox.showinfo("完成", f"已从云端配置导入 {imported} 个新游戏。")

    # ---- 云端配置辅助 ----
    def _list_cloud_configs(self):
        """列出所有可用的云端配置备份，返回 [(source, filename, games_list), ...] 按时间倒序"""
        results = []

        # OneDrive
        if self.cloud_root:
            cf_dir = os.path.join(self.cloud_root, "云存档配置备份")
            if os.path.isdir(cf_dir):
                for fn in os.listdir(cf_dir):
                    if fn.startswith("config_") and fn.endswith(".json"):
                        fp = os.path.join(cf_dir, fn)
                        try:
                            with open(fp, "r", encoding="utf-8") as f:
                                data = json.load(f)
                            results.append(("OneDrive", fn, data if isinstance(data, list) else []))
                        except Exception:
                            pass

        # WebDAV
        if self.webdav_url:
            wc = self.get_webdav_client()
            if wc:
                try:
                    items = wc.propfind("云存档配置备份")
                    cf_files = [(n, h) for h, n in items if n.startswith("config_")]
                    cf_files.sort(key=lambda x: x[0], reverse=True)
                    for name, _ in cf_files[:5]:  # 最多取5个避免过多请求
                        try:
                            import tempfile
                            tmpdir = tempfile.mkdtemp()
                            try:
                                tmp_fp = os.path.join(tmpdir, name)
                                if wc.get_file(f"云存档配置备份/{name}", tmp_fp):
                                    with open(tmp_fp, "r", encoding="utf-8") as f:
                                        data = json.load(f)
                                    results.append(("WebDAV", name, data if isinstance(data, list) else []))
                            finally:
                                rmtree_safe(tmpdir)
                        except Exception:
                            pass
                except Exception:
                    pass

        # 按文件名排序（时间倒序）
        results.sort(key=lambda x: x[1], reverse=True)
        return results

    def _auto_test_webdav(self):
        """后台测试 WebDAV 连接并将结果输出到日志"""
        try:
            wc = WebDAVClient(self.webdav_url, self.webdav_username, self.webdav_password)
            if wc.test_connection():
                self.log(f"☁ WebDAV 连接成功: {self.webdav_url}")
            else:
                self.log(f"☁ WebDAV 连接失败，请检查地址和账号密码: {self.webdav_url}")
        except Exception as e:
            self.log(f"☁ WebDAV 连接异常: {e}")

    def toggle_monitoring(self):
        if self.is_monitoring:
            self.stop_monitoring()
        else:
            self.start_monitoring()

    def start_monitoring(self, icon=None, item=None):
        if not self.games_config:
            return
        if self.is_monitoring:
            return

        # 检查是否有任何可用的备份目标
        with self._config_lock:
            has_od = any("onedrive" in g.get("backup_methods", ["onedrive"]) for g in self.games_config)
            has_wd = any("webdav" in g.get("backup_methods", []) for g in self.games_config)
        if has_od and not self.cloud_root:
            self.log("❌ 有游戏启用了 OneDrive 备份但未设置云盘目录！")
            return
        if has_wd and not self.webdav_url:
            self.log("❌ 有游戏启用了 WebDAV 备份但未配置服务器！")
            return
        if not has_od and not has_wd:
            self.log("❌ 没有游戏设置备份方式，请在游戏上右键设置。")
            return

        self.observers = []
        monitored_count = 0
        games = self._get_games_snapshot()
        for game in games:
            if game.get("paused", False):
                continue
            if not game.get("backup_methods"):
                continue
            if not os.path.exists(game["save"]):
                try:
                    os.makedirs(game["save"], exist_ok=True)
                except Exception:
                    pass
            handler = SaveHandler(game["name"], game["save"], self._monitor_backup, self.log)
            observer = Observer()
            try:
                observer.schedule(handler, game["save"], recursive=True)
                observer.start()
                self.observers.append(observer)
                monitored_count += 1
            except Exception as e:
                self.log(f"⚠ {game['name']} 监控失败: {e}")

        self.is_monitoring = True
        self.btn_toggle.configure(text="⏹ 停止监控", bg='#EF4444', fg='#FFFFFF')
        self.tray_icon.icon = self.icon_active
        self.tray_icon.title = f"云存档管家 (监控中: {monitored_count} 个)"
        self.log(f"✅ 已启动，当前监控 {monitored_count} 个游戏。")

    def stop_monitoring(self, icon=None, item=None):
        if not self.is_monitoring:
            return
        for observer in self.observers:
            observer.stop()
        for observer in self.observers:
            observer.join()
        self.observers = []
        self.is_monitoring = False
        self.btn_toggle.configure(text="▶ 启动监控", bg='#10B981', fg='#FFFFFF')
        self.tray_icon.icon = self.icon_inactive
        self.tray_icon.title = "云存档管家 (未监控)"
        self.log("🛑 所有监控已暂停。")
