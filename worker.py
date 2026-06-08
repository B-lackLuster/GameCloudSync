"""
后台任务队列 + 文件监控 handler
"""

import os
import time
import threading
import queue
import traceback

from watchdog.events import FileSystemEventHandler

from constants import COOLDOWN_SECONDS


# ================= 后台任务队列 =================
class BackgroundWorker:
    """后台工作线程 + 任务队列，避免 I/O 阻塞 tkinter 主线程"""

    def __init__(self, root):
        self.root = root
        self._queue = queue.Queue()
        self._busy = False
        self._lock = threading.Lock()

    @property
    def busy(self):
        with self._lock:
            return self._busy

    def start(self):
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _run(self):
        while True:
            task = self._queue.get()
            if task is None:
                break
            with self._lock:
                self._busy = True
            try:
                task()
            except Exception:
                traceback.print_exc()
            finally:
                with self._lock:
                    self._busy = False

    def submit(self, fn, on_done=None):
        """提交任务到后台执行；on_done 通过 root.after 在主线程回调"""
        def wrapped():
            fn()
            if on_done:
                self.root.after(0, on_done)
        self._queue.put(wrapped)

    def try_submit(self, fn, on_done=None):
        """原子性地尝试提交任务：仅当空闲时提交，返回 True；
        繁忙时直接返回 False，避免 TOCTOU 竞态条件。"""
        def wrapped():
            fn()
            if on_done:
                self.root.after(0, on_done)
        with self._lock:
            if self._busy:
                return False
            self._busy = True  # 提前预约，防止并发的 try_submit 通过检查
        self._queue.put(wrapped)
        return True

    def shutdown(self):
        self._queue.put(None)


# --- 后台监控逻辑 ---
class SaveHandler(FileSystemEventHandler):
    """处理游戏存档文件的自动备份（支持 OneDrive + WebDAV）"""

    def __init__(self, game_name, save_dir, backup_callback, log_cb):
        self.game_name = game_name
        self.save_dir = save_dir
        self.backup_callback = backup_callback    # 签名: (game_name, save_dir)
        self.log_callback = log_cb
        self.last_backup_time = 0
        self.last_backup_mtime = 0

    def _get_max_mtime(self):
        """获取存档目录中最新的文件修改时间"""
        latest = 0
        if not os.path.exists(self.save_dir):
            return 0
        for root, _, files in os.walk(self.save_dir):
            for f in files:
                try:
                    mt = os.path.getmtime(os.path.join(root, f))
                    if mt > latest:
                        latest = mt
                except Exception:
                    pass
        return latest

    def backup(self):
        current_time = time.time()
        if current_time - self.last_backup_time < COOLDOWN_SECONDS:
            return
        current_mtime = self._get_max_mtime()
        if current_mtime <= self.last_backup_mtime:
            return
        self.last_backup_time = current_time
        self.last_backup_mtime = current_mtime
        self.backup_callback(self.game_name, self.save_dir)

    def on_modified(self, event):
        if not event.is_directory:
            self.backup()

    def on_created(self, event):
        if not event.is_directory:
            self.backup()
