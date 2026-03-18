# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for P2L web client.
#
# Build with (from repo root):
#   pyinstaller client/p2l-client.spec --distpath <out> --workpath .pyinstaller-work
#
# Output:
#   Linux/macOS  → <out>/p2l-client      (single file)
#   Windows      → <out>/p2l-client/     (onedir — avoids AV/DEP temp-extraction block)

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
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "tkinter",
        "unittest",
        "email",
        "http.server",
        "xmlrpc",
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

if IS_WINDOWS:
    # Windows: onedir mode — no extraction to %TEMP%, avoids AV/DEP blocking
    exe = EXE(
        pyz,
        a.scripts,
        [],
        name="p2l-client",
        debug=False,
        bootloader_ignore_signals=False,
        strip=False,       # strip is Unix-only; on Windows it corrupts PE files
        upx=False,
        console=True,
        disable_windowed_traceback=False,
        argv_emulation=False,
        target_arch=None,
        codesign_identity=None,
        entitlements_file=None,
    )
    coll = COLLECT(
        exe,
        a.binaries,
        a.zipfiles,
        a.datas,
        strip=False,
        upx=False,
        upx_exclude=[],
        name="p2l-client",
    )
else:
    # Linux / macOS: single-file binary for easy distribution
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
        strip=True,
        upx=False,
        console=True,
        disable_windowed_traceback=False,
        argv_emulation=False,
        target_arch=None,
        codesign_identity=None,
        entitlements_file=None,
    )
