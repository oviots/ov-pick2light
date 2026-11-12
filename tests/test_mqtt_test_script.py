"""Test payloads and callbacks only; no broker, credentials or physical controller."""
import contextlib
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from test_p2l_mqtt import Tester, address, block


class FakeClient:
    def __init__(self):
        self.sent = []

    def is_connected(self):
        return True

    def publish(self, topic, payload, qos, retain):
        self.sent.append((topic, json.loads(payload), qos, retain))
        return SimpleNamespace(rc=0, wait_for_publish=lambda timeout: None, is_published=lambda: True)


class MqttScriptTests(unittest.TestCase):
    def setUp(self):
        self.output = io.StringIO()
        self.stack = contextlib.ExitStack()
        self.stack.enter_context(contextlib.redirect_stdout(self.output))
        self.addCleanup(self.stack.close)
        self.tester = Tester('p2l/test', {'A': ('01', '0008', 5), 'B': ('02', '0014', 3)})
        self.client = self.tester.client = FakeClient()

    def test_scope_session_uniqueness_and_non_retained_commands(self):
        self.tester.start('A')
        self.tester.start('B')
        first, second = [row[1] for row in self.client.sent]
        self.assertNotEqual(first['correlationId'], second['correlationId'])
        self.assertEqual(first['items'], [{'address': '0008', 'quantity': 5}])
        self.assertEqual(second['blockNo'], '02')
        self.tester.start('A')  # avoid replacing a session when PUBACK didn't prove execution
        self.assertEqual(len(self.client.sent), 2)
        self.tester.finish('A')
        stop = self.client.sent[-1][1]
        self.assertEqual(stop, {'type': 'batch_finish', 'blockNo': '01', 'correlationId': first['correlationId']})
        self.assertTrue(all(topic == 'p2l/test/commands' and qos == 1 and not retain
                            for topic, _, qos, retain in self.client.sent))

    def test_completion_requires_own_session_block_and_live_event(self):
        self.tester.start('A')
        correlation = self.client.sent[0][1]['correlationId']
        def receive(block_no, retained=False):
            self.tester.on_message(None, None, SimpleNamespace(topic='p2l/test/events', retain=retained,
                payload=json.dumps({'type': 'batch_completed', 'blockNo': block_no, 'correlationId': correlation}).encode()))
        receive('02')
        receive('01', retained=True)
        self.assertIn('A', self.tester.running)
        receive('01')
        self.assertNotIn('A', self.tester.running)
        self.tester.start('A')
        self.assertNotEqual(self.client.sent[-1][1]['correlationId'], correlation)

    def test_dry_run_never_publishes_and_unknown_session_cannot_cancel(self):
        self.tester.finish('A')
        self.assertEqual(self.client.sent, [])
        self.tester.dry_run = True
        self.tester.start('A')
        self.assertEqual(self.client.sent, [])
        self.assertIn('[DRY RUN]', self.output.getvalue())

    def test_validation_and_subscription_failure(self):
        self.assertEqual(address('8'), '0008')
        self.assertEqual(block('02'), '02')
        for value in ('00', '1', 'A1', '001'):
            with self.assertRaises(ValueError):
                block(value)
        for value in ('0', '10000', '-1', 'abcd'):
            with self.assertRaises(ValueError):
                address(value)
        self.tester.on_subscribe(None, None, 1, [SimpleNamespace(is_failure=True)], None)
        self.assertTrue(self.tester.ready.is_set())
        self.assertIn('Subscribe', self.tester.error)


if __name__ == '__main__':
    unittest.main()
