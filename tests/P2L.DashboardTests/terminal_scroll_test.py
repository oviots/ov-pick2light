import os,pty,fcntl,termios,struct,subprocess,select,time,signal,json
from pathlib import Path
root=Path(__file__).resolve().parents[2]
master,slave=pty.openpty()
def resize(cols,rows):fcntl.ioctl(slave,termios.TIOCSWINSZ,struct.pack('HHHH',rows,cols,0,0))
resize(120,24)
env=dict(os.environ,TERM='xterm-256color',COLORTERM='truecolor')
proc=subprocess.Popen(['dotnet',str(root/'tests/P2L.DashboardTests/bin/Debug/net10.0/P2L.DashboardTests.dll'),'--live','--many'],stdin=slave,stdout=slave,stderr=slave,env=env,start_new_session=True)
output=bytearray()
def read_for(seconds):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  if select.select([master],[],[],0.03)[0]:
   try: output.extend(os.read(master,65536))
   except OSError: break
read_for(.7)
assert not (termios.tcgetattr(slave)[3]&termios.ECHO),'terminal echo remains on'
os.write(master,b'\x1b[B'*12);read_for(.5)
os.write(master,b'\x1b[6~'*3);read_for(.5)
os.write(master,b'\x1b[F');read_for(.5)
assert b'2399' in output,'End did not display last module'
resize(40,24);os.kill(proc.pid,signal.SIGWINCH);read_for(.5)
os.write(master,b'\x1b[A'*5);read_for(.5)
os.write(master,b'\x1b[H');read_for(.5)
proc.send_signal(signal.SIGINT);read_for(.6)
read_for(3)
if proc.poll() is None:
 proc.kill(); read_for(.2)
 Path('/tmp/p2l-dashboard-scroll-fail.ansi').write_bytes(output)
 raise AssertionError('Dashboard failed to exit after cancellation')
proc.wait(timeout=1)
assert proc.returncode==0,output.decode(errors='replace')[-2000:]
assert b'^[[A' not in output and b'^[[B' not in output,'arrow keys echoed into dashboard'
assert output.count(b'\x1b[?1049h')==1 and output.count(b'\x1b[?1049l')==1,'alternate buffer not restored'
assert b'\x1b[?25h\x1b[?1007l\x1b[?1049l' in output,'terminal modes not restored'
Path('/tmp/p2l-dashboard-scroll.ansi').write_bytes(output)
print(json.dumps({'modules':403,'scrollKeys':17,'pageKeys':3,'endReachedLastLight':True,'arrowEcho':False,'resize':'120x24 -> 40x24','ctrlCExit':proc.returncode,'terminalModesRestored':True,'hardwareConnections':0}))
os.close(master);os.close(slave)
