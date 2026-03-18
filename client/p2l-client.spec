# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for P2L web client (single-file binary, all platforms).
#
# Build with (from repo root):
#   pyinstaller client/p2l-client.spec --distpath <out> --workpath .pyinstaller-work
#
# Output: <out>/p2l-client  (or p2l-client.exe on Windows)
#
# Note: strip=False on Windows — the MSYS2 strip tool on GitHub Actions runners
# corrupts Windows PE files, causing "Invalid access to memory location" at load time.

import sys

IS_WINDOWS = sys.platform == "win32"

block_cipher = None

a = Analysis(
    ["run.py"],
    pathex=[],
    binaries=[],
    datas=[
        # Bundle read-only web assets into the binary
        ("templates", "templates"),
        ("static",    "static"),
    ],
    hiddenimports=[
        # uvicorn internals — not auto-detected by PyInstaller
        "uvicorn.logging",
        "uvicorn.loops",
        "uvicorn.loops.auto",
        "uvicorn.loops.asyncio",
        "uvicorn.protocols",
        "uvicorn.protocols.http",
        "uvicorn.protocols.http.auto",
        "uvicorn.protocols.http.h11_impl",
        "uvicorn.protocols.websockets",
        "uvicorn.protocols.websockets.auto",
        "uvicorn.protocols.websockets.websockets_impl",
        "uvicorn.lifespan",
        "uvicorn.lifespan.on",
        "uvicorn.lifespan.off",
        # fastapi / pydantic
        "fastapi",
        "fastapi.staticfiles",
        "fastapi.templating",
        "pydantic",
        "pydantic.deprecated.decorator",
        # async dependencies
        "aiomqtt",
        "aiosqlite",
        "aiofiles",
        "aiofiles.os",
        "aiofiles.threadpool",
        # web/transport
        "websockets",
        "websockets.legacy",
        "websockets.legacy.server",
        "h11",
        "httptools",
        "watchfiles",
        # templating
        "jinja2",
        "jinja2.ext",
        # utils
        "psutil",
        "anyio",
        "anyio._backends._asyncio",
        "starlette.routing",
        "starlette.staticfiles",
        "starlette.templating",
        # Windows service integration (conditional import in run.py — must be explicit)
        "app.windows_service",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "tkinter",
        "unittest",
        "http.server",
        "xmlrpc",
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="p2l-client",
    debug=False,
    bootloader_ignore_signals=False,
    strip=not IS_WINDOWS,  # strip is Unix-only; corrupts PE files on Windows
    upx=False,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
