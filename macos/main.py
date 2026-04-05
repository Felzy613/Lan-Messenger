import base64
import importlib
import json
import os
import queue
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid
import webbrowser
from dataclasses import dataclass
from pathlib import Path
import tkinter as tk
from tkinter import ttk, messagebox, filedialog, simpledialog
from urllib.error import URLError
from urllib.parse import urljoin
from urllib.request import urlopen
from typing import Any, cast

from PIL import Image, ImageDraw
import pystray
from pystray import MenuItem as TrayItem

try:
    notification_module = importlib.import_module("plyer").notification
except Exception:
    notification_module = None

try:
    tkinterdnd2_module = importlib.import_module("tkinterdnd2")
    TkinterDnD = getattr(tkinterdnd2_module, "TkinterDnD", None)
    DND_FILES = getattr(tkinterdnd2_module, "DND_FILES", None)
except Exception:
    TkinterDnD = None
    DND_FILES = None

try:
    appkit_module = importlib.import_module("AppKit")
    NSApp = getattr(appkit_module, "NSApp", None)
    NSApplicationActivationPolicyAccessory = getattr(appkit_module, "NSApplicationActivationPolicyAccessory", None)
except Exception:
    NSApp = None
    NSApplicationActivationPolicyAccessory = None

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import x25519
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


APP_NAME = "LAN Messenger"
APP_VERSION = "1.2.0"
APP_TITLE = f"{APP_NAME} v{APP_VERSION}"
UPDATE_MANIFEST_FILENAME = "lan-messenger-update.json"
DISCOVERY_PORT = 54231
TCP_PORT = 54232
DISCOVERY_MULTICAST_GROUP = "239.255.42.99"
DISCOVERY_MULTICAST_TTL = 1
DISCOVERY_INTERVAL = 3
PEER_TIMEOUT = 30
BUFFER_SIZE = 64 * 1024
CONFIG_DIR = Path.home() / ".lan_messenger"
CONFIG_FILE = CONFIG_DIR / "config.json"
INBOX_DIR = CONFIG_DIR / "received"


def now() -> float:
    return time.time()


def b64e(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def b64d(value: str) -> bytes:
    return base64.b64decode(value.encode("ascii"))


def send_frame(sock: socket.socket, payload: dict) -> None:
    data = json.dumps(payload).encode("utf-8")
    sock.sendall(struct.pack("!I", len(data)))
    sock.sendall(data)


def recv_exact(sock: socket.socket, size: int) -> bytes | None:
    chunks = bytearray()
    while len(chunks) < size:
        chunk = sock.recv(size - len(chunks))
        if not chunk:
            return None
        chunks.extend(chunk)
    return bytes(chunks)


def recv_frame(sock: socket.socket) -> dict | None:
    header = recv_exact(sock, 4)
    if not header:
        return None
    size = struct.unpack("!I", header)[0]
    if size <= 0 or size > 50 * 1024 * 1024:
        raise ValueError("Invalid frame size")
    payload = recv_exact(sock, size)
    if not payload:
        return None
    return json.loads(payload.decode("utf-8"))


def sanitize_filename(name: str) -> str:
    safe = Path(name).name.strip() or "file"
    return safe.replace("\x00", "")


def format_bytes(value: int) -> str:
    size = float(value)
    for unit in ["B", "KB", "MB", "GB"]:
        if size < 1024 or unit == "GB":
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{value} B"


def format_message_time(timestamp: float | None = None) -> str:
    if timestamp is None:
        timestamp = time.time()
    return time.strftime("%I:%M %p", time.localtime(timestamp)).lstrip("0")


def normalize_update_manifest_url(value: str) -> str:
    value = value.strip()
    if not value:
        return ""
    if value.endswith(".json"):
        return value
    if not value.endswith("/"):
        value = f"{value}/"
    return urljoin(value, UPDATE_MANIFEST_FILENAME)


def parse_version_parts(value: str) -> tuple[int, ...]:
    parts: list[int] = []
    for chunk in value.strip().split("."):
        digits = "".join(char for char in chunk if char.isdigit())
        parts.append(int(digits or "0"))
    return tuple(parts)


def is_ipv4_address(value: str) -> bool:
    parts = value.strip().split(".")
    if len(parts) != 4:
        return False
    try:
        return all(0 <= int(part) <= 255 for part in parts)
    except ValueError:
        return False


def create_root() -> tk.Tk:
    # Always use a plain Tk root. If tkinterdnd2 is installed but its native
    # library fails to initialize on a packaged build, TkinterDnD.Tk() can
    # leave behind a stray default root window titled "tk".
    return tk.Tk()


def configure_hidden_root(root: tk.Tk) -> None:
    root.title(APP_TITLE)
    root.geometry("1x1+0+0")
    root.overrideredirect(True)
    root.resizable(False, False)
    try:
        root.attributes("-alpha", 0.0)
    except Exception:
        pass
    root.withdraw()
    root.update_idletasks()


def verify_gui_runtime() -> tuple[bool, str | None]:
    if getattr(sys, "frozen", False):
        return True, None

    checks = [
        (
            "tkinter",
            "import tkinter as tk; root = tk.Tk(); root.withdraw(); root.destroy()",
        ),
        (
            "pystray",
            "from PIL import Image; import pystray; "
            "icon = pystray.Icon('probe', Image.new('RGBA', (16, 16), (0, 0, 0, 0)), 'probe'); "
            "icon.visible = False",
        ),
    ]

    for name, script in checks:
        result = subprocess.run(
            [sys.executable, "-c", script],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        if result.returncode != 0:
            return False, (
                f"The {name} GUI backend failed to start in this Python environment. "
                f"Reinstall or repair the GUI runtime for {sys.executable}."
            )

    return True, None


@dataclass
class Peer:
    ip: str
    username: str
    port: int
    public_key_b64: str
    last_seen: float


class ConfigStore:
    def __init__(self) -> None:
        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        INBOX_DIR.mkdir(parents=True, exist_ok=True)
        self.data = self._load()

    def _load(self) -> dict:
        if CONFIG_FILE.exists():
            try:
                return json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
            except Exception:
                pass

        private_key = x25519.X25519PrivateKey.generate()
        data = {
            "username": socket.gethostname(),
            "contacts": [],
            "update_server_url": "",
            "private_key_b64": b64e(
                private_key.private_bytes(
                    encoding=serialization.Encoding.Raw,
                    format=serialization.PrivateFormat.Raw,
                    encryption_algorithm=serialization.NoEncryption(),
                )
            ),
        }
        self._save(data)
        return data

    def _save(self, data: dict) -> None:
        CONFIG_FILE.write_text(json.dumps(data, indent=2), encoding="utf-8")

    @property
    def username(self) -> str:
        return self.data.get("username", socket.gethostname())

    @username.setter
    def username(self, value: str) -> None:
        self.data["username"] = value.strip() or socket.gethostname()
        self._save(self.data)

    @property
    def private_key(self) -> x25519.X25519PrivateKey:
        return x25519.X25519PrivateKey.from_private_bytes(b64d(self.data["private_key_b64"]))

    @property
    def contacts(self) -> list[dict[str, str]]:
        contacts = self.data.get("contacts", [])
        if isinstance(contacts, list):
            return contacts
        return []

    def save_contacts(self, contacts: list[dict[str, str]]) -> None:
        self.data["contacts"] = contacts
        self._save(self.data)

    @property
    def pending_messages(self) -> list[dict[str, Any]]:
        pending = self.data.get("pending_messages", [])
        if isinstance(pending, list):
            return [item for item in pending if isinstance(item, dict)]
        return []

    def save_pending_messages(self, pending_messages: list[dict[str, Any]]) -> None:
        self.data["pending_messages"] = pending_messages
        self._save(self.data)

    @property
    def update_server_url(self) -> str:
        return str(self.data.get("update_server_url", "")).strip()

    @update_server_url.setter
    def update_server_url(self, value: str) -> None:
        self.data["update_server_url"] = normalize_update_manifest_url(value)
        self._save(self.data)


class CryptoBox:
    def __init__(self, private_key: x25519.X25519PrivateKey) -> None:
        self.private_key = private_key
        self.public_key = private_key.public_key()
        self.public_key_b64 = b64e(
            self.public_key.public_bytes(
                encoding=serialization.Encoding.Raw,
                format=serialization.PublicFormat.Raw,
            )
        )

    def encrypt_for_peer(self, peer_public_key_b64: str, plaintext: bytes, aad: bytes = b"") -> tuple[str, str]:
        peer_public_key = x25519.X25519PublicKey.from_public_bytes(b64d(peer_public_key_b64))
        shared = self.private_key.exchange(peer_public_key)
        key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=b"lan-messenger").derive(shared)
        nonce = os.urandom(12)
        ciphertext = AESGCM(key).encrypt(nonce, plaintext, aad)
        return b64e(nonce), b64e(ciphertext)

    def decrypt_from_peer(self, peer_public_key_b64: str, nonce_b64: str, ciphertext_b64: str, aad: bytes = b"") -> bytes:
        peer_public_key = x25519.X25519PublicKey.from_public_bytes(b64d(peer_public_key_b64))
        shared = self.private_key.exchange(peer_public_key)
        key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=b"lan-messenger").derive(shared)
        return AESGCM(key).decrypt(b64d(nonce_b64), b64d(ciphertext_b64), aad)


