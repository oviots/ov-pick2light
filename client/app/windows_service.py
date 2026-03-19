"""Windows Service Control Manager (SCM) integration for P2L Web Dashboard.

Uses ctypes to call Win32 service APIs directly — no pywin32 dependency required.
Implements the SERVICE_MAIN_FUNCTIONW + control handler pattern so SCM correctly
transitions the service to SERVICE_RUNNING before uvicorn starts serving.

Usage (called from run.py):
    if try_start_as_service():
        sys.exit(0)   # was a service; SCM loop finished

Design:
  StartServiceCtrlDispatcher blocks until the service stops.
  If called outside SCM (interactive terminal), it returns immediately with
  ERROR_FAILED_SERVICE_CONTROLLER_CONNECT → try_start_as_service() returns False
  and the caller falls through to normal interactive mode.
"""
import ctypes
import ctypes.wintypes as wintypes
import os
import threading

_k32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
_adv = ctypes.windll.advapi32  # type: ignore[attr-defined]

# ── Win32 constants ───────────────────────────────────────────────────────────
SERVICE_WIN32_OWN_PROCESS = 0x00000010
SERVICE_RUNNING           = 4
SERVICE_STOPPED           = 1
SERVICE_STOP_PENDING      = 3
SERVICE_ACCEPT_STOP       = 0x00000001
NO_ERROR                  = 0
ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063

# ── Win32 structures ──────────────────────────────────────────────────────────
class SERVICE_STATUS(ctypes.Structure):
    _fields_ = [
        ("dwServiceType",             wintypes.DWORD),
        ("dwCurrentState",            wintypes.DWORD),
        ("dwControlsAccepted",        wintypes.DWORD),
        ("dwWin32ExitCode",           wintypes.DWORD),
        ("dwServiceSpecificExitCode", wintypes.DWORD),
        ("dwCheckPoint",              wintypes.DWORD),
        ("dwWaitHint",                wintypes.DWORD),
    ]

LPHANDLER_FUNCTION       = ctypes.WINFUNCTYPE(None, wintypes.DWORD)  # type: ignore[attr-defined]
LPSERVICE_MAIN_FUNCTIONW = ctypes.WINFUNCTYPE(                        # type: ignore[attr-defined]
    None, wintypes.DWORD, ctypes.POINTER(wintypes.LPWSTR)
)

class _SERVICE_TABLE_ENTRYW(ctypes.Structure):
    _fields_ = [
        ("lpServiceName", wintypes.LPWSTR),
        ("lpServiceProc", LPSERVICE_MAIN_FUNCTIONW),
    ]

# ── Module-level state (must outlive StartServiceCtrlDispatcher) ──────────────
_stop_event    = threading.Event()
_status_handle = wintypes.HANDLE(0)
_server_ref    = None   # uvicorn.Server — held so _stop can call .should_exit


def _set_status(state: int, controls_accepted: int = 0, wait_hint: int = 0) -> None:
    _adv.SetServiceStatus(
        _status_handle,
        ctypes.byref(SERVICE_STATUS(
            dwServiceType             = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState            = state,
            dwControlsAccepted        = controls_accepted,
            dwWin32ExitCode           = NO_ERROR,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint              = 0,
            dwWaitHint                = wait_hint,
        )),
    )


def _ctrl_handler(ctrl_code: int) -> None:
    """Win32 service control handler — called by SCM on SERVICE_CONTROL_STOP."""
    if ctrl_code == 1:  # SERVICE_CONTROL_STOP
        _set_status(SERVICE_STOP_PENDING, wait_hint=15_000)
        _stop_event.set()


def _service_main(argc: int, argv) -> None:  # noqa: ANN001
    """ServiceMain — SCM calls this (in a new thread) after StartServiceCtrlDispatcher."""
    global _status_handle, _server_ref

    svc_name = ctypes.cast(argv[0], ctypes.c_wchar_p).value or "P2LClient"

    # Keep ctrl_fn alive for the life of this function (Windows holds a raw ptr).
    ctrl_fn = LPHANDLER_FUNCTION(_ctrl_handler)  # noqa: F841 — must NOT be GC'd
    _status_handle.value = _adv.RegisterServiceCtrlHandlerW(svc_name, ctrl_fn)
    if not _status_handle.value:
        return

    _set_status(SERVICE_RUNNING, controls_accepted=SERVICE_ACCEPT_STOP)

    # Import deferred to here so module-level import is fast (no uvicorn startup).
    import asyncio
    import uvicorn
    from app.main import app as fastapi_app  # noqa: PLC0415

    # Force SelectorEventLoop — ProactorEventLoop (Windows default) does not
    # support add_reader/add_writer which aiomqtt/paho-mqtt requires.
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

    host = os.environ.get("P2L_CLIENT_HOST", "0.0.0.0")
    port = int(os.environ.get("P2L_CLIENT_PORT", "8765"))

    _server_ref = uvicorn.Server(
        uvicorn.Config(fastapi_app, host=host, port=port, log_level="info")
    )
    server_thread = threading.Thread(target=_server_ref.run, daemon=True)
    server_thread.start()

    # Block until SCM sends STOP.
    _stop_event.wait()
    _server_ref.should_exit = True
    server_thread.join(timeout=15)

    _set_status(SERVICE_STOPPED)


# Module-level reference so ctypes callback is never garbage-collected.
_svc_main_fn = LPSERVICE_MAIN_FUNCTIONW(_service_main)


def try_start_as_service() -> bool:
    """
    Attempt to hand control to SCM via StartServiceCtrlDispatcher.

    Returns
    -------
    True  — process was launched by SCM; dispatcher loop ran and finished.
            Caller should ``sys.exit(0)``.
    False — not a service (ERROR_FAILED_SERVICE_CONTROLLER_CONNECT) or
            unexpected failure; caller should start uvicorn interactively.
    """
    table = (_SERVICE_TABLE_ENTRYW * 2)()
    table[0].lpServiceName = "P2LClient"
    table[0].lpServiceProc = _svc_main_fn
    table[1].lpServiceName = None                                      # NULL terminator
    table[1].lpServiceProc = ctypes.cast(None, LPSERVICE_MAIN_FUNCTIONW)

    ok = _adv.StartServiceCtrlDispatcherW(table)
    if ok:
        return True

    err = _k32.GetLastError()
    if err == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT:
        return False  # interactive mode — caller runs uvicorn normally

    import sys
    print(f"[P2L] StartServiceCtrlDispatcher failed with error {err}", file=sys.stderr)
    return False
