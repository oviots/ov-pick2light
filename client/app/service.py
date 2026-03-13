"""P2L .NET service lifecycle manager.

Checks whether the P2L process is running and starts it on demand.
"""
import asyncio
import logging
import os
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

# Tracked subprocess spawned by this process
_proc: Optional[asyncio.subprocess.Process] = None


def _find_pid(binary_path: str) -> Optional[int]:
    """Return the PID of a running p2l process, or None."""
    try:
        import psutil
        target = Path(binary_path).resolve()
        for proc in psutil.process_iter(["pid", "exe", "name"]):
            try:
                exe = proc.info.get("exe") or ""
                name = proc.info.get("name") or ""
                if str(target) == exe or name in ("p2l", "p2l.exe"):
                    return proc.pid
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
    except ImportError:
        # Fall back to tracking our own subprocess
        pass
    return None


def is_running(binary_path: str) -> bool:
    """Return True if a p2l process is currently running."""
    global _proc
    # Check the subprocess we spawned
    if _proc is not None and _proc.returncode is None:
        return True
    pid = _find_pid(binary_path)
    return pid is not None


async def start(binary_path: str, args: list[str], working_dir: str) -> None:
    """Start the P2L service in the background.

    Raises RuntimeError if the binary is not found or the process fails to launch.
    """
    global _proc

    if is_running(binary_path):
        logger.info("P2L service already running — skipping start")
        return

    binary = Path(binary_path)
    if not binary.exists():
        raise RuntimeError(f"P2L binary not found: {binary_path}")

    wdir = working_dir or str(binary.parent)
    cmd = [str(binary)] + args
    logger.info("Starting P2L service: %s (cwd=%s)", " ".join(cmd), wdir)

    log_path = Path(wdir) / "p2l_webclient.log"
    log_file = open(log_path, "a")  # noqa: WPS515 — intentional long-lived handle

    _proc = await asyncio.create_subprocess_exec(
        *cmd,
        cwd=wdir,
        stdout=log_file,
        stderr=log_file,
        stdin=asyncio.subprocess.DEVNULL,
    )
    logger.info("P2L service started (pid=%d)", _proc.pid)

    # Give it a moment to start; raise if it exits immediately
    try:
        await asyncio.wait_for(_proc.wait(), timeout=1.5)
        code = _proc.returncode
        _proc = None
        raise RuntimeError(f"P2L service exited immediately (code {code})")
    except asyncio.TimeoutError:
        pass  # Still running — good


async def stop() -> None:
    """Terminate the service subprocess started by this manager (if any)."""
    global _proc
    if _proc is None or _proc.returncode is not None:
        _proc = None
        return
    logger.info("Stopping P2L service (pid=%d)", _proc.pid)
    _proc.terminate()
    try:
        await asyncio.wait_for(_proc.wait(), timeout=5)
    except asyncio.TimeoutError:
        _proc.kill()
    _proc = None
