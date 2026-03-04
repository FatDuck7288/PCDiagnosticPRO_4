import os, ssl, urllib.request, time, json, sys

URL = 'https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf?download=true'
workspace = r'd:\Git_Cursor_Sux\Cursor_Suce'
model_dir = os.path.join(workspace, 'models')
os.makedirs(model_dir, exist_ok=True)
final_path = os.path.join(model_dir, 'tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf')
part_path = final_path + '.part'
state_path = final_path + '.state.json'

ctx = ssl._create_unverified_context()
opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),
    urllib.request.HTTPSHandler(context=ctx)
)

def write_state(**kwargs):
    payload = {'ts': time.time(), **kwargs}
    with open(state_path, 'w', encoding='utf-8') as f:
        json.dump(payload, f)

head = urllib.request.Request(URL, method='HEAD')
with opener.open(head, timeout=60) as r:
    total = int(r.headers.get('Content-Length') or 0)

if os.path.exists(final_path) and total > 0 and os.path.getsize(final_path) == total:
    write_state(status='done', downloaded=total, total=total, path=final_path)
    print('MODEL_ALREADY_PRESENT', final_path)
    sys.exit(0)

existing = os.path.getsize(part_path) if os.path.exists(part_path) else 0
headers = {}
if existing > 0:
    headers['Range'] = f'bytes={existing}-'

req = urllib.request.Request(URL, headers=headers)
resp = opener.open(req, timeout=120)
status = getattr(resp, 'status', None)
if existing > 0 and status != 206:
    existing = 0
    resp.close()
    req = urllib.request.Request(URL)
    resp = opener.open(req, timeout=120)

mode = 'ab' if existing > 0 else 'wb'
downloaded = existing
last_emit = time.time()
start = time.time()

write_state(status='downloading', downloaded=downloaded, total=total, path=final_path)
with open(part_path, mode) as out:
    while True:
        chunk = resp.read(8 * 1024 * 1024)
        if not chunk:
            break
        out.write(chunk)
        downloaded += len(chunk)
        now = time.time()
        if now - last_emit >= 2:
            speed = downloaded / max(1e-6, now - start)
            eta = int((total - downloaded) / speed) if total and speed > 0 else -1
            write_state(status='downloading', downloaded=downloaded, total=total, speed_bps=speed, eta_sec=eta, path=final_path)
            print(f'PROGRESS {downloaded}/{total} speed={speed:.1f}Bps eta={eta}s')
            last_emit = now

resp.close()

if total > 0 and downloaded != total:
    write_state(status='error', downloaded=downloaded, total=total, path=final_path, message='size_mismatch')
    print(f'ERROR size mismatch downloaded={downloaded} total={total}')
    sys.exit(2)

os.replace(part_path, final_path)
write_state(status='done', downloaded=downloaded, total=total, path=final_path)
print('DOWNLOAD_DONE', final_path)
