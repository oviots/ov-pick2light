"""Activation and signed configuration requests shared by all local UI connect paths."""
import asyncio
import base64
import hashlib
import platform

from . import credential_store
from .api import LicenseError, post


def key(create=False):
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    saved = credential_store.load()
    if saved:
        return serialization.load_der_private_key(base64.b64decode(saved, validate=True), password=None)
    if not create: raise LicenseError('Máy chưa kích hoạt. Vui lòng nhập license')
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    # Save before activation: retry with the same key if the server response is lost.
    credential_store.save(base64.b64encode(private.private_bytes(serialization.Encoding.DER,
        serialization.PrivateFormat.PKCS8, serialization.NoEncryption())).decode())
    return private


def identity(private):
    from cryptography.hazmat.primitives import serialization
    public = private.public_key()
    der = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    pem = public.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    return hashlib.sha256(der).hexdigest(), pem


def sign(private, message):
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import padding
    return base64.b64encode(private.sign(message.encode(), padding.PKCS1v15(), hashes.SHA256())).decode()


def activate(code):
    code = code.strip().upper()
    private = key(create=True)
    device_id, pem = identity(private)
    code_hash = hashlib.sha256(code.encode()).hexdigest()
    challenge = post('challenge', {'deviceId': device_id, 'role': 'activation'}).get('challenge')
    if not isinstance(challenge, str) or len(challenge) != 64 or any(c not in '0123456789abcdef' for c in challenge):
        raise LicenseError('Challenge kích hoạt không hợp lệ')
    message = f'p2l.activate.v2\n{code_hash}\n{device_id}\n{challenge}'
    response = post('activate', {'licenseCode': code, 'publicKey': pem, 'deviceName': platform.node() or 'P2L',
                                 'challenge': challenge, 'signature': sign(private, message)})
    if response.get('deviceId') != device_id: raise LicenseError('Phản hồi thiết bị không hợp lệ')
    return {'device_id': device_id}


def fetch():
    private = key()
    device_id, _ = identity(private)
    challenge = post('challenge', {'deviceId': device_id, 'role': 'client'}).get('challenge')
    if not isinstance(challenge, str) or len(challenge) != 64: raise LicenseError('Challenge không hợp lệ')
    result = post('config', {'deviceId': device_id, 'role': 'client', 'challenge': challenge,
                           'signature': sign(private, f'p2l.config.v1\n{device_id}\nclient\n{challenge}')})
    validate(result)
    return result


def validate(result):
    mqtt = result.get('mqtt', {})
    required = ('host', 'clientId', 'username', 'password', 'commandTopic', 'eventTopic', 'devicesTopic', 'statusTopic')
    read_ca(mqtt.get('tls'))
    if any(not isinstance(mqtt.get(k), str) or not mqtt[k] for k in required):
        raise LicenseError('Config MQTT không hợp lệ')
    if not isinstance(mqtt.get('port'), int) or not 1 <= mqtt['port'] <= 65535:
        raise LicenseError('Port MQTT không hợp lệ')
    block = result.get('gateway', {}).get('blockNo')
    if not isinstance(block, str) or len(block) != 2 or not block.isascii() or not block.isdigit():
        raise LicenseError('Block không hợp lệ')
    for field in ('commandTopic', 'eventTopic', 'devicesTopic', 'statusTopic'):
        if any(c in mqtt[field] for c in ('+', '#', '\0')): raise LicenseError('Topic không hợp lệ')


def read_ca(tls):
    if tls is True:
        return None
    if not isinstance(tls, dict) or tls.get('rejectUnauthorized', True) is not True or tls.get('enabled', True) is not True:
        raise LicenseError('MQTT yêu cầu xác minh TLS')
    ca = tls.get('ca')
    if ca is None:
        return None
    if not isinstance(ca, str) or len(ca.encode('utf-8')) > 32768:
        raise LicenseError('CA Certificate không hợp lệ')
    return ca if ca.strip() else None


_lock = asyncio.Lock()


async def activate_async(code):
    async with _lock:
        return await asyncio.to_thread(activate, code)


async def load_config(config, expected_fingerprint=None):
    from ..config import MqttConfig, TlsConfig
    async with _lock:
        result = await asyncio.to_thread(fetch)
        import json
        fingerprint = hashlib.sha256(json.dumps(result, sort_keys=True).encode()).hexdigest()
        if expected_fingerprint is not None and fingerprint != expected_fingerprint:
            raise LicenseError('Cấu hình đã thay đổi. Ngắt và kết nối lại vào thời điểm an toàn')
        mqtt = result['mqtt']
        config.mqtt = MqttConfig(enabled=True, host=mqtt['host'], port=mqtt['port'], client_id=mqtt['clientId'],
            username=mqtt['username'], password=mqtt['password'], command_topic=mqtt['commandTopic'],
            event_topic=mqtt['eventTopic'], devices_topic=mqtt['devicesTopic'], status_topic=mqtt['statusTopic'],
            tls=TlsConfig(ca_cert_pem=read_ca(mqtt.get('tls'))))
        config.block_no = result['gateway']['blockNo']
    return {'version': result.get('version'), 'site_code': result.get('siteCode'), 'fingerprint': fingerprint}
