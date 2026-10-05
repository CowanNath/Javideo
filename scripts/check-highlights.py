"""Integration checks using a temporary database; never opens the user's library.

Run after dotnet build worker -c Release: python scripts/check-highlights.py
Optional browser checks: python scripts/check-highlights.py --ui
"""
import argparse
import base64
import concurrent.futures
from contextlib import closing
import io
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7x8AAAAASUVORK5CYII=')


def multipart(metadata, files=()):
    boundary = 'javideo-check-' + uuid.uuid4().hex
    chunks = [f'--{boundary}\r\nContent-Disposition: form-data; name="metadata"\r\n\r\n'.encode(), json.dumps(metadata).encode(), b'\r\n']
    for name, content in files:
        chunks += [f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode(), content, b'\r\n']
    chunks.append(f'--{boundary}--\r\n'.encode())
    return b''.join(chunks), {'Content-Type': 'multipart/form-data; boundary=' + boundary}


def request(base, path, method='GET', data=None, headers=None, expected=200):
    if isinstance(data, dict):
        data = json.dumps(data).encode()
        headers = {'Content-Type': 'application/json', **(headers or {})}
    req = urllib.request.Request(base + path, data=data, headers=headers or {}, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    body = response.read()
    assert response.status == expected, (path, response.status, body.decode(errors='replace'))
    return body, response.headers


class Worker:
    def __init__(self, directory):
        self.directory = directory
        self.proc = None
        self.log = None

    def start(self):
        env = {**os.environ, 'Javideo__DataDir': str(self.directory / 'data')}
        dll = ROOT / 'worker/bin/Release/net8.0/win-x64/javideo-worker.dll'
        assert dll.exists(), 'Build the Release worker first.'
        self.log_path = self.directory / ('worker-' + uuid.uuid4().hex + '.log')
        self.log = self.log_path.open('w', encoding='utf-8')
        self.proc = subprocess.Popen(['dotnet', str(dll)], cwd=ROOT / 'worker', env=env, stdout=self.log, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        for _ in range(150):
            content = self.log_path.read_text(encoding='utf-8', errors='replace')
            match = re.search(r'JAVIDEO_WORKER_PORT=(\d+)', content)
            if match:
                self.base = 'http://127.0.0.1:' + match[1]
                return self.base
            if self.proc.poll() is not None:
                raise AssertionError(content)
            time.sleep(.1)
        raise AssertionError('Worker startup timeout: ' + content)

    def stop(self):
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
            self.proc = None
        if self.log:
            self.log.close()
            self.log = None


def check_snapshot(body, directory):
    with zipfile.ZipFile(io.BytesIO(body)) as archive:
        db_path = directory / ('snapshot-' + uuid.uuid4().hex + '.db')
        db_path.write_bytes(archive.read('library.db'))
        with closing(sqlite3.connect(db_path)) as db:
            assets = db.execute('SELECT movie_id,storage_name FROM movie_highlight_assets').fetchall()
        for movie_id, name in assets:
            assert archive.read(f'highlights/{movie_id}/{name}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--ui', action='store_true')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='javideo-highlights-check-') as tmp:
        directory = Path(tmp)
        worker = Worker(directory)
        try:
            base = worker.start()
            library = directory / 'library'
            library.mkdir()
            target = directory / 'target'
            target.mkdir()
            db_path = directory / 'data/library.db'
            with closing(sqlite3.connect(db_path)) as db:
                db.execute("INSERT INTO libraries(id,name) VALUES(1,'Check'),(2,'Target')")
                db.execute('INSERT INTO library_directories(library_id,path) VALUES(1,?),(2,?)', (str(library), str(target)))
                db.commit()
            movie = json.loads(request(base, '/api/movies/ingest', 'POST', {'libraryId': 1, 'movie': {'number': 'TEST-001', 'title': 'Highlights check'}, 'magnets': []})[0])
            mid = movie['movieId']
            endpoint = f'/api/movies/{mid}/highlights'
            folder = Path(movie['folderPath'])
            for name in ['part-1.mp4', 'part-2.mp4', 'TEST-001-trailer.mp4']:
                (folder / name).write_bytes(b'\x00\x00\x00\x18ftypmp42' + b'\x00' * 32)
            assert set(json.loads(request(base, endpoint + '/video-files')[0])) == {'part-1.mp4', 'part-2.mp4'}

            def save(metadata, files=(), id=None, status=200):
                body, headers = multipart(metadata, files)
                result = request(base, endpoint + (f'/{id}' if id else ''), 'PUT' if id else 'POST', body, headers, status)[0]
                return json.loads(result) if result else None

            image = save({}, [('image.png', PNG)])
            assert image['title'] is None and image['startSeconds'] is None and len(image['assets']) == 1
            note = save({'note': 'Note only'})
            timed = save({'startSeconds': 0, 'endSeconds': 65, 'sourceFileName': 'part-2.mp4'})
            assert timed['title'] is None and timed['startSeconds'] == 0
            save({}, status=400)
            save({'endSeconds': 10}, status=400)
            save({'startSeconds': 20, 'endSeconds': 10}, status=400)
            save({'startSeconds': -1}, status=400)
            save({'startSeconds': 360000}, status=400)
            save({'title': 'x' * 121}, status=400)
            save({'note': 'bad', 'sourceFileName': '../escape.mp4'}, status=400)
            save({'note': 'bad', 'keepAssetIds': [image['assets'][0]['id']]}, id=note['id'], status=400)
            before = {p.relative_to(directory / 'data') for p in (directory / 'data/highlights').rglob('*') if p.is_file()}
            save({'note': 'Should roll back'}, [('valid.png', PNG), ('invalid.png', b'not an image')], status=400)
            after = {p.relative_to(directory / 'data') for p in (directory / 'data/highlights').rglob('*') if p.is_file()}
            assert after == before
            assert len(json.loads(request(base, endpoint)[0])) == 3
            save({'note': 'Should preserve', 'keepAssetIds': []}, [('invalid.png', b'invalid')], id=image['id'], status=400)
            assert json.loads(request(base, endpoint)[0])[1]['note'] is None
            asset_url = image['assets'][0]['url']
            content, headers = request(base, asset_url)
            assert content == PNG and headers['Content-Type'] == 'image/png' and headers['X-Content-Type-Options'] == 'nosniff'
            partial, _ = request(base, asset_url, headers={'Range': 'bytes=0-7'}, expected=206)
            assert partial == PNG[:8]
            request(base, asset_url.replace(f'/movies/{mid}/', '/movies/999999/'), expected=404)

            # Exercise the same upsert/cache rebuild used by rescraping, without network scraping.
            same = json.loads(request(base, '/api/movies/ingest', 'POST', {'libraryId': 1, 'movie': {'number': 'TEST-001', 'title': 'Rescraped'}, 'magnets': []})[0])
            assert same['movieId'] == mid and len(json.loads(request(base, endpoint)[0])) == 3
            request(base, f'/api/movies/{mid}/move', 'POST', {'targetLibraryId': 2})
            assert request(base, asset_url)[0] == PNG
            assert len(json.loads(request(base, endpoint)[0])) == 3

            with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
                exports = [pool.submit(request, base, '/api/backup/export'), pool.submit(save, {'note': 'Concurrent import'}, [('concurrent.png', PNG)])]
                snapshot = exports[0].result()[0]
                exports[1].result()
            check_snapshot(snapshot, directory)
            backup = request(base, '/api/backup/export')[0]
            check_snapshot(backup, directory)
            request(base, endpoint + f'/{image["id"]}', 'DELETE', expected=204)
            request(base, asset_url, expected=404)
            assert request(base, asset_url, expected=404)[0] == b''
            boundary = 'restore-check'
            body = f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="backup.zip"\r\nContent-Type: application/zip\r\n\r\n'.encode() + backup + f'\r\n--{boundary}--\r\n'.encode()
            request(base, '/api/backup/import', 'POST', body, {'Content-Type': 'multipart/form-data; boundary=' + boundary})
            assert list((directory / 'data').glob('before-import-*.zip'))
            worker.stop()
            base = worker.start()
            assert request(base, asset_url)[0] == PNG
            assert len(json.loads(request(base, endpoint)[0])) == 4

            # A backup with missing attachments must fail before changing the current library.
            stripped = io.BytesIO()
            with zipfile.ZipFile(io.BytesIO(backup)) as src, zipfile.ZipFile(stripped, 'w') as dst:
                dst.writestr('library.db', src.read('library.db'))
            invalid_body = body[:body.index(backup)] + stripped.getvalue() + f'\r\n--{boundary}--\r\n'.encode()
            request(base, '/api/backup/import', 'POST', invalid_body, {'Content-Type': 'multipart/form-data; boundary=' + boundary}, expected=500)
            assert request(base, asset_url)[0] == PNG

            # A clip-containing backup can exceed Kestrel's default 30 MB limit.
            large = io.BytesIO()
            with zipfile.ZipFile(io.BytesIO(backup)) as src, zipfile.ZipFile(large, 'w') as dst:
                for item in src.infolist():
                    dst.writestr(item.filename, src.read(item.filename))
                dst.writestr('padding.bin', bytes(31 * 1024 * 1024))
            large_body = body[:body.index(backup)] + large.getvalue() + f'\r\n--{boundary}--\r\n'.encode()
            request(base, '/api/backup/import', 'POST', large_body, {'Content-Type': 'multipart/form-data; boundary=' + boundary})
            worker.stop()
            base = worker.start()
            assert request(base, asset_url)[0] == PNG

            if args.ui:
                env = {**os.environ, 'VITE_DEV_WORKER_PORT': base.rsplit(':', 1)[1]}
                node = os.environ.get('NODE_EXE', shutil.which('node'))
                assert node, 'Node.js is required for browser checks.'
                vite_log = (directory / 'vite.log').open('w', encoding='utf-8')
                vite = subprocess.Popen([node, str(ROOT / 'node_modules/vite/bin/vite.js'), '--host', '127.0.0.1'], cwd=ROOT, env=env, stdout=vite_log, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                try:
                    subprocess.run([node, str(ROOT / 'scripts/check-highlights-ui.cjs'), str(mid), str(directory)], cwd=ROOT, env=env, check=True, timeout=100)
                finally:
                    vite.terminate()
                    vite.wait(timeout=10)
                    vite_log.close()

            request(base, f'/api/movies/{mid}', 'DELETE', expected=204)
            with closing(sqlite3.connect(db_path)) as db:
                assert db.execute('SELECT COUNT(*) FROM movie_highlights').fetchone()[0] == 0
                assert db.execute('SELECT COUNT(*) FROM movie_highlight_assets').fetchone()[0] == 0
            assert not (directory / 'data/highlights' / str(mid)).exists()
            # Older backups have no highlight tables. Import, then migrate on restart.
            legacy_db = directory / 'legacy.db'
            with zipfile.ZipFile(io.BytesIO(backup)) as archive:
                legacy_db.write_bytes(archive.read('library.db'))
            with closing(sqlite3.connect(legacy_db)) as db:
                db.execute('DROP TABLE movie_highlight_assets')
                db.execute('DROP TABLE movie_highlights')
                db.commit()
            legacy = io.BytesIO()
            with zipfile.ZipFile(legacy, 'w') as archive:
                archive.writestr('library.db', legacy_db.read_bytes())
            legacy_body = body[:body.index(backup)] + legacy.getvalue() + f'\r\n--{boundary}--\r\n'.encode()
            request(base, '/api/backup/import', 'POST', legacy_body, {'Content-Type': 'multipart/form-data; boundary=' + boundary})
            worker.stop()
            base = worker.start()
            assert json.loads(request(base, endpoint)[0]) == []
            print('PASS: optional title/time; validation; file signatures; ownership; rollback; range requests; rescrape; move; concurrent backup; restart; restore; missing-attachment rejection; >30 MB backup import; movie cleanup; legacy-backup migration.')
        finally:
            worker.stop()


if __name__ == '__main__':
    main()
