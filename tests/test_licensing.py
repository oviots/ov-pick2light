"""Isolated license tests: fake vault/API, real RSA signatures; no broker/hardware."""
import asyncio
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'client'))
from app.config import AppConfig, MqttConfig
from app.licensing import provider
from app.licensing.api import LicenseError, api_url
from app.licensing.local_access import LocalAccessMiddleware
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives import hashes, serialization


class LicensingTests(unittest.TestCase):
    def setUp(self):
        self.vault = {}
        self.stack = __import__('contextlib').ExitStack()
        self.stack.enter_context(patch.object(provider.credential_store, 'load', side_effect=lambda: self.vault.get('key')))
        self.stack.enter_context(patch.object(provider.credential_store, 'save', side_effect=lambda v: self.vault.update(key=v)))
        self.addCleanup(self.stack.close)

    def test_activation_retry_keeps_key_and_does_not_store_license(self):
        code = 'P2L-' + 'AB' * 24
        calls = []
        def post(path, body):
            if path == 'challenge':
                return {'challenge': 'ab' * 32}
            calls.append(body)
            public = serialization.load_pem_public_key(body['publicKey'].encode())
            public.verify(base64.b64decode(body['signature']), f"p2l.activate.v2\n{hashlib.sha256(code.encode()).hexdigest()}\n{provider.identity(provider.key())[0]}\n{'ab' * 32}".encode(), padding.PKCS1v15(), hashes.SHA256())
            if len(calls) == 1: raise LicenseError('lost response')
            return {'deviceId': provider.identity(provider.key())[0]}
        with patch.object(provider, 'post', side_effect=post):
            with self.assertRaises(LicenseError): provider.activate(code)
            saved = self.vault['key']
            provider.activate(code)
        self.assertEqual(saved, self.vault['key'])
        self.assertEqual(calls[0]['publicKey'], calls[1]['publicKey'])
        self.assertNotIn(code, json.dumps(self.vault))

    def test_fetch_refuses_unactivated_device_without_network(self):
        with patch.object(provider, 'post') as post:
            with self.assertRaises(LicenseError): provider.fetch()
            post.assert_not_called()

    def test_fetch_signs_role_and_challenge_and_loads_only_memory(self):
        private = provider.key(create=True)
        device_id = provider.identity(private)[0]
        config = {'version': 1, 'siteCode': 'TEST', 'gateway': {'blockNo': '01'}, 'mqtt': {
            'tls': True, 'host': 'broker.example', 'port': 8883, 'clientId': 'fixture', 'username': 'user', 'password': 'secret',
            'commandTopic': 'test/commands', 'eventTopic': 'test/events', 'devicesTopic': 'test/devices', 'statusTopic': 'test/status'}}
        def post(path, body):
            if path == 'challenge': return {'challenge': 'ab' * 32}
            private.public_key().verify(base64.b64decode(body['signature']),
                f"p2l.config.v1\n{device_id}\nclient\n{'ab'*32}".encode(), padding.PKCS1v15(), hashes.SHA256())
            return config
        app = AppConfig()
        for tls, expected_ca in ((True, None), ({'ca': 'fixture PEM', 'rejectUnauthorized': True}, 'fixture PEM')):
            config['mqtt']['tls'] = tls
            with patch.object(provider, 'post', side_effect=post):
                asyncio.run(provider.load_config(app))
            self.assertEqual(app.mqtt.tls.ca_cert_pem, expected_ca)
            self.assertEqual(app.mqtt.username, 'user')
            self.assertEqual(app.mqtt.password, 'secret')
        self.assertEqual(app.mqtt.password, 'secret')
        self.assertEqual(app.block_no, '01')
        with tempfile.TemporaryDirectory() as directory, patch('app.config.CONFIG_PATH', Path(directory) / 'local.json'):
            app.save()
            saved = (Path(directory) / 'local.json').read_text()
            self.assertNotIn('secret', saved)
            self.assertNotIn('mqtt', saved)
            self.assertIsNone(AppConfig.load().mqtt.password)

    def test_optional_ca_and_verification_policy(self):
        for tls in (True, {}, {'ca': ''}, {'ca': None}):
            self.assertIsNone(provider.read_ca(tls))
        self.assertEqual(provider.read_ca({'ca': 'fixture PEM', 'rejectUnauthorized': True}), 'fixture PEM')
        for tls in (False, None, {'rejectUnauthorized': False}, {'enabled': False}, {'ca': 42}):
            with self.assertRaises(LicenseError):
                provider.read_ca(tls)

    def test_no_http_or_embedded_credentials_or_redirect_target_in_bootstrap(self):
        for address in ('http://example.com', 'https://user:pass@example.com', 'https://example.com?token=x', ''):
            with patch.dict(os.environ, {'P2L_LICENSE_API_URL': address}):
                with self.assertRaises(LicenseError): api_url()

    def test_changed_reconnect_config_is_rejected_before_mutating_live_settings(self):
        config = AppConfig(mqtt=MqttConfig(host='old-host', password='old-password'))
        changed = {'version': 2, 'gateway': {'blockNo': '01'}, 'mqtt': {
            'tls': True, 'host': 'new-host', 'port': 8883, 'clientId': 'fixture', 'username': 'user', 'password': 'new-password',
            'commandTopic': 'a', 'eventTopic': 'b', 'devicesTopic': 'c', 'statusTopic': 'd'}}
        with patch.object(provider, 'fetch', return_value=changed):
            with self.assertRaises(LicenseError): asyncio.run(provider.load_config(config, 'old-fingerprint'))
        self.assertEqual(config.mqtt.host, 'old-host')
        self.assertEqual(config.mqtt.password, 'old-password')

    def test_local_http_endpoints_do_not_accept_operational_overrides_or_expose_passwords(self):
        from fastapi import FastAPI
        from app.api.routes import router
        app = FastAPI()
        app.include_router(router)
        app.state.config = AppConfig(mqtt=MqttConfig(password='must-not-leak'))
        async def call(method, path, body=None):
            sent = []
            async def receive(): return {'type': 'http.request', 'body': json.dumps(body or {}).encode(), 'more_body': False}
            async def send(message): sent.append(message)
            await app({'type': 'http', 'http_version': '1.1', 'method': method, 'path': path,
                'raw_path': path.encode(), 'query_string': b'', 'root_path': '', 'scheme': 'http',
                'headers': [(b'host', b'127.0.0.1:8765'), (b'content-type', b'application/json')],
                'server': ('127.0.0.1', 8765), 'client': ('127.0.0.1', 12345)}, receive, send)
            return sent[0]['status'], b''.join(m.get('body', b'') for m in sent[1:])
        status, body = asyncio.run(call('GET', '/api/config'))
        self.assertEqual(status, 200)
        self.assertNotIn(b'must-not-leak', body)
        self.assertEqual(asyncio.run(call('PUT', '/api/config', {'mqtt_password': 'override'}))[0], 403)
        self.assertEqual(asyncio.run(call('GET', '/api/appsettings'))[0], 403)
        self.assertEqual(asyncio.run(call('PUT', '/api/appsettings', {'mqtt_password': 'override'}))[0], 403)

    def test_node_and_python_use_identical_signature_and_device_identity(self):
        private = provider.key(create=True)
        device_id, pem = provider.identity(private)
        message = 'p2l.config.v1\n' + device_id + '\nclient\n' + 'ab' * 32
        js = "const c=require('node:crypto');let s='';process.stdin.on('data',b=>s+=b);process.stdin.on('end',()=>{let x=JSON.parse(s);let k=c.createPublicKey(x.pem);process.stdout.write(JSON.stringify({valid:c.verify('sha256',Buffer.from(x.message),k,Buffer.from(x.signature,'base64')),id:c.createHash('sha256').update(k.export({type:'spki',format:'der'})).digest('hex')}));});"
        result = subprocess.run(['node', '-e', js], input=json.dumps({'pem': pem, 'message': message, 'signature': provider.sign(private, message)}), text=True, capture_output=True, check=True)
        self.assertEqual(json.loads(result.stdout), {'valid': True, 'id': device_id})

    def test_local_access_blocks_foreign_origin_and_rebinding_for_http_and_websocket(self):
        async def check(kind, host, origin):
            sent = []
            async def app(scope, receive, send): await send({'type': 'allowed'})
            async def send(message): sent.append(message)
            await LocalAccessMiddleware(app)({'type': kind, 'scheme': 'ws' if kind == 'websocket' else 'http',
                'headers': [(b'host', host.encode()), (b'origin', origin.encode())]}, None, send)
            return sent
        self.assertEqual(asyncio.run(check('http', '127.0.0.1:8765', 'http://127.0.0.1:8765'))[0]['type'], 'allowed')
        self.assertEqual(asyncio.run(check('http', 'attacker.example:8765', 'http://attacker.example:8765'))[0]['status'], 403)
        self.assertEqual(asyncio.run(check('http', '127.0.0.1:8765', 'http://attacker.example'))[0]['status'], 403)
        self.assertEqual(asyncio.run(check('websocket', '127.0.0.1:8765', 'http://attacker.example'))[0]['code'], 1008)


if __name__ == '__main__': unittest.main()
