"""Verify a standalone executable and package it with the end-user PDF."""
import argparse
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--runtime', required=True, choices=[
    'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'win-x64', 'win-arm64'])
parser.add_argument('--version', required=True)
parser.add_argument('--publish-dir', required=True, type=Path)
parser.add_argument('--output-dir', default='dist', type=Path)
args = parser.parse_args()
if not re.fullmatch(r'\d+\.\d+\.\d+', args.version):
    parser.error('Version must have the form X.Y.Z')

executable = 'p2l.exe' if args.runtime.startswith('win-') else 'p2l'
published = list(args.publish_dir.iterdir())
if {p.name for p in published} != {executable} or not published[0].is_file():
    raise SystemExit('Publish directory must contain exactly the P2L executable')
guide = Path(__file__).resolve().parents[1] / 'output/pdf/HUONG_DAN_SU_DUNG_P2L.pdf'
if not guide.is_file() or not guide.read_bytes().startswith(b'%PDF-'):
    raise SystemExit('User guide PDF is missing or invalid')

# Run outside the checkout with only the executable present: no DLLs or JSON config.
with tempfile.TemporaryDirectory(prefix='p2l-standalone-') as directory:
    binary = Path(directory) / executable
    shutil.copy2(args.publish_dir / executable, binary)
    result = subprocess.run([str(binary), '--version'], cwd=directory,
                            check=True, capture_output=True, text=True, timeout=30)
    if result.stdout.strip() != args.version:
        raise SystemExit(f'Unexpected executable version: {result.stdout.strip()}')
    subprocess.run([str(binary), 'license', '--help'], cwd=directory,
                   check=True, capture_output=True, timeout=30)

args.output_dir.mkdir(parents=True, exist_ok=True)
archive = args.output_dir / f'p2l-{args.version}-{args.runtime}.zip'
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as package:
    package.write(args.publish_dir / executable, executable)
    package.write(guide, guide.name)
with zipfile.ZipFile(archive) as package:
    if set(package.namelist()) != {executable, guide.name} or package.testzip():
        raise SystemExit('Release archive validation failed')
print(f'PASS: standalone {args.runtime} {args.version}; executable + PDF: {archive}')
