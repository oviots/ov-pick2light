# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for P2L web client (single-file binary).
#
# Build with:
#   cd client/
#   pyinstaller p2l-client.spec
#
# Output: dist/p2l-client  (or dist/p2l-client.exe on Windows)

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
