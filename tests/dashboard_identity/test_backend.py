"""Inert module/AST tests: no FastAPI app bootstrap, SQLite, MQTT or HTTP socket."""
import ast
import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import types
import unittest
from datetime import datetime, timedelta, timezone
from typing import Optional
ROOT=Path(__file__).resolve().parents[2]
for name in ('app','app.protocol','app.core','app.mqtt','app.api'):
    package=types.ModuleType(name);package.__path__=[];sys.modules[name]=package

def module(name,path):
    spec=importlib.util.spec_from_file_location(name,ROOT/path);mod=importlib.util.module_from_spec(spec);sys.modules[name]=mod;spec.loader.exec_module(mod);return mod
E=module('app.protocol.enums','client/app/protocol/enums.py')
M=module('app.protocol.models','client/app/protocol/models.py')
R=module('app.core.registry','client/app/core/registry.py')
C=module('app.mqtt.client','client/app/mqtt/client.py')
config=types.ModuleType('app.config');config.MqttConfig=object;sys.modules['app.config']=config

def batch_status(registry):
    tree=ast.parse((ROOT/'client/app/api/routes.py').read_text());fn=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='batch_status');fn.decorator_list=[]
    code=ast.Module(body=[fn],type_ignores=[]);ast.fix_missing_locations(code)
    ns={'__name__':'app.api.routes','__package__':'app.api','Request':object,'Optional':Optional,'_registry':lambda _:registry}
    exec(compile(code,'routes.py','exec'),ns);return ns['batch_status']
class FakeMessages:
    def __init__(self,values):self.values=values
    async def __aiter__(self):
        for value in self.values:yield types.SimpleNamespace(topic='events',payload=json.dumps(value[0]).encode(),retain=value[1])

class DashboardTests(unittest.IsolatedAsyncioTestCase):
    def client(self):
        return C.MqttClient(types.SimpleNamespace(event_topic='events',command_topic='commands'))
    async def test_mqtt_dedup_and_retained_filter_without_stealing_wms_ack(self):
        c=self.client();events=[]
        async def receive(topic,payload):events.append(payload)
        c.set_event_callback(receive)
        first=dict(type='confirmed',address='0001',eventId='e',correlationId='s',commandItemId='i',pickingDetailId='d')
        values=[(first,True),(first,False),(first,False),({**first,'eventId':'another'},False),({**first,'commandItemId':'next','eventId':'next'},False)]
        await c._recv_loop(types.SimpleNamespace(messages=FakeMessages(values)))
        self.assertEqual(['i','next'],[e['commandItemId'] for e in events]);self.assertTrue(c._publish_queue.empty())
    async def test_publish_preserves_exact_command_id_expiry_and_application_outcome(self):
        c=self.client();c._is_connected=True
        command=dict(type='batch_start',commandId='c',expiresAt='2030-01-01T00:00:00Z',correlationId='s',items=[{'address':'0001','quantity':1,'commandItemId':'i'}])
        waiter=asyncio.create_task(c.publish_command(command));await asyncio.sleep(0)
        queued=c._publish_queue.get_nowait();self.assertEqual(command,json.loads(queued['payload']));self.assertFalse(waiter.done())
        await c._recv_loop(types.SimpleNamespace(messages=FakeMessages([({'type':'command_result','commandId':'c','status':'applied'},False)])))
        await waiter
    async def test_rejected_result_preserves_outcome_for_http_and_does_not_report_success(self):
        c=self.client();c._is_connected=True
        waiter=asyncio.create_task(c.publish_command(dict(type='light_on',commandId='c',expiresAt='2030-01-01T00:00:00Z')));await asyncio.sleep(0)
        await c._recv_loop(types.SimpleNamespace(messages=FakeMessages([({'type':'command_result','commandId':'c','status':'rejected','errorCode':'ADDRESS_BUSY'},False)])))
        with self.assertRaises(C.CommandOutcomeError) as error:await waiter
        self.assertEqual('ADDRESS_BUSY',error.exception.outcome['errorCode'])
    async def test_status_pending_means_unconfirmed_and_is_scoped_to_correlation(self):
        reg=R.LightModuleRegistry();now=datetime.now(timezone.utc)
        reg.update_module_state(M.LightModuleState('0001',status=E.ModuleStatus.COMPLETED,correlation_id='s',command_item_id='a'))
        reg.update_module_state(M.LightModuleState('0002',status=E.ModuleStatus.OPERATING,correlation_id='s',command_item_id='b'))
        reg.update_module_state(M.LightModuleState('0003',status=E.ModuleStatus.OPERATING,correlation_id='other'))
        result=await batch_status(reg)(None,'s');self.assertEqual(['0002'],result['pending']);self.assertEqual('a',result['confirmed'][0]['command_item_id']);self.assertFalse(result['complete'])
        result=await batch_status(reg)(None,'missing');self.assertFalse(result['known']);self.assertFalse(result['complete'])
    async def test_registry_rejects_event_older_than_instruction_but_accepts_confirm_before_telemetry(self):
        reg=R.LightModuleRegistry();now=datetime.now(timezone.utc)
        reg.update_module_state(M.LightModuleState('0001',status=E.ModuleStatus.OPERATING,last_instruction_at=now,correlation_id='old-telemetry'))
        old=M.ConfirmationEvent('0001',E.CompletionStatus.NORMAL,None,now-timedelta(seconds=1),1,correlation_id='old',command_item_id='old')
        self.assertFalse(reg.set_confirmed(old));self.assertEqual(E.ModuleStatus.OPERATING,reg.get('0001').status)
        fresh=M.ConfirmationEvent('0001',E.CompletionStatus.NORMAL,None,now+timedelta(seconds=1),2,correlation_id='new',command_item_id='new',event_id='e')
        self.assertTrue(reg.set_confirmed(fresh));self.assertEqual('new',reg.get('0001').command_item_id);self.assertEqual('e',fresh.to_dict()['event_id'])
if __name__=='__main__':unittest.main()
