"""Native OS credential vault. Same service/account contract as the .NET bridge."""
import ctypes
import ctypes.wintypes as w
import subprocess
import sys

SERVICE = b"P2L.License"
ACCOUNT = b"device"
TARGET = "P2L.License/device"


def _mac():
    lib = ctypes.CDLL('/System/Library/Frameworks/Security.framework/Security')
    lib.SecKeychainFindGenericPassword.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_uint32, ctypes.c_char_p,
                                                  ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    lib.SecKeychainAddGenericPassword.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_uint32, ctypes.c_char_p,
                                                 ctypes.c_uint32, ctypes.c_void_p, ctypes.c_void_p]
    lib.SecKeychainItemModifyAttributesAndData.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p]
    lib.SecKeychainItemFreeContent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    return lib


class Credential(ctypes.Structure):
    _fields_ = [('Flags', w.DWORD), ('Type', w.DWORD), ('TargetName', w.LPWSTR), ('Comment', w.LPWSTR),
                ('LastWritten', w.FILETIME), ('CredentialBlobSize', w.DWORD), ('CredentialBlob', ctypes.c_void_p),
                ('Persist', w.DWORD), ('AttributeCount', w.DWORD), ('Attributes', ctypes.c_void_p),
                ('TargetAlias', w.LPWSTR), ('UserName', w.LPWSTR)]


def _windows():
    lib = ctypes.WinDLL('advapi32', use_last_error=True)
    lib.CredReadW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.POINTER(ctypes.POINTER(Credential))]
    lib.CredWriteW.argtypes = [ctypes.POINTER(Credential), w.DWORD]
    lib.CredFree.argtypes = [ctypes.c_void_p]
    lib.CredFree.restype = None
    return lib


def load() -> str | None:
    if sys.platform == 'win32':
        lib = _windows()
        pointer = ctypes.POINTER(Credential)()
        if not lib.CredReadW(TARGET, 1, 0, ctypes.byref(pointer)):
            if ctypes.get_last_error() == 1168:
                return None
            raise RuntimeError('Không đọc được Windows Credential Manager')
        try:
            return ctypes.string_at(pointer.contents.CredentialBlob, pointer.contents.CredentialBlobSize).decode('ascii')
        finally:
            lib.CredFree(pointer)
    if sys.platform == 'darwin':
        lib = _mac()
        size = ctypes.c_uint32()
        data = ctypes.c_void_p()
        result = lib.SecKeychainFindGenericPassword(None, len(SERVICE), SERVICE, len(ACCOUNT), ACCOUNT, ctypes.byref(size), ctypes.byref(data), None)
        if result == -25300:
            return None
        if result != 0:
            raise RuntimeError('Không đọc được macOS Keychain')
        try:
            return ctypes.string_at(data, size.value).decode('ascii')
        finally:
            lib.SecKeychainItemFreeContent(None, data)
    result = subprocess.run(['secret-tool', 'lookup', 'application', SERVICE.decode(), 'account', ACCOUNT.decode()], capture_output=True, timeout=15)
    if result.returncode == 1: return None
    if result.returncode != 0: raise RuntimeError('Không đọc được Secret Service')
    return result.stdout.decode('ascii').strip() or None


def save(value: str) -> None:
    blob = value.encode('ascii')
    if sys.platform == 'win32':
        buffer = ctypes.create_string_buffer(blob)
        credential = Credential(Type=1, TargetName=TARGET, CredentialBlobSize=len(blob),
                                CredentialBlob=ctypes.cast(buffer, ctypes.c_void_p), Persist=2, UserName='device')
        if not _windows().CredWriteW(ctypes.byref(credential), 0):
            raise RuntimeError('Không ghi được Windows Credential Manager')
        return
    if sys.platform == 'darwin':
        lib = _mac()
        item = ctypes.c_void_p()
        size = ctypes.c_uint32()
        data = ctypes.c_void_p()
        result = lib.SecKeychainFindGenericPassword(None, len(SERVICE), SERVICE, len(ACCOUNT), ACCOUNT, ctypes.byref(size), ctypes.byref(data), ctypes.byref(item))
        if result == -25300:
            result = lib.SecKeychainAddGenericPassword(None, len(SERVICE), SERVICE, len(ACCOUNT), ACCOUNT, len(blob), blob, None)
        elif result == 0:
            lib.SecKeychainItemFreeContent(None, data)
            try:
                result = lib.SecKeychainItemModifyAttributesAndData(item, None, len(blob), blob)
            finally:
                core = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
                core.CFRelease.argtypes = [ctypes.c_void_p]
                core.CFRelease.restype = None
                core.CFRelease(item)
        if result != 0: raise RuntimeError('Không ghi được macOS Keychain')
        return
    result = subprocess.run(['secret-tool', 'store', '--label=Pick2Light device', 'application', SERVICE.decode(), 'account', ACCOUNT.decode()],
                            input=blob, capture_output=True, timeout=15)
    if result.returncode: raise RuntimeError('Không ghi được Secret Service')
