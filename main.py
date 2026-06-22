"""
全自动多游戏云存档管家 - 程序入口
"""

import ctypes
import sys
import tkinter as tk

from constants import WAKEUP_FILE, MUTEX_NAME

# 单实例互斥锁：若已有实例运行则唤醒它并退出
mutex = ctypes.windll.kernel32.CreateMutexW(None, False, MUTEX_NAME)
if ctypes.windll.kernel32.GetLastError() == 183:
    try:
        with open(WAKEUP_FILE, "w") as f:
            f.write("wakeup")
    except Exception:
        pass
    sys.exit(0)

from app import CloudBackupApp

if __name__ == "__main__":
    root = tk.Tk()
    app = CloudBackupApp(root)
    root.mainloop()
