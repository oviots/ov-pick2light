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
                address  TEXT PRIMARY KEY,
                label    TEXT    NOT NULL DEFAULT '',
                order_no INTEGER NOT NULL DEFAULT 0
            )
        """)
        # Migrate existing tables that lack the order_no column
        cols = {row[1] for row in db.execute("PRAGMA table_info(devices)").fetchall()}
        if "order_no" not in cols:
            db.execute("ALTER TABLE devices ADD COLUMN order_no INTEGER NOT NULL DEFAULT 0")


def load_devices() -> list[dict]:
    with _conn() as db:
        rows = db.execute(
            "SELECT address, label, order_no FROM devices ORDER BY order_no, address"
        ).fetchall()
    return [{"address": r["address"], "label": r["label"], "order_no": r["order_no"]} for r in rows]


def upsert_device(address: str, label: str, order_no: int = 0) -> None:
    """Insert or overwrite a device record (used when the user explicitly sets label/order)."""
    with _conn() as db:
        db.execute(
            "INSERT INTO devices (address, label, order_no) VALUES (?, ?, ?)"
            " ON CONFLICT(address) DO UPDATE SET label = excluded.label, order_no = excluded.order_no",
            (address, label, order_no),
        )


def insert_if_new(address: str) -> dict:
    """Register an address with empty label / order 0 only if it doesn't exist yet.

    Returns a dict with the current label and order_no.
    """
    with _conn() as db:
        db.execute(
            "INSERT INTO devices (address, label, order_no) VALUES (?, '', 0)"
            " ON CONFLICT(address) DO NOTHING",
            (address,),
        )
        row = db.execute(
            "SELECT label, order_no FROM devices WHERE address = ?", (address,)
        ).fetchone()
    return {"label": row["label"], "order_no": row["order_no"]} if row else {"label": "", "order_no": 0}


def update_label(address: str, label: str) -> None:
    with _conn() as db:
        db.execute(
            "UPDATE devices SET label = ? WHERE address = ?",
            (label, address),
        )


def update_order(address: str, order_no: int) -> None:
    with _conn() as db:
        db.execute(
            "UPDATE devices SET order_no = ? WHERE address = ?",
            (order_no, address),
        )


def remove_device(address: str) -> None:
    with _conn() as db:
        db.execute("DELETE FROM devices WHERE address = ?", (address,))


def clear_devices() -> None:
    with _conn() as db:
        db.execute("DELETE FROM devices")
