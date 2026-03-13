"""WebSocket connection manager for broadcasting real-time events to the browser."""
import asyncio
import json
import logging
from typing import Any

from fastapi import WebSocket

logger = logging.getLogger(__name__)


class WebSocketManager:
    """Manages all active WebSocket connections and broadcasts messages."""

    def __init__(self) -> None:
        self._connections: set[WebSocket] = set()

    async def connect(self, ws: WebSocket) -> None:
        await ws.accept()
        self._connections.add(ws)
        logger.debug("WebSocket connected; total=%d", len(self._connections))

    def disconnect(self, ws: WebSocket) -> None:
        self._connections.discard(ws)
        logger.debug("WebSocket disconnected; total=%d", len(self._connections))

    async def broadcast(self, message: dict[str, Any]) -> None:
        """Send a JSON message to all connected clients."""
        if not self._connections:
            return
        text = json.dumps(message, default=str)
        dead: list[WebSocket] = []
        for ws in list(self._connections):
            try:
                await ws.send_text(text)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self._connections.discard(ws)

    async def broadcast_sync(self, message: dict[str, Any]) -> None:
        """Thread-safe broadcast — schedules on the current event loop."""
        try:
            loop = asyncio.get_event_loop()
            asyncio.run_coroutine_threadsafe(self.broadcast(message), loop)
        except Exception as exc:
            logger.error("broadcast_sync error: %s", exc)

    @property
    def count(self) -> int:
        return len(self._connections)
