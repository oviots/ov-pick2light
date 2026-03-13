"""SQLite persistence for device registry."""
import sqlite3
from pathlib import Path

DB_PATH = Path(__file__).parent.parent / "p2l_devices.db"


def _conn() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    with _conn() as db:
        db.execute("""
            CREATE TABLE IF NOT EXISTS devices (
                address TEXT PRIMARY KEY,
                label   TEXT NOT NULL DEFAULT ''
            )
        """)


def load_devices() -> list[dict]:
    with _conn() as db:
        rows = db.execute(
            "SELECT address, label FROM devices ORDER BY address"
        ).fetchall()
    return [{"address": r["address"], "label": r["label"]} for r in rows]


def upsert_device(address: str, label: str) -> None:
    """Insert or overwrite a device record (used when the user explicitly sets label)."""
    with _conn() as db:
        db.execute(
            "INSERT INTO devices (address, label) VALUES (?, ?)"
            " ON CONFLICT(address) DO UPDATE SET label = excluded.label",
            (address, label),
        )


def insert_if_new(address: str) -> str:
    """Register an address with an empty label only if it doesn't exist yet.

    Returns the current label (existing or empty string for new rows).
    """
    with _conn() as db:
        db.execute(
            "INSERT INTO devices (address, label) VALUES (?, '')"
            " ON CONFLICT(address) DO NOTHING",
            (address,),
        )
        row = db.execute(
            "SELECT label FROM devices WHERE address = ?", (address,)
        ).fetchone()
    return row["label"] if row else ""


def update_label(address: str, label: str) -> None:
    with _conn() as db:
        db.execute(
            "UPDATE devices SET label = ? WHERE address = ?",
            (label, address),
        )


def remove_device(address: str) -> None:
    with _conn() as db:
        db.execute("DELETE FROM devices WHERE address = ?", (address,))


def clear_devices() -> None:
    with _conn() as db:
        db.execute("DELETE FROM devices")
