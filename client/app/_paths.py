"""Path helpers that work in both dev mode and PyInstaller frozen mode."""
import sys
from pathlib import Path


def app_data_dir() -> Path:
    """Writable directory for runtime files (config, DB).

    - Frozen (PyInstaller): next to the binary
    - Dev: client/ root (parent of this package)
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent  # type: ignore[attr-defined]
    return Path(__file__).parent.parent


def bundle_dir() -> Path:
    """Directory containing bundled read-only assets (templates, static).

    - Frozen (PyInstaller): sys._MEIPASS temp extraction dir
    - Dev: client/ root
    """
    if getattr(sys, "frozen", False):
        return Path(sys._MEIPASS)  # type: ignore[attr-defined]
    return Path(__file__).parent.parent
