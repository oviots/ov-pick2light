#!/usr/bin/env python3
"""Entry point for the P2L Web Dashboard."""
import os
import sys

# ── Windows: force SelectorEventLoop so aiomqtt/paho-mqtt can use add_reader/add_writer.
# ProactorEventLoop (Windows default) does NOT support these socket primitives.
if sys.platform == "win32":
    import asyncio
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

if __name__ == "__main__":

    # ── Windows PyInstaller binary: try SCM dispatcher first ─────────────────
    # StartServiceCtrlDispatcher blocks when run by SCM and returns only after
    # the service stops.  When run interactively it returns immediately with
    # ERROR_FAILED_SERVICE_CONTROLLER_CONNECT → try_start_as_service() returns
    # False and we fall through to the normal uvicorn startup below.
    if sys.platform == "win32" and getattr(sys, "frozen", False):
        from app.windows_service import try_start_as_service
        if try_start_as_service():
            sys.exit(0)

    # ── Interactive / non-Windows / development mode ──────────────────────────
    import uvicorn

    host = os.environ.get("P2L_CLIENT_HOST", "127.0.0.1")
    port = int(os.environ.get("P2L_CLIENT_PORT", "8765"))

    # loop="none" prevents uvicorn from calling WindowsProactorEventLoopPolicy on Windows,
    # preserving the WindowsSelectorEventLoopPolicy we set above.
    if getattr(sys, "frozen", False):
        # PyInstaller binary: must pass the app object directly; string import won't work
        from app.main import app  # noqa: F401 — triggers all module-level path resolution
        uvicorn.run(app, host=host, port=port, log_level="info", loop="none")
    else:
        # Development: string import enables --reload
        uvicorn.run("app.main:app", host=host, port=port, reload=True, log_level="info", loop="none")
