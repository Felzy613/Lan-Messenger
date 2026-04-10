import base64
import importlib
import json
import os
import queue
import socket
import struct
import sys
import threading
import time
import traceback
import uuid
import webbrowser
from dataclasses import dataclass
from pathlib import Path
from types import ModuleType
import tkinter as tk
from tkinter import ttk, messagebox, filedialog, simpledialog, font as tkfont
from urllib.error import URLError
from urllib.parse import urljoin
from urllib.request import urlopen
from typing import Any, cast

from PIL import Image, ImageDraw

try:
    import pystray
    from pystray import MenuItem as TrayItem
except Exception:
    pystray = None
    TrayItem = None

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
    NSApplicationActivationPolicyAccessory = getattr(appkit_module, "NSApplicationActivationPolicyAccessory", 1)
except Exception:
    NSApp = None
    NSApplicationActivationPolicyAccessory = None

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import x25519
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


APP_NAME = "LAN Messenger"
APP_VERSION = "1.3.3"
APP_TITLE = f"{APP_NAME} v{APP_VERSION}"
UPDATE_MANIFEST_FILENAME = "lan-messenger-update.json"
DISCOVERY_PORT = 54231
TCP_PORT = 54232
DISCOVERY_MULTICAST_GROUP = "239.255.42.99"
DISCOVERY_MULTICAST_TTL = 1
DISCOVERY_INTERVAL = 1.5
PEER_TIMEOUT = 7
BUFFER_SIZE = 64 * 1024
CONFIG_DIR = Path.home() / ".lan_messenger"
CONFIG_FILE = CONFIG_DIR / "config.json"
INBOX_DIR = CONFIG_DIR / "received"
HISTORY_FILE = CONFIG_DIR / "history.enc"
LOG_FILE = CONFIG_DIR / "app.log"

UI_COLORS = {
    "app_bg": "#eef3f8",
    "panel_bg": "#f7fafc",
    "card_bg": "#ffffff",
    "card_selected": "#dceeff",
    "border": "#d6dee8",
    "shadow": "#dbe5f0",
    "text": "#16202a",
    "muted": "#5f6f82",
    "accent": "#2f80ed",
    "accent_active": "#1f6fd8",
    "accent_soft": "#eaf3ff",
    "success": "#1f9d68",
    "success_bg": "#dff7eb",
    "danger": "#cf3f4f",
    "danger_bg": "#fde8eb",
    "composer_bg": "#ffffff",
    "incoming_bg": "#ffffff",
    "outgoing_bg": "#dff1ff",
}
UI_FONT = "Segoe UI"
TRANSFER_STATUS_CLEAR_DELAY_MS = 1800


def resolve_ui_font_family(root: tk.Tk) -> str:
    try:
        available_fonts = {name.lower() for name in tkfont.families(root)}
    except Exception:
        return UI_FONT

    candidates = ["Segoe UI", "SF Pro Text", "Helvetica Neue", "Arial", "Helvetica"]
    for family in candidates:
        if family.lower() in available_fonts:
            return family

    try:
        return cast(str, tkfont.nametofont("TkDefaultFont").cget("family"))
    except Exception:
        return UI_FONT


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
    try:
        root.lower()
    except Exception:
        pass
    root.update_idletasks()


