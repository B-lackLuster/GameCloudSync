"""
基于标准库的 WebDAV 客户端，支持 PROPFIND / MKCOL / PUT / GET / DELETE
"""

import os
import base64
import urllib.request
import urllib.error
import urllib.parse
import http.client
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed

from constants import WEBDAV_WORKERS

# 允许 http.client 使用 WebDAV 自定义方法（Python 3.14+ 已移除 _METHODS，无需此补丁）
try:
    http.client._METHODS.update({'PROPFIND', 'MKCOL', 'LOCK', 'UNLOCK', 'MOVE', 'COPY', 'PROPPATCH'})
except AttributeError:
    pass


class WebDAVClient:
    """基于标准库的 WebDAV 客户端，支持 PROPFIND / MKCOL / PUT / GET / DELETE"""

    def __init__(self, url, username, password, log_cb=None, debug_cb=None):
        self.url = url.rstrip('/')
        self.username = username
        self.password = password
        self._auth_header = 'Basic ' + base64.b64encode(
            f'{username}:{password}'.encode('utf-8')
        ).decode('ascii')
        self._log = log_cb
        self._dbg = debug_cb
        # 提取 URL 路径前缀，用于匹配服务器返回的相对路径 href
        self._url_path = urllib.parse.urlparse(self.url).path.rstrip('/')

    def _dlog(self, msg):
        """调试日志：仅在传入 debug_cb 时输出，用于 suppress 细节日志"""
        if self._dbg:
            self._dbg(msg)

    def _request(self, method, path, data=None, extra_headers=None):
        """发送 HTTP 请求到 WebDAV 服务器"""
        # URL-encode 中文等非 ASCII 字符，逐段编码保留斜杠
        parts = path.lstrip('/').split('/')
        encoded_path = '/'.join(urllib.parse.quote(p, safe='') for p in parts)
        full_url = f"{self.url}/{encoded_path}"
        req = urllib.request.Request(full_url, data=data, method=method)
        req.add_header('Authorization', self._auth_header)
        req.add_header('User-Agent', 'GameCloudSync/2.0')
        if extra_headers:
            for k, v in extra_headers.items():
                req.add_header(k, v)

        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return resp.status, resp.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def test_connection(self):
        """测试 WebDAV 连接是否正常"""
        status, _ = self._request('PROPFIND', '', extra_headers={'Depth': '0'})
        return status in (207, 200)

    def mkcol(self, path):
        """递归创建远程目录"""
        parts = [p for p in path.strip('/').split('/') if p]
        current = ''
        for part in parts:
            current = f"{current}/{part}" if current else part
            status, _ = self._request('MKCOL', current)
            if status not in (201, 405, 301):
                return False
        return True

    def propfind(self, path, depth='1'):
        """列出远程目录内容，返回 [(href, display_name), ...]"""
        status, body = self._request('PROPFIND', path, extra_headers={'Depth': depth})
        if status != 207:
            if self._log:
                self._log(f"🌐 WebDAV PROPFIND {path}: HTTP {status}")
            return []
        results = []
        try:
            root = ET.fromstring(body)
            ns = '{DAV:}'
            for resp in root.findall(f'{ns}response'):
                href_el = resp.find(f'{ns}href')
                # 可能有多个 propstat，找一个包含 prop 的（跳过 404 等状态）
                prop = None
                for ps in resp.findall(f'{ns}propstat'):
                    prop = ps.find(f'{ns}prop')
                    if prop is not None:
                        break
                if href_el is None or prop is None:
                    continue
                href = href_el.text or ''
                name_el = prop.find(f'{ns}displayname')
                name = name_el.text if name_el is not None else href.rstrip('/').split('/')[-1]
                if name:
                    results.append((href, name))
        except ET.ParseError as e:
            if self._log:
                self._log(f"🌐 WebDAV PROPFIND {path}: XML 解析失败 ({e})")
        self._dlog(f"🌐 WebDAV PROPFIND {path}: {len(results)} 项")
        return results

    def list_dir(self, path):
        """列出远程目录中的子项名称"""
        items = self.propfind(path)
        server_path = f"{self._url_path}/{path.lstrip('/')}".rstrip('/')
        names = []
        for href, name in items:
            decoded = urllib.request.unquote(href)
            href_path = urllib.parse.urlparse(decoded).path.rstrip('/')
            if href_path == server_path:
                continue
            names.append(name)
        return names

    def put_file(self, local_path, remote_path):
        """上传单个文件"""
        with open(local_path, 'rb') as f:
            data = f.read()
        status, _ = self._request('PUT', remote_path, data=data)
        return status in (200, 201, 204)

    def get_file(self, remote_path, local_path):
        """下载单个文件"""
        status, body = self._request('GET', remote_path)
        if status == 200:
            os.makedirs(os.path.dirname(local_path), exist_ok=True)
            with open(local_path, 'wb') as f:
                f.write(body)
            return True
        return False

    def delete_path(self, path):
        """删除远程文件或目录"""
        status, _ = self._request('DELETE', path)
        return status in (200, 204)

    def upload_dir(self, local_dir, remote_dir):
        """并行递归上传整个目录"""
        if not self.mkcol(remote_dir):
            if self._log:
                self._log(f"🌐 WebDAV MKCOL {remote_dir}: 创建目录失败")
            return False
        # 第一遍：创建目录结构 + 收集文件任务
        file_tasks = []
        for walk_root, dirs, files in os.walk(local_dir):
            rel_path = os.path.relpath(walk_root, local_dir)
            if rel_path == '.':
                current_remote = remote_dir
            else:
                current_remote = f"{remote_dir}/{rel_path.replace(os.sep, '/')}"
                if not self.mkcol(current_remote):
                    if self._log:
                        self._log(f"🌐 WebDAV MKCOL {current_remote}: 创建目录失败")
                    return False
            for fname in files:
                file_tasks.append((
                    os.path.join(walk_root, fname),
                    f"{current_remote}/{fname}"
                ))
        if not file_tasks:
            return True

        # 第二遍：并行上传文件
        pool = ThreadPoolExecutor(max_workers=WEBDAV_WORKERS)
        try:
            futures = {pool.submit(self.put_file, lp, rp): rp for lp, rp in file_tasks}
            for future in as_completed(futures):
                try:
                    if not future.result():
                        pool.shutdown(wait=False, cancel_futures=True)
                        return False
                except Exception:
                    pool.shutdown(wait=False, cancel_futures=True)
                    return False
            return True
        finally:
            pool.shutdown(wait=False)

    def download_dir(self, remote_dir, local_dir):
        """并行递归下载整个目录，返回下载的文件数（0 表示未获取到任何文件）"""
        # Phase 1: 递归 PROPFIND 收集所有文件（快，只列目录）
        all_files = []
        self._collect_files(remote_dir, local_dir, all_files)
        if not all_files:
            self._log(f"🌐 WebDAV download_dir '{remote_dir}': 未收集到任何文件")
            return 0

        self._dlog(f"🌐 WebDAV download_dir '{remote_dir}': 发现 {len(all_files)} 个文件，开始下载...")

        # Phase 2: 创建所有本地子目录
        dirs_created = set()
        for _, local_path in all_files:
            d = os.path.dirname(local_path)
            if d not in dirs_created:
                os.makedirs(d, exist_ok=True)
                dirs_created.add(d)

        # Phase 3: 并行下载所有文件
        downloaded = 0
        pool = ThreadPoolExecutor(max_workers=WEBDAV_WORKERS)
        try:
            futures = {pool.submit(self.get_file, rp, lp): lp for rp, lp in all_files}
            for future in as_completed(futures):
                try:
                    if future.result():
                        downloaded += 1
                except Exception:
                    pass
            return downloaded
        finally:
            pool.shutdown(wait=False)

    def _collect_files(self, remote_dir, local_dir, result):
        """递归 PROPFIND，将 (remote_path, local_path) 收集到 result 列表中"""
        os.makedirs(local_dir, exist_ok=True)
        items = self.propfind(remote_dir)
        server_path = f"{self._url_path}/{remote_dir.lstrip('/')}".rstrip('/')

        self._dlog(f"🌐 _collect_files '{remote_dir}': {len(items)} 项")

        for href, name in items:
            decoded = urllib.request.unquote(href)
            href_path = urllib.parse.urlparse(decoded).path.rstrip('/')
            if href_path == server_path:
                self._dlog(f"🌐 _collect_files: 跳过自身 {href_path}")
                continue
            local_child = os.path.join(local_dir, name)
            if href.endswith('/'):
                sub_remote = f"{remote_dir.rstrip('/')}/{name}"
                self._collect_files(sub_remote, local_child, result)
            else:
                result.append((f"{remote_dir}/{name}", local_child))
                self._dlog(f"🌐 _collect_files: 发现 {remote_dir}/{name}")
