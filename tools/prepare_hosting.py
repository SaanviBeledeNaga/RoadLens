import argparse
import json
import shutil
from pathlib import Path
from urllib.parse import urlsplit

parser=argparse.ArgumentParser()
parser.add_argument('--api-url',required=True)
args=parser.parse_args()
url=args.api_url.rstrip('/')
parsed=urlsplit(url)
if parsed.scheme!='https' or not parsed.netloc or parsed.username or parsed.password or parsed.path or parsed.query or parsed.fragment:
    raise SystemExit('Provide the HTTPS origin of the deployed Render API, with no path or credentials.')
root=Path(__file__).resolve().parents[1]
source=root/'src'/'VehicleCounter.Api'/'wwwroot'
target=root/'hosting'
shutil.copytree(source,target,dirs_exist_ok=True)
(target/'config.js').write_text('window.ROADLENS_CONFIG = Object.freeze('+json.dumps({'apiBaseUrl':url})+');\n',encoding='utf-8')
print('Firebase Hosting directory is ready; only static dashboard files are included.')