class NotificationManager:
    def notify(self, title: str, message: str) -> None:
        if notification_module is None:
            return
        try:
            notification_module.notify(title=title, message=message, app_name=APP_NAME, timeout=5)
        except Exception:
            pass


@dataclass
class Contact:
    public_key_b64: str
    username: str
    last_ip: str


@dataclass
class PendingMessage:
    message_id: str
    public_key_b64: str
    username: str
    text: str
    queued_at: float


@dataclass
class MessageEntry:
    sender: str
    text: str
    incoming: bool
    timestamp: float
    message_id: str | None = None
    status: str = ""
    read_receipt_sent: bool = False


@dataclass
class UpdateInfo:
    version: str
    download_url: str
    notes: str
    manifest_url: str


class LanMessengerApp:
    def __init__(self) -> None:
        self.root = create_root()
        configure_hidden_root(self.root)
        self.root.protocol("WM_DELETE_WINDOW", lambda: None)

        if NSApp is not None and NSApplicationActivationPolicyAccessory is not None:
            try:
                app = cast(Any, NSApp() if callable(NSApp) else NSApp)
                if app is not None:
                    app.setActivationPolicy_(NSApplicationActivationPolicyAccessory)
            except Exception:
                pass

        self.config = ConfigStore()
        self.crypto = CryptoBox(self.config.private_key)
        self.notifications = NotificationManager()

        self.peers: dict[str, Peer] = {}
        self.message_history: dict[str, list[MessageEntry]] = {}
        self.unread_counts: dict[str, int] = {}
        self.transfer_statuses: dict[str, tuple[str, int, int]] = {}
        self.incoming_files: dict[tuple[str, str], dict] = {}
        self.ui_queue: "queue.Queue[tuple]" = queue.Queue()
        self.running = True
        self.local_ips = self._detect_local_ips()
        self.local_ip = self._preferred_local_ip()
        self.main_window: "MainChatWindow | None" = None
        self.contacts_window: "ContactsWindow | None" = None
        self.settings_window: "SettingsWindow | None" = None
        self.latest_update_info: UpdateInfo | None = None

        self.icon = self._create_tray_icon()

        self.discovery_thread = threading.Thread(target=self.discovery_broadcast_loop, daemon=True)
        self.discovery_listener_thread = threading.Thread(target=self.discovery_listener_loop, daemon=True)
        self.server_thread = threading.Thread(target=self.tcp_server_loop, daemon=True)
        self.cleanup_thread = threading.Thread(target=self.peer_cleanup_loop, daemon=True)

        self.discovery_thread.start()
        self.discovery_listener_thread.start()
        self.server_thread.start()
        self.cleanup_thread.start()
        self._start_tray_icon()

        self.root.after(100, self.process_ui_queue)

    @property
    def username(self) -> str:
        return self.config.username

    @property
    def contacts(self) -> list[Contact]:
        items: list[Contact] = []
        for contact in self.config.contacts:
            public_key = str(contact.get("public_key_b64", "")).strip()
            if not public_key:
                continue
            items.append(Contact(
                public_key_b64=public_key,
                username=str(contact.get("username", "")).strip() or "Unknown",
                last_ip=str(contact.get("last_ip", "")).strip(),
            ))
        return items

    @property
    def pending_messages(self) -> list[PendingMessage]:
        items: list[PendingMessage] = []
        for pending in self.config.pending_messages:
            public_key = str(pending.get("public_key_b64", "")).strip()
            text = str(pending.get("text", ""))
            if not public_key or not text:
                continue
            items.append(PendingMessage(
                message_id=str(pending.get("message_id", "")).strip() or uuid.uuid4().hex,
                public_key_b64=public_key,
                username=str(pending.get("username", "")).strip() or "Unknown",
                text=text,
                queued_at=float(pending.get("queued_at") or now()),
            ))
        return items

    def enqueue_ui(self, action: str, *args) -> None:
        self.ui_queue.put((action, args))

    def process_ui_queue(self) -> None:
        while True:
            try:
                action, args = self.ui_queue.get_nowait()
            except queue.Empty:
                break

            if action == "peer_update":
                self.refresh_tray_menu()
            elif action == "message":
                ip, sender, text, message_id, timestamp = args
                self.add_message(ip, sender, text, incoming=True, show_popup=True, message_id=message_id, timestamp=timestamp)
            elif action == "message_status":
                ip, message_id, status = args
                self.update_message_status(ip, message_id, status)
            elif action == "transfer_progress":
                ip, label, current, total = args
                self.update_transfer_status(ip, label, current, total)
            elif action == "transfer_complete":
                ip, label = args
                self.finish_transfer_status(ip, label)
            elif action == "incoming_file":
                ip, sender, path = args
                self.add_message(ip, "System", f"Received file: {Path(path).name}", incoming=False, show_popup=True)
                self.notifications.notify(f"File from {sender}", f"Saved to {path}")
            elif action == "network_error":
                ip, text = args
                self.add_message(ip, "System", text, incoming=False, show_popup=True)
            elif action == "show_quick_chat":
                ip = args[0]
                self.open_quick_chat(ip)
            elif action == "show_main_chat":
                self.show_main_window()
            elif action == "show_contacts":
                self.show_contacts_window()
            elif action == "show_settings":
                self.show_settings_window()
            elif action == "check_updates":
                manual = bool(args[0]) if args else True
                self.check_for_updates(manual=manual)
            elif action == "update_available":
                self._handle_update_available(args[0], bool(args[1]) if len(args) > 1 else True)
            elif action == "update_not_available":
                self._handle_no_update(bool(args[0]) if args else True)
            elif action == "update_error":
                self._handle_update_error(str(args[0]), bool(args[1]) if len(args) > 1 else True)
            elif action == "prompt_username":
                self.prompt_username_change()
            elif action == "add_contact":
                ip = args[0]
                self.add_contact_from_peer(ip)
            elif action == "remove_contact":
                public_key_b64 = args[0]
                self.remove_contact(public_key_b64)
            elif action == "prompt_send_file":
                ip = args[0]
                self.prompt_send_file(ip)
            elif action == "shutdown":
                self._shutdown_ui()

        if self.running:
            self.root.after(100, self.process_ui_queue)

    def _detect_local_ips(self) -> set[str]:
        addresses: set[str] = set()

        for host in {socket.gethostname(), socket.getfqdn()}:
            try:
                infos = socket.getaddrinfo(host, None, socket.AF_INET, socket.SOCK_DGRAM)
            except OSError:
                continue
            for info in infos:
                ip = info[4][0]
                if not isinstance(ip, str):
                    continue
                if is_ipv4_address(ip) and not ip.startswith("127."):
                    addresses.add(ip)

        for target in [("8.8.8.8", 80), ("1.1.1.1", 80), ("192.0.2.1", 80)]:
            test_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                test_socket.connect(target)
                ip = test_socket.getsockname()[0]
                if is_ipv4_address(ip):
                    addresses.add(ip)
            except OSError:
                continue
            finally:
                test_socket.close()

        if not addresses:
            addresses.add("127.0.0.1")
        return addresses

    def _preferred_local_ip(self) -> str:
        for ip in sorted(self.local_ips):
            if ip != "127.0.0.1":
                return ip
        return "127.0.0.1"

    def current_platform(self) -> str:
        if sys.platform.startswith("win"):
            return "windows"
        if sys.platform == "darwin":
            return "macos"
        return "unknown"

    def discovery_payload(self, packet_type: str = "discovery") -> dict:
        return {
            "type": packet_type,
            "username": self.username,
            "port": TCP_PORT,
            "public_key_b64": self.crypto.public_key_b64,
            "ips": sorted(self.local_ips),
        }

    def discovery_targets(self) -> list[str]:
        targets = {"255.255.255.255", DISCOVERY_MULTICAST_GROUP}
        for local_ip in sorted(self.local_ips):
            if is_ipv4_address(local_ip) and local_ip != "127.0.0.1":
                octets = local_ip.split(".")
                targets.add(".".join([octets[0], octets[1], octets[2], "255"]))

        for contact in self.contacts:
            candidate = contact.last_ip.strip()
            if candidate and candidate not in self.local_ips and is_ipv4_address(candidate):
                targets.add(candidate)

        for peer in self.peers.values():
            if peer.ip not in self.local_ips and is_ipv4_address(peer.ip):
                targets.add(peer.ip)

        return sorted(targets)

    def discovery_broadcast_loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, DISCOVERY_MULTICAST_TTL)
            except OSError:
                pass
            while self.running:
                try:
                    payload = json.dumps(self.discovery_payload("discovery")).encode("utf-8")
                    for target in self.discovery_targets():
                        try:
                            sock.sendto(payload, (target, DISCOVERY_PORT))
                        except OSError:
                            pass
                except OSError:
                    pass
                time.sleep(DISCOVERY_INTERVAL)
        finally:
            sock.close()

    def discovery_listener_loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
            except Exception:
                pass
            sock.bind(("", DISCOVERY_PORT))
            try:
                membership = socket.inet_aton(DISCOVERY_MULTICAST_GROUP) + socket.inet_aton("0.0.0.0")
                sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, membership)
            except OSError:
                pass
            sock.settimeout(1.0)

            while self.running:
                try:
                    data, addr = sock.recvfrom(8192)
                except socket.timeout:
                    continue
                except OSError:
                    break
                try:
                    packet = json.loads(data.decode("utf-8"))
                except Exception:
                    continue

                packet_type = packet.get("type")
                if packet_type not in {"discovery", "discovery_reply"}:
                    continue

                ip = addr[0]
                if ip in self.local_ips:
                    continue
                public_key_b64 = str(packet.get("public_key_b64", "")).strip()
                if public_key_b64 == self.crypto.public_key_b64 or not public_key_b64:
                    continue

                peer, changed = self._upsert_peer(
                    ip=ip,
                    username=str(packet.get("username") or ip),
                    port=int(packet.get("port") or TCP_PORT),
                    public_key_b64=public_key_b64,
                )
                if packet_type == "discovery":
                    try:
                        reply = json.dumps(self.discovery_payload("discovery_reply")).encode("utf-8")
                        sock.sendto(reply, (ip, DISCOVERY_PORT))
                    except OSError:
                        pass
                if changed:
                    self.enqueue_ui("peer_update")
        finally:
            sock.close()

    def peer_cleanup_loop(self) -> None:
        while self.running:
            expired = [
                ip for ip, peer in list(self.peers.items())
                if now() - peer.last_seen > PEER_TIMEOUT
            ]
            for ip in expired:
                self.peers.pop(ip, None)
            if expired:
                self.enqueue_ui("peer_update")
            time.sleep(2)

    def tcp_server_loop(self) -> None:
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind(("", TCP_PORT))
            server.listen(16)
            while self.running:
                try:
                    conn, addr = server.accept()
                except OSError:
                    break
                threading.Thread(target=self.handle_connection, args=(conn, addr), daemon=True).start()
        finally:
            server.close()

    def handle_connection(self, conn: socket.socket, addr: tuple[str, int]) -> None:
        ip = addr[0]
        try:
            with conn:
                while self.running:
                    packet = recv_frame(conn)
                    if packet is None:
                        break
                    self.process_packet(ip, packet)
        except Exception:
            self.enqueue_ui("network_error", ip, "A network packet could not be processed.")

    def process_packet(self, ip: str, packet: dict) -> None:
        packet_type = packet.get("type")
        transfer_id = str(packet.get("transfer_id", "")).strip()
        transfer = self.incoming_files.get((ip, transfer_id)) if transfer_id else None
        if packet.get("sender_public_key_b64") == self.crypto.public_key_b64:
            return
        public_key_b64 = str(packet.get("sender_public_key_b64", "")).strip()
        peer: Peer | None = None
        if public_key_b64:
            peer, changed = self._upsert_peer(
                ip=ip,
                username=str(packet.get("sender") or ip),
                port=int(packet.get("port") or TCP_PORT),
                public_key_b64=public_key_b64,
            )
            if changed:
                self.enqueue_ui("peer_update")
        elif transfer is not None:
            stored_key = str(transfer.get("public_key_b64", "")).strip()
            peer = self.find_peer_by_public_key(stored_key) if stored_key else self.find_peer_by_ip(ip)

        if peer is None:
            return

        if packet_type == "text":
            aad = packet["message_id"].encode("utf-8")
            plaintext = self.crypto.decrypt_from_peer(peer.public_key_b64, packet["nonce"], packet["ciphertext"], aad=aad)
            text = plaintext.decode("utf-8", errors="replace")
            sender = packet.get("sender") or peer.username
            timestamp = float(packet.get("timestamp") or now())
            self.enqueue_ui("message", ip, sender, text, packet["message_id"], timestamp)
            self._send_receipt_to_peer(peer, "sent_receipt", packet["message_id"])
            self.notifications.notify(f"Message from {sender}", text[:120])
            return

        if packet_type == "sent_receipt":
            message_id = str(packet.get("message_id", "")).strip()
            if message_id:
                self.enqueue_ui("message_status", ip, message_id, "Sent")
            return

        if packet_type == "read_receipt":
            message_id = str(packet.get("message_id", "")).strip()
            if message_id:
                self.enqueue_ui("message_status", ip, message_id, "Read")
            return

        if packet_type == "file_start":
            transfer_id = packet["transfer_id"]
            sender = packet.get("sender") or peer.username
            filename = sanitize_filename(packet["filename"])
            temp_path = INBOX_DIR / f"{transfer_id}_{filename}.part"
            handle = temp_path.open("wb")
            self.incoming_files[(ip, transfer_id)] = {
                "handle": handle,
                "path": temp_path,
                "filename": filename,
                "size": int(packet["size"]),
                "received": 0,
                "sender": sender,
                "public_key_b64": peer.public_key_b64,
            }
            self.enqueue_ui("transfer_progress", ip, f"Receiving {filename}", 0, int(packet["size"]))
            return

        if packet_type == "file_chunk":
            transfer = self.incoming_files.get((ip, packet["transfer_id"]))
            if not transfer:
                return
            aad = packet["transfer_id"].encode("utf-8")
            chunk = self.crypto.decrypt_from_peer(peer.public_key_b64, packet["nonce"], packet["ciphertext"], aad=aad)
            transfer["handle"].write(chunk)
            transfer["received"] += len(chunk)
            self.enqueue_ui(
                "transfer_progress",
                ip,
                f"Receiving {transfer['filename']}",
                transfer["received"],
                transfer["size"],
            )
            return

        if packet_type == "file_end":
            transfer = self.incoming_files.pop((ip, packet["transfer_id"]), None)
            if not transfer:
                return
            transfer["handle"].close()
            final_path = self._unique_inbox_path(transfer["filename"])
            transfer["path"].replace(final_path)
            self.enqueue_ui("transfer_complete", ip, f"Receiving {transfer['filename']}")
            self.enqueue_ui("incoming_file", ip, transfer["sender"], str(final_path))
            return

    def _unique_inbox_path(self, filename: str) -> Path:
        target = INBOX_DIR / sanitize_filename(filename)
        if not target.exists():
            return target
        stem = target.stem
        suffix = target.suffix
        for index in range(1, 1000):
            candidate = target.with_name(f"{stem}_{index}{suffix}")
            if not candidate.exists():
                return candidate
        return target.with_name(f"{stem}_{uuid.uuid4().hex[:8]}{suffix}")

    def send_text(self, ip: str, text: str) -> None:
        message_id = uuid.uuid4().hex
        peer = self._resolve_delivery_peer(ip)
        initial_status = "Sending" if peer is not None else "Queued"
        history_ip = peer.ip if peer is not None else ip
        self.add_message(history_ip, self.username, text, incoming=False, message_id=message_id, status=initial_status)

        if not peer:
            contact = self._find_contact_by_ip(ip)
            if contact is not None:
                self._queue_pending_message(message_id, contact.public_key_b64, contact.username, text)
                self.enqueue_ui("network_error", ip, f"{contact.username} is offline. Message queued for delivery.")
            else:
                self.enqueue_ui("network_error", ip, "Peer is no longer available.")
                self.enqueue_ui("message_status", history_ip, message_id, "Failed")
            return

        def worker() -> None:
            if not self._send_text_to_peer(peer, text, message_id=message_id):
                self._queue_pending_message(message_id, peer.public_key_b64, peer.username, text)
                self.enqueue_ui("message_status", peer.ip, message_id, "Queued")
                self.enqueue_ui("network_error", peer.ip, f"{peer.username} went offline. Message queued for delivery.")

        threading.Thread(target=worker, daemon=True).start()

    def send_file(self, ip: str, path: str, progress_callback=None) -> None:
        peer = self._resolve_delivery_peer(ip)
        if not peer:
            self.enqueue_ui("network_error", ip, "Peer is no longer available.")
            return

        file_path = Path(path)
        if not file_path.exists():
            self.enqueue_ui("network_error", ip, "Selected file no longer exists.")
            return

        def worker() -> None:
            transfer_id = uuid.uuid4().hex
            total_size = file_path.stat().st_size
            packets = [{
                "type": "file_start",
                "transfer_id": transfer_id,
                "filename": file_path.name,
                "size": total_size,
                "sender": self.username,
                "sender_public_key_b64": self.crypto.public_key_b64,
                "port": TCP_PORT,
            }]

            try:
                with socket.create_connection((peer.ip, peer.port), timeout=5) as sock, file_path.open("rb") as handle:
                    for packet in packets:
                        send_frame(sock, packet)
                    sent = 0
                    self.enqueue_ui("transfer_progress", ip, f"Sending {file_path.name}", 0, total_size)
                    while True:
                        chunk = handle.read(BUFFER_SIZE)
                        if not chunk:
                            break
                        aad = transfer_id.encode("utf-8")
                        nonce, ciphertext = self.crypto.encrypt_for_peer(peer.public_key_b64, chunk, aad=aad)
                        send_frame(sock, {
                            "type": "file_chunk",
                            "transfer_id": transfer_id,
                            "sender": self.username,
                            "sender_public_key_b64": self.crypto.public_key_b64,
                            "port": TCP_PORT,
                            "nonce": nonce,
                            "ciphertext": ciphertext,
                        })
                        sent += len(chunk)
                        self.enqueue_ui("transfer_progress", ip, f"Sending {file_path.name}", sent, total_size)
                        if progress_callback:
                            progress_callback(sent, total_size)

                    send_frame(sock, {
                        "type": "file_end",
                        "transfer_id": transfer_id,
                        "sender": self.username,
                        "sender_public_key_b64": self.crypto.public_key_b64,
                        "port": TCP_PORT,
                    })
                    self.enqueue_ui("transfer_complete", ip, f"Sending {file_path.name}")
            except Exception:
                self.enqueue_ui("network_error", ip, f"File transfer failed for {file_path.name}.")

        threading.Thread(target=worker, daemon=True).start()

    def _send_packets(self, peer: Peer, packets: list[dict]) -> bool:
        attempts = [(peer.ip, peer.port)]
        replacement_peer = self.find_peer_by_public_key(peer.public_key_b64)
        if replacement_peer is not None and replacement_peer.ip != peer.ip:
            attempts.append((replacement_peer.ip, replacement_peer.port))

        for attempt_ip, attempt_port in attempts:
            try:
                with socket.create_connection((attempt_ip, attempt_port), timeout=5) as sock:
                    for packet in packets:
                        send_frame(sock, packet)
                if attempt_ip != peer.ip:
                    self._rebind_peer_state(peer.ip, attempt_ip, replacement_peer.username if replacement_peer is not None else peer.username)
                    self.enqueue_ui("peer_update")
                return True
            except Exception:
                continue
        return False

    def _send_text_to_peer(self, peer: Peer, text: str, message_id: str | None = None, timestamp: float | None = None) -> bool:
        if message_id is None:
            message_id = uuid.uuid4().hex
        if timestamp is None:
            timestamp = now()
        aad = message_id.encode("utf-8")
        nonce, ciphertext = self.crypto.encrypt_for_peer(peer.public_key_b64, text.encode("utf-8"), aad=aad)
        packet = {
            "type": "text",
            "message_id": message_id,
            "timestamp": timestamp,
            "sender": self.username,
            "sender_public_key_b64": self.crypto.public_key_b64,
            "port": TCP_PORT,
            "nonce": nonce,
            "ciphertext": ciphertext,
        }
        return self._send_packets(peer, [packet])

    def _send_receipt_to_peer(self, peer: Peer, receipt_type: str, message_id: str) -> bool:
        packet = {
            "type": receipt_type,
            "message_id": message_id,
            "sender": self.username,
            "sender_public_key_b64": self.crypto.public_key_b64,
            "port": TCP_PORT,
        }
        return self._send_packets(peer, [packet])

    def _rebind_peer_state(self, old_ip: str, new_ip: str, peer_name: str) -> None:
        if old_ip == new_ip:
            return

        old_history = self.message_history.pop(old_ip, [])
        if old_history:
            new_history = self.message_history.setdefault(new_ip, [])
            new_history.extend(old_history)
            new_history.sort(key=lambda entry: entry.timestamp)

        unread = self.unread_counts.pop(old_ip, 0)
        if unread:
            self.unread_counts[new_ip] = self.unread_counts.get(new_ip, 0) + unread

        transfer_status = self.transfer_statuses.pop(old_ip, None)
        if transfer_status is not None:
            self.transfer_statuses[new_ip] = transfer_status

        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.rebind_peer(old_ip, new_ip, peer_name)

    def _upsert_peer(self, ip: str, username: str, port: int, public_key_b64: str) -> tuple[Peer, bool]:
        changed = False
        existing_by_key = self.find_peer_by_public_key(public_key_b64)
        if existing_by_key is not None and existing_by_key.ip != ip:
            self.peers.pop(existing_by_key.ip, None)
            self._rebind_peer_state(existing_by_key.ip, ip, username or ip)
            changed = True

        peer = self.peers.get(ip)
        if peer is None or peer.public_key_b64 != public_key_b64:
            peer = Peer(
                ip=ip,
                username=username or ip,
                port=port,
                public_key_b64=public_key_b64,
                last_seen=now(),
            )
            self.peers[ip] = peer
            changed = True
            newly_online = True
        else:
            newly_online = False
            if peer.username != (username or ip):
                peer.username = username or ip
                changed = True
            if peer.port != port:
                peer.port = port
                changed = True
            peer.last_seen = now()

        self._update_contact_last_seen(peer)
        if newly_online:
            self._deliver_pending_messages_for_peer(peer)

        return peer, (changed or newly_online)

    def _resolve_active_peer(self, ip: str) -> Peer | None:
        peer = self.peers.get(ip)
        if peer is not None:
            return peer

        contact = self._find_contact_by_ip(ip)
        if contact is None:
            return None

        live_peer = self.find_peer_by_public_key(contact.public_key_b64)
        if live_peer is not None and live_peer.ip != ip:
            self._rebind_peer_state(ip, live_peer.ip, live_peer.username)
            self.enqueue_ui("peer_update")
        return live_peer

    def _resolve_delivery_peer(self, ip: str) -> Peer | None:
        peer = self._resolve_active_peer(ip)
        if peer is not None:
            return peer

        contact = self._find_contact_by_ip(ip)
        if contact is None or not contact.last_ip:
            return None

        return Peer(
            ip=contact.last_ip,
            username=contact.username,
            port=TCP_PORT,
            public_key_b64=contact.public_key_b64,
            last_seen=now(),
        )

    def get_peer_name(self, ip: str) -> str:
        peer = self._resolve_active_peer(ip)
        if peer is not None:
            return peer.username
        contact = self._find_contact_by_ip(ip)
        if contact is not None:
            return contact.username
        return ip

    def find_peer_by_public_key(self, public_key_b64: str) -> Peer | None:
        for peer in self.peers.values():
            if peer.public_key_b64 == public_key_b64:
                return peer
        return None

    def find_contact_by_public_key(self, public_key_b64: str) -> Contact | None:
        for contact in self.contacts:
            if contact.public_key_b64 == public_key_b64:
                return contact
        return None

    def find_peer_by_ip(self, ip: str) -> Peer | None:
        return self.peers.get(ip)

    def _find_contact_by_ip(self, ip: str) -> Contact | None:
        for contact in self.contacts:
            if contact.last_ip == ip:
                return contact
        return None

    def find_peer_for_contact(self, contact: Contact) -> Peer | None:
        peer = self.find_peer_by_public_key(contact.public_key_b64)
        if peer is not None:
            return peer
        if contact.last_ip:
            return self.find_peer_by_ip(contact.last_ip)
        return None

    def conversation_name(self, ip: str) -> str:
        peer = self.peers.get(ip)
        if peer is not None:
            return peer.username
        contact = self._find_contact_by_ip(ip)
        if contact is not None:
            return contact.username
        return ip

    def conversation_status(self, ip: str) -> str:
        peer = self.peers.get(ip)
        if peer is not None:
            return f"Online  {peer.ip}"
        contact = self._find_contact_by_ip(ip)
        if contact is not None and contact.last_ip:
            return f"Offline  {contact.last_ip}"
        return "Offline"

    def conversation_preview(self, ip: str) -> str:
        history = self.message_history.get(ip, [])
        if not history:
            return "Ready to chat" if self.peers.get(ip) is not None else "No messages yet"

        entry = history[-1]
        if entry.sender.strip().lower() == "system" or entry.incoming:
            text = entry.text
        else:
            text = f"You: {entry.text}"
        return text if len(text) <= 48 else f"{text[:45]}..."

    def conversation_targets(self) -> list[str]:
        targets: set[str] = set(self.message_history.keys())

        for peer in self.online_peers():
            targets.add(peer.ip)

        for contact in self.contacts:
            peer = self.find_peer_for_contact(contact)
            if peer is not None:
                targets.add(peer.ip)
            elif contact.last_ip:
                targets.add(contact.last_ip)

        return sorted(
            targets,
            key=lambda ip: (
                -(self.message_history.get(ip, [])[-1].timestamp if self.message_history.get(ip) else 0.0),
                0 if self.peers.get(ip) is not None else 1,
                self.conversation_name(ip).lower(),
            ),
        )

    def online_peers(self) -> list[Peer]:
        peers = [
            peer for peer in self.peers.values()
            if peer.public_key_b64 != self.crypto.public_key_b64
        ]
        peers.sort(key=lambda peer: peer.username.lower())
        return peers

    def is_contact(self, peer: Peer) -> bool:
        return any(contact.public_key_b64 == peer.public_key_b64 for contact in self.contacts)

    def _update_contact_last_seen(self, peer: Peer) -> None:
        changed = False
        contacts: list[dict[str, str]] = []
        for contact in self.config.contacts:
            item = dict(contact)
            if item.get("public_key_b64") == peer.public_key_b64:
                old_ip = str(item.get("last_ip", "")).strip()
                if old_ip and old_ip != peer.ip:
                    self._rebind_peer_state(old_ip, peer.ip, peer.username)
                if old_ip != peer.ip or item.get("username") != peer.username:
                    item["last_ip"] = peer.ip
                    item["username"] = peer.username
                    changed = True
            contacts.append(item)
        if changed:
            self.config.save_contacts(contacts)

    def add_contact_from_peer(self, ip: str) -> None:
        peer = self.peers.get(ip)
        if peer is None:
            return
        contacts = [contact for contact in self.config.contacts if contact.get("public_key_b64") != peer.public_key_b64]
        contacts.append({
            "public_key_b64": peer.public_key_b64,
            "username": peer.username,
            "last_ip": peer.ip,
        })
        contacts.sort(key=lambda item: str(item.get("username", "")).lower())
        self.config.save_contacts(contacts)
        self.refresh_tray_menu()

    def remove_contact(self, public_key_b64: str) -> None:
        contacts = [contact for contact in self.config.contacts if contact.get("public_key_b64") != public_key_b64]
        self.config.save_contacts(contacts)
        pending_messages = [
            pending for pending in self.config.pending_messages
            if pending.get("public_key_b64") != public_key_b64
        ]
        self.config.save_pending_messages(pending_messages)
        self.refresh_tray_menu()

    def _queue_pending_message(self, message_id: str, public_key_b64: str, username: str, text: str) -> None:
        pending = self.config.pending_messages
        pending.append({
            "message_id": message_id,
            "public_key_b64": public_key_b64,
            "username": username,
            "text": text,
            "queued_at": now(),
        })
        self.config.save_pending_messages(pending)

    def _deliver_pending_messages_for_peer(self, peer: Peer) -> None:
        pending_for_peer = [
            pending for pending in self.pending_messages
            if pending.public_key_b64 == peer.public_key_b64
        ]
        if not pending_for_peer:
            return

        def worker() -> None:
            delivered = 0
            remaining: list[dict[str, Any]] = []
            for pending in self.config.pending_messages:
                if pending.get("public_key_b64") != peer.public_key_b64:
                    remaining.append(pending)
                    continue

                message_id = str(pending.get("message_id", "")).strip() or uuid.uuid4().hex
                text = str(pending.get("text", ""))
                self.enqueue_ui("message_status", peer.ip, message_id, "Sending")
                if text and self._send_text_to_peer(peer, text, message_id=message_id):
                    delivered += 1
                else:
                    self.enqueue_ui("message_status", peer.ip, message_id, "Queued")
                    pending["message_id"] = message_id
                    remaining.append(pending)

            self.config.save_pending_messages(remaining)
            if delivered:
                self.enqueue_ui(
                    "network_error",
                    peer.ip,
                    f"Delivered {delivered} queued message{'s' if delivered != 1 else ''} to {peer.username}.",
                )

        threading.Thread(target=worker, daemon=True).start()

    def prompt_username_change(self) -> None:
        value = simpledialog.askstring("Username", "Enter your username:", initialvalue=self.username, parent=self.root)
        if value is None:
            return
        value = value.strip()
        if not value:
            messagebox.showerror("Invalid username", "Username cannot be empty.", parent=self.root)
            return
        self.config.username = value
        self.refresh_tray_menu()

    def prompt_send_file(self, ip: str) -> None:
        path = filedialog.askopenfilename(parent=self.root)
        if not path:
            return
        self.add_message(ip, "System", f"Sending file: {Path(path).name}", incoming=False)
        self.send_file(ip, path)

    def check_for_updates(self, manual: bool = True) -> None:
        manifest_url = self.config.update_server_url
        if not manifest_url:
            if manual:
                messagebox.showinfo(
                    "Updates",
                    "Set an update server URL in Settings first.\n\n"
                    "Host a manifest JSON file or a folder containing "
                    f"`{UPDATE_MANIFEST_FILENAME}`.",
                    parent=self.root,
                )
            return

        def worker() -> None:
            try:
                with urlopen(manifest_url, timeout=10) as response:
                    payload = json.loads(response.read().decode("utf-8"))
            except URLError as exc:
                self.enqueue_ui("update_error", f"Could not reach the update server: {exc.reason}", manual)
                return
            except Exception as exc:
                self.enqueue_ui("update_error", f"Update check failed: {exc}", manual)
                return

            info = self._parse_update_manifest(payload, manifest_url)
            if info is None:
                self.enqueue_ui("update_error", "The update manifest is missing required fields.", manual)
                return

            if parse_version_parts(info.version) > parse_version_parts(APP_VERSION):
                self.enqueue_ui("update_available", info, manual)
            else:
                self.enqueue_ui("update_not_available", manual)

        threading.Thread(target=worker, daemon=True).start()

    def _parse_update_manifest(self, payload: dict[str, Any], manifest_url: str) -> UpdateInfo | None:
        version = str(payload.get("version", "")).strip()
        notes = str(payload.get("notes", "")).strip()
        downloads = payload.get("downloads", {})
        if not version or not isinstance(downloads, dict):
            return None

        platform_key = self.current_platform()
        download_url = str(downloads.get(platform_key, "")).strip()
        if not download_url:
            return None

        if not download_url.startswith(("http://", "https://")):
            download_url = urljoin(manifest_url, download_url)

        return UpdateInfo(
            version=version,
            download_url=download_url,
            notes=notes,
            manifest_url=manifest_url,
        )

    def _handle_update_available(self, info: UpdateInfo, manual: bool) -> None:
        self.latest_update_info = info
        notes = f"\n\nRelease notes:\n{info.notes}" if info.notes else ""
        open_download = messagebox.askyesno(
            "Update Available",
            f"Version {info.version} is available.\n"
            f"You are on {APP_VERSION}.{notes}\n\n"
            "Open the download page now?",
            parent=self.root,
        )
        if open_download:
            self.open_update_download(info.download_url)
        elif manual:
            self.notifications.notify("Update Available", f"LAN Messenger {info.version} is ready to download.")

    def _handle_no_update(self, manual: bool) -> None:
        if manual:
            messagebox.showinfo("Updates", f"You are already on the latest version ({APP_VERSION}).", parent=self.root)

    def _handle_update_error(self, message: str, manual: bool) -> None:
        if manual:
            messagebox.showerror("Update Check Failed", message, parent=self.root)
        else:
            self.notifications.notify("Update Check Failed", message)

    def open_update_download(self, download_url: str) -> None:
        try:
            webbrowser.open(download_url)
        except Exception:
            messagebox.showerror("Updates", f"Could not open download URL:\n{download_url}", parent=self.root)

    def refresh_tray_menu(self) -> None:
        try:
            self.icon.icon = self._tray_image()
            self.icon.menu = self._build_tray_menu()
            update_menu = getattr(self.icon, "update_menu", None)
            if callable(update_menu):
                update_menu()
        except Exception:
            pass

        if self.contacts_window is not None and self.contacts_window.winfo_exists():
            self.contacts_window.refresh()
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh()

    def _quick_chat_menu_items(self) -> list[Any]:
        peers = self.online_peers()[:10]
        items: list[Any] = []
        for peer in peers:
            unread = self.unread_counts.get(peer.ip, 0)
            label = peer.username if unread == 0 else f"{peer.username} ({unread})"
            items.append(TrayItem(label, lambda _, __, ip=peer.ip: self.enqueue_ui("show_quick_chat", ip)))
        if not items:
            items.append(TrayItem("No peers online", lambda *_: None, enabled=False))
        return items

    def _chat_menu_items(self) -> list[Any]:
        peers = self.online_peers()
        if not peers:
            return [TrayItem("No peers online", lambda *_: None, enabled=False)]

        items: list[Any] = []
        for peer in peers[:20]:
            unread = self.unread_counts.get(peer.ip, 0)
            status = "online"
            label = f"{peer.username} [{status}]"
            if unread:
                label = f"{label} ({unread})"
            items.append(TrayItem(label, lambda _, __, ip=peer.ip: self.enqueue_ui("show_quick_chat", ip)))
        return items

    def _file_transfer_menu_items(self) -> list[Any]:
        peers = self.online_peers()
        if not peers:
            return [TrayItem("No peers online", lambda *_: None, enabled=False)]

        return [
            TrayItem(peer.username, lambda _, __, ip=peer.ip: self.enqueue_ui("prompt_send_file", ip))
            for peer in peers[:20]
        ]

    def _build_tray_menu(self):
        return pystray.Menu(
            TrayItem(APP_TITLE, lambda *_: None, enabled=False),
            TrayItem("Open Chat", lambda *_: self.enqueue_ui("show_main_chat"), default=True),
            TrayItem("Contact List", lambda *_: self.enqueue_ui("show_contacts")),
            TrayItem("Check for Updates", lambda *_: self.enqueue_ui("check_updates", True)),
            TrayItem("Settings", lambda *_: self.enqueue_ui("show_settings")),
            TrayItem("Exit", lambda *_: self.quit()),
        )

    def _open_first_peer(self, *_args) -> None:
        self.enqueue_ui("show_main_chat")

    def add_message(
        self,
        ip: str,
        sender: str,
        text: str,
        incoming: bool,
        show_popup: bool = False,
        message_id: str | None = None,
        status: str = "",
        timestamp: float | None = None,
    ) -> None:
        history = self.message_history.setdefault(ip, [])
        history.append(MessageEntry(
            sender=sender,
            text=text,
            incoming=incoming,
            timestamp=now() if timestamp is None else timestamp,
            message_id=message_id,
            status=status,
        ))
        if len(history) > 200:
            del history[:-200]

        if incoming:
            self.unread_counts[ip] = self.unread_counts.get(ip, 0) + 1

        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_for_message(ip)
            if incoming and self.main_window.is_conversation_active(ip):
                self.mark_peer_read(ip)

        self.refresh_tray_menu()

    def mark_peer_read(self, ip: str) -> None:
        peer = self._resolve_active_peer(ip)
        receipts: list[str] = []
        for entry in self.message_history.get(ip, []):
            if entry.incoming and entry.message_id and not entry.read_receipt_sent:
                entry.read_receipt_sent = True
                receipts.append(entry.message_id)

        if self.unread_counts.get(ip, 0):
            self.unread_counts[ip] = 0
            self.refresh_tray_menu()

        if peer is None or not receipts:
            return

        def worker() -> None:
            for message_id in receipts:
                if not self._send_receipt_to_peer(peer, "read_receipt", message_id):
                    for entry in self.message_history.get(ip, []):
                        if entry.message_id == message_id:
                            entry.read_receipt_sent = False
                            break

        threading.Thread(target=worker, daemon=True).start()

    def update_message_status(self, ip: str, message_id: str, status: str) -> None:
        updated = False
        candidate_histories = [self.message_history.get(ip, [])]
        candidate_histories.extend(
            history for history_ip, history in self.message_history.items()
            if history_ip != ip
        )

        for history in candidate_histories:
            for entry in history:
                if entry.message_id != message_id or entry.incoming:
                    continue
                if status == "Read" or entry.status != "Read":
                    entry.status = status
                    updated = True
                break
            if updated:
                break

        if not updated:
            return

        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_for_message(ip)
        self.refresh_tray_menu()

    def update_transfer_status(self, ip: str, label: str, current: int, total: int) -> None:
        self.transfer_statuses[ip] = (label, current, total)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_transfer(ip)

    def finish_transfer_status(self, ip: str, label: str) -> None:
        current = self.transfer_statuses.get(ip)
        total = current[2] if current is not None else 1
        self.transfer_statuses[ip] = (f"{label} complete", total, total)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_transfer(ip)

    def open_quick_chat(self, ip: str) -> "MainChatWindow":
        return self.show_main_window(ip)

    def show_main_window(self, ip: str | None = None) -> "MainChatWindow":
        if self.main_window is None or not self.main_window.winfo_exists():
            self.main_window = MainChatWindow(self)
        self.main_window.show_chat(ip)
        return self.main_window

    def show_contacts_window(self) -> None:
        if self.contacts_window is None or not self.contacts_window.winfo_exists():
            self.contacts_window = ContactsWindow(self)
        self.contacts_window.show()

    def show_settings_window(self) -> None:
        if self.settings_window is None or not self.settings_window.winfo_exists():
            self.settings_window = SettingsWindow(self)
        self.settings_window.show()

    def _create_tray_icon(self):
        return pystray.Icon(APP_NAME, self._tray_image(), APP_TITLE, self._build_tray_menu())

    def _start_tray_icon(self) -> None:
        try:
            self.icon.run_detached()
            self.icon.visible = True
        except Exception:
            # Fallback for backends that do not support detached mode.
            threading.Thread(target=self.icon.run, daemon=True).start()

    def _tray_image(self) -> Image.Image:
        image = Image.new("RGBA", (64, 64), (20, 28, 38, 0))
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((8, 8, 56, 56), radius=14, fill=(25, 132, 197, 255))
        draw.ellipse((18, 18, 31, 31), fill=(255, 255, 255, 255))
        draw.ellipse((33, 18, 46, 31), fill=(255, 255, 255, 255))
        draw.rounded_rectangle((18, 34, 46, 45), radius=5, fill=(255, 255, 255, 255))
        if sum(self.unread_counts.values()) > 0:
            draw.ellipse((42, 6, 58, 22), fill=(220, 53, 69, 255), outline=(255, 255, 255, 255), width=2)
        return image

    def quit(self) -> None:
        self.running = False
        try:
            self.icon.stop()
        except Exception:
            pass
        self.enqueue_ui("shutdown")

    def _shutdown_ui(self) -> None:
        for transfer in list(self.incoming_files.values()):
            try:
                transfer["handle"].close()
            except Exception:
                pass
        self.root.quit()
        self.root.destroy()


