#!/usr/bin/env python3
"""Interactive P2L MQTT test. Dependency: paho-mqtt>=2,<3. No credentials are saved.

Run from the repository: .venv/bin/python scripts/test_p2l_mqtt.py
Preview payloads without a broker: .venv/bin/python scripts/test_p2l_mqtt.py --dry-run
"""
import argparse
import getpass
import json
import ssl
import threading
import uuid
from datetime import datetime, timezone


DEFAULT_HOST = 's20a2471.ala.asia-southeast1.emqxsl.com'
DEFAULT_PREFIX = 'p2l/uat/test01'


def block(value):
    if len(value) != 2 or not value.isascii() or not value.isdigit() or value == '00':
        raise ValueError('Block phải gồm hai số 01–99, ví dụ 01 hoặc 02.')
    return value


def address(value):
    if not value or len(value) > 4 or not value.isascii() or not value.isdigit() or int(value) == 0:
        raise ValueError('Địa chỉ đèn phải từ 0001 đến 9999.')
    return value.zfill(4)


def ask(label, default=''):
    return input(f'{label}' + (f' [{default}]' if default else '') + ': ').strip() or default


class Tester:
    def __init__(self, prefix, rows, dry_run=False):
        self.prefix = prefix
        self.rows = rows
        self.dry_run = dry_run
        self.client = None
        self.sessions = {}  # only sessions initiated by THIS script
        self.running = set()
        self.lock = threading.RLock()
        self.ready = threading.Event()
        self.error = None

    def connect(self, host, port, username, password, ca=None):
        try:
            import paho.mqtt.client as mqtt
        except ImportError as error:
            raise RuntimeError("Thiếu paho-mqtt. Cài: python3 -m pip install 'paho-mqtt>=2,<3'") from error
        self.client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2,
                                  client_id='p2l-test-' + uuid.uuid4().hex[:12],
                                  protocol=mqtt.MQTTv311, reconnect_on_failure=False)
        self.client.username_pw_set(username, password)
        self.client.tls_set_context(ssl.create_default_context(cafile=ca))
        self.client.on_connect = self.on_connect
        self.client.on_subscribe = self.on_subscribe
        self.client.on_message = self.on_message
        self.client.on_disconnect = self.on_disconnect
        self.client.connect_timeout = 10
        self.client.connect(host, port, keepalive=30)
        self.client.loop_start()
        if not self.ready.wait(15):
            raise RuntimeError('Hết thời gian chờ MQTT/SUBACK.')
        if self.error:
            raise RuntimeError(self.error)
        print('MQTT TLS đã kết nối; đang nghe events, devices và status.')

    def on_connect(self, client, userdata, flags, reason, properties):
        if reason.is_failure:
            self.error = f'MQTT từ chối kết nối: {reason}'
            self.ready.set()
            return
        result, _ = client.subscribe([(self.prefix + '/events', 1),
                                      (self.prefix + '/devices/#', 1),
                                      (self.prefix + '/status', 1)])
        if result != 0:
            self.error = 'Không gửi được yêu cầu subscribe.'
            self.ready.set()

    def on_subscribe(self, client, userdata, mid, reasons, properties):
        if not reasons or any(reason.is_failure for reason in reasons):
            self.error = 'Subscribe bị từ chối. Kiểm tra quyền đọc topics của MQTT Client.'
        self.ready.set()

    def on_disconnect(self, client, userdata, flags, reason, properties):
        if not self.ready.is_set():
            self.error = 'MQTT ngắt kết nối trước khi subscribe hoàn tất.'
            self.ready.set()
        if reason.is_failure:
            print(f'\n[MQTT] Mất kết nối: {reason}. Thoát và chạy lại script để kết nối lại.')

    def on_message(self, client, userdata, message):
        try:
            data = json.loads(message.payload)
            if not isinstance(data, dict):
                return
        except (ValueError, UnicodeError):
            print(f'\n[RX] {message.topic}: payload không phải JSON object')
            return
        # Print operational fields, never arbitrary payloads/passwords/tokens.
        fields = ('type', 'blockNo', 'correlationId', 'address', 'moduleStatus', 'status',
                  'displayData', 'adjustedQuantity', 'online', 'gatewayConnected')
        visible = {key: data[key] for key in fields if key in data}
        print(f'\n[RX{" retained" if message.retain else ""}] {message.topic}: '
              + json.dumps(visible, ensure_ascii=False))
        if data.get('type') == 'batch_completed' and not message.retain:
            with self.lock:
                for row, session in self.sessions.items():
                    if session == data.get('correlationId') and self.rows[row][0] == data.get('blockNo'):
                        self.running.discard(row)

    def publish(self, payload):
        text = json.dumps(payload, separators=(',', ':'), ensure_ascii=False)
        if self.dry_run:
            print(f'[DRY RUN] {self.prefix}/commands {text}')
            return
        if self.client is None or not self.client.is_connected():
            raise RuntimeError('MQTT chưa kết nối; chưa gửi lệnh.')
        info = self.client.publish(self.prefix + '/commands', text, qos=1, retain=False)
        info.wait_for_publish(timeout=10)
        if info.rc != 0 or not info.is_published():
            raise RuntimeError('Chưa nhận được PUBACK. Không tự gửi lại; xem event/P2L trước khi thử tiếp.')
        print('[TX] Broker đã ACK: ' + text)
        print('     PUBACK không xác nhận Controller đã thực thi; theo dõi event và đèn thực tế.')

    def start(self, row):
        with self.lock:
            if row in self.running:
                print(f'Dãy {row} đã có lệnh test đang chờ: {self.sessions[row]}. Kết thúc dãy trước khi tạo session mới.')
                return
            number, lamp, quantity = self.rows[row]
            session = f'TEST-{row}-{datetime.now(timezone.utc):%Y%m%dT%H%M%S}-{uuid.uuid4().hex[:6]}'
            # Register before publish so an immediate completion cannot race past us.
            self.sessions[row] = session
            self.running.add(row)
        self.publish({'type': 'batch_start', 'blockNo': number, 'correlationId': session,
                      'color': 'Green' if row == 'A' else 'Red',
                      'items': [{'address': lamp, 'quantity': quantity}]})

    def finish(self, row):
        with self.lock:
            session = self.sessions.get(row)
        if not session:
            print(f'Chưa tạo session test dãy {row} trong lần chạy này.')
            return
        self.publish({'type': 'batch_finish', 'blockNo': self.rows[row][0], 'correlationId': session})
        with self.lock:
            self.running.discard(row)

    def close(self):
        if self.client:
            self.client.disconnect()
            self.client.loop_stop()


