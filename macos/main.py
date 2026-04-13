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
    NSUserNotification = getattr(appkit_module, "NSUserNotification", None)
    NSUserNotificationCenter = getattr(appkit_module, "NSUserNotificationCenter", None)
except Exception:
    NSApp = None
    NSApplicationActivationPolicyAccessory = None
    NSUserNotification = None
    NSUserNotificationCenter = None

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import x25519
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


APP_NAME = "LAN Messenger"
APP_VERSION = "1.5.0"
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


def current_ui_platform() -> str:
    if sys.platform == "darwin":
        return "macos"
    if sys.platform.startswith("win"):
        return "windows"
    return "default"


def build_ui_colors(platform_name: str) -> dict[str, str]:
    if platform_name == "macos":
        return {
            "app_bg": "#eef1f5",
            "shell_bg": "#e6ebf2",
            "sidebar_bg": "#f5f7fb",
            "panel_bg": "#f7f9fc",
            "panel_alt": "#eef3f8",
            "card_bg": "#ffffff",
            "card_elevated": "#fbfcfe",
            "card_selected": "#e4f0ff",
            "border": "#d6dde8",
            "border_strong": "#c3cedd",
            "shadow": "#dbe3ee",
            "text": "#18212d",
            "muted": "#627082",
            "subtle": "#8190a3",
            "accent": "#0a84ff",
            "accent_active": "#0062cc",
            "accent_soft": "#e8f2ff",
            "accent_contrast": "#ffffff",
            "success": "#137a53",
            "success_bg": "#dff7ea",
            "danger": "#c23d4f",
            "danger_bg": "#fde9ed",
            "warning": "#a96a15",
            "warning_bg": "#fff1d8",
            "composer_bg": "#ffffff",
            "incoming_bg": "#ffffff",
            "outgoing_bg": "#e7f2ff",
            "input_bg": "#f8fafc",
            "input_focus": "#dcecff",
        }
    if platform_name == "windows":
        return {
            "app_bg": "#edf2f7",
            "shell_bg": "#e4ebf3",
            "sidebar_bg": "#f4f7fb",
            "panel_bg": "#f7f9fc",
            "panel_alt": "#edf3f9",
            "card_bg": "#ffffff",
            "card_elevated": "#fbfdff",
            "card_selected": "#dfeeff",
            "border": "#d4dce7",
            "border_strong": "#b9c7d8",
            "shadow": "#d8e1ed",
            "text": "#17212b",
            "muted": "#5c6e80",
            "subtle": "#8293a6",
            "accent": "#0f6cbd",
            "accent_active": "#0c5a9e",
            "accent_soft": "#e6f1fb",
            "accent_contrast": "#ffffff",
            "success": "#1a7f58",
            "success_bg": "#dff6ea",
            "danger": "#c93b52",
            "danger_bg": "#fde8ed",
            "warning": "#9f6718",
            "warning_bg": "#fff1d9",
            "composer_bg": "#ffffff",
            "incoming_bg": "#ffffff",
            "outgoing_bg": "#e4f1ff",
            "input_bg": "#f8fafc",
            "input_focus": "#dbeaff",
        }
    return {
        "app_bg": "#eef3f8",
        "shell_bg": "#e7edf5",
        "sidebar_bg": "#f7fafc",
        "panel_bg": "#f7fafc",
        "panel_alt": "#eef3f8",
        "card_bg": "#ffffff",
        "card_elevated": "#fbfcfe",
        "card_selected": "#dceeff",
        "border": "#d6dee8",
        "border_strong": "#c4cfdb",
        "shadow": "#dbe5f0",
        "text": "#16202a",
        "muted": "#5f6f82",
        "subtle": "#8090a2",
        "accent": "#2f80ed",
        "accent_active": "#1f6fd8",
        "accent_soft": "#eaf3ff",
        "accent_contrast": "#ffffff",
        "success": "#1f9d68",
        "success_bg": "#dff7eb",
        "danger": "#cf3f4f",
        "danger_bg": "#fde8eb",
        "warning": "#aa6a16",
        "warning_bg": "#fff0d8",
        "composer_bg": "#ffffff",
        "incoming_bg": "#ffffff",
        "outgoing_bg": "#dff1ff",
        "input_bg": "#f8fafc",
        "input_focus": "#deecff",
    }


def build_ui_metrics(platform_name: str) -> dict[str, int]:
    if platform_name == "macos":
        return {
            "radius_window": 30,
            "radius_panel": 26,
            "radius_card": 22,
            "radius_chip": 16,
            "radius_button": 17,
            "sidebar_width": 330,
            "main_width": 1220,
            "main_height": 780,
            "contacts_width": 980,
            "contacts_height": 660,
            "settings_width": 760,
            "settings_height": 680,
        }
    if platform_name == "windows":
        return {
            "radius_window": 26,
            "radius_panel": 24,
            "radius_card": 20,
            "radius_chip": 14,
            "radius_button": 15,
            "sidebar_width": 316,
            "main_width": 1160,
            "main_height": 740,
            "contacts_width": 940,
            "contacts_height": 640,
            "settings_width": 740,
            "settings_height": 650,
        }
    return {
        "radius_window": 28,
        "radius_panel": 24,
        "radius_card": 20,
        "radius_chip": 15,
        "radius_button": 16,
        "sidebar_width": 320,
        "main_width": 1180,
        "main_height": 760,
        "contacts_width": 960,
        "contacts_height": 650,
        "settings_width": 750,
        "settings_height": 660,
    }


UI_PLATFORM = current_ui_platform()
UI_COLORS = build_ui_colors(UI_PLATFORM)
UI_METRICS = build_ui_metrics(UI_PLATFORM)
UI_FONT = "Segoe UI"
TRANSFER_STATUS_CLEAR_DELAY_MS = 1800
COMPOSER_MIN_CHARS = 18
COMPOSER_MAX_CHARS = 34
COMPOSER_MAX_LINES = 4
TYPING_IDLE_TIMEOUT_MS = 1500
TYPING_STATUS_TTL = 4.0
TYPING_SEND_THROTTLE = 1.0

AVATAR_SWATCHES = [
    ("#1f6feb", "#ffffff"),
    ("#0f9d8a", "#ffffff"),
    ("#ef7d32", "#ffffff"),
    ("#8a63d2", "#ffffff"),
    ("#c54d73", "#ffffff"),
    ("#5b8c2a", "#ffffff"),
]


def resolve_ui_font_family(root: tk.Tk) -> str:
    try:
        available_fonts = {name.lower() for name in tkfont.families(root)}
    except Exception:
        return UI_FONT

    if UI_PLATFORM == "macos":
        candidates = ["SF Pro Text", "SF Pro Display", "Helvetica Neue", "Arial", "Helvetica"]
    elif UI_PLATFORM == "windows":
        candidates = ["Segoe UI Variable Text", "Segoe UI", "Arial", "Helvetica"]
    else:
        candidates = ["Segoe UI", "SF Pro Text", "Helvetica Neue", "Arial", "Helvetica"]
    for family in candidates:
        if family.lower() in available_fonts:
            return family

    try:
        return cast(str, tkfont.nametofont("TkDefaultFont").cget("family"))
    except Exception:
        return UI_FONT


def truncate_text(value: str, limit: int) -> str:
    normalized = " ".join(value.split())
    if len(normalized) <= limit:
        return normalized
    return f"{normalized[: max(limit - 3, 1)].rstrip()}..."


def initials_for_name(name: str) -> str:
    parts = [part for part in name.strip().split() if part]
    if not parts:
        return "?"
    if len(parts) == 1:
        return parts[0][:2].upper()
    return f"{parts[0][0]}{parts[-1][0]}".upper()


def avatar_colors(name: str) -> tuple[str, str]:
    index = sum(ord(char) for char in name) % len(AVATAR_SWATCHES)
    return AVATAR_SWATCHES[index]


def format_sidebar_timestamp(timestamp: float | None) -> str:
    if not timestamp:
        return ""
    current = time.localtime()
    then = time.localtime(timestamp)
    if current.tm_year == then.tm_year and current.tm_yday == then.tm_yday:
        return format_message_time(timestamp)
    if current.tm_year == then.tm_year:
        return time.strftime("%b %d", then)
    return time.strftime("%m/%d/%y", then)


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