class BaseWindow(tk.Toplevel):
    def __init__(self, app: LanMessengerApp, title: str, size: str) -> None:
        super().__init__(app.root)
        self.app = app
        self.title(title)
        self.geometry(size)
        self.withdraw()
        self.protocol("WM_DELETE_WINDOW", self.hide)

    def show(self) -> None:
        self.deiconify()
        self.lift()
        self.focus_force()

    def hide(self) -> None:
        self.withdraw()

    def is_visible(self) -> bool:
        try:
            return self.state() != "withdrawn"
        except tk.TclError:
            return False

    def _setup_message_view(self, widget: tk.Text) -> None:
        widget.configure(
            bg="#f5f7fb",
            relief="flat",
            padx=14,
            pady=14,
            spacing1=4,
            spacing2=1,
            spacing3=8,
        )
        widget.tag_configure(
            "meta",
            foreground="#7b8798",
            font=("Helvetica", 9),
            spacing1=10,
            spacing3=2,
        )
        widget.tag_configure(
            "incoming",
            background="#ffffff",
            foreground="#17212b",
            lmargin1=18,
            lmargin2=18,
            rmargin=90,
            borderwidth=8,
            relief="flat",
            font=("Helvetica", 11),
        )
        widget.tag_configure(
            "outgoing",
            background="#dff4ff",
            foreground="#12344d",
            lmargin1=90,
            lmargin2=90,
            rmargin=18,
            borderwidth=8,
            relief="flat",
            justify="right",
            font=("Helvetica", 11),
        )
        widget.tag_configure(
            "system",
            foreground="#5b6573",
            lmargin1=36,
            lmargin2=36,
            rmargin=36,
            justify="center",
            font=("Helvetica", 10, "italic"),
        )

    def _append_bubble(self, widget: tk.Text, my_username: str, entry: MessageEntry) -> None:
        timestamp = format_message_time(entry.timestamp)
        normalized_sender = entry.sender.strip().lower()
        own_sender = my_username.strip().lower()

        if normalized_sender == "system":
            widget.config(state="normal")
            widget.insert("end", f"{timestamp}\n", ("meta",))
            widget.insert("end", f"{entry.text}\n\n", ("system",))
            widget.see("end")
            widget.config(state="disabled")
            return

        direction = "outgoing" if normalized_sender == own_sender else "incoming"
        header = "You" if direction == "outgoing" else entry.sender
        header_line = f"{header}  {timestamp}"
        if direction == "outgoing" and entry.status:
            header_line = f"{header_line}  {entry.status}"

        widget.config(state="normal")
        widget.insert("end", f"{header_line}\n", ("meta", direction))
        widget.insert("end", f"{entry.text}\n\n", (direction,))
        widget.see("end")
        widget.config(state="disabled")


class ContactsWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(app, f"Contact List - {APP_TITLE}", "430x420")
        self.resizable(False, False)

        frame = ttk.Frame(self)
        frame.pack(fill="both", expand=True, padx=12, pady=12)

        ttk.Label(frame, text="Contact Book").pack(anchor="w")
        self.tree = ttk.Treeview(frame, columns=("name", "status", "ip"), show="headings", height=12)
        self.tree.heading("name", text="Name")
        self.tree.heading("status", text="Status")
        self.tree.heading("ip", text="Last IP")
        self.tree.column("name", width=170, anchor="w")
        self.tree.column("status", width=80, anchor="center")
        self.tree.column("ip", width=140, anchor="w")
        self.tree.pack(fill="both", expand=True, pady=(8, 10))

        online_frame = ttk.LabelFrame(frame, text="Add Online Peer")
        online_frame.pack(fill="x", pady=(0, 10))
        self.online_var = tk.StringVar()
        self.online_combo = ttk.Combobox(online_frame, textvariable=self.online_var, state="readonly")
        self.online_combo.pack(side="left", fill="x", expand=True, padx=(8, 8), pady=8)
        ttk.Button(online_frame, text="Add", command=self.add_selected_online).pack(side="right", padx=(0, 8), pady=8)

        actions = ttk.Frame(frame)
        actions.pack(fill="x")
        ttk.Button(actions, text="Chat", command=self.chat_selected).pack(side="left")
        ttk.Button(actions, text="Remove", command=self.remove_selected).pack(side="left", padx=(8, 0))
        ttk.Button(actions, text="Refresh", command=self.refresh).pack(side="right")

        self.refresh()

    def refresh(self) -> None:
        for item_id in self.tree.get_children():
            self.tree.delete(item_id)

        for contact in sorted(self.app.contacts, key=lambda contact: contact.username.lower()):
            peer = self.app.find_peer_for_contact(contact)
            status = "Online" if peer is not None else "Offline"
            display_ip = peer.ip if peer is not None else contact.last_ip
            self.tree.insert("", "end", iid=contact.public_key_b64, values=(contact.username, status, display_ip))

        online_peers = [
            peer for peer in sorted(self.app.peers.values(), key=lambda peer: peer.username.lower())
            if not self.app.is_contact(peer)
        ]
        self._online_options = {f"{peer.username} ({peer.ip})": peer.ip for peer in online_peers}
        self.online_combo["values"] = list(self._online_options.keys())
        if self._online_options:
            self.online_combo.current(0)
        else:
            self.online_var.set("")

    def add_selected_online(self) -> None:
        label = self.online_var.get()
        ip = self._online_options.get(label)
        if ip:
            self.app.add_contact_from_peer(ip)

    def remove_selected(self) -> None:
        selection = self.tree.selection()
        if not selection:
            return
        self.app.remove_contact(selection[0])

    def chat_selected(self) -> None:
        selection = self.tree.selection()
        if not selection:
            return
        contact_key = selection[0]
        contact = self.app.find_contact_by_public_key(contact_key)
        if contact is None:
            return
        peer = self.app.find_peer_for_contact(contact)
        if peer is not None:
            self.app.open_quick_chat(peer.ip)
            return
        if not contact.last_ip:
            messagebox.showinfo("Offline", "That contact is currently offline and has no saved address yet.", parent=self)
            return
        self.app.open_quick_chat(contact.last_ip)


class SettingsWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(app, f"Settings - {APP_TITLE}", "420x330")
        self.resizable(False, False)

        frame = ttk.Frame(self)
        frame.pack(fill="both", expand=True, padx=14, pady=14)

        ttk.Label(frame, text="Username").pack(anchor="w")
        self.username_var = tk.StringVar(value=self.app.username)
        ttk.Entry(frame, textvariable=self.username_var).pack(fill="x", pady=(6, 10))

        ttk.Label(frame, text=f"Version: {APP_VERSION}").pack(anchor="w", pady=(0, 10))

        ttk.Label(frame, text="Update Server URL").pack(anchor="w")
        self.update_server_var = tk.StringVar(value=self.app.config.update_server_url)
        ttk.Entry(frame, textvariable=self.update_server_var).pack(fill="x", pady=(6, 4))
        ttk.Label(
            frame,
            text=(
                "Host a manifest JSON file or a folder containing "
                f"{UPDATE_MANIFEST_FILENAME}."
            ),
            justify="left",
        ).pack(anchor="w", pady=(0, 10))

        ttk.Label(frame, text=f"Received files are saved to:\n{INBOX_DIR}", justify="left").pack(anchor="w", pady=(0, 12))

        buttons = ttk.Frame(frame)
        buttons.pack(fill="x")
        ttk.Button(buttons, text="Save", command=self.save).pack(side="left")
        ttk.Button(buttons, text="Check Updates", command=lambda: self.app.check_for_updates(manual=True)).pack(side="left", padx=(8, 0))
        ttk.Button(buttons, text="Close", command=self.hide).pack(side="right")

    def save(self) -> None:
        value = self.username_var.get().strip()
        if not value:
            messagebox.showerror("Invalid username", "Username cannot be empty.", parent=self)
            return
        self.app.config.username = value
        self.app.config.update_server_url = self.update_server_var.get().strip()
        self.app.refresh_tray_menu()
        self.hide()


class MainChatWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(app, f"{APP_NAME} - {APP_VERSION}", "980x660")
        self.minsize(860, 560)
        self.selected_ip: str | None = None

        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)

        container = ttk.Frame(self, padding=12)
        container.grid(row=0, column=0, sticky="nsew")
        container.columnconfigure(1, weight=1)
        container.rowconfigure(0, weight=1)

        sidebar = ttk.Frame(container, width=290)
        sidebar.grid(row=0, column=0, sticky="nsw", padx=(0, 12))
        sidebar.grid_propagate(False)

        ttk.Label(sidebar, text=f"Conversations  {APP_VERSION}").pack(anchor="w")
        ttk.Label(sidebar, text="Open a chat or use the paperclip to send a file.").pack(anchor="w", pady=(2, 8))

        sidebar_holder = tk.Frame(sidebar, bg="#eaf0f7", bd=0, highlightthickness=0)
        sidebar_holder.pack(fill="both", expand=True)
        self.sidebar_canvas = tk.Canvas(sidebar_holder, bg="#eaf0f7", bd=0, highlightthickness=0)
        self.sidebar_scroll = ttk.Scrollbar(sidebar_holder, orient="vertical", command=self.sidebar_canvas.yview)
        self.sidebar_canvas.configure(yscrollcommand=self.sidebar_scroll.set)
        self.sidebar_canvas.pack(side="left", fill="both", expand=True)
        self.sidebar_scroll.pack(side="right", fill="y")

        self.sidebar_list = tk.Frame(self.sidebar_canvas, bg="#eaf0f7")
        self.sidebar_window = self.sidebar_canvas.create_window((0, 0), window=self.sidebar_list, anchor="nw")
        self.sidebar_list.bind(
            "<Configure>",
            lambda _event: self.sidebar_canvas.configure(scrollregion=self.sidebar_canvas.bbox("all")),
        )
        self.sidebar_canvas.bind(
            "<Configure>",
            lambda event: self.sidebar_canvas.itemconfigure(self.sidebar_window, width=event.width),
        )

        chat = ttk.Frame(container)
        chat.grid(row=0, column=1, sticky="nsew")
        chat.columnconfigure(0, weight=1)
        chat.rowconfigure(1, weight=1)

        header = ttk.Frame(chat)
        header.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        header.columnconfigure(0, weight=1)
        self.header_name_var = tk.StringVar(value="No chat selected")
        self.header_status_var = tk.StringVar(value="Discovered contacts will appear in the sidebar.")
        ttk.Label(header, textvariable=self.header_name_var, font=("Helvetica", 16, "bold")).grid(row=0, column=0, sticky="w")
        ttk.Label(header, textvariable=self.header_status_var).grid(row=1, column=0, sticky="w", pady=(3, 0))
        self.contact_button = ttk.Button(header, text="Add Contact", command=self.add_selected_contact)
        self.contact_button.grid(row=0, column=1, rowspan=2, sticky="e")

        self.text = tk.Text(chat, wrap="word", state="disabled")
        self.text.grid(row=1, column=0, sticky="nsew")
        self._setup_message_view(self.text)

        self.transfer_label = ttk.Label(chat, text="Idle")
        self.transfer_label.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        self.transfer_bar = ttk.Progressbar(chat, mode="determinate")
        self.transfer_bar.grid(row=3, column=0, sticky="ew", pady=(4, 10))

        composer = ttk.Frame(chat)
        composer.grid(row=4, column=0, sticky="ew")
        composer.columnconfigure(1, weight=1)

        self.attach_button = ttk.Button(composer, text="📎", width=3, command=self.pick_file)
        self.attach_button.grid(row=0, column=0, sticky="nsw", padx=(0, 8))

        self.entry = tk.Text(composer, height=4, wrap="word")
        self.entry.grid(row=0, column=1, sticky="ew")
        self.entry.bind("<Return>", self.on_enter)

        ttk.Button(composer, text="Send", command=self.send_text).grid(row=0, column=2, sticky="nse", padx=(8, 0))

        self._init_drop_target()
        self.refresh()

    def show(self) -> None:
        super().show()
        if self.selected_ip is None:
            self.select_chat(None)
        elif self.selected_ip:
            self.app.mark_peer_read(self.selected_ip)

    def show_chat(self, ip: str | None) -> None:
        self.select_chat(ip)
        self.show()

    def is_conversation_active(self, ip: str) -> bool:
        return self.is_visible() and self.selected_ip == ip

    def rebind_peer(self, old_ip: str, new_ip: str, _peer_name: str) -> None:
        if self.selected_ip == old_ip:
            self.selected_ip = new_ip
        self.refresh()

    def refresh(self) -> None:
        conversation_ips = self.app.conversation_targets()
        if self.selected_ip not in conversation_ips:
            self.selected_ip = conversation_ips[0] if conversation_ips else None
        self.refresh_sidebar()
        self.refresh_current_chat()

    def refresh_for_message(self, ip: str) -> None:
        if self.selected_ip is None:
            self.selected_ip = ip
        self.refresh()

    def refresh_sidebar(self) -> None:
        for child in self.sidebar_list.winfo_children():
            child.destroy()

        conversation_ips = self.app.conversation_targets()
        if not conversation_ips:
            empty = tk.Label(
                self.sidebar_list,
                text="No conversations yet.\nPeers discovered on your LAN will appear here.",
                justify="left",
                bg="#eaf0f7",
                fg="#516273",
                padx=14,
                pady=14,
            )
            empty.pack(fill="x", pady=(2, 0))
            return

        for ip in conversation_ips:
            self._build_row(ip)

    def _build_row(self, ip: str) -> None:
        selected = ip == self.selected_ip
        bg = "#d8ebff" if selected else "#ffffff"
        frame = tk.Frame(self.sidebar_list, bg=bg, bd=1, highlightthickness=0)
        frame.pack(fill="x", pady=(0, 6))

        text_wrap = tk.Frame(frame, bg=bg)
        text_wrap.pack(side="left", fill="both", expand=True, padx=(10, 6), pady=10)

        name = self.app.conversation_name(ip)
        unread = self.app.unread_counts.get(ip, 0)
        title = name if unread == 0 else f"{name} ({unread})"
        preview = self.app.conversation_preview(ip)
        status = self.app.conversation_status(ip)

        title_label = tk.Label(
            text_wrap,
            text=title,
            anchor="w",
            bg=bg,
            fg="#15202b",
            font=("Helvetica", 11, "bold"),
        )
        title_label.pack(fill="x")
        preview_label = tk.Label(
            text_wrap,
            text=preview,
            anchor="w",
            justify="left",
            bg=bg,
            fg="#334556",
            font=("Helvetica", 10),
        )
        preview_label.pack(fill="x", pady=(2, 1))
        status_label = tk.Label(
            text_wrap,
            text=status,
            anchor="w",
            bg=bg,
            fg="#5f7284",
            font=("Helvetica", 9),
        )
        status_label.pack(fill="x")

        attach_button = tk.Button(
            frame,
            text="📎",
            command=lambda target_ip=ip: self.pick_file(target_ip),
            relief="flat",
            bg=bg,
            activebackground="#c7def7",
            bd=0,
            padx=10,
            pady=8,
        )
        attach_button.pack(side="right", padx=(0, 6), pady=6)

        for widget in (frame, text_wrap, title_label, preview_label, status_label):
            widget.bind("<Button-1>", lambda _event, target_ip=ip: self.select_chat(target_ip))

    def select_chat(self, ip: str | None) -> None:
        if ip is None:
            conversation_ips = self.app.conversation_targets()
            ip = conversation_ips[0] if conversation_ips else None
        if ip is None:
            self.selected_ip = None
            self.refresh_current_chat()
            return

        peer = self.app._resolve_active_peer(ip)
        self.selected_ip = peer.ip if peer is not None else ip
        self.refresh()
        if self.selected_ip:
            self.app.mark_peer_read(self.selected_ip)

    def refresh_current_chat(self) -> None:
        if not self.selected_ip:
            self.title(f"{APP_NAME} - {APP_VERSION}")
            self.header_name_var.set("No chat selected")
            self.header_status_var.set("Discovered contacts and saved contacts appear in the sidebar.")
            self.contact_button.state(["disabled"])
            self.attach_button.state(["disabled"])
            self._render_history([])
            self.transfer_label.config(text="Idle")
            self.transfer_bar["value"] = 0
            self.transfer_bar["maximum"] = 1
            return

        ip = self.selected_ip
        self.title(f"{APP_NAME} - {self.app.conversation_name(ip)} - {APP_VERSION}")
        self.header_name_var.set(self.app.conversation_name(ip))
        self.header_status_var.set(self.app.conversation_status(ip))

        peer = self.app.find_peer_by_ip(ip)
        if peer is not None and not self.app.is_contact(peer):
            self.contact_button.state(["!disabled"])
        else:
            self.contact_button.state(["disabled"])
        self.attach_button.state(["!disabled"])

        self._render_history(self.app.message_history.get(ip, []))
        self.refresh_transfer(ip)

    def _render_history(self, entries: list[MessageEntry]) -> None:
        self.text.config(state="normal")
        self.text.delete("1.0", "end")
        self.text.config(state="disabled")
        for entry in entries:
            self._append_bubble(self.text, self.app.username, entry)

    def refresh_transfer(self, ip: str | None = None) -> None:
        target_ip = ip or self.selected_ip
        if not target_ip or target_ip != self.selected_ip:
            return

        transfer = self.app.transfer_statuses.get(target_ip)
        if transfer is None:
            self.transfer_label.config(text="Idle")
            self.transfer_bar["value"] = 0
            self.transfer_bar["maximum"] = 1
            return

        label, current, total = transfer
        self.transfer_label.config(text=f"{label} ({format_bytes(current)} / {format_bytes(total)})")
        self.transfer_bar["maximum"] = max(total, 1)
        self.transfer_bar["value"] = current

    def add_selected_contact(self) -> None:
        if not self.selected_ip:
            return
        self.app.add_contact_from_peer(self.selected_ip)

    def _init_drop_target(self) -> None:
        if DND_FILES is None:
            return
        try:
            dnd_entry = cast(Any, self.entry)
            dnd_entry.drop_target_register(DND_FILES)
            dnd_entry.dnd_bind("<<Drop>>", self.on_drop_files)
        except Exception:
            pass

    def on_drop_files(self, event: Any) -> None:
        if not self.selected_ip:
            return
        paths = self.tk.splitlist(event.data)
        for raw_path in paths:
            path = raw_path.strip("{}")
            if Path(path).is_file():
                self.app.add_message(self.selected_ip, "System", f"Sending file: {Path(path).name}", incoming=False)
                self.app.send_file(self.selected_ip, path)

    def on_enter(self, event) -> str | None:
        if event.state & 0x0001:
            return None
        self.send_text()
        return "break"

    def send_text(self) -> None:
        if not self.selected_ip:
            return
        content = self.entry.get("1.0", "end").strip()
        if not content:
            return
        self.entry.delete("1.0", "end")
        self.app.send_text(self.selected_ip, content)

    def pick_file(self, ip: str | None = None) -> None:
        target_ip = ip or self.selected_ip
        if not target_ip:
            return
        path = filedialog.askopenfilename(parent=self)
        if not path:
            return
        self.app.add_message(target_ip, "System", f"Sending file: {Path(path).name}", incoming=False)
        self.app.send_file(target_ip, path)


def main() -> None:
    ok, error = verify_gui_runtime()
    if not ok:
        print(error, file=sys.stderr)
        sys.exit(1)

    app = LanMessengerApp()
    app.root.mainloop()


if __name__ == "__main__":
    main()
