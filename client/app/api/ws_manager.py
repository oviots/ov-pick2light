"""WebSocket connection manager for broadcasting real-time events to the browser."""
import asyncio
import json
import logging
from typing import Any, Callable, Optional

from fastapi import WebSocket

logger = logging.getLogger(__name__)


class WebSocketManager:
    """Manages all active WebSocket connections and broadcasts messages."""

    def __init__(self) -> None:
        self._connections: set[WebSocket] = set()
        # Called (no args) when the connection count drops from 1 → 0.
        self.on_empty:    Optional[Callable[[], None]] = None
        # Called (no args) when the connection count rises from 0 → 1.
        self.on_nonempty: Optional[Callable[[], None]] = None

    async def connect(self, ws: WebSocket) -> None:
        await ws.accept()
        was_empty = len(self._connections) == 0
        self._connections.add(ws)
        logger.debug("WebSocket connected; total=%d", len(self._connections))
        if was_empty and self.on_nonempty:
            self.on_nonempty()

    def disconnect(self, ws: WebSocket) -> None:
        self._connections.discard(ws)
        logger.debug("WebSocket disconnected; total=%d", len(self._connections))
        if len(self._connections) == 0 and self.on_empty:
            self.on_empty()

    async def broadcast(self, message: dict[str, Any]) -> None:
        """Send a JSON message to all connected clients in parallel."""
        if not self._connections:
            return
        text = json.dumps(message, default=str)

        results = await asyncio.gather(
            *[ws.send_text(text) for ws in list(self._connections)],
            return_exceptions=True,
        )
        dead = [ws for ws, result in zip(list(self._connections), results)
                if isinstance(result, Exception)]
        for ws in dead:
            self._connections.discard(ws)

    async def broadcast_sync(self, message: dict[str, Any]) -> None:
        """Thread-safe broadcast — schedules on the running event loop."""
        try:
            loop = asyncio.get_running_loop()
            asyncio.run_coroutine_threadsafe(self.broadcast(message), loop)
        except Exception as exc:
            logger.error("broadcast_sync error: %s", exc)

    @property
    def count(self) -> int:
        return len(self._connections)

    async def broadcast(self, message: dict[str, Any]) -> None:
        """Send a JSON message to all connected clients in parallel."""
        if not self._connections:
            return
        text = json.dumps(message, default=str)

        results = await asyncio.gather(
            *[ws.send_text(text) for ws in list(self._connections)],
            return_exceptions=True,
        )
        dead = [ws for ws, result in zip(list(self._connections), results)
                if isinstance(result, Exception)]
        for ws in dead:
            self._connections.discard(ws)

    async def broadcast_sync(self, message: dict[str, Any]) -> None:
        """Thread-safe broadcast — schedules on the running event loop."""
        try:
            loop = asyncio.get_running_loop()
            asyncio.run_coroutine_threadsafe(self.broadcast(message), loop)
        except Exception as exc:
            logger.error("broadcast_sync error: %s", exc)

    @property
    def count(self) -> int:
        return len(self._connections)