class AvatarBadge(tk.Canvas):
    def __init__(
        self,
        parent: tk.Misc,
        *,
        name: str,
        diameter: int,
        background: str,
    ) -> None:
        super().__init__(
            parent,
            width=diameter,
            height=diameter,
            bg=background,
            highlightthickness=0,
            bd=0,
            relief="flat",
        )
        self._name = name
        self._diameter = diameter
        self._background = background
        self._redraw()

    def set_name(self, name: str) -> None:
        self._name = name
        self._redraw()

    def _redraw(self) -> None:
        fill, text_color = avatar_colors(self._name)
        inset = 2
        self.delete("all")
        self.create_oval(
            inset,
            inset,
            self._diameter - inset,
            self._diameter - inset,
            fill=fill,
            outline="",
        )
        self.create_text(
            self._diameter / 2,
            self._diameter / 2,
            text=initials_for_name(self._name),
            fill=text_color,
            font=(UI_FONT, max(10, self._diameter // 3), "bold"),
        )


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
            "hidden_conversations": [],
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
    def hidden_conversations(self) -> list[str]:
        hidden = self.data.get("hidden_conversations", [])
        if isinstance(hidden, list):
            return [str(item).strip() for item in hidden if str(item).strip()]
        return []

    def save_hidden_conversations(self, hidden_conversations: list[str]) -> None:
        self.data["hidden_conversations"] = sorted(set(hidden_conversations))
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
    def _notify_macos(self, title: str, message: str) -> bool:
        if NSUserNotification is None or NSUserNotificationCenter is None:
            return False
        try:
            notification = NSUserNotification.alloc().init()
            notification.setTitle_(title)
            notification.setInformativeText_(message)
            notification.setSoundName_("NSUserNotificationDefaultSoundName")
            center = NSUserNotificationCenter.defaultUserNotificationCenter()
            center.deliverNotification_(notification)
            return True
        except Exception:
            return False

    def notify(self, title: str, message: str) -> None:
        if sys.platform == "darwin" and self._notify_macos(title, message):
            return
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
        self.file_queues: dict[str, list[dict[str, Any]]] = {}
        self.active_file_transfers: set[str] = set()
        self.typing_states: dict[str, dict[str, Any]] = {}
        self.typing_state_tokens: dict[str, int] = {}
        self.outgoing_typing_state: dict[str, bool] = {}
        self.outgoing_typing_sent_at: dict[str, float] = {}
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
        style.configure("AppTitle.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"], font=(UI_FONT, 18, "bold"))
        style.configure("Heading.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"], font=(UI_FONT, 16, "bold"))
        style.configure("Section.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["text"], font=(UI_FONT, 13, "bold"))
        style.configure("Subheading.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["muted"], font=(UI_FONT, 10))
        style.configure("Muted.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["muted"], font=(UI_FONT, 9))
        style.configure("Caption.TLabel", background=UI_COLORS["app_bg"], foreground=UI_COLORS["subtle"], font=(UI_FONT, 9))
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
        style.map("TButton", background=[("active", UI_COLORS["accent_soft"])], foreground=[("active", UI_COLORS["text"])])
        style.configure(
            "Primary.TButton",
            background=UI_COLORS["accent"],
            foreground=UI_COLORS["accent_contrast"],
            borderwidth=0,
            focusthickness=0,
            focuscolor=UI_COLORS["accent"],
            padding=(14, 9),
            relief="flat",
        )
        style.map(
            "Primary.TButton",
            background=[("active", UI_COLORS["accent_active"])],
            foreground=[("active", UI_COLORS["accent_contrast"])],
        )
        style.configure(
            "TEntry",
            fieldbackground=UI_COLORS["input_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            insertcolor=UI_COLORS["text"],
            padding=9,
            relief="flat",
        )
        style.map("TEntry", fieldbackground=[("focus", UI_COLORS["card_bg"])])
        style.configure(
            "TCombobox",
            fieldbackground=UI_COLORS["input_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            arrowsize=16,
            padding=7,
            relief="flat",
        )
        style.map("TCombobox", fieldbackground=[("readonly", UI_COLORS["input_bg"])], selectbackground=[("readonly", UI_COLORS["card_bg"])])
        style.configure(
            "Treeview",
            background=UI_COLORS["card_bg"],
            fieldbackground=UI_COLORS["card_bg"],
            foreground=UI_COLORS["text"],
            bordercolor=UI_COLORS["border"],
            rowheight=36,
            relief="flat",
        )
        style.map("Treeview", background=[("selected", UI_COLORS["card_selected"])], foreground=[("selected", UI_COLORS["text"])])
        style.configure(
            "Treeview.Heading",
            background=UI_COLORS["panel_alt"],
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
            self.refresh_tray_menu(refresh_windows=True)
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
        elif action == "typing_state":
            ip, sender, is_typing = args
            self.set_typing_state(ip, sender, bool(is_typing))
        elif action == "refresh_file_queue":
            self.refresh_file_queue_status(str(args[0]))
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
                self.outgoing_typing_state.pop(ip, None)
                self.outgoing_typing_sent_at.pop(ip, None)
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
            self.enqueue_ui("typing_state", ip, sender, False)
            self.enqueue_ui("message", ip, sender, text, packet["message_id"], timestamp)
            self._send_receipt_to_peer(peer, "sent_receipt", packet["message_id"])
            self.notifications.notify(f"Message from {sender}", text[:120])
            return

        if packet_type == "typing":
            sender = str(packet.get("sender") or peer.username).strip() or peer.username
            self.enqueue_ui("typing_state", ip, sender, bool(packet.get("active")))
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
        self._reveal_conversation(history_ip)
        self.send_typing_state(history_ip, False, force=True)
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
        self.queue_files(ip, [path], progress_callback=progress_callback)

    def queue_files(self, ip: str, paths: list[str], progress_callback=None) -> None:
        if not paths:
            return

        conversation_key = self._conversation_key(ip)
        queued_items = self.file_queues.setdefault(conversation_key, [])
        had_work = conversation_key in self.active_file_transfers or bool(queued_items)
        valid_added = 0

        for raw_path in paths:
            file_path = Path(raw_path).expanduser()
            if not file_path.exists() or not file_path.is_file():
                self.enqueue_ui("network_error", ip, f"File not found: {file_path.name or raw_path}")
                continue
            queued_items.append({
                "path": str(file_path),
                "filename": file_path.name,
                "queued_at": now(),
                "progress_callback": progress_callback,
                "conversation_ip": ip,
            })
            valid_added += 1
            queue_depth = len(queued_items) - (1 if conversation_key in self.active_file_transfers else 0)
            if had_work or queue_depth > 1:
                self.add_message(ip, "System", f"Queued file: {file_path.name}", incoming=False)
            else:
                self.add_message(ip, "System", f"Sending file: {file_path.name}", incoming=False)
            had_work = True

        if valid_added == 0:
            if not queued_items:
                self.file_queues.pop(conversation_key, None)
            return

        self._reveal_conversation(ip)
        self.send_typing_state(ip, False, force=True)
        self.enqueue_ui("refresh_file_queue", conversation_key)
        self._start_next_file_transfer(conversation_key)

    def _start_next_file_transfer(self, conversation_key: str) -> None:
        if conversation_key in self.active_file_transfers:
            return

        queued_items = self.file_queues.get(conversation_key, [])
        if not queued_items:
            self.file_queues.pop(conversation_key, None)
            self.enqueue_ui("refresh_file_queue", conversation_key)
            return

        peer = self._resolve_delivery_peer_for_key(conversation_key)
        if peer is None:
            self.enqueue_ui("refresh_file_queue", conversation_key)
            return

        item = queued_items[0]
        file_path = Path(str(item.get("path", "")))
        if not file_path.exists() or not file_path.is_file():
            queued_items.pop(0)
            self.enqueue_ui("network_error", peer.ip, f"Queued file is missing: {item.get('filename', file_path.name)}")
            self.enqueue_ui("refresh_file_queue", conversation_key)
            self._start_next_file_transfer(conversation_key)
            return

        self.active_file_transfers.add(conversation_key)

        def worker() -> None:
            transfer_id = uuid.uuid4().hex
            total_size = file_path.stat().st_size
            success = False
            progress_cb = item.get("progress_callback")
            try:
                with socket.create_connection((peer.ip, peer.port), timeout=5) as sock, file_path.open("rb") as handle:
                    send_frame(sock, {
                        "type": "file_start",
                        "transfer_id": transfer_id,
                        "filename": file_path.name,
                        "size": total_size,
                        "sender": self.username,
                        "sender_public_key_b64": self.crypto.public_key_b64,
                        "port": TCP_PORT,
                    })
                    sent = 0
                    self.enqueue_ui("transfer_progress", peer.ip, f"Sending {file_path.name}", 0, total_size)
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
                        self.enqueue_ui("transfer_progress", peer.ip, f"Sending {file_path.name}", sent, total_size)
                        if callable(progress_cb):
                            progress_cb(sent, total_size)

                    send_frame(sock, {
                        "type": "file_end",
                        "transfer_id": transfer_id,
                        "sender": self.username,
                        "sender_public_key_b64": self.crypto.public_key_b64,
                        "port": TCP_PORT,
                    })
                    self.enqueue_ui("transfer_complete", peer.ip, f"Sending {file_path.name}")
                    success = True
            except Exception:
                self.enqueue_ui(
                    "network_error",
                    peer.ip,
                    f"File transfer paused for {file_path.name}. It will retry when {peer.username} is available.",
                )
            finally:
                self.active_file_transfers.discard(conversation_key)
                current_items = self.file_queues.get(conversation_key, [])
                if success and current_items:
                    current_items.pop(0)
                    if not current_items:
                        self.file_queues.pop(conversation_key, None)
                self.enqueue_ui("refresh_file_queue", conversation_key)
                self._start_next_file_transfer(conversation_key)

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

    def send_typing_state(self, ip: str, active: bool, force: bool = False) -> None:
        peer = self._resolve_active_peer(ip)
        if peer is None:
            self.outgoing_typing_state[ip] = active
            return

        last_state = self.outgoing_typing_state.get(peer.ip)
        last_sent = self.outgoing_typing_sent_at.get(peer.ip, 0.0)
        if not force and last_state == active:
            if not active or (now() - last_sent) < TYPING_SEND_THROTTLE:
                return

        self.outgoing_typing_state[peer.ip] = active
        self.outgoing_typing_sent_at[peer.ip] = now()

        packet = {
            "type": "typing",
            "active": active,
            "sender": self.username,
            "sender_public_key_b64": self.crypto.public_key_b64,
            "port": TCP_PORT,
        }
        threading.Thread(target=lambda: self._send_packets(peer, [packet]), daemon=True).start()

    def set_typing_state(self, ip: str, sender: str, is_typing: bool) -> None:
        if is_typing:
            token = self.typing_state_tokens.get(ip, 0) + 1
            self.typing_state_tokens[ip] = token
            self.typing_states[ip] = {
                "sender": sender.strip() or self.conversation_name(ip),
                "expires_at": now() + TYPING_STATUS_TTL,
            }
            self.root.after(int(TYPING_STATUS_TTL * 1000), lambda target_ip=ip, target_token=token: self._expire_typing_state(target_ip, target_token))
        else:
            self.typing_state_tokens[ip] = self.typing_state_tokens.get(ip, 0) + 1
            self.typing_states.pop(ip, None)

        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh()

    def _expire_typing_state(self, ip: str, token: int) -> None:
        if self.typing_state_tokens.get(ip) != token:
            return
        state = self.typing_states.get(ip)
        if state is None or float(state.get("expires_at") or 0.0) > now():
            return
        self.typing_states.pop(ip, None)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh()

    def typing_status_text(self, ip: str) -> str:
        state = self.typing_states.get(ip)
        if state is None:
            return ""
        if float(state.get("expires_at") or 0.0) <= now():
            self.typing_states.pop(ip, None)
            return ""
        sender = str(state.get("sender", "")).strip() or self.conversation_name(ip)
        if sender == self.conversation_name(ip):
            return "Typing..."
        return f"{sender} is typing..."

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

        typing_state = self.typing_states.pop(old_ip, None)
        if typing_state is not None:
            self.typing_states[new_ip] = typing_state
        typing_token = self.typing_state_tokens.pop(old_ip, None)
        if typing_token is not None:
            self.typing_state_tokens[new_ip] = typing_token

        outgoing_typing = self.outgoing_typing_state.pop(old_ip, None)
        if outgoing_typing is not None:
            self.outgoing_typing_state[new_ip] = outgoing_typing
        outgoing_sent_at = self.outgoing_typing_sent_at.pop(old_ip, None)
        if outgoing_sent_at is not None:
            self.outgoing_typing_sent_at[new_ip] = outgoing_sent_at

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
            self._start_next_file_transfer(f"pk:{peer.public_key_b64}")

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

    def _conversation_key(self, ip: str) -> str:
        peer = self.peers.get(ip)
        if peer is not None:
            return f"pk:{peer.public_key_b64}"
        contact = self._find_contact_by_ip(ip)
        if contact is not None:
            return f"pk:{contact.public_key_b64}"
        return f"ip:{ip}"

    def _resolve_delivery_peer_for_key(self, conversation_key: str) -> Peer | None:
        if conversation_key.startswith("pk:"):
            public_key_b64 = conversation_key[3:]
            peer = self.find_peer_by_public_key(public_key_b64)
            if peer is not None:
                return peer
            contact = self.find_contact_by_public_key(public_key_b64)
            if contact is not None and contact.last_ip:
                return Peer(
                    ip=contact.last_ip,
                    username=contact.username,
                    port=TCP_PORT,
                    public_key_b64=contact.public_key_b64,
                    last_seen=now(),
                )
            return None
        if conversation_key.startswith("ip:"):
            return self._resolve_delivery_peer(conversation_key[3:])
        return None

    def _display_ip_for_conversation_key(self, conversation_key: str) -> str:
        if conversation_key.startswith("pk:"):
            public_key_b64 = conversation_key[3:]
            peer = self.find_peer_by_public_key(public_key_b64)
            if peer is not None:
                return peer.ip
            contact = self.find_contact_by_public_key(public_key_b64)
            if contact is not None:
                return contact.last_ip
            return ""
        if conversation_key.startswith("ip:"):
            return conversation_key[3:]
        return ""

    def _is_hidden_conversation(self, ip: str) -> bool:
        return self._conversation_key(ip) in set(self.config.hidden_conversations)

    def _hide_conversation(self, ip: str) -> None:
        key = self._conversation_key(ip)
        hidden = set(self.config.hidden_conversations)
        hidden.add(key)
        self.config.save_hidden_conversations(list(hidden))

    def _reveal_conversation(self, ip: str) -> None:
        key = self._conversation_key(ip)
        hidden = set(self.config.hidden_conversations)
        if key not in hidden:
            return
        hidden.discard(key)
        self.config.save_hidden_conversations(list(hidden))

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
            return "Online"
        contact = self._find_contact_by_ip(ip)
        if contact is not None and contact.last_ip:
            return "Offline"
        return "Offline"

    def conversation_preview(self, ip: str) -> str:
        typing_text = self.typing_status_text(ip)
        if typing_text:
            return typing_text

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
        hidden = set(self.config.hidden_conversations)
        targets: set[str] = set(self.message_history.keys())

        for peer in self.online_peers():
            if f"pk:{peer.public_key_b64}" not in hidden:
                targets.add(peer.ip)

        for contact in self.contacts:
            peer = self.find_peer_for_contact(contact)
            if peer is not None:
                if f"pk:{contact.public_key_b64}" not in hidden:
                    targets.add(peer.ip)
            elif contact.last_ip and f"pk:{contact.public_key_b64}" not in hidden:
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
        self.refresh_tray_menu(refresh_windows=True)

    def remove_contact(self, public_key_b64: str) -> None:
        contacts = [contact for contact in self.config.contacts if contact.get("public_key_b64") != public_key_b64]
        self.config.save_contacts(contacts)
        pending_messages = [
            pending for pending in self.config.pending_messages
            if pending.get("public_key_b64") != public_key_b64
        ]
        self.config.save_pending_messages(pending_messages)
        self.refresh_tray_menu(refresh_windows=True)

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
        self.refresh_tray_menu(refresh_windows=True)

    def prompt_send_file(self, ip: str) -> None:
        paths = filedialog.askopenfilenames(parent=self.root)
        if not paths:
            return
        self.queue_files(ip, list(paths))

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
        open_download = self.ask_centered_yes_no(
            "Update Available",
            (
                f"Version {info.version} is available.\n"
                f"You are on {APP_VERSION}.{notes}\n\n"
                "Open the download page now?"
            ),
            parent=self.settings_window if self.settings_window is not None and self.settings_window.winfo_exists() else None,
        )
        if open_download:
            self.open_update_download(info.download_url)

    def _handle_no_update(self, manual: bool) -> None:
        if manual:
            self.show_centered_info(
                "Updates",
                f"You are already on the latest version ({APP_VERSION}).",
                parent=self.settings_window if self.settings_window is not None and self.settings_window.winfo_exists() else None,
            )

    def _handle_update_error(self, message: str, manual: bool) -> None:
        if manual:
            self.show_centered_error(
                "Update Check Failed",
                message,
                parent=self.settings_window if self.settings_window is not None and self.settings_window.winfo_exists() else None,
            )
        else:
            self.notifications.notify("Update Check Failed", message)

    def open_update_download(self, download_url: str) -> None:
        try:
            webbrowser.open(download_url)
        except Exception:
            self.show_centered_error("Updates", f"Could not open download URL:\n{download_url}")

    def _dialog_parent(self, parent: tk.Misc | None = None) -> tk.Misc:
        candidates: list[tk.Misc] = []
        if parent is not None:
            candidates.append(parent)
        if self.settings_window is not None and self.settings_window.winfo_exists() and self.settings_window.is_visible():
            candidates.append(self.settings_window)
        if self.main_window is not None and self.main_window.winfo_exists() and self.main_window.is_visible():
            candidates.append(self.main_window)
        if self.contacts_window is not None and self.contacts_window.winfo_exists() and self.contacts_window.is_visible():
            candidates.append(self.contacts_window)

        for candidate in candidates:
            try:
                candidate.update_idletasks()
                return candidate
            except Exception:
                continue
        self.prepare_window_host()
        return self.root

    def _create_centered_dialog_host(self, parent: tk.Misc | None = None) -> tk.Toplevel | None:
        anchor = self._dialog_parent(parent)
        if isinstance(anchor, tk.Toplevel):
            try:
                host = tk.Toplevel(anchor)
                host.withdraw()
                host.overrideredirect(True)
                host.transient(anchor)
                anchor.update_idletasks()
                width = max(anchor.winfo_width(), anchor.winfo_reqwidth(), 1)
                height = max(anchor.winfo_height(), anchor.winfo_reqheight(), 1)
                x = anchor.winfo_rootx() + max((width - 1) // 2, 0)
                y = anchor.winfo_rooty() + max((height - 1) // 2, 0)
                host.geometry(f"1x1+{x}+{y}")
                host.deiconify()
                host.lift()
                return host
            except Exception:
                return None
        return None

    def show_centered_info(self, title: str, message: str, parent: tk.Misc | None = None) -> None:
        host = self._create_centered_dialog_host(parent)
        try:
            messagebox.showinfo(title, message, parent=host or self._dialog_parent(parent))
        finally:
            if host is not None and host.winfo_exists():
                host.destroy()

    def show_centered_error(self, title: str, message: str, parent: tk.Misc | None = None) -> None:
        host = self._create_centered_dialog_host(parent)
        try:
            messagebox.showerror(title, message, parent=host or self._dialog_parent(parent))
        finally:
            if host is not None and host.winfo_exists():
                host.destroy()

    def ask_centered_yes_no(self, title: str, message: str, parent: tk.Misc | None = None) -> bool:
        host = self._create_centered_dialog_host(parent)
        try:
            return bool(messagebox.askyesno(title, message, parent=host or self._dialog_parent(parent)))
        finally:
            if host is not None and host.winfo_exists():
                host.destroy()

    def show_conversation_info(self, ip: str, parent: tk.Misc | None = None) -> None:
        peer = self.find_peer_by_ip(ip)
        contact = self._find_contact_by_ip(ip)
        status = "Online" if peer is not None else "Offline"
        contact_name = self.conversation_name(ip)
        display_ip = peer.ip if peer is not None else (contact.last_ip if contact is not None else ip)
        self.show_centered_info(
            "Chat Info",
            f"Name: {contact_name}\nStatus: {status}\nIP Address: {display_ip}",
            parent=parent,
        )

    def delete_conversation(self, ip: str) -> None:
        conversation_key = self._conversation_key(ip)
        self._hide_conversation(ip)
        if conversation_key.startswith("pk:"):
            public_key_b64 = conversation_key[3:]
            pending_messages = [
                pending for pending in self.config.pending_messages
                if pending.get("public_key_b64") != public_key_b64
            ]
            self.config.save_pending_messages(pending_messages)
        self.message_history.pop(ip, None)
        self.unread_counts.pop(ip, None)
        self.transfer_statuses.pop(ip, None)
        self.transfer_status_tokens.pop(ip, None)
        self.typing_states.pop(ip, None)
        self.typing_state_tokens[ip] = self.typing_state_tokens.get(ip, 0) + 1
        self.file_queues.pop(conversation_key, None)
        self.active_file_transfers.discard(conversation_key)
        self.send_typing_state(ip, False, force=True)
        self._save_message_history()
        self.refresh_tray_menu(refresh_windows=True)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh()

    def refresh_tray_menu(self, *, refresh_windows: bool = False) -> None:
        if self.icon is not None:
            try:
                self.icon.icon = self._tray_image()
                self.icon.menu = self._build_tray_menu()
                update_menu = getattr(self.icon, "update_menu", None)
                if callable(update_menu):
                    update_menu()
            except Exception:
                pass

        if not refresh_windows:
            return
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
        self._reveal_conversation(ip)
        if sender.strip().lower() != self.username.strip().lower():
            self.typing_states.pop(ip, None)
            self.typing_state_tokens[ip] = self.typing_state_tokens.get(ip, 0) + 1
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
            if self.main_window is not None and self.main_window.winfo_exists():
                self.main_window.refresh_sidebar()

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
        self._reveal_conversation(ip)
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

    def refresh_file_queue_status(self, conversation_key: str) -> None:
        if conversation_key in self.active_file_transfers:
            return

        queued_items = self.file_queues.get(conversation_key, [])
        display_ip = self._display_ip_for_conversation_key(conversation_key)
        if not display_ip and queued_items:
            display_ip = str(queued_items[0].get("conversation_ip", "")).strip()
        if not display_ip:
            return

        if not queued_items:
            self.transfer_statuses.pop(display_ip, None)
            if self.main_window is not None and self.main_window.winfo_exists():
                self.main_window.refresh_transfer(display_ip)
            return

        next_item = queued_items[0]
        label = f"Queued {next_item['filename']}"
        if len(queued_items) > 1:
            label = f"{label} (+{len(queued_items) - 1} more)"
        self.transfer_status_tokens[display_ip] = self.transfer_status_tokens.get(display_ip, 0) + 1
        self.transfer_statuses[display_ip] = (label, 0, 1)
        if self.main_window is not None and self.main_window.winfo_exists():
            self.main_window.refresh_transfer(display_ip)

    def open_quick_chat(self, ip: str) -> "MainChatWindow":
        return self.show_main_window(ip)

    def show_main_window(self, ip: str | None = None) -> "MainChatWindow":
        if self.main_window is None or not self.main_window.winfo_exists():
            self.main_window = MainChatWindow(self)
        if ip:
            self._reveal_conversation(ip)
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
        self.bind_all("<MouseWheel>", self._dispatch_mousewheel, add="+")
        self.bind_all("<Button-4>", self._dispatch_mousewheel, add="+")
        self.bind_all("<Button-5>", self._dispatch_mousewheel, add="+")

    def _configure_window_chrome(self) -> None:
        try:
            if sys.platform == "darwin":
                self.tk.call("::tk::unsupported::MacWindowStyle", "style", self._w, "document", "closeBox")
        except Exception:
            pass

    def show(self) -> None:
        self.app.prepare_window_host()
        self.deiconify()
        self.after_idle(self._center_on_screen)
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
        for widget in (canvas, *widgets):
            setattr(widget, "_scroll_canvas_target", canvas)

    def _dispatch_mousewheel(self, event: Any) -> str | None:
        canvas = self._scroll_canvas_for_widget(getattr(event, "widget", None))
        if canvas is None or not self.is_visible():
            return None
        return self._on_mousewheel(canvas, event)

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
            try:
                start, end = canvas.yview()
                if delta < 0 and start <= 0.0:
                    canvas.yview_moveto(0.0)
                    return "break"
                if delta > 0 and end >= 1.0:
                    canvas.yview_moveto(1.0)
                    return "break"
            except tk.TclError:
                return "break"
            canvas.yview_scroll(delta, "units")
        return "break"

    def _scroll_canvas_for_widget(self, widget: tk.Misc | None) -> tk.Canvas | None:
        current = widget
        while current is not None:
            canvas = getattr(current, "_scroll_canvas_target", None)
            if isinstance(canvas, tk.Canvas):
                return canvas
            current = cast(tk.Misc | None, getattr(current, "master", None))
        return None

    def _center_on_screen(self) -> None:
        try:
            self.update_idletasks()
            width = max(self.winfo_width(), self.winfo_reqwidth())
            height = max(self.winfo_height(), self.winfo_reqheight())
            x = max((self.winfo_screenwidth() - width) // 2, 0)
            y = max((self.winfo_screenheight() - height) // 2, 0)
            self.geometry(f"{width}x{height}+{x}+{y}")
        except Exception:
            pass

    def _append_bubble(self, widget: tk.Frame, my_username: str, entry: MessageEntry, wraplength: int) -> None:
        timestamp = format_message_time(entry.timestamp)
        normalized_sender = entry.sender.strip().lower()
        own_sender = my_username.strip().lower()

        if normalized_sender == "system":
            card = RoundedPanel(
                widget,
                background=UI_COLORS["panel_bg"],
                fill=UI_COLORS["card_elevated"],
                border=UI_COLORS["border"],
                radius=UI_METRICS["radius_chip"],
                padding=(16, 10),
            )
            card.pack(pady=(0, 10))
            tk.Label(
                card.content,
                text=f"{timestamp}\n{entry.text}",
                justify="center",
                bg=UI_COLORS["card_elevated"],
                fg=UI_COLORS["muted"],
                font=(UI_FONT, 10, "italic"),
                wraplength=wraplength,
            ).pack()
            return

        direction = "outgoing" if normalized_sender == own_sender else "incoming"
        header = "You" if direction == "outgoing" else entry.sender
        row = tk.Frame(widget, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        row.pack(fill="x", pady=(0, 10))

        align = tk.Frame(row, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        align.pack(anchor="e" if direction == "outgoing" else "w")

        justify = "right" if direction == "outgoing" else "left"
        anchor = "e" if direction == "outgoing" else "w"
        bubble_fill = UI_COLORS["outgoing_bg"] if direction == "outgoing" else UI_COLORS["incoming_bg"]
        meta_row = tk.Frame(align, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        meta_row.pack(anchor=anchor, fill="x", pady=(0, 4))
        if direction == "incoming":
            avatar = AvatarBadge(meta_row, name=entry.sender, diameter=26, background=UI_COLORS["panel_bg"])
            avatar.pack(side="left", padx=(0, 8))
        meta_text = tk.Frame(meta_row, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        meta_text.pack(side="left" if direction == "incoming" else "right")
        tk.Label(
            meta_text,
            text=header,
            justify=justify,
            anchor=anchor,
            bg=UI_COLORS["panel_bg"],
            fg=UI_COLORS["text"],
            font=(UI_FONT, 10, "bold"),
        ).pack(anchor=anchor)
        status_text = timestamp
        if direction == "outgoing" and entry.status:
            status_text = f"{status_text}  {entry.status}"
        tk.Label(
            meta_text,
            text=status_text,
            justify=justify,
            anchor=anchor,
            bg=UI_COLORS["panel_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9),
        ).pack(anchor=anchor)

        bubble = RoundedPanel(
            align,
            background=UI_COLORS["panel_bg"],
            fill=bubble_fill,
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(15, 12),
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
        super().__init__(
            app,
            f"Contacts - {APP_TITLE}",
            f"{UI_METRICS['contacts_width']}x{UI_METRICS['contacts_height']}",
        )
        self.minsize(820, 560)
        self._online_options: dict[str, str] = {}

        frame = ttk.Frame(self, padding=18)
        frame.pack(fill="both", expand=True, padx=10, pady=10)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)

        hero = RoundedPanel(
            frame,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(22, 20),
            stretch=True,
        )
        hero.grid(row=0, column=0, sticky="ew", pady=(0, 14))
        hero.content.columnconfigure(0, weight=1)
        ttk.Label(hero.content, text="Contacts", style="AppTitle.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(
            hero.content,
            text="Manage saved people and live LAN discovery without touching the messaging backend.",
            style="Subheading.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 14))
        hero_actions = tk.Frame(hero.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        hero_actions.grid(row=0, column=1, rowspan=2, sticky="e")
        RoundedButton(
            hero_actions,
            text="Scan LAN",
            command=self.search_for_contacts,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=112,
        ).pack(side="left")
        RoundedButton(
            hero_actions,
            text="Open Chat",
            command=self.chat_selected,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=112,
        ).pack(side="left", padx=(10, 0))

        content = ttk.Frame(frame)
        content.grid(row=1, column=0, sticky="nsew")
        content.columnconfigure(0, weight=1)
        content.columnconfigure(1, weight=1)
        content.rowconfigure(0, weight=1)

        saved_card = RoundedPanel(
            content,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(16, 16),
            stretch=True,
        )
        saved_card.grid(row=0, column=0, sticky="nsew", padx=(0, 8))
        saved_card.content.columnconfigure(0, weight=1)
        saved_card.content.rowconfigure(2, weight=1)

        ttk.Label(saved_card.content, text="Saved Contacts", style="Section.TLabel").grid(row=0, column=0, sticky="w")
        self.saved_summary_var = tk.StringVar(value="")
        ttk.Label(saved_card.content, textvariable=self.saved_summary_var, style="Subheading.TLabel").grid(row=1, column=0, sticky="w", pady=(4, 12))

        self.tree = ttk.Treeview(saved_card.content, columns=("name", "status"), show="headings", height=14)
        self.tree.heading("name", text="Name")
        self.tree.heading("status", text="Availability")
        self.tree.column("name", width=250, anchor="w")
        self.tree.column("status", width=150, anchor="w")
        self.tree.tag_configure("online", background=UI_COLORS["success_bg"])
        self.tree.tag_configure("offline", background=UI_COLORS["card_bg"])
        self.tree.grid(row=2, column=0, sticky="nsew")
        self.tree.bind("<Double-1>", lambda _event: self.chat_selected(), add="+")

        saved_actions = tk.Frame(saved_card.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        saved_actions.grid(row=3, column=0, sticky="ew", pady=(12, 0))
        RoundedButton(
            saved_actions,
            text="Open Chat",
            command=self.chat_selected,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=112,
        ).pack(side="left")
        RoundedButton(
            saved_actions,
            text="Remove",
            command=self.remove_selected,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["danger_bg"],
            hover_fill="#f9d8de",
            text_color=UI_COLORS["danger"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=112,
        ).pack(side="left", padx=(10, 0))

        discovered_card = RoundedPanel(
            content,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(16, 16),
            stretch=True,
        )
        discovered_card.grid(row=0, column=1, sticky="nsew", padx=(8, 0))
        discovered_card.content.columnconfigure(0, weight=1)
        discovered_card.content.rowconfigure(2, weight=1)

        ttk.Label(discovered_card.content, text="Live Discovery", style="Section.TLabel").grid(row=0, column=0, sticky="w")
        self.discovered_summary_var = tk.StringVar(value="")
        ttk.Label(discovered_card.content, textvariable=self.discovered_summary_var, style="Subheading.TLabel").grid(row=1, column=0, sticky="w", pady=(4, 12))

        self.discovered_tree = ttk.Treeview(discovered_card.content, columns=("name", "address"), show="headings", height=14)
        self.discovered_tree.heading("name", text="Discovered Peer")
        self.discovered_tree.heading("address", text="Address")
        self.discovered_tree.column("name", width=250, anchor="w")
        self.discovered_tree.column("address", width=150, anchor="w")
        self.discovered_tree.grid(row=2, column=0, sticky="nsew")
        self.discovered_tree.bind("<Double-1>", lambda _event: self.add_selected_online(), add="+")

        discovered_actions = tk.Frame(discovered_card.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        discovered_actions.grid(row=3, column=0, sticky="ew", pady=(12, 0))
        RoundedButton(
            discovered_actions,
            text="Add Contact",
            command=self.add_selected_online,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=122,
        ).pack(side="left")
        RoundedButton(
            discovered_actions,
            text="Refresh",
            command=self.search_for_contacts,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=110,
        ).pack(side="left", padx=(10, 0))

        self.footer_var = tk.StringVar(value="")
        ttk.Label(frame, textvariable=self.footer_var, style="Caption.TLabel").grid(row=2, column=0, sticky="w", pady=(10, 0))

        self.refresh()

    def search_for_contacts(self) -> None:
        self.app.trigger_discovery_scan()
        self.refresh()

    def refresh(self) -> None:
        for item_id in self.tree.get_children():
            self.tree.delete(item_id)
        for item_id in self.discovered_tree.get_children():
            self.discovered_tree.delete(item_id)

        for contact in sorted(self.app.contacts, key=lambda contact: contact.username.lower()):
            peer = self.app.find_peer_for_contact(contact)
            status = "Online" if peer is not None else "Offline"
            tag = "online" if peer is not None else "offline"
            self.tree.insert("", "end", iid=contact.public_key_b64, values=(contact.username, status), tags=(tag,))

        online_peers = [
            peer for peer in sorted(self.app.peers.values(), key=lambda peer: peer.username.lower())
            if not self.app.is_contact(peer)
        ]
        self._online_options = {}
        for peer in online_peers:
            label = peer.username
            suffix = 2
            while label in self._online_options:
                label = f"{peer.username} ({suffix})"
                suffix += 1
            self._online_options[label] = peer.ip
            self.discovered_tree.insert("", "end", iid=peer.ip, values=(label, peer.ip))

        saved_total = len(self.app.contacts)
        online_total = sum(1 for contact in self.app.contacts if self.app.find_peer_for_contact(contact) is not None)
        self.saved_summary_var.set(f"{saved_total} saved contact{'s' if saved_total != 1 else ''} • {online_total} online now")

        discovered_total = len(online_peers)
        self.discovered_summary_var.set(
            "Peers currently visible on your LAN."
            if discovered_total
            else "No unsaved peers are visible right now."
        )
        self.footer_var.set(
            f"Discovery: {discovered_total} peer{'s' if discovered_total != 1 else ''} available to add."
        )

    def add_selected_online(self) -> None:
        selection = self.discovered_tree.selection()
        if not selection:
            return
        ip = selection[0]
        self.app.add_contact_from_peer(ip)
        self.refresh()

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
        super().__init__(
            app,
            f"Settings - {APP_TITLE}",
            f"{UI_METRICS['settings_width']}x{UI_METRICS['settings_height']}",
        )
        self.minsize(680, 560)

        frame = ttk.Frame(self, padding=18)
        frame.pack(fill="both", expand=True, padx=10, pady=10)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)

        hero = RoundedPanel(
            frame,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(22, 20),
            stretch=True,
        )
        hero.grid(row=0, column=0, sticky="ew", pady=(0, 14))
        hero.content.columnconfigure(0, weight=1)

        ttk.Label(hero.content, text="Settings", style="AppTitle.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(
            hero.content,
            text="Identity, storage, and update behavior with the same secure local backend.",
            style="Subheading.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 14))

        stats = tk.Frame(hero.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        stats.grid(row=2, column=0, sticky="w")
        for index, text in enumerate((
            f"Version {APP_VERSION}",
            f"Local IP {self.app.local_ip}",
            "Encrypted history enabled",
        )):
            label = tk.Label(
                stats,
                text=text,
                bg=UI_COLORS["accent_soft"] if index == 0 else UI_COLORS["panel_alt"],
                fg=UI_COLORS["accent"] if index == 0 else UI_COLORS["muted"],
                font=(UI_FONT, 9, "bold"),
                padx=10,
                pady=6,
            )
            label.pack(side="left", padx=(0, 8))

        hero_actions = tk.Frame(hero.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        hero_actions.grid(row=0, column=1, rowspan=3, sticky="ne")
        RoundedButton(
            hero_actions,
            text="Check Updates",
            command=lambda: self.app.check_for_updates(manual=True),
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=136,
        ).pack(side="left")

        shell = RoundedPanel(
            frame,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(18, 18),
            stretch=True,
        )
        shell.grid(row=1, column=0, sticky="nsew")
        shell.content.columnconfigure(0, weight=1)

        self.settings_body = tk.Frame(shell.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.settings_body.grid(row=0, column=0, sticky="nsew")
        self.settings_body.columnconfigure(0, weight=1)
        self.settings_body.columnconfigure(1, weight=1)

        self.username_var = tk.StringVar(value=self.app.username)
        self.update_server_var = tk.StringVar(value=self.app.config.update_server_url)
        self.inbox_dir_var = tk.StringVar(value=str(self.app.inbox_dir))

        profile_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(16, 16),
            stretch=True,
        )
        profile_card.grid(row=0, column=0, sticky="nsew", padx=(0, 8), pady=(0, 12))
        profile_card.content.columnconfigure(0, weight=1)
        ttk.Label(profile_card.content, text="Profile", style="Section.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(profile_card.content, text="Shown to everyone on your LAN.", style="Subheading.TLabel").grid(row=1, column=0, sticky="w", pady=(4, 12))
        ttk.Label(profile_card.content, text="Display Name").grid(row=2, column=0, sticky="w")
        ttk.Entry(profile_card.content, textvariable=self.username_var).grid(row=3, column=0, sticky="ew", pady=(6, 12))
        ttk.Label(profile_card.content, text="App Version").grid(row=4, column=0, sticky="w")
        ttk.Label(profile_card.content, text=APP_VERSION, style="Subheading.TLabel").grid(row=5, column=0, sticky="w", pady=(6, 0))

        updates_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(16, 16),
            stretch=True,
        )
        updates_card.grid(row=0, column=1, sticky="nsew", padx=(8, 0), pady=(0, 12))
        updates_card.content.columnconfigure(0, weight=1)
        ttk.Label(updates_card.content, text="Updates", style="Section.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(
            updates_card.content,
            text=f"Point the app at a manifest JSON or a folder containing {UPDATE_MANIFEST_FILENAME}.",
            style="Subheading.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 12))
        ttk.Label(updates_card.content, text="Update Server URL").grid(row=2, column=0, sticky="w")
        ttk.Entry(updates_card.content, textvariable=self.update_server_var).grid(row=3, column=0, sticky="ew", pady=(6, 12))
        ttk.Label(
            updates_card.content,
            text="Manual update checks open the platform download page when a newer version is found.",
            style="Caption.TLabel",
        ).grid(row=4, column=0, sticky="w")

        storage_card = RoundedPanel(
            self.settings_body,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(16, 16),
            stretch=True,
        )
        storage_card.grid(row=1, column=0, columnspan=2, sticky="nsew")
        storage_card.content.columnconfigure(0, weight=1)
        ttk.Label(storage_card.content, text="Storage", style="Section.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(
            storage_card.content,
            text="Received files are stored here, while conversation history remains encrypted on disk.",
            style="Subheading.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 12))
        ttk.Label(storage_card.content, text="Received Files Folder").grid(row=2, column=0, sticky="w")

        inbox_row = tk.Frame(storage_card.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        inbox_row.grid(row=3, column=0, sticky="ew", pady=(6, 0))
        inbox_row.columnconfigure(0, weight=1)
        ttk.Entry(inbox_row, textvariable=self.inbox_dir_var).grid(row=0, column=0, sticky="ew")
        RoundedButton(
            inbox_row,
            text="Browse",
            command=self.pick_inbox_dir,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=102,
        ).grid(row=0, column=1, padx=(10, 0))

        ttk.Label(
            storage_card.content,
            text="Tip: choose a synced folder if you want received files to surface in other tools automatically.",
            style="Caption.TLabel",
        ).grid(row=4, column=0, sticky="w", pady=(12, 0))

        buttons = tk.Frame(frame, bg=UI_COLORS["app_bg"], bd=0, highlightthickness=0)
        buttons.grid(row=2, column=0, sticky="ew", pady=(14, 0))
        RoundedButton(
            buttons,
            text="Save Changes",
            command=self.save,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=138,
        ).pack(side="left")
        RoundedButton(
            buttons,
            text="Close",
            command=self.hide,
            background=UI_COLORS["app_bg"],
            fill=UI_COLORS["card_bg"],
            hover_fill=UI_COLORS["accent_soft"],
            text_color=UI_COLORS["text"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=102,
        ).pack(side="right")

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
        self.app.refresh_tray_menu(refresh_windows=True)
        self.hide()


class MainChatWindow(BaseWindow):
    def __init__(self, app: LanMessengerApp) -> None:
        super().__init__(
            app,
            APP_NAME,
            f"{UI_METRICS['main_width']}x{UI_METRICS['main_height']}",
        )
        self.minsize(940, 620)
        self.selected_ip: str | None = None
        self.row_menus: list[tk.Menu] = []
        self.typing_idle_job: str | None = None
        self.typing_target_ip: str | None = None
        self.sidebar_search_var = tk.StringVar()
        self.sidebar_search_var.trace_add("write", lambda *_args: self.refresh_sidebar())
        self.sidebar_count_var = tk.StringVar(value="No conversations yet")
        self.header_name_var = tk.StringVar(value=APP_NAME)
        self.header_status_var = tk.StringVar(value="Secure local messaging on your network.")
        self.header_meta_var = tk.StringVar(value="Select a conversation to begin.")
        self.header_typing_var = tk.StringVar(value="")
        self.composer_hint_var = tk.StringVar(
            value="Enter to send. Shift+Enter for a new line. Drop files into the message area to queue them."
        )

        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)
        self.configure(bg=UI_COLORS["shell_bg"])

        container = tk.Frame(self, bg=UI_COLORS["shell_bg"], bd=0, highlightthickness=0)
        container.grid(row=0, column=0, sticky="nsew")
        container.configure(padx=18, pady=18)
        container.columnconfigure(1, weight=1)
        container.rowconfigure(0, weight=1)

        sidebar = tk.Frame(
            container,
            bg=UI_COLORS["shell_bg"],
            width=UI_METRICS["sidebar_width"],
            bd=0,
            highlightthickness=0,
        )
        sidebar.grid(row=0, column=0, sticky="nsw", padx=(0, 12))
        sidebar.grid_propagate(False)

        sidebar_hero = RoundedPanel(
            sidebar,
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(18, 18),
        )
        sidebar_hero.pack(fill="x", pady=(0, 12))
        sidebar_hero.content.columnconfigure(0, weight=1)
        ttk.Label(sidebar_hero.content, text="Chats", style="AppTitle.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(
            sidebar_hero.content,
            text="Modern local messaging with encrypted history, drag-and-drop files, and live LAN presence.",
            style="Subheading.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 14))

        identity = tk.Frame(sidebar_hero.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        identity.grid(row=2, column=0, sticky="w")
        tk.Label(
            identity,
            text=self.app.username,
            bg=UI_COLORS["accent_soft"],
            fg=UI_COLORS["accent"],
            font=(UI_FONT, 9, "bold"),
            padx=10,
            pady=6,
        ).pack(side="left", padx=(0, 8))
        tk.Label(
            identity,
            text=self.app.local_ip,
            bg=UI_COLORS["panel_alt"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9, "bold"),
            padx=10,
            pady=6,
        ).pack(side="left")

        sidebar_actions = tk.Frame(sidebar_hero.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        sidebar_actions.grid(row=3, column=0, sticky="ew", pady=(14, 0))
        RoundedButton(
            sidebar_actions,
            text="Contacts",
            command=self.app.show_contacts_window,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=110,
        ).pack(side="left")
        RoundedButton(
            sidebar_actions,
            text="Settings",
            command=self.app.show_settings_window,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=108,
        ).pack(side="left", padx=(8, 0))
        RoundedButton(
            sidebar_actions,
            text="Refresh",
            command=self.app.trigger_discovery_scan,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["card_elevated"],
            hover_fill=UI_COLORS["panel_alt"],
            text_color=UI_COLORS["text"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=98,
        ).pack(side="left", padx=(8, 0))

        sidebar_holder = RoundedPanel(
            sidebar,
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(16, 16),
            stretch=True,
        )
        sidebar_holder.pack(fill="both", expand=True)
        sidebar_holder.content.columnconfigure(0, weight=1)
        sidebar_holder.content.rowconfigure(3, weight=1)

        top_row = tk.Frame(sidebar_holder.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        top_row.grid(row=0, column=0, sticky="ew")
        ttk.Label(top_row, text="Recent Conversations", style="Section.TLabel").pack(side="left")
        ttk.Label(top_row, textvariable=self.sidebar_count_var, style="Caption.TLabel").pack(side="right")

        ttk.Label(
            sidebar_holder.content,
            text="Filter by name, address, or preview text.",
            style="Caption.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 10))

        search_card = RoundedPanel(
            sidebar_holder.content,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["input_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_chip"],
            padding=(10, 8),
            stretch=True,
        )
        search_card.grid(row=2, column=0, sticky="ew", pady=(0, 12))
        search_card.content.columnconfigure(0, weight=1)
        self.search_entry = ttk.Entry(search_card.content, textvariable=self.sidebar_search_var)
        self.search_entry.grid(row=0, column=0, sticky="ew")

        list_shell = tk.Frame(sidebar_holder.content, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        list_shell.grid(row=3, column=0, sticky="nsew")
        list_shell.columnconfigure(0, weight=1)
        list_shell.rowconfigure(0, weight=1)
        self.sidebar_canvas = tk.Canvas(list_shell, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        self.sidebar_canvas.grid(row=0, column=0, sticky="nsew")

        self.sidebar_list = tk.Frame(self.sidebar_canvas, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
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

        chat = tk.Frame(container, bg=UI_COLORS["shell_bg"], bd=0, highlightthickness=0)
        chat.grid(row=0, column=1, sticky="nsew")
        chat.columnconfigure(0, weight=1)
        chat.rowconfigure(1, weight=1)

        header = RoundedPanel(
            chat,
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(18, 16),
            stretch=True,
        )
        header.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        header.content.columnconfigure(1, weight=1)
        self.header_avatar = AvatarBadge(header.content, name=self.app.username, diameter=54, background=UI_COLORS["card_bg"])
        self.header_avatar.grid(row=0, column=0, rowspan=4, sticky="w", padx=(0, 14))
        tk.Label(
            header.content,
            textvariable=self.header_name_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["text"],
            font=(UI_FONT, 16, "bold"),
        ).grid(row=0, column=1, sticky="w")
        self.header_badge = tk.Label(
            header.content,
            text="Idle",
            bg=UI_COLORS["panel_alt"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9, "bold"),
            padx=10,
            pady=4,
        )
        self.header_badge.grid(row=0, column=2, sticky="w", padx=(10, 0))
        tk.Label(
            header.content,
            textvariable=self.header_status_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 10),
        ).grid(row=1, column=1, columnspan=2, sticky="w", pady=(6, 0))
        tk.Label(
            header.content,
            textvariable=self.header_meta_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["subtle"],
            font=(UI_FONT, 9),
        ).grid(row=2, column=1, columnspan=2, sticky="w", pady=(3, 0))
        self.header_typing_label = tk.Label(
            header.content,
            textvariable=self.header_typing_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["accent"],
            font=(UI_FONT, 10, "italic"),
        )
        self.header_typing_label.grid(row=3, column=1, columnspan=2, sticky="w", pady=(6, 0))

        header_actions = tk.Frame(header.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        header_actions.grid(row=0, column=3, rowspan=4, sticky="e", padx=(18, 0))
        self.file_button = RoundedButton(
            header_actions,
            text="Send File",
            command=self.pick_file,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=118,
        )
        self.file_button.pack(side="left")
        self.info_button = RoundedButton(
            header_actions,
            text="Info",
            command=self._show_active_chat_info,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent_soft"],
            hover_fill=UI_COLORS["card_selected"],
            text_color=UI_COLORS["accent"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=88,
        )
        self.info_button.pack(side="left", padx=(8, 0))
        self.delete_button = RoundedButton(
            header_actions,
            text="Delete",
            command=self._delete_active_thread,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["danger_bg"],
            hover_fill="#f9d8de",
            text_color=UI_COLORS["danger"],
            disabled_fill=UI_COLORS["panel_alt"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=92,
        )
        self.delete_button.pack(side="left", padx=(8, 0))

        history_card = RoundedPanel(
            chat,
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["panel_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
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
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["card_elevated"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(14, 12),
            stretch=True,
        )
        self.transfer_card.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        self.transfer_label = tk.Label(
            self.transfer_card.content,
            text="",
            bg=UI_COLORS["card_elevated"],
            fg=UI_COLORS["text"],
            font=(UI_FONT, 10, "bold"),
            anchor="w",
        )
        self.transfer_label.pack(anchor="w")
        self.transfer_bar = ttk.Progressbar(self.transfer_card.content, mode="determinate")
        self.transfer_bar.pack(fill="x", pady=(6, 0))
        self.transfer_card.grid_remove()

        composer = RoundedPanel(
            chat,
            background=UI_COLORS["shell_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(12, 12),
            stretch=True,
        )
        composer.grid(row=3, column=0, sticky="ew", pady=(10, 0))
        composer.content.columnconfigure(1, weight=1, minsize=200)

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
            radius=UI_METRICS["radius_button"],
            min_width=82,
        )
        self.attach_button.grid(row=0, column=0, rowspan=2, sticky="nw", padx=(0, 10))

        self.entry_shell = RoundedPanel(
            composer.content,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["input_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_card"],
            padding=(8, 7),
            stretch=False,
        )
        self.entry_shell.grid(row=0, column=1, sticky="ew")
        self.entry_shell.content.columnconfigure(0, weight=1)

        self.entry = tk.Text(
            self.entry_shell.content,
            height=1,
            width=1,
            wrap="word",
            bg=UI_COLORS["input_bg"],
            fg=UI_COLORS["text"],
            insertbackground=UI_COLORS["text"],
            relief="flat",
            bd=0,
            highlightthickness=0,
            padx=8,
            pady=4,
            font=(UI_FONT, 11),
        )
        self.entry.grid(row=0, column=0, sticky="ew")
        self.entry.bind("<Return>", self.on_enter)
        self.entry.bind("<KeyRelease>", self._schedule_composer_resize, add="+")
        self.entry.bind("<KeyRelease>", self._handle_composer_change, add="+")
        self.entry.bind("<<Paste>>", self._schedule_composer_resize, add="+")
        self.entry.bind("<<Paste>>", self._handle_composer_change, add="+")
        self.entry.bind("<FocusIn>", lambda _event: self._update_entry_placeholder(), add="+")
        self.entry.bind("<FocusOut>", lambda _event: self._update_entry_placeholder(), add="+")

        self.entry_placeholder = tk.Label(
            self.entry_shell.content,
            text="Write a message",
            bg=UI_COLORS["input_bg"],
            fg=UI_COLORS["subtle"],
            font=(UI_FONT, 11),
            cursor="xterm",
        )
        self.entry_placeholder.place(x=14, y=9)
        self.entry_placeholder.bind("<Button-1>", lambda _event: self.entry.focus_set())

        self.send_button = RoundedButton(
            composer.content,
            text="Send",
            command=self.send_text,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=76,
        )
        self.send_button.grid(row=0, column=2, sticky="ne", padx=(10, 0))

        tk.Label(
            composer.content,
            textvariable=self.composer_hint_var,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9),
            anchor="w",
        ).grid(row=1, column=1, columnspan=2, sticky="ew", pady=(8, 0))

        self._init_drop_target()
        self.after_idle(self._resize_composer_to_content)
        self.refresh()

    def show(self) -> None:
        super().show()
        if self.selected_ip is None:
            self.select_chat(None)
        elif self.selected_ip:
            self.app.mark_peer_read(self.selected_ip)
        if self.selected_ip:
            self.entry.focus_set()

    def hide(self) -> None:
        self._flush_typing_state()
        super().hide()

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
        self.refresh_sidebar()
        if self.selected_ip == ip:
            self.refresh_current_chat()

    def _show_active_chat_info(self) -> None:
        if self.selected_ip:
            self._show_chat_info(self.selected_ip)

    def _delete_active_thread(self) -> None:
        if self.selected_ip:
            self._delete_thread(self.selected_ip)

    def _filtered_conversation_targets(self) -> list[str]:
        query = self.sidebar_search_var.get().strip().lower()
        targets = self.app.conversation_targets()
        if not query:
            return targets

        filtered: list[str] = []
        for ip in targets:
            haystack = " ".join((self.app.conversation_name(ip), ip, self.app.conversation_preview(ip))).lower()
            if query in haystack:
                filtered.append(ip)
        return filtered

    def refresh_sidebar(self) -> None:
        try:
            sidebar_y = self.sidebar_canvas.yview()[0] if self.sidebar_canvas.winfo_exists() else 0.0
        except tk.TclError:
            sidebar_y = 0.0
        self.row_menus.clear()
        for child in self.sidebar_list.winfo_children():
            child.destroy()

        conversation_ips = self._filtered_conversation_targets()
        total_conversations = len(self.app.conversation_targets())
        self.sidebar_count_var.set(
            f"{len(conversation_ips)} shown"
            if self.sidebar_search_var.get().strip()
            else f"{total_conversations} total"
        )
        if not conversation_ips:
            empty = RoundedPanel(
                self.sidebar_list,
                background=UI_COLORS["panel_bg"],
                fill=UI_COLORS["card_elevated"],
                border=UI_COLORS["border"],
                radius=UI_METRICS["radius_card"],
                padding=(16, 16),
                stretch=True,
            )
            empty.pack(fill="x", pady=(2, 0))
            message = (
                "No conversations match your search."
                if self.sidebar_search_var.get().strip()
                else "No conversations yet.\nPeers discovered on your LAN will appear here."
            )
            tk.Label(
                empty.content,
                text=message,
                justify="left",
                bg=UI_COLORS["card_elevated"],
                fg=UI_COLORS["muted"],
                font=(UI_FONT, 10),
                wraplength=220,
            ).pack(anchor="w")
            self._bind_mousewheel_recursive(self.sidebar_list, self.sidebar_canvas)
            self.after_idle(lambda: self.sidebar_canvas.yview_moveto(0.0))
            return

        for ip in conversation_ips:
            self._build_row(ip)
        self._bind_mousewheel_recursive(self.sidebar_list, self.sidebar_canvas)
        self.after_idle(lambda value=sidebar_y: self.sidebar_canvas.yview_moveto(value))

    def _build_row(self, ip: str) -> None:
        selected = ip == self.selected_ip
        bg = UI_COLORS["card_selected"] if selected else UI_COLORS["card_bg"]
        border = UI_COLORS["accent"] if selected else UI_COLORS["border"]
        frame = RoundedPanel(
            self.sidebar_list,
            background=UI_COLORS["panel_bg"],
            fill=bg,
            border=border,
            radius=UI_METRICS["radius_card"],
            padding=(12, 12),
            stretch=True,
        )
        frame.pack(fill="x", pady=(0, 6))
        frame.content.columnconfigure(1, weight=1)

        name = self.app.conversation_name(ip)
        unread = self.app.unread_counts.get(ip, 0)
        preview = truncate_text(self.app.conversation_preview(ip), 78)
        online = self.app.find_peer_by_ip(ip) is not None
        timestamp = format_sidebar_timestamp(self._conversation_timestamp(ip))
        status = "Online now" if online else "Saved contact" if self.app._find_contact_by_ip(ip) is not None else "Offline"

        avatar = AvatarBadge(frame.content, name=name, diameter=42, background=bg)
        avatar.grid(row=0, column=0, rowspan=2, sticky="nw", padx=(0, 10))

        text_wrap = tk.Frame(frame.content, bg=bg, bd=0, highlightthickness=0)
        text_wrap.grid(row=0, column=1, sticky="ew")

        title_row = tk.Frame(text_wrap, bg=bg, bd=0, highlightthickness=0)
        title_row.pack(fill="x")
        title_label = tk.Label(
            title_row,
            text=name,
            anchor="w",
            bg=bg,
            fg=UI_COLORS["text"],
            font=(UI_FONT, 11, "bold"),
        )
        title_label.pack(side="left", fill="x", expand=True)
        if timestamp:
            tk.Label(
                title_row,
                text=timestamp,
                anchor="e",
                bg=bg,
                fg=UI_COLORS["subtle"],
                font=(UI_FONT, 9),
            ).pack(side="right")

        preview_label = tk.Label(
            text_wrap,
            text=preview,
            anchor="w",
            justify="left",
            bg=bg,
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 9),
            wraplength=184,
        )
        preview_label.pack(fill="x", pady=(4, 2))
        status_row = tk.Frame(text_wrap, bg=bg, bd=0, highlightthickness=0)
        status_row.pack(fill="x", pady=(2, 0))
        status_label = tk.Label(
            status_row,
            text=status,
            anchor="w",
            bg=UI_COLORS["success_bg"] if online else UI_COLORS["panel_alt"],
            fg=UI_COLORS["success"] if online else UI_COLORS["muted"],
            font=(UI_FONT, 8, "bold"),
            padx=8,
            pady=3,
        )
        status_label.pack(side="left")
        if unread:
            tk.Label(
                status_row,
                text=f"{unread} new",
                anchor="e",
                bg=UI_COLORS["accent"],
                fg=UI_COLORS["accent_contrast"],
                font=(UI_FONT, 8, "bold"),
                padx=8,
                pady=3,
            ).pack(side="right")

        menu_button = tk.Label(
            frame.content,
            text="...",
            bg=bg,
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 12, "bold"),
            cursor="hand2",
            padx=0,
            pady=0,
        )
        menu_button.bind("<Button-1>", lambda _event, target_ip=ip, button=menu_button: self._show_row_menu(target_ip, button))
        menu_button.grid(row=0, column=2, sticky="ne", padx=(10, 0))

        for widget in (frame, frame.content, avatar, text_wrap, title_label, preview_label, status_row, status_label):
            widget.bind("<Button-1>", lambda _event, target_ip=ip: self.select_chat(target_ip))

    def _show_row_menu(self, ip: str, button: tk.Widget) -> None:
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(label="Show Info", command=lambda target_ip=ip: self._show_chat_info(target_ip))
        menu.add_command(label="Delete Thread", command=lambda target_ip=ip: self._delete_thread(target_ip))
        self.row_menus.append(menu)

        try:
            menu.tk_popup(button.winfo_rootx(), button.winfo_rooty() + button.winfo_height())
        finally:
            menu.grab_release()

    def _show_chat_info(self, ip: str) -> None:
        self.app.show_conversation_info(ip, parent=self)

    def _delete_thread(self, ip: str) -> None:
        if self.app.ask_centered_yes_no("Delete Thread", f"Delete the conversation with {self.app.conversation_name(ip)}?", parent=self):
            self.app.delete_conversation(ip)
            if self.selected_ip == ip:
                self.selected_ip = None
            self.refresh()

    def select_chat(self, ip: str | None) -> None:
        previous_ip = self.selected_ip
        if ip is None:
            conversation_ips = self.app.conversation_targets()
            ip = conversation_ips[0] if conversation_ips else None
        if ip is None:
            self._flush_typing_state()
            self.selected_ip = None
            self.refresh_current_chat()
            return

        peer = self.app._resolve_active_peer(ip)
        if previous_ip and previous_ip != (peer.ip if peer is not None else ip):
            self._flush_typing_state(previous_ip)
        self.selected_ip = peer.ip if peer is not None else ip
        self.refresh()
        if self.selected_ip:
            self.app.mark_peer_read(self.selected_ip)

    def refresh_current_chat(self) -> None:
        if not self.selected_ip:
            self.title(APP_NAME)
            self.header_name_var.set(APP_NAME)
            self.header_status_var.set("Choose a conversation or scan your LAN to start chatting.")
            self.header_meta_var.set(
                "Contacts, live peers, and encrypted message history stay synchronized in the sidebar."
            )
            self.header_typing_var.set("")
            self.header_avatar.set_name(self.app.username)
            self._set_status_badge("Idle", UI_COLORS["panel_alt"], UI_COLORS["muted"])
            self.attach_button.set_enabled(False)
            self.send_button.set_enabled(False)
            self.file_button.set_enabled(False)
            self.info_button.set_enabled(False)
            self.delete_button.set_enabled(False)
            self._set_entry_enabled(False)
            self._render_history([])
            self.transfer_card.grid_remove()
            return

        ip = self.selected_ip
        contact = self.app._find_contact_by_ip(ip)
        self.title(f"{APP_NAME} - {self.app.conversation_name(ip)}")
        self.header_name_var.set(self.app.conversation_name(ip))
        self.header_avatar.set_name(self.app.conversation_name(ip))
        peer = self.app.find_peer_by_ip(ip)
        if peer is not None:
            self.header_status_var.set("Available on your LAN now.")
            self.header_meta_var.set(f"Direct encrypted peer chat • {peer.ip}")
            self._set_status_badge("Online", UI_COLORS["success_bg"], UI_COLORS["success"])
        elif contact is not None and contact.last_ip:
            self.header_status_var.set("Saved contact. Messages queue until this peer is seen again.")
            self.header_meta_var.set(f"Last known address • {contact.last_ip}")
            self._set_status_badge("Saved", UI_COLORS["warning_bg"], UI_COLORS["warning"])
        else:
            self.header_status_var.set("Peer is currently offline.")
            self.header_meta_var.set(f"Conversation target • {ip}")
            self._set_status_badge("Offline", UI_COLORS["danger_bg"], UI_COLORS["danger"])
        self.header_typing_var.set(self.app.typing_status_text(ip))

        self.attach_button.set_enabled(True)
        self.send_button.set_enabled(True)
        self.file_button.set_enabled(True)
        self.info_button.set_enabled(True)
        self.delete_button.set_enabled(True)
        self._set_entry_enabled(True)

        self._render_history(self.app.message_history.get(ip, []))
        self.refresh_transfer(ip)

    def _render_history(self, entries: list[MessageEntry]) -> None:
        for child in self.history_frame.winfo_children():
            child.destroy()
        if not self.selected_ip:
            self._render_history_placeholder(
                "Select a conversation",
                "Your recent chats, saved contacts, and live peers are listed on the left.",
                primary_text="Open Contacts",
                primary_command=self.app.show_contacts_window,
                secondary_text="Refresh LAN",
                secondary_command=self.app.trigger_discovery_scan,
            )
            self._bind_mousewheel_recursive(self.history_frame, self.history_canvas)
            self.after_idle(self._scroll_history_to_latest)
            return
        if not entries:
            self._render_history_placeholder(
                f"Start a conversation with {self.app.conversation_name(self.selected_ip)}",
                "Messages and file transfers stay local to your network and are saved with encrypted history.",
                primary_text="Send File",
                primary_command=lambda: self.pick_file(self.selected_ip),
            )
            self._bind_mousewheel_recursive(self.history_frame, self.history_canvas)
            self.after_idle(self._scroll_history_to_latest)
            return

        wraplength = max(min(self.history_canvas.winfo_width() - 220, 420), 220)
        for entry in entries:
            self._append_bubble(self.history_frame, self.app.username, entry, wraplength)
        self._bind_mousewheel_recursive(self.history_frame, self.history_canvas)
        if not self._history_is_near_bottom():
            return
        self.after_idle(self._scroll_history_to_latest)
        self.after(25, self._scroll_history_to_latest)

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

    def _set_status_badge(self, text: str, background: str, foreground: str) -> None:
        self.header_badge.config(text=text, bg=background, fg=foreground)

    def _schedule_composer_resize(self, _event=None) -> None:
        self.after_idle(self._resize_composer_to_content)

    def _resize_composer_to_content(self) -> None:
        try:
            content = self.entry.get("1.0", "end-1c")
        except tk.TclError:
            return

        if not content:
            target_lines = 1
        else:
            self.entry.update_idletasks()
            try:
                count_result = self.entry.count("1.0", "end-1c", "displaylines")
                if count_result:
                    display_lines = int(count_result[0])
                else:
                    display_lines = len(content.splitlines()) or 1
            except Exception:
                display_lines = len(content.splitlines()) or 1
            target_lines = max(1, min(COMPOSER_MAX_LINES, display_lines))

        self.entry.configure(height=target_lines)
        self.entry_shell._queue_redraw()
        self._update_entry_placeholder()

    def _bind_mousewheel_recursive(self, root: tk.Misc, canvas: tk.Canvas) -> None:
        self._bind_mousewheel(canvas, root)
        for child in root.winfo_children():
            self._bind_mousewheel_recursive(child, canvas)

    def _history_is_near_bottom(self) -> bool:
        try:
            _start, end = self.history_canvas.yview()
            return end >= 0.97
        except tk.TclError:
            return True

    def _scroll_history_to_latest(self) -> None:
        try:
            self.history_frame.update_idletasks()
            self.history_canvas.update_idletasks()
            self.history_canvas.configure(scrollregion=self.history_canvas.bbox("all"))
            self.history_canvas.yview_moveto(1.0)
        except tk.TclError:
            pass

    def _render_history_placeholder(
        self,
        title: str,
        subtitle: str,
        *,
        primary_text: str,
        primary_command,
        secondary_text: str | None = None,
        secondary_command=None,
    ) -> None:
        shell = tk.Frame(self.history_frame, bg=UI_COLORS["panel_bg"], bd=0, highlightthickness=0)
        shell.pack(fill="both", expand=True, pady=50)

        card = RoundedPanel(
            shell,
            background=UI_COLORS["panel_bg"],
            fill=UI_COLORS["card_bg"],
            border=UI_COLORS["border"],
            radius=UI_METRICS["radius_panel"],
            padding=(24, 24),
        )
        card.pack()
        card.content.columnconfigure(0, weight=1)

        placeholder_avatar = AvatarBadge(card.content, name=title, diameter=60, background=UI_COLORS["card_bg"])
        placeholder_avatar.grid(row=0, column=0, pady=(0, 14))
        tk.Label(
            card.content,
            text=title,
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["text"],
            font=(UI_FONT, 15, "bold"),
        ).grid(row=1, column=0)
        tk.Label(
            card.content,
            text=subtitle,
            justify="center",
            bg=UI_COLORS["card_bg"],
            fg=UI_COLORS["muted"],
            font=(UI_FONT, 10),
            wraplength=420,
        ).grid(row=2, column=0, pady=(8, 16))

        actions = tk.Frame(card.content, bg=UI_COLORS["card_bg"], bd=0, highlightthickness=0)
        actions.grid(row=3, column=0)
        RoundedButton(
            actions,
            text=primary_text,
            command=primary_command,
            background=UI_COLORS["card_bg"],
            fill=UI_COLORS["accent"],
            hover_fill=UI_COLORS["accent_active"],
            text_color=UI_COLORS["accent_contrast"],
            disabled_fill=UI_COLORS["accent_soft"],
            disabled_text=UI_COLORS["muted"],
            radius=UI_METRICS["radius_button"],
            min_width=132,
        ).pack(side="left")
        if secondary_text and secondary_command is not None:
            RoundedButton(
                actions,
                text=secondary_text,
                command=secondary_command,
                background=UI_COLORS["card_bg"],
                fill=UI_COLORS["accent_soft"],
                hover_fill=UI_COLORS["card_selected"],
                text_color=UI_COLORS["accent"],
                disabled_fill=UI_COLORS["panel_alt"],
                disabled_text=UI_COLORS["muted"],
                radius=UI_METRICS["radius_button"],
                min_width=124,
            ).pack(side="left", padx=(8, 0))

    def _conversation_timestamp(self, ip: str) -> float | None:
        history = self.app.message_history.get(ip, [])
        if history:
            return history[-1].timestamp
        peer = self.app.find_peer_by_ip(ip)
        if peer is not None:
            return peer.last_seen
        return None

    def _set_entry_enabled(self, enabled: bool) -> None:
        self.entry.config(state="normal" if enabled else "disabled")
        self._update_entry_placeholder()

    def _update_entry_placeholder(self) -> None:
        try:
            content = self.entry.get("1.0", "end-1c")
        except tk.TclError:
            return
        if self.entry.cget("state") == "disabled" or content.strip():
            self.entry_placeholder.place_forget()
        else:
            self.entry_placeholder.place(x=14, y=9)

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
        queued_paths = [raw_path.strip("{}") for raw_path in paths if Path(raw_path.strip("{}")).is_file()]
        if queued_paths:
            self.app.queue_files(self.selected_ip, queued_paths)

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
        self._resize_composer_to_content()
        self._update_entry_placeholder()
        self._flush_typing_state(self.selected_ip)
        self.app.send_text(self.selected_ip, content)

    def pick_file(self, ip: str | None = None) -> None:
        target_ip = ip or self.selected_ip
        if not target_ip:
            return
        paths = filedialog.askopenfilenames(parent=self)
        if not paths:
            return
        self.app.queue_files(target_ip, list(paths))

    def _handle_composer_change(self, _event=None) -> None:
        self._update_entry_placeholder()
        if not self.selected_ip:
            return
        content = self.entry.get("1.0", "end-1c").strip()
        if content:
            self.typing_target_ip = self.selected_ip
            self.app.send_typing_state(self.selected_ip, True)
            if self.typing_idle_job is not None:
                self.after_cancel(self.typing_idle_job)
            self.typing_idle_job = self.after(TYPING_IDLE_TIMEOUT_MS, self._flush_typing_state)
        else:
            self._flush_typing_state(self.selected_ip)

    def _flush_typing_state(self, ip: str | None = None) -> None:
        target_ip = ip or self.typing_target_ip or self.selected_ip
        if self.typing_idle_job is not None:
            self.after_cancel(self.typing_idle_job)
            self.typing_idle_job = None
        if target_ip:
            self.app.send_typing_state(target_ip, False, force=True)
        self.typing_target_ip = None


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