def rounded_rect_points(x1: int, y1: int, x2: int, y2: int, radius: int) -> list[int]:
    radius = max(0, min(radius, (x2 - x1) // 2, (y2 - y1) // 2))
    return [
        x1 + radius, y1,
        x1 + radius, y1,
        x2 - radius, y1,
        x2 - radius, y1,
        x2, y1,
        x2, y1 + radius,
        x2, y1 + radius,
        x2, y2 - radius,
        x2, y2 - radius,
        x2, y2,
        x2 - radius, y2,
        x2 - radius, y2,
        x1 + radius, y2,
        x1 + radius, y2,
        x1, y2,
        x1, y2 - radius,
        x1, y2 - radius,
        x1, y1 + radius,
        x1, y1 + radius,
        x1, y1,
    ]


class RoundedPanel(tk.Canvas):
    def __init__(
        self,
        parent: tk.Misc,
        *,
        background: str,
        fill: str,
        border: str,
        radius: int = 18,
        padding: tuple[int, int] = (14, 12),
        stretch: bool = False,
    ) -> None:
        super().__init__(parent, bg=background, highlightthickness=0, bd=0, relief="flat")
        self.fill = fill
        self.border = border
        self.radius = radius
        self.pad_x, self.pad_y = padding
        self.stretch = stretch
        self._last_size = (0, 0)
        self._redraw_pending = False
        self._redrawing = False
        self.content = tk.Frame(self, bg=fill, bd=0, highlightthickness=0)
        self._window_id = self.create_window((self.pad_x, self.pad_y), window=self.content, anchor="nw")
        self.bind("<Configure>", self._queue_redraw)
        self.content.bind("<Configure>", self._queue_redraw)
        self.after_idle(self._redraw)

    def _queue_redraw(self, _event=None) -> None:
        if self._redraw_pending or not self.winfo_exists():
            return
        self._redraw_pending = True
        self.after_idle(self._redraw)

    def _redraw(self) -> None:
        self._redraw_pending = False
        if not self.winfo_exists() or self._redrawing:
            return
        self._redrawing = True
        try:
            requested_width = max(self.content.winfo_reqwidth() + (self.pad_x * 2), 2)
            requested_height = max(self.content.winfo_reqheight() + (self.pad_y * 2), 2)
            width = requested_width
            height = requested_height
            if self.stretch:
                width = max(width, self.winfo_width(), 2)
                height = max(height, self.winfo_height(), 2)
            if self._last_size != (width, height):
                self._last_size = (width, height)
                self.configure(width=width, height=height)

            inner_width = max(width - (self.pad_x * 2), 1)
            self.coords(self._window_id, self.pad_x, self.pad_y)
            self.itemconfigure(
                self._window_id,
                width=inner_width if self.stretch else max(self.content.winfo_reqwidth(), 1),
            )

            self.delete("panel")
            self.create_polygon(
                rounded_rect_points(3, 4, max(width - 2, 4), max(height - 2, 4), self.radius),
                smooth=True,
                splinesteps=36,
                fill=UI_COLORS["shadow"],
                outline=UI_COLORS["shadow"],
                width=1,
                tags="panel",
            )
            self.create_polygon(
                rounded_rect_points(1, 1, max(width - 1, 2), max(height - 1, 2), self.radius),
                smooth=True,
                splinesteps=36,
                fill=self.fill,
                outline=self.border,
                width=1,
                tags="panel",
            )
            self.tag_lower("panel")
        finally:
            self._redrawing = False


class RoundedButton(tk.Canvas):
    def __init__(
        self,
        parent: tk.Misc,
        *,
        text: str,
        command,
        background: str,
        fill: str,
        hover_fill: str,
        text_color: str,
        disabled_fill: str,
        disabled_text: str,
        radius: int = 16,
        padding: tuple[int, int] = (14, 9),
        font: tuple[str, int, str] | tuple[str, int] | None = None,
        min_width: int = 0,
    ) -> None:
        super().__init__(parent, bg=background, highlightthickness=0, bd=0, relief="flat", cursor="hand2")
        self._text = text
        self._command = command
        self._fill = fill
        self._hover_fill = hover_fill
        self._text_color = text_color
        self._disabled_fill = disabled_fill
        self._disabled_text = disabled_text
        self._radius = radius
        self._pad_x, self._pad_y = padding
        self._font = tkfont.Font(root=self, font=font or (UI_FONT, 10, "bold"))
        self._min_width = min_width
        self._hovered = False
        self._enabled = True
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<Button-1>", self._on_click)
        self._redraw()

    def _button_size(self) -> tuple[int, int]:
        width = max(self._min_width, self._font.measure(self._text) + (self._pad_x * 2))
        height = self._font.metrics("linespace") + (self._pad_y * 2)
        return width, height

    def _on_enter(self, _event) -> None:
        self._hovered = True
        self._redraw()

    def _on_leave(self, _event) -> None:
        self._hovered = False
        self._redraw()

    def _on_click(self, _event) -> None:
        if self._enabled:
            self._command()

    def set_enabled(self, enabled: bool) -> None:
        self._enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._redraw()

    def _redraw(self) -> None:
        width, height = self._button_size()
        self.configure(width=width, height=height)
        fill = self._fill if self._enabled else self._disabled_fill
        if self._enabled and self._hovered:
            fill = self._hover_fill
        text_color = self._text_color if self._enabled else self._disabled_text
        self.delete("all")
        self.create_polygon(
            rounded_rect_points(1, 1, max(width - 1, 2), max(height - 1, 2), self._radius),
            smooth=True,
            splinesteps=36,
            fill=fill,
            outline=fill,
            width=1,
        )
        self.create_text(width / 2, height / 2, text=self._text, fill=text_color, font=self._font)


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
        self.inbox_dir.mkdir(parents=True, exist_ok=True)

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
            "inbox_dir": str(INBOX_DIR),
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

    @property
    def inbox_dir(self) -> Path:
        raw_value = str(self.data.get("inbox_dir", "")).strip()
        path = Path(raw_value).expanduser() if raw_value else INBOX_DIR
        return path

    @inbox_dir.setter
    def inbox_dir(self, value: str | Path) -> None:
        path = Path(value).expanduser()
        if not path.is_absolute():
            path = (CONFIG_DIR / path).resolve()
        path.mkdir(parents=True, exist_ok=True)
        self.data["inbox_dir"] = str(path)
        self._save(self.data)


class CryptoBox:
    def __init__(self, private_key: x25519.X25519PrivateKey) -> None:
        self.private_key = private_key
        self.private_key_bytes = private_key.private_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PrivateFormat.Raw,
            encryption_algorithm=serialization.NoEncryption(),
        )
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

    def _history_key(self) -> bytes:
        return HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=None,
            info=b"lan-messenger-history",
        ).derive(self.private_key_bytes)

    def encrypt_local(self, plaintext: bytes, aad: bytes = b"") -> tuple[str, str]:
        nonce = os.urandom(12)
        ciphertext = AESGCM(self._history_key()).encrypt(nonce, plaintext, aad)
        return b64e(nonce), b64e(ciphertext)

    def decrypt_local(self, nonce_b64: str, ciphertext_b64: str, aad: bytes = b"") -> bytes:
        return AESGCM(self._history_key()).decrypt(b64d(nonce_b64), b64d(ciphertext_b64), aad)


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
        self._configure_theme()

        self.config = ConfigStore()
        self.crypto = CryptoBox(self.config.private_key)
        self.notifications = NotificationManager()

        self.peers: dict[str, Peer] = {}
        self.message_history: dict[str, list[MessageEntry]] = {}
        self.unread_counts: dict[str, int] = {}
        self.transfer_statuses: dict[str, tuple[str, int, int]] = {}
        self.transfer_status_tokens: dict[str, int] = {}
        self.incoming_files: dict[tuple[str, str], dict] = {}
        self.ui_queue: "queue.Queue[tuple]" = queue.Queue()
        self.running = True
        self.local_ips = self._detect_local_ips()
        self.local_ip = self._preferred_local_ip()
        self.main_window: "MainChatWindow | None" = None
        self.contacts_window: "ContactsWindow | None" = None
        self.settings_window: "SettingsWindow | None" = None
        self.latest_update_info: UpdateInfo | None = None
        self.history_lock = threading.Lock()

        self.message_history = self._load_message_history()

        self.icon = self._create_tray_icon()

        self.discovery_thread = threading.Thread(target=self.discovery_broadcast_loop, daemon=True)
        self.discovery_listener_thread = threading.Thread(target=self.discovery_listener_loop, daemon=True)
        self.server_thread = threading.Thread(target=self.tcp_server_loop, daemon=True)
        self.cleanup_thread = threading.Thread(target=self.peer_cleanup_loop, daemon=True)

        self.discovery_thread.start()
        self.discovery_listener_thread.start()
        self.server_thread.start()
        self.cleanup_thread.start()

        self.root.after(0, self.show_main_window)
        self.root.after(200, self._start_tray_icon)
        self.root.after(100, self.process_ui_queue)

    def prepare_window_host(self) -> None:
        try:
            self.root.deiconify()
            self.root.overrideredirect(True)
            self.root.geometry("1x1+0+0")
            self.root.configure(bg=UI_COLORS["app_bg"])
            try:
                self.root.attributes("-alpha", 0.0)
            except Exception:
                pass
            self.root.lower()
            self.root.update_idletasks()
        except Exception:
            pass

    def activate_application(self) -> None:
        if NSApp is None:
            return
        try:
            app = cast(Any, NSApp() if callable(NSApp) else NSApp)
            if (
                app is not None
                and NSApplicationActivationPolicyAccessory is not None
                and hasattr(app, "setActivationPolicy_")
            ):
                app.setActivationPolicy_(NSApplicationActivationPolicyAccessory)
            if app is not None and hasattr(app, "activateIgnoringOtherApps_"):
                app.activateIgnoringOtherApps_(True)
        except Exception:
            pass

    def _configure_theme(self) -> None:
        global UI_FONT
        UI_FONT = resolve_ui_font_family(self.root)

        for font_name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont", "TkIconFont", "TkTooltipFont"):
            try:
                tkfont.nametofont(font_name).configure(family=UI_FONT, size=10)
            except Exception:
                pass
        self.root.configure(bg=UI_COLORS["app_bg"])

        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except Exception:
            pass

        style.configure(".", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"], font=(UI_FONT, 10))
        style.configure("TFrame", background=UI_COLORS["app_bg"])
        style.configure("TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"])
        style.configure("Muted.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["muted"], font=(UI_FONT, 9))
        style.configure("Heading.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"], font=(UI_FONT, 16, "bold"))
        style.configure("Subheading.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["muted"], font=(UI_FONT, 10))
        style.configure(
            "TButton",
            background=UI_COLORS["card_bg"],
            foreground=UI_COLORS["text"],
            borderwidth=0,
            focusthickness=0,
            focuscolor=UI_COLORS["card_bg"],
            padding=(12, 8),
            relief="flat",
        )
        style.map("TButton", background=[("active", UI_COLORS["accent_soft"])])
        style.configure(
            "Primary.TButton",
            background=UI_COLORS["accent"],
            foreground="#ffffff",
            borderwidth=0,
            focusthickness=0,
            focuscolor=UI_COLORS["accent"],
            padding=(14, 9),
            relief="flat",
        )
        style.map("Primary.TButton", background=[("active", UI_COLORS["accent_active"])], foreground=[("active", "#ffffff")])
        style.configure(
            "TEntry",
            fieldbackground=UI_COLORS["card_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            insertcolor=UI_COLORS["text"],
            padding=8,
            relief="flat",
        )
        style.configure(
            "TCombobox",
            fieldbackground=UI_COLORS["card_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            arrowsize=16,
            padding=6,
            relief="flat",
        )
        style.configure(
            "Treeview",
            background=UI_COLORS["card_bg"],
            fieldbackground=UI_COLORS["card_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            rowheight=32,
            relief="flat",
        )
        style.map("Treeview", background=[("selected", UI_COLORS["card_selected"])], foreground=[("selected", UI_COLORS["text"])])
        style.configure(
            "Treeview.Heading",
            background=UI_COLORS["panel_bg"],
            foreground=UI_COLORS["muted"],
            borderwidth=0,
            relief="flat",
            padding=(10, 8),
            font=(UI_FONT, 9, "bold"),
        )
        style.configure(
            "Horizontal.TProgressbar",
            troughcolor=UI_COLORS["accent_soft"],
            background=UI_COLORS["accent"],
            borderwidth=0,
            lightcolor=UI_COLORS["accent"],
            darkcolor=UI_COLORS["accent"],
        )

    def _load_message_history(self) -> dict[str, list[MessageEntry]]:
        if not HISTORY_FILE.exists():
            return {}

        try:
            payload = json.loads(HISTORY_FILE.read_text(encoding="utf-8"))
            nonce = str(payload.get("nonce", "")).strip()
            ciphertext = str(payload.get("ciphertext", "")).strip()
            if not nonce or not ciphertext:
                return {}
            decrypted = self.crypto.decrypt_local(nonce, ciphertext, aad=b"history-v1")
            raw_history = json.loads(decrypted.decode("utf-8"))
        except Exception:
            return {}

        history: dict[str, list[MessageEntry]] = {}
        if not isinstance(raw_history, dict):
            return history

        for ip, entries in raw_history.items():
            if not isinstance(ip, str) or not isinstance(entries, list):
                continue
            parsed: list[MessageEntry] = []
            for item in entries:
                if not isinstance(item, dict):
                    continue
                parsed.append(MessageEntry(
                    sender=str(item.get("sender", "")).strip() or "Unknown",
                    text=str(item.get("text", "")),
                    incoming=bool(item.get("incoming")),
                    timestamp=float(item.get("timestamp") or now()),
                    message_id=str(item.get("message_id", "")).strip() or None,
                    status=str(item.get("status", "")),
                    read_receipt_sent=bool(item.get("read_receipt_sent")),
                ))
            if parsed:
                history[ip] = parsed[-200:]
        return history

    def _save_message_history(self) -> None:
        serializable = {
            ip: [
                {
                    "sender": entry.sender,
                    "text": entry.text,
                    "incoming": entry.incoming,
                    "timestamp": entry.timestamp,
                    "message_id": entry.message_id,
                    "status": entry.status,
                    "read_receipt_sent": entry.read_receipt_sent,
                }
                for entry in entries[-200:]
            ]
            for ip, entries in self.message_history.items()
            if entries
        }

        try:
            plaintext = json.dumps(serializable, separators=(",", ":")).encode("utf-8")
            nonce, ciphertext = self.crypto.encrypt_local(plaintext, aad=b"history-v1")
            with self.history_lock:
                HISTORY_FILE.write_text(
                    json.dumps({"nonce": nonce, "ciphertext": ciphertext}),
                    encoding="utf-8",
                )
        except Exception:
            pass

    @property
    def username(self) -> str:
        return self.config.username

    @property
    def inbox_dir(self) -> Path:
        return self.config.inbox_dir

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

    def log_runtime_error(self, context: str, exc: BaseException) -> None:
        try:
            CONFIG_DIR.mkdir(parents=True, exist_ok=True)
            with LOG_FILE.open("a", encoding="utf-8") as handle:
                handle.write(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {context}\n")
                handle.write("".join(traceback.format_exception(type(exc), exc, exc.__traceback__)))
                handle.write("\n")
        except Exception:
            pass

    def _dispatch_ui_action(self, action: str, args: tuple[Any, ...]) -> None:
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
        elif action == "shutdown_from_tray":
            self.quit()
        elif action == "shutdown":
            self._shutdown_ui()

    def process_ui_queue(self) -> None:
        try:
            while True:
                try:
                    action, args = self.ui_queue.get_nowait()
                except queue.Empty:
                    break

                try:
                    self._dispatch_ui_action(action, args)
                except Exception as exc:
                    self.log_runtime_error(f"UI action failed: {action}", exc)
        finally:
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

    def trigger_discovery_scan(self) -> None:
        payload = json.dumps(self.discovery_payload("discovery")).encode("utf-8")
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            try:
                sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, DISCOVERY_MULTICAST_TTL)
            except OSError:
                pass
            for target in self.discovery_targets():
                try:
                    sock.sendto(payload, (target, DISCOVERY_PORT))
                except OSError:
                    pass
        finally:
            sock.close()

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
            time.sleep(1)

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
            temp_path = self.inbox_dir / f"{transfer_id}_{filename}.part"
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
        target = self.inbox_dir / sanitize_filename(filename)
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
        self._save_message_history()

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
        if self.icon is None:
            return
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

    def _tray_item(self, *args: Any, **kwargs: Any) -> Any:
        tray_item_factory = cast(Any, TrayItem)
        if tray_item_factory is None:
            raise RuntimeError("Tray menu item factory is unavailable.")
        return tray_item_factory(*args, **kwargs)

    def _pystray_module(self) -> ModuleType:
        module = pystray
        if module is None:
            raise RuntimeError("pystray is unavailable.")
        return module

    def _quick_chat_menu_items(self) -> list[Any]:
        peers = self.online_peers()[:10]
        items: list[Any] = []
        for peer in peers:
            unread = self.unread_counts.get(peer.ip, 0)
            label = peer.username if unread == 0 else f"{peer.username} ({unread})"
            items.append(self._tray_item(label, lambda _, __, ip=peer.ip: self.enqueue_ui("show_quick_chat", ip)))
        if not items:
            items.append(self._tray_item("No peers online", lambda *_: None, enabled=False))
        return items

    def _chat_menu_items(self) -> list[Any]:
        peers = self.online_peers()
        if not peers:
            return [self._tray_item("No peers online", lambda *_: None, enabled=False)]

        items: list[Any] = []
        for peer in peers[:20]:
            unread = self.unread_counts.get(peer.ip, 0)
            status = "online"
            label = f"{peer.username} [{status}]"
            if unread:
                label = f"{label} ({unread})"
            items.append(self._tray_item(label, lambda _, __, ip=peer.ip: self.enqueue_ui("show_quick_chat", ip)))
        return items

    def _file_transfer_menu_items(self) -> list[Any]:
        peers = self.online_peers()
        if not peers:
            return [self._tray_item("No peers online", lambda *_: None, enabled=False)]

        return [
            self._tray_item(peer.username, lambda _, __, ip=peer.ip: self.enqueue_ui("prompt_send_file", ip))
            for peer in peers[:20]
        ]

    def _build_tray_menu(self):
        return self._pystray_module().Menu(
            self._tray_item(APP_TITLE, lambda *_: None, enabled=False),
            self._tray_item("Open Chat", lambda *_: self.enqueue_ui("show_main_chat"), default=True),
            self._tray_item("Contact List", lambda *_: self.enqueue_ui("show_contacts")),
            self._tray_item("Check for Updates", lambda *_: self.enqueue_ui("check_updates", True)),
            self._tray_item("Settings", lambda *_: self.enqueue_ui("show_settings")),
            self._tray_item("Exit", lambda *_: self.enqueue_ui("shutdown_from_tray")),
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

        self._save_message_history()
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

        if receipts:
            self._save_message_history()

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
        self._save_message_history()
        self.refresh_tray_menu()

    def update_transfer_status(self, ip: str, label: str, current: int, total: int) -> None:
        self.transfer_status_tokens[ip] = self.transfer_status_tokens.get(ip, 0) + 1
        self.transfer_statuses[ip] = (label, current, total)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_transfer(ip)

    def finish_transfer_status(self, ip: str, label: str) -> None:
        current = self.transfer_statuses.get(ip)
        total = current[2] if current is not None else 1
        token = self.transfer_status_tokens.get(ip, 0) + 1
        self.transfer_status_tokens[ip] = token
        self.transfer_statuses[ip] = (f"{label} complete", total, total)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_transfer(ip)
        self.root.after(TRANSFER_STATUS_CLEAR_DELAY_MS, lambda: self._clear_transfer_status(ip, token))

    def _clear_transfer_status(self, ip: str, token: int) -> None:
        if self.transfer_status_tokens.get(ip) != token:
            return
        self.transfer_statuses.pop(ip, None)
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
        if pystray is None or TrayItem is None:
            return None
        try:
            return pystray.Icon(APP_NAME, self._tray_image(), APP_TITLE, self._build_tray_menu())
        except Exception as exc:
            self.log_runtime_error("Tray icon initialization failed", exc)
            return None

    def _start_tray_icon(self) -> None:
        if self.icon is None:
            return
        if sys.platform.startswith("win"):
            threading.Thread(target=self.icon.run, daemon=True).start()
            return

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
        if self.icon is not None:
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
        self.configure(bg=UI_COLORS["app_bg"])
        self.withdraw()
        self.protocol("WM_DELETE_WINDOW", self.hide)
        self._configure_window_chrome()

    def _configure_window_chrome(self) -> None:
        try:
            if sys.platform.startswith("win"):
                self.wm_attributes("-toolwindow", True)
        except Exception:
            pass

    def show(self) -> None:
        self.app.prepare_window_host()
        self.deiconify()
        self.after_idle(self.lift)
        self.after_idle(self.focus_force)
        self.app.activate_application()

    def hide(self) -> None:
        if self is getattr(self.app, "main_window", None) and self.app.icon is None:
            self.iconify()
            return
        self.withdraw()

    def is_visible(self) -> bool:
        try:
            return self.state() != "withdrawn"
        except tk.TclError:
            return False

    def _bind_mousewheel(self, canvas: tk.Canvas, *widgets: tk.Misc) -> None:
        def bind(_event=None) -> None:
            self.bind_all("<MouseWheel>", lambda event: self._on_mousewheel(canvas, event), add="+")
            self.bind_all("<Button-4>", lambda event: self._on_mousewheel(canvas, event), add="+")
            self.bind_all("<Button-5>", lambda event: self._on_mousewheel(canvas, event), add="+")

        def unbind(_event=None) -> None:
            self.unbind_all("<MouseWheel>")
            self.unbind_all("<Button-4>")
            self.unbind_all("<Button-5>")

        for widget in (canvas, *widgets):
            widget.bind("<Enter>", bind, add="+")
            widget.bind("<Leave>", unbind, add="+")

    def _on_mousewheel(self, canvas: tk.Canvas, event: Any) -> str:
        if getattr(event, "num", None) == 4:
            delta = -1
        elif getattr(event, "num", None) == 5:
            delta = 1
        elif sys.platform == "darwin":
            delta = -1 if event.delta > 0 else 1
        else:
            delta = -int(event.delta / 120) if event.delta else 0
        if delta:
            canvas.yview_scroll(delta, "units")
        return "break"

    def _append_bubble(self, widget: tk.Frame, my_username: str, entry: MessageEntry, wraplength: int) -> None:
        timestamp = format_message_time(entry.timestamp)
        normalized_sender = entry.sender.strip().lower()
        own_sender = my_username.strip().lower()

        if normalized_sender == "system":
            card = RoundedPanel(
                widget,
                background=UI_COLORS["panel_bg"],
                fill=UI_COLORS["card_bg"],
                border=UI_COLORS["border"],
                radius=16,
                padding=(14, 10),
            )
            card.pack(pady=(0, 10))
            tk.Label(
                card.content,
                text=f"{timestamp}\n{entry.text}",
                justify="center",
                bg=UI_COLORS["card_bg"],
                fg=UI_COLORS["muted"],
                font=(UI_FONT, 10, "italic"),
                wraplength=wraplength,
            ).pack()
            return

        direction = "outgoing" if normalized_sender == own_sender else "incoming"
        header = "You" if direction == "outgoing" else entry.sender
        header_line = f"{header}  {timestamp}"
        if direction == "outgoing" and entry.status:
            header_line = f"{header_line}  {entry.status}"
        row = tk.Frame(widget, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        row.pack(fill="x", pady=(0, 10))

        align = tk.Frame(row, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        align.pack(anchor="e" if direction == "outgoing" else "w")

        justify = "right" if direction == "outgoing" else "left"
        anchor = "e" if direction == "outgoing" else "w"
        bubble_fill = UI_COLORS["outgoing_bg"] if direction == "outgoing" else UI_COLORS["incoming_bg"]
        header_label = tk.Label(
            align,
            text=header_line,
            justify=justify,
            anchor=anchor,
            bg=UI_COLORS["panel_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9),
        )
        header_label.pack(anchor=anchor, padx=4, pady=(0, 4))

        bubble = RoundedPanel(
            align,
            background=UI_COLORS["panel_bg"],
            fill=bubble_fill,
            border=UI_COLORS["border"],
            radius=18,
            padding=(14, 11),
        )
        bubble.pack(anchor=anchor)
        tk.Label(
            bubble.content,
            text=entry.text,
            justify=justify,
            anchor=anchor,
            bg=bubble_fill,
            fg=UI_COLORS["text"],
            font=(UI_FONT, 11),
            wraplength=wraplength,
        ).pack()


class ContactsWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(app, f"Contact List - {APP_TITLE}", "560x500")
        self.minsize(540, 470)

        frame = ttk.Frame(self, padding=18)
        frame.pack(fill="both", expand=True, padx=12, pady=12)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(2, weight=1)

        ttk.Label(frame, text="Contact List", style="Heading.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(frame, text="Saved peers and live LAN discovery.", style="Subheading.TLabel").grid(row=1, column=0, sticky="w", pady=(2, 12))

        toolbar = ttk.Frame(frame)
        toolbar.grid(row=2, column=0, sticky="ew", pady=(0, 10))
        toolbar.columnconfigure(0, weight=1)
        ttk.Button(toolbar, text="Search for New Contacts", command=self.search_for_contacts, style="Primary.TButton").pack(side="left")
        ttk.Button(toolbar, text="Open Chat", command=self.chat_selected).pack(side="left", padx=(8, 0))
        ttk.Button(toolbar, text="Remove", command=self.remove_selected).pack(side="left", padx=(8, 0))

        table_card = RoundedPanel(
            frame,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=22,
            padding=(12, 12),
            stretch=True,
        )
        table_card.grid(row=3, column=0, sticky="nsew")
        table_card.content.columnconfigure(0, weight=1)
        table_card.content.rowconfigure(0, weight=1)

        self.tree = ttk.Treeview(table_card.content, columns=("name", "status", "ip"), show="headings", height=12)
        self.tree.heading("name", text="Name")
        self.tree.heading("status", text="Status")
        self.tree.heading("ip", text="Last IP")
        self.tree.column("name", width=200, anchor="w")
        self.tree.column("status", width=110, anchor="center")
        self.tree.column("ip", width=170, anchor="w")
        self.tree.tag_configure("online", background="#eefaf4")
        self.tree.tag_configure("offline", background="#fff4f6")
        self.tree.grid(row=0, column=0, sticky="nsew")

        online_frame = ttk.Frame(frame)
        online_frame.grid(row=4, column=0, sticky="ew", pady=(12, 0))
        online_frame.columnconfigure(0, weight=1)
        ttk.Label(online_frame, text="Add new contact from discovered peers", style="Subheading.TLabel").pack(anchor="w", pady=(0, 6))
        self.online_var = tk.StringVar()
        self.online_combo = ttk.Combobox(online_frame, textvariable=self.online_var, state="readonly")
        self.online_combo.pack(side="left", fill="x", expand=True, padx=(0, 8))
        ttk.Button(online_frame, text="Add New Contact", command=self.add_selected_online, style="Primary.TButton").pack(side="right")

        self.refresh()

    def search_for_contacts(self) -> None:
        self.app.trigger_discovery_scan()
        self.refresh()

    def refresh(self) -> None:
        for item_id in self.tree.get_children():
            self.tree.delete(item_id)

        for contact in sorted(self.app.contacts, key=lambda contact: contact.username.lower()):
            peer = self.app.find_peer_for_contact(contact)
            status = "Online" if peer is not None else "Offline"
            display_ip = peer.ip if peer is not None else contact.last_ip
            tag = "online" if peer is not None else "offline"
            self.tree.insert("", "end", iid=contact.public_key_b64, values=(contact.username, status, display_ip), tags=(tag,))

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
        super().__init__(app, f"Settings - {APP_TITLE}", "580x620")
        self.minsize(540, 560)

        frame = ttk.Frame(self, padding=18)
        frame.pack(fill="both", expand=True, padx=12, pady=12)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)

        ttk.Label(frame, text="Settings", style="Heading.TLabel").grid(row=0, column=0, sticky="w")

        shell = RoundedPanel(
            frame,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=24,
            padding=(0, 0),
            stretch=True,
        )
        shell.grid(row=1, column=0, sticky="nsew", pady=(12, 0))
        shell.content.columnconfigure(0, weight=1)
        shell.content.rowconfigure(0, weight=1)

        self.settings_canvas = tk.Canvas(shell.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.settings_canvas.grid(row=0, column=0, sticky="nsew")
        self.settings_body = tk.Frame(self.settings_canvas, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.settings_window = self.settings_canvas.create_window((0, 0), window=self.settings_body, anchor="nw")
        self.settings_body.bind(
            "<Configure>",
            lambda _event: self.settings_canvas.configure(scrollregion=self.settings_canvas.bbox("all")),
        )
        self.settings_canvas.bind(
            "<Configure>",
            lambda event: self.settings_canvas.itemconfigure(self.settings_window, width=event.width),
        )

        self._bind_mousewheel(self.settings_canvas, self.settings_body)

        self.settings_body.columnconfigure(0, weight=1)
        ttk.Label(
            self.settings_body,
            text="Identity, updates, and encrypted local storage.",
            style="Subheading.TLabel",
        ).grid(row=0, column=0, sticky="w", padx=18, pady=(18, 14))

        profile_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=20,
            padding=(16, 16),
            stretch=True,
        )
        profile_card.grid(row=1, column=0, sticky="ew", padx=18)
        profile_card.content.columnconfigure(0, weight=1)

        ttk.Label(profile_card.content, text="Profile", style="Heading.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(profile_card.content, text=f"Version: {APP_VERSION}", style="Subheading.TLabel").grid(row=1, column=0, sticky="w", pady=(4, 12))
        ttk.Label(profile_card.content, text="Username").grid(row=2, column=0, sticky="w")
        self.username_var = tk.StringVar(value=self.app.username)
        ttk.Entry(profile_card.content, textvariable=self.username_var).grid(row=3, column=0, sticky="ew", pady=(6, 0))

        updates_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=20,
            padding=(16, 16),
            stretch=True,
        )
        updates_card.grid(row=2, column=0, sticky="ew", padx=18, pady=(12, 0))
        updates_card.content.columnconfigure(0, weight=1)

        ttk.Label(updates_card.content, text="Updates", style="Heading.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(updates_card.content, text="Update Server URL").grid(row=1, column=0, sticky="w", pady=(12, 0))
        self.update_server_var = tk.StringVar(value=self.app.config.update_server_url)
        ttk.Entry(updates_card.content, textvariable=self.update_server_var).grid(row=2, column=0, sticky="ew", pady=(6, 4))
        ttk.Label(
            updates_card.content,
            text=(
                "Host a manifest JSON file or a folder containing "
                f"{UPDATE_MANIFEST_FILENAME}."
            ),
            justify="left",
            style="Subheading.TLabel",
        ).grid(row=3, column=0, sticky="w")

        storage_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=20,
            padding=(16, 16),
            stretch=True,
        )
        storage_card.grid(row=3, column=0, sticky="ew", padx=18, pady=(12, 0))
        storage_card.content.columnconfigure(0, weight=1)

        ttk.Label(storage_card.content, text="Storage", style="Heading.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(storage_card.content, text="Received Files Folder").grid(row=1, column=0, sticky="w", pady=(12, 0))
        inbox_row = ttk.Frame(storage_card.content)
        inbox_row.grid(row=2, column=0, sticky="ew", pady=(6, 0))
        inbox_row.columnconfigure(0, weight=1)
        self.inbox_dir_var = tk.StringVar(value=str(self.app.inbox_dir))
        ttk.Entry(inbox_row, textvariable=self.inbox_dir_var).grid(row=0, column=0, sticky="ew")
        ttk.Button(inbox_row, text="Browse", command=self.pick_inbox_dir).grid(row=0, column=1, padx=(8, 0))
        ttk.Label(
            storage_card.content,
            text="Chat history is encrypted and restored on launch.",
            style="Subheading.TLabel",
        ).grid(row=3, column=0, sticky="w", pady=(10, 0))

        buttons = ttk.Frame(self.settings_body)
        buttons.grid(row=4, column=0, sticky="ew", padx=18, pady=(16, 18))
        ttk.Button(buttons, text="Save", command=self.save, style="Primary.TButton").pack(side="left")
        ttk.Button(buttons, text="Check Updates", command=lambda: self.app.check_for_updates(manual=True)).pack(side="left", padx=(8, 0))
        ttk.Button(buttons, text="Close", command=self.hide).pack(side="right")

    def show(self) -> None:
        self.username_var.set(self.app.username)
        self.update_server_var.set(self.app.config.update_server_url)
        self.inbox_dir_var.set(str(self.app.inbox_dir))
        super().show()

    def pick_inbox_dir(self) -> None:
        selected = filedialog.askdirectory(parent=self, initialdir=self.inbox_dir_var.get() or str(self.app.inbox_dir))
        if selected:
            self.inbox_dir_var.set(selected)
            try:
                self.app.config.inbox_dir = selected
            except Exception as exc:
                messagebox.showerror("Invalid folder", f"Could not use that folder:\n{exc}", parent=self)
                self.inbox_dir_var.set(str(self.app.inbox_dir))

    def save(self) -> None:
        value = self.username_var.get().strip()
        if not value:
            messagebox.showerror("Invalid username", "Username cannot be empty.", parent=self)
            return
        inbox_dir = self.inbox_dir_var.get().strip()
        if not inbox_dir:
            messagebox.showerror("Invalid folder", "Choose a folder for received files.", parent=self)
            return
        self.app.config.username = value
        self.app.config.update_server_url = self.update_server_var.get().strip()
        try:
            self.app.config.inbox_dir = inbox_dir
        except Exception as exc:
            messagebox.showerror("Invalid folder", f"Could not use that folder:\n{exc}", parent=self)
            return
        self.app.refresh_tray_menu()
        self.hide()


class MainChatWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(app, f"{APP_NAME} - {APP_VERSION}", "700x520")
        self.minsize(640, 480)
        self.selected_ip: str | None = None

        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)

        container = ttk.Frame(self, padding=14)
        container.grid(row=0, column=0, sticky="nsew")
        container.columnconfigure(1, weight=1)
        container.rowconfigure(0, weight=1)

        sidebar = ttk.Frame(container, width=228)
        sidebar.grid(row=0, column=0, sticky="nsw", padx=(0, 12))
        sidebar.grid_propagate(False)

        ttk.Label(sidebar, text=f"Chats  {APP_VERSION}", style="Heading.TLabel").pack(anchor="w")
        ttk.Label(sidebar, text="Fast local messaging with encrypted history.", style="Subheading.TLabel").pack(anchor="w", pady=(2, 10))
        sidebar_actions = ttk.Frame(sidebar)
        sidebar_actions.pack(fill="x", pady=(0, 10))
        ttk.Button(sidebar_actions, text="Contacts", command=self.app.show_contacts_window, style="Primary.TButton").pack(side="left")
        ttk.Button(sidebar_actions, text="Search LAN", command=self.app.trigger_discovery_scan).pack(side="left", padx=(8, 0))

        sidebar_holder = RoundedPanel(
            sidebar,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=20,
            padding=(0, 0),
            stretch=True,
        )
        sidebar_holder.pack(fill="both", expand=True)
        self.sidebar_canvas = tk.Canvas(sidebar_holder.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.sidebar_canvas.pack(side="left", fill="both", expand=True)

        self.sidebar_list = tk.Frame(self.sidebar_canvas, bg=UI_COLORS["panel_bg"])
        self.sidebar_window = self.sidebar_canvas.create_window((0, 0), window=self.sidebar_list, anchor="nw")
        self.sidebar_list.bind(
            "<Configure>",
            lambda _event: self.sidebar_canvas.configure(scrollregion=self.sidebar_canvas.bbox("all")),
        )
        self.sidebar_canvas.bind(
            "<Configure>",
            lambda event: self.sidebar_canvas.itemconfigure(self.sidebar_window, width=event.width),
        )
        self._bind_mousewheel(self.sidebar_canvas, self.sidebar_list)

        chat = ttk.Frame(container)
        chat.grid(row=0, column=1, sticky="nsew")
        chat.columnconfigure(0, weight=1)
        chat.rowconfigure(1, weight=1)

        header = RoundedPanel(
            chat,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=22,
            padding=(14, 12),
            stretch=True,
        )
        header.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        header.content.columnconfigure(0, weight=1)
        self.header_name_var = tk.StringVar(value="No chat selected")
        self.header_status_var = tk.StringVar(value="Discovered contacts will appear in the sidebar.")
        tk.Label(
            header.content,
            textvariable=self.header_name_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["text"],
            font=(UI_FONT, 15, "bold"),
        ).grid(row=0, column=0, sticky="w")
        self.header_badge = tk.Label(
            header.content,
            text="Offline",
            bg=UI_COLORS["danger_bg"],
            fg=UI_COLORS["danger"],
            font=(UI_FONT, 9, "bold"),
            padx=10,
            pady=4,
        )
        self.header_badge.grid(row=0, column=1, sticky="e")
        tk.Label(
            header.content,
            textvariable=self.header_status_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 10),
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(6, 0))

        history_card = RoundedPanel(
            chat,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=22,
            padding=(0, 0),
            stretch=True,
        )
        history_card.grid(row=1, column=0, sticky="nsew")
        history_card.content.columnconfigure(0, weight=1)
        history_card.content.rowconfigure(0, weight=1)
        self.history_canvas = tk.Canvas(history_card.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.history_canvas.grid(row=0, column=0, sticky="nsew")
        self.history_frame = tk.Frame(self.history_canvas, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.history_window = self.history_canvas.create_window((0, 0), window=self.history_frame, anchor="nw")
        self.history_frame.bind(
            "<Configure>",
            lambda _event: self.history_canvas.configure(scrollregion=self.history_canvas.bbox("all")),
        )
        self.history_canvas.bind(
            "<Configure>",
            lambda event: self.history_canvas.itemconfigure(self.history_window, width=event.width),
        )
        self._bind_mousewheel(self.history_canvas, self.history_frame)

        self.transfer_card = RoundedPanel(
            chat,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=18,
            padding=(12, 12),
            stretch=True,
        )
        self.transfer_card.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        self.transfer_label = ttk.Label(self.transfer_card.content, text="", style="Muted.TLabel")
        self.transfer_label.pack(anchor="w")
        self.transfer_bar = ttk.Progressbar(self.transfer_card.content, mode="determinate")
        self.transfer_bar.pack(fill="x", pady=(6, 0))
        self.transfer_card.grid_remove()

        composer = RoundedPanel(
            chat,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=22,
            padding=(10, 10),
            stretch=True,
        )
        composer.grid(row=3, column=0, sticky="ew", pady=(10, 0))
        composer.content.columnconfigure(1, weight=1)

        self.attach_button = RoundedButton(
            composer.content,
            text="Attach",
            command=self.pick_file,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_bg"],
            disabled_text=UI_COLORS["muted"],
            font=(UI_FONT, 10, "bold"),
            min_width=82,
        )
        self.attach_button.grid(row=0, column=0, sticky="w", padx=(0, 8))

        entry_shell = RoundedPanel(
            composer.content,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["composer_bg"],
            border=UI_COLORS["border"],
            radius=18,
            padding=(8, 5),
            stretch=True,
        )
        entry_shell.grid(row=0, column=1, sticky="ew")
        entry_shell.content.columnconfigure(0, weight=1)

        self.entry = tk.Text(
            entry_shell.content,
            height=1,
            wrap="word",
            bg=UI_COLORS["composer_bg"],
            fg=UI_COLORS["text"],
            insertbackground=UI_COLORS["text"],
            relief="flat",
            bd=0,
            highlightthickness=0,
            padx=8,
            pady=6,
            font=(UI_FONT, 11),
        )
        self.entry.grid(row=0, column=0, sticky="ew")
        self.entry.bind("<Return>", self.on_enter)

        self.send_button = RoundedButton(
            composer.content,
            text="Send",
            command=self.send_text,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color="#ffffff",
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            min_width=76,
        )
        self.send_button.grid(row=0, column=2, sticky="e", padx=(8, 0))

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
            empty = RoundedPanel(
                self.sidebar_list,
                background=UI_COLORS["panel_bg"],
                fill=UI_COLORS["card_bg"],
                border=UI_COLORS["border"],
                radius=16,
                padding=(14, 14),
                stretch=True,
            )
            empty.pack(fill="x", pady=(2, 0))
            tk.Label(
                empty.content,
                text="No conversations yet.\nPeers discovered on your LAN will appear here.",
                justify="left",
                bg=UI_COLORS["card_bg"],
                fg=UI_COLORS["muted"],
                font=(UI_FONT, 10),
            ).pack(anchor="w")
            return

        for ip in conversation_ips:
            self._build_row(ip)

    def _build_row(self, ip: str) -> None:
        selected = ip == self.selected_ip
        bg = UI_COLORS["card_selected"] if selected else UI_COLORS["card_bg"]
        frame = RoundedPanel(
            self.sidebar_list,
            background=UI_COLORS["panel_bg"],
            fill=bg,
            border=UI_COLORS["border"],
            radius=16,
            padding=(10, 10),
            stretch=True,
        )
        frame.pack(fill="x", pady=(0, 6))

        text_wrap = tk.Frame(frame.content, bg=bg, bd=0, highlightthickness=0)
        text_wrap.pack(fill="both", expand=True)

        name = self.app.conversation_name(ip)
        unread = self.app.unread_counts.get(ip, 0)
        title = name if unread == 0 else f"{name} ({unread})"
        preview = self.app.conversation_preview(ip)
        online = self.app.find_peer_by_ip(ip) is not None
        status_text = self.app.conversation_status(ip)
        status = "Online" if online else "Offline"
        status_detail = status_text.split("  ", 1)[1] if "  " in status_text else ""

        title_label = tk.Label(
            text_wrap,
            text=title,
            anchor="w",
            bg=bg,
            fg=UI_COLORS["text"],
            font=(UI_FONT, 10, "bold"),
        )
        title_label.pack(fill="x")
        preview_label = tk.Label(
            text_wrap,
            text=preview,
            anchor="w",
            justify="left",
            bg=bg,
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9),
            wraplength=170,
        )
        preview_label.pack(fill="x", pady=(2, 1))
        status_row = tk.Frame(text_wrap, bg=bg, bd=0, highlightthickness=0)
        status_row.pack(fill="x", pady=(2, 0))
        status_label = tk.Label(
            status_row,
            text=status,
            anchor="w",
            bg=UI_COLORS["success_bg"] if online else UI_COLORS["danger_bg"],
            fg=UI_COLORS["success"] if online else UI_COLORS["danger"],
            font=(UI_FONT, 8, "bold"),
            padx=8,
            pady=3,
        )
        status_label.pack(side="left")
        if status_detail:
            tk.Label(
                status_row,
                text=status_detail,
                anchor="w",
                bg=bg,
                fg=UI_COLORS["muted"],
                font=(UI_FONT, 8),
            ).pack(side="left", padx=(6, 0))

        for widget in (frame, text_wrap, title_label, preview_label, status_row, status_label):
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
            self._set_status_badge(False)
            self.attach_button.set_enabled(False)
            self.send_button.set_enabled(False)
            self.entry.config(state="disabled")
            self._render_history([])
            self.transfer_card.grid_remove()
            return

        ip = self.selected_ip
        self.title(f"{APP_NAME} - {self.app.conversation_name(ip)} - {APP_VERSION}")
        self.header_name_var.set(self.app.conversation_name(ip))
        peer = self.app.find_peer_by_ip(ip)
        contact = self.app._find_contact_by_ip(ip)
        self.header_status_var.set(peer.ip if peer is not None else (contact.last_ip if contact is not None and contact.last_ip else "Offline"))
        self._set_status_badge(peer is not None)

        self.attach_button.set_enabled(True)
        self.send_button.set_enabled(True)
        self.entry.config(state="normal")

        self._render_history(self.app.message_history.get(ip, []))
        self.refresh_transfer(ip)

    def _render_history(self, entries: list[MessageEntry]) -> None:
        for child in self.history_frame.winfo_children():
            child.destroy()
        wraplength = max(min(self.history_canvas.winfo_width() - 220, 420), 220)
        for entry in entries:
            self._append_bubble(self.history_frame, self.app.username, entry, wraplength)
        self.after_idle(lambda: self.history_canvas.yview_moveto(1.0))

    def refresh_transfer(self, ip: str | None = None) -> None:
        target_ip = ip or self.selected_ip
        if not target_ip or target_ip != self.selected_ip:
            return

        transfer = self.app.transfer_statuses.get(target_ip)
        if transfer is None:
            self.transfer_card.grid_remove()
            return

        label, current, total = transfer
        self.transfer_card.grid()
        self.transfer_label.config(text=f"{label} ({format_bytes(current)} / {format_bytes(total)})")
        self.transfer_bar["maximum"] = max(total, 1)
        self.transfer_bar["value"] = current

    def _set_status_badge(self, is_online: bool) -> None:
        self.header_badge.config(
            text="Online" if is_online else "Offline",
            bg=UI_COLORS["success_bg"] if is_online else UI_COLORS["danger_bg"],
            fg=UI_COLORS["success"] if is_online else UI_COLORS["danger"],
        )

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
    try:
        app = LanMessengerApp()
    except Exception as exc:
        print(f"Failed to start {APP_NAME}: {exc}", file=sys.stderr)
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
    app.root.mainloop()


if __name__ == "__main__":
    main()
