"""Talk to the Blender MCP add-on socket that a running Blender is listening on.

The add-on opens a raw TCP socket (no HTTP) and answers one JSON request per
connection:

    {"type": "execute_code", "params": {"code": "python..."}}

    python Tools/Blender/mcp_client.py ping
    python Tools/Blender/mcp_client.py run snippet.py
    echo "result = bpy.app.version_string" | python Tools/Blender/mcp_client.py run -

Exits non-zero when the socket refuses, Blender replies with an error, or the
reply says the snippet raised, so a failed call can never look like a success.
"""

import argparse
import json
import socket
import sys

DEFAULT_PORT = 8599


def send(payload, port=DEFAULT_PORT, wait=600.0):
    with socket.create_connection(("127.0.0.1", port), timeout=10) as sock:
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
                return json.loads(b"".join(chunks).decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                continue
        return {"raw": b"".join(chunks).decode("utf-8", "replace")}


def execute(code, port=DEFAULT_PORT, wait=600.0):
    return send({"type": "execute_code", "params": {"code": code}}, port, wait)


def report(reply):
    """Print whatever Blender said and return a shell status."""
    if reply is None:
        print("[mcp] no reply", file=sys.stderr)
        return 2
    if "status" in reply and reply.get("status") not in ("success", None):
        print("[mcp] status: %s" % reply.get("status"), file=sys.stderr)
        if reply.get("message"):
            print(reply["message"], file=sys.stderr)
        return 1
    if "raw" in reply:
        print(reply["raw"])
        return 1
    if "executed" in reply and not reply["executed"]:
        print("[mcp] Blender did not execute the snippet", file=sys.stderr)
        return 1
    text = reply.get("result", json.dumps(reply)[:4000])
    if reply.get("error"):
        print(text)
        print("[mcp] error: %s" % reply["error"], file=sys.stderr)
        return 1
    print(text)
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("ping", "run"))
    parser.add_argument("script", nargs="?", help="file with bpy code, or - for stdin")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--wait", type=float, default=600.0)
    args = parser.parse_args()

    if args.mode == "ping":
        # The add-on captures stdout into its reply, so the ping prints.
        code = ("import bpy\n"
                "print('Blender ' + bpy.app.version_string\n"
                "      + ' | file: ' + (bpy.data.filepath or '(unsaved)')\n"
                "      + ' | objects: ' + str(len(bpy.data.objects))\n"
                "      + ' | mode: ' + bpy.context.mode)\n")
    else:
        if not args.script:
            parser.error("run needs a script path or -")
        code = sys.stdin.read() if args.script == "-" else open(
            args.script, encoding="utf-8").read()

    try:
        reply = execute(code, args.port, args.wait)
    except OSError as exc:
        print("[mcp] cannot reach Blender on 127.0.0.1:%d (%s)" % (args.port, exc),
              file=sys.stderr)
        return 3
    return report(reply)


if __name__ == "__main__":
    sys.exit(main())
