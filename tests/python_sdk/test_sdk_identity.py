"""SDK tests use a fake Paho surface: no socket, broker or device connections."""
import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import types
import unittest

mqtt = types.ModuleType('paho.mqtt.client')
class FakeMqtt:
    def __init__(self, **kwargs): self.sent = []
    def publish(self, topic, payload, qos=0):
        self.sent.append((topic, payload, qos))
        return types.SimpleNamespace(rc=0)
    def username_pw_set(self, *args): pass
    def tls_set_context(self, *args): pass
mqtt.Client = FakeMqtt
mqtt.MQTTv5 = 5
mqtt.CallbackAPIVersion = types.SimpleNamespace(VERSION2=2)
mqtt.MQTT_ERR_SUCCESS = 0
prior_modules = {name: sys.modules.get(name) for name in ('paho', 'paho.mqtt', 'paho.mqtt.client')}
sys.modules['paho'] = types.ModuleType('paho')
sys.modules['paho.mqtt'] = types.ModuleType('paho.mqtt')
sys.modules['paho.mqtt.client'] = mqtt
source = Path(__file__).resolve().parents[2] / 'tools/sdk/python/p2l.py'
spec = importlib.util.spec_from_file_location('p2l_sdk_tested', source)
sdk = importlib.util.module_from_spec(spec); sys.modules[spec.name] = sdk; spec.loader.exec_module(sdk)
for name, prior in prior_modules.items():
    if prior is None: sys.modules.pop(name, None)
    else: sys.modules[name] = prior