def main():
    parser = argparse.ArgumentParser(description='Test P2L hai block trên MQTT TLS; không lưu mật khẩu.')
    parser.add_argument('--host', help='Broker host, mặc định hỏi khi chạy')
    parser.add_argument('--port', type=int, default=8883)
    parser.add_argument('--prefix', help='MQTT prefix, không gồm /commands')
    parser.add_argument('--username', help='MQTT Client username trong authen')
    parser.add_argument('--ca', help='File CA PEM tùy chọn; mặc định dùng CA hệ thống')
    parser.add_argument('--dry-run', action='store_true', help='Chỉ in payload, không kết nối mạng')
    args = parser.parse_args()
    tester = None
    try:
        if not 1 <= args.port <= 65535:
            raise ValueError('Port phải từ 1 đến 65535.')
        host = args.host or ask('Broker host', DEFAULT_HOST)
        prefix = (args.prefix or ask('MQTT prefix', DEFAULT_PREFIX)).rstrip('/')
        if not prefix or any(c in prefix for c in ('#', '+', '\0')) or prefix.endswith('/commands'):
            raise ValueError('Prefix không chứa wildcard và không gồm hậu tố /commands.')
        rows = {}
        for row, default_block, default_address, quantity in [('A', '01', '0008', 5), ('B', '02', '0014', 3)]:
            number = block(ask(f'Block dãy {row}', default_block))
            lamp = address(ask(f'Địa chỉ đèn dãy {row}', default_address))
            rows[row] = (number, lamp, quantity)
        if rows['A'][0] == rows['B'][0] or rows['A'][1] == rows['B'][1]:
            raise ValueError('Hai dãy cần block khác nhau và địa chỉ đèn không trùng.')
        tester = Tester(prefix, rows, args.dry_run)
        if not args.dry_run:
            username = args.username or ask('MQTT Client username (trong authen)')
            password = getpass.getpass('MQTT Client password (ẩn): ')
            if not username or not password:
                raise ValueError('Cần username/password MQTT Client.')
            tester.connect(host, args.port, username, password, args.ca)
            del password
        print('\nCần chạy p2l serve và cho phép block ' + ','.join(v[0] for v in rows.values()) + ' trong authen.')
        print('1 Snapshot | 2 Bật dãy A (5) | 3 Bật dãy B (3) | 4 Bật cả hai')
        print('5 Kết thúc A | 6 Kết thúc B | 0 Thoát')
        print('Bấm xác nhận trên đèn để xem confirmed/batch_completed. Thoát script không tự tắt đèn.')
        while True:
            choice = input('\nChọn: ').strip()
            try:
                if choice == '0':
                    break
                if choice == '1':
                    tester.publish({'type': 'get_snapshot'})
                elif choice in ('2', '3'):
                    tester.start('A' if choice == '2' else 'B')
                elif choice == '4':
                    tester.start('A')
                    tester.start('B')
                elif choice in ('5', '6'):
                    tester.finish('A' if choice == '5' else 'B')
                else:
                    print('Chọn 0–6.')
            except (RuntimeError, ValueError) as error:
                print(f'[LỖI] {error}')
    except (KeyboardInterrupt, EOFError):
        print('\nĐóng script.')
    except Exception as error:
        print(f'[LỖI] {type(error).__name__}: {error}')
        return 1
    finally:
        if tester:
            tester.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
