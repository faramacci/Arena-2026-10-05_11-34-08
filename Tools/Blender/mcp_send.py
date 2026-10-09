import json
import socket


def send(payload, wait=180):
    with socket.create_connection(("127.0.0.1", 9876), timeout=10) as sock:
        sock.sendall(json.dumps(payload).encode("utf-8"))
        sock.settimeout(wait)
        chunks = []
        while True:
            try:
                data = sock.recv(65536)
            except socket.timeout:
                break
            if not data:
                break
            chunks.append(data)
            try:
                json.loads(b"".join(chunks).decode("utf-8"))
                break
            except (ValueError, UnicodeDecodeError):
                continue
        return b"".join(chunks).decode("utf-8", "replace")


import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    code = handle.read()
print(send({"type": "execute_code", "params": {"code": code}}))
