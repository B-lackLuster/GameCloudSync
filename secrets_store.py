"""
WebDAV 密码安全存储。

设计目标：密码绝不以明文写入 config.json，且与当前 Windows 用户/机器绑定，
拷到别的设备或别的用户账户下无法解密。

实现策略（按优先级自动选择）：
  1. Windows DPAPI（ctypes 调用 CryptProtectData）
     - 优点：零额外依赖、零外部密钥文件、原生机器+用户绑定、打包无坑
     - 行为：当前 Windows 用户登录后才能解密；换用户/换机器即失效
  2. cryptography.Fernet（跨平台后备）
     - 密钥由机器标识（机器名 + 安装目录）派生，绑定到本机
     - 配合一个本地盐文件（.gcs_salt），避免纯靠机器名被暴力推算

对外只暴露四个函数：load_password / save_password / has_stored_password / clear_password。
所有函数对异常做容错：读取/解密失败一律视为“无密码”，调用方据此让用户重新输入。
"""

import os
import base64
import json

try:
    import ctypes
    import ctypes.wintypes
    _HAS_WIN = hasattr(ctypes, "windll") and hasattr(ctypes.windll, "crypt32")
except Exception:
    _HAS_WIN = False

# DPAPI 常量
# CRYPTPROTECT_UI_FORBIDDEN = 0x01 （不在解密时弹 Windows 对话框）
_DPAPI_FLAGS = 0x01


def _dpapi_protect(plain_bytes: bytes) -> bytes:
    """用 DPAPI 加密，返回密文 bytes。失败抛 OSError。"""
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.wintypes.DWORD),
                    ("pbData", ctypes.POINTER(ctypes.c_char))]

    in_blob = DATA_BLOB(len(plain_bytes),
                        ctypes.cast(ctypes.create_string_buffer(plain_bytes, len(plain_bytes)),
                                    ctypes.POINTER(ctypes.c_char)))
    out_blob = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(
            ctypes.byref(in_blob), None, None, None, None,
            _DPAPI_FLAGS, ctypes.byref(out_blob)):
        raise OSError("CryptProtectData failed")
    try:
        encrypted = ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out_blob.pbData)
    return encrypted


def _dpapi_unprotect(cipher_bytes: bytes) -> bytes:
    """用 DPAPI 解密，返回明文 bytes。失败抛 OSError。"""
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.wintypes.DWORD),
                    ("pbData", ctypes.POINTER(ctypes.c_char))]

    in_blob = DATA_BLOB(len(cipher_bytes),
                        ctypes.cast(ctypes.create_string_buffer(cipher_bytes, len(cipher_bytes)),
                                    ctypes.POINTER(ctypes.c_char)))
    out_blob = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(
            ctypes.byref(in_blob), None, None, None, None,
            _DPAPI_FLAGS, ctypes.byref(out_blob)):
        raise OSError("CryptUnprotectData failed")
    try:
        plain = ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out_blob.pbData)
    return plain


# ---- Fernet 后备实现 ----
_FERNET_KEY_CACHE = None


def _fernet_key(app_dir: str):
    """基于机器名 + 安装目录 + 本地盐文件派生 Fernet 密钥（缓存）。"""
    global _FERNET_KEY_CACHE
    if _FERNET_KEY_CACHE is not None:
        return _FERNET_KEY_CACHE

    from cryptography.hazmat.primitives.kdf.hkdf import HKDF
    from cryptography.hazmat.primitives import hashes

    salt_path = os.path.join(app_dir, ".gcs_salt")
    if os.path.exists(salt_path):
        with open(salt_path, "rb") as f:
            salt = f.read()
    else:
        salt = os.urandom(32)
        # 盐文件需要与机器绑定，能读到才解得开；写失败不影响本次加密
        try:
            with open(salt_path, "wb") as f:
                f.write(salt)
        except Exception:
            pass

    machine = os.environ.get("COMPUTERNAME") or os.uname().nodename if hasattr(os, "uname") else "unknown"
    material = f"{machine}|{app_dir}".encode("utf-8")
    hkdf = HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=b"GameCloudSync/webdav")
    _FERNET_KEY_CACHE = base64.urlsafe_b64encode(hkdf.derive(material))
    return _FERNET_KEY_CACHE


def _fernet_encrypt(plain: bytes, app_dir: str) -> bytes:
    from cryptography.fernet import Fernet
    return Fernet(_fernet_key(app_dir)).encrypt(plain)


def _fernet_decrypt(cipher: bytes, app_dir: str) -> bytes:
    from cryptography.fernet import Fernet
    return Fernet(_fernet_key(app_dir)).decrypt(cipher)


# ---- 凭据文件路径 ----
def _cred_file(app_dir: str) -> str:
    return os.path.join(app_dir, ".gcs_webdav.cred")


def _load_cred_json(app_dir: str):
    """读取凭据 JSON。返回 dict 或 None。"""
    path = _cred_file(app_dir)
    if not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


# ---- 公共 API ----
def has_stored_password(app_dir: str) -> bool:
    """是否已存储过密码（且加密方案匹配当前平台）。"""
    cred = _load_cred_json(app_dir)
    if not cred or "cipher" not in cred:
        return False
    scheme = cred.get("scheme", "dpapi")
    return (scheme == "dpapi" and _HAS_WIN) or scheme == "fernet"


def load_password(app_dir: str):
    """
    读取并解密密码。成功返回明文 str，失败（无凭据/解密失败/换机器）返回 None。
    调用方应在返回 None 时让用户重新输入。
    """
    cred = _load_cred_json(app_dir)
    if not cred or "cipher" not in cred:
        return None
    try:
        cipher = base64.b64decode(cred["cipher"])
        scheme = cred.get("scheme", "dpapi")
        if scheme == "dpapi" and _HAS_WIN:
            plain = _dpapi_unprotect(cipher)
        elif scheme == "fernet":
            plain = _fernet_decrypt(cipher, app_dir)
        else:
            return None
        return plain.decode("utf-8")
    except Exception:
        return None


def save_password(app_dir: str, password: str) -> bool:
    """
    加密并存储密码。成功返回 True，失败返回 False（调用方此时可回退明文或提示）。
    """
    if not password:
        return clear_password(app_dir)
    try:
        plain = password.encode("utf-8")
        if _HAS_WIN:
            cipher, scheme = _dpapi_protect(plain), "dpapi"
        else:
            cipher, scheme = _fernet_encrypt(plain, app_dir), "fernet"
        cred = {"scheme": scheme, "cipher": base64.b64encode(cipher).decode("ascii")}
        # 原子写：先临时文件再替换，避免中途崩溃导致凭据损坏
        path = _cred_file(app_dir)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cred, f)
        os.replace(tmp, path)
        return True
    except Exception:
        return False


def clear_password(app_dir: str) -> bool:
    """删除凭据文件。"""
    path = _cred_file(app_dir)
    try:
        if os.path.exists(path):
            os.remove(path)
        return True
    except Exception:
        return False