class SdkIdentityTests(unittest.IsolatedAsyncioTestCase):
    def client(self): return sdk.P2LClient('unused.invalid', port=1883)
    def event(self, client, value, retained=False): client._dispatch(client.EVENT_TOPIC, json.dumps(value).encode(), retained)
    def published(self, client): return [json.loads(p[1]) for p in client._mqtt.sent]
    def apply(self, client, command):
        self.event(client, dict(type='command_result', commandId=command['commandId'], correlationId=command.get('correlationId'), status='applied'))
    def confirm(self, command, event_id, item=None):
        return dict(type='confirmed', eventId=event_id, address='0001', status='Normal', correlationId=command['correlationId'],
                    commandItemId=(item or command)['commandItemId'])

    async def test_puback_is_not_execution_and_exact_result_resolves_awaitable_handle(self):
        client = self.client()
        handle = client.light_on('0001', correlation_id='session', command_item_id='item', picking_detail_id='detail')
        command = self.published(client)[0]
        self.assertTrue(command['commandId']); self.assertTrue(command['expiresAt'].endswith('Z'))
        waiter = asyncio.create_task(handle.wait(.5)); await asyncio.sleep(0)
        self.assertFalse(waiter.done())
        self.event(client, dict(type='command_result', commandId='other', correlationId='session', status='applied'))
        self.event(client, dict(type='command_result', commandId=handle.command_id, correlationId='wrong', status='applied'))
        self.assertFalse(waiter.done())
        self.apply(client, command)
        result = await waiter
        self.assertEqual('applied', result.status)
        self.assertEqual('detail', command['pickingDetailId'])

    async def test_retry_preserves_exact_bytes_id_and_expiry_after_unknown_timeout(self):
        client = self.client(); handle = client.light_on('0001')
        original = client._mqtt.sent[0][1]
        with self.assertRaises(TimeoutError): await handle.wait(.001)
        self.assertIs(handle, handle.retry())
        self.assertEqual(original, client._mqtt.sent[1][1])
        self.apply(client, self.published(client)[0]); self.assertEqual('applied', (await handle).status)

    async def test_rejected_and_uncertain_results_are_not_success_or_unobserved_future_errors(self):
        client = self.client(); errors = []
        asyncio.get_running_loop().set_exception_handler(lambda loop, context: errors.append(context))
        for status in ('rejected', 'uncertain'):
            handle = client.light_on('0001'); command = self.published(client)[-1]
            self.event(client, dict(type='command_result', commandId=handle.command_id, status=status, errorCode='TEST_ERROR'))
            with self.assertRaises(sdk.CommandError) as caught: await handle
            self.assertEqual(status, caught.exception.result.status)
        # Existing sync callers may ignore the return value, including a rejected result.
        client.light_off('0001'); command = self.published(client)[-1]
        self.event(client, dict(type='command_result', commandId=command['commandId'], status='rejected'))
        await asyncio.sleep(0); self.assertEqual([], errors)

    async def test_retry_resolves_same_cached_command_result_event_id(self):
        client = self.client(); handle = client.light_on('0001')
        result = dict(type='command_result', eventId='cached-result', commandId=handle.command_id, status='applied')
        self.event(client, result); await handle
        handle.retry(); self.event(client, result)
        self.assertEqual('applied', (await handle.wait(.1)).status)

    async def test_wave_old_duplicate_confirm_cannot_consume_next_item_at_same_address(self):
        client = self.client(); wave = sdk.WaveDispatcher(client, [sdk.DispatchItem('A', 1), sdk.DispatchItem('B', 2)], positions=['0001'])
        await wave.start(); first = self.published(client)[0]; self.apply(client, first)
        raw = self.confirm(first, 'event-a'); self.event(client, raw)
        second = self.published(client)[1]; self.apply(client, second)
        self.event(client, raw)
        self.event(client, {**raw, 'eventId':'different-event-same-old-item'})
        self.assertEqual(1, wave._confirmed)
        self.assertEqual('B', wave._active['0001'].sku)
        self.event(client, self.confirm(second, 'event-b'))
        self.assertEqual(2, (await wave.wait(.1)).confirmed)

    async def test_batch_dispatcher_uses_exact_item_identity_for_reused_position(self):
        client = self.client(); client._states['0001'] = sdk.ModuleState('0001', 'Idle', label='POS')
        batch = sdk.BatchDispatcher(client, sdk.BatchDispatch('batch', [sdk.BatchDispatchItem('A','POS',1), sdk.BatchDispatchItem('B','POS',2)]))
        await batch.start(); first = self.published(client)[0]; self.apply(client, first)
        raw = self.confirm(first, 'batch-event-a', first['items'][0]); self.event(client, raw)
        second = self.published(client)[1]; self.apply(client, second)
        self.event(client, raw); self.event(client, {**raw, 'eventId':'old-again'})
        self.assertEqual(1, batch._confirmed)
        self.event(client, self.confirm(second, 'batch-event-b'))
        self.assertEqual(2, (await batch.wait(.1))['confirmed'])

    async def test_retained_and_wrong_item_cannot_resolve_exact_confirm_waiter(self):
        client = self.client(); waiter = asyncio.create_task(client.wait_confirm('0001', .5, correlation_id='s', command_item_id='new'))
        await asyncio.sleep(0)
        event = dict(type='confirmed', address='0001', status='Normal', correlationId='s', commandItemId='new', eventId='retained')
        self.event(client, event, True)
        self.event(client, {**event, 'eventId':'wrong', 'commandItemId':'old'})
        self.assertFalse(waiter.done())
        self.event(client, {**event, 'eventId':'right'})
        self.assertEqual('new', (await waiter).command_item_id)

    async def test_wave_execution_rejection_surfaces_from_wait_without_advancing(self):
        client = self.client(); wave = sdk.WaveDispatcher(client, [sdk.DispatchItem('A', 1)], positions=['0001'])
        await wave.start(); command = self.published(client)[0]
        self.event(client, dict(type='command_result',commandId=command['commandId'],correlationId=command['correlationId'],status='rejected',errorCode='BRIDGE_BUSY'))
        with self.assertRaises(sdk.CommandError): await wave.wait(.5)
        self.assertEqual(0, wave._confirmed)

if __name__ == '__main__': unittest.main()
