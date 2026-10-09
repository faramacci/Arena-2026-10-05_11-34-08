"""Send a snippet to the live Blender bridge and print what came back.

    python Tools/Blender/live_client.py snippet.py
    echo "result = bpy.app.version_string" | python Tools/Blender/live_client.py -
    python Tools/Blender/live_client.py --timeout 30 -  < snippet.py

Exits non-zero when Blender never replied or the snippet raised, so a failed call can
never be mistaken for a successful one.
"""

import argparse
import json
import os
import sys
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
QUEUE = os.path.join(HERE, "live_queue")
INBOX = os.path.join(QUEUE, "inbox.json")
OUTBOX = os.path.join(QUEUE, "outbox.json")


def send(code, timeout):
    os.makedirs(QUEUE, exist_ok=True)
    for path in (INBOX, OUTBOX):
        if os.path.exists(path):
            os.remove(path)
    request_id = uuid.uuid4().hex
    tmp = INBOX + ".tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump({"id": request_id, "code": code}, handle)
    os.replace(tmp, INBOX)

    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.exists(OUTBOX):
            try:
                with open(OUTBOX, encoding="utf-8") as handle:
                    reply = json.load(handle)
            except (OSError, ValueError):
                time.sleep(0.05)
                continue
            os.remove(OUTBOX)
            if reply.get("id") == request_id:
                return reply
        time.sleep(0.05)
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("script", help="file with the bpy snippet, or - for stdin")
    parser.add_argument("--timeout", type=float, default=300.0)
    args = parser.parse_args()

    code = sys.stdin.read() if args.script == "-" else open(args.script, encoding="utf-8").read()
    reply = send(code, args.timeout)
    if reply is None:
        print("no reply from the live bridge (%.0fs). Is Blender running the bridge?"
              % args.timeout, file=sys.stderr)
        return 2
    if reply.get("stdout"):
        sys.stdout.write(reply["stdout"])
    if reply.get("result") is not None:
        print("result: " + reply["result"])
    if reply.get("error"):
        sys.stderr.write(reply["error"])
        return 1
    print("[live] ok in %ss" % reply.get("seconds"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
