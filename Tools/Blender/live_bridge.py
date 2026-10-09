"""Live bridge: let an agent drive a *running* Blender GUI that you are watching.

Start it by opening Blender with this script, optionally with a file to load:

    "O:/Blender/blender.exe" Tools/Blender/vanguard_review.blend --python Tools/Blender/live_bridge.py

Then the other side writes

    Tools/Blender/live_queue/inbox.json   {"id": "...", "code": "bpy code"}

and reads the answer from

    Tools/Blender/live_queue/outbox.json  {"id", "stdout", "error", "result", "seconds"}

Use Tools/Blender/live_client.py to send snippets; it writes the inbox atomically and
waits for the matching reply. Snippets run on Blender's main thread from a timer, so
`bpy.ops` behaves as if you typed it into the Python console - which is the point: the
window stays open and every change shows up in the viewport as it happens.

Every snippet is echoed in the status bar, appended to live_bridge.log and mirrored
into a text data-block called "agent_log" (Window > Toggle System Console shows prints
too). Nothing listens on a network port; the only way in is this file in the project.

A snippet can set a variable named `result` to return something, and can call
`bpy.ops.screen.screenshot(filepath=...)` (or the `shot(path)` helper) to save the
window so the agent sees the session.
"""

import contextlib
import io
import json
import os
import sys
import time
import traceback

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
QUEUE = os.path.join(HERE, "live_queue")
INBOX = os.path.join(QUEUE, "inbox.json")
OUTBOX = os.path.join(QUEUE, "outbox.json")
LOG = os.path.join(HERE, "live_bridge.log")
INTERVAL = 0.15


def _log(line):
    with open(LOG, "a", encoding="utf-8") as handle:
        handle.write(line + "\n")
    try:
        text = bpy.data.texts.get("agent_log") or bpy.data.texts.new("agent_log")
        text.write(line + "\n")
    except Exception:
        pass


def _write(payload):
    os.makedirs(QUEUE, exist_ok=True)
    tmp = OUTBOX + ".tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    os.replace(tmp, OUTBOX)


def _status(text):
    try:
        bpy.context.workspace.status_text_set(text)
    except Exception:
        pass


def shot(path):
    """Save the whole Blender window so the agent sees what you see."""
    try:
        bpy.ops.screen.screenshot(filepath=path, check_existing=False)
        return "window screenshot " + path
    except Exception as exc:
        # Fall back to an OpenGL render of the viewport.
        bpy.ops.render.opengl(write_still=True, view_context=False)
        return "screenshot op failed (%s), used opengl render" % exc


def _override():
    """A VIEW_3D context, because bpy.ops needs one and a timer has no area."""
    window = bpy.context.window_manager.windows[0] if bpy.context.window_manager.windows else None
    screen = window.screen if window else None
    area = None
    for candidate in screen.areas if screen else []:
        if candidate.type == "VIEW_3D":
            area = candidate
            break
    region = None
    for candidate in area.regions if area else []:
        if candidate.type == "WINDOW":
            region = candidate
            break
    values = {}
    if window:
        values["window"] = window
    if screen:
        values["screen"] = screen
    if area:
        values["area"] = area
    if region:
        values["region"] = region
    return values


def _execute(code):
    started = time.time()
    out = io.StringIO()
    namespace = {"bpy": bpy, "shot": shot, "os": os, "result": None}
    error = None
    try:
        compiled = compile(code, "<agent>", "exec")
        with contextlib.redirect_stdout(out):
            override = _override()
            if override:
                with bpy.context.temp_override(**override):
                    exec(compiled, namespace)
            else:
                exec(compiled, namespace)
    except Exception:
        error = traceback.format_exc()
    return {
        "stdout": out.getvalue()[-4000:],
        "error": error,
        "result": None if namespace.get("result") is None else repr(namespace["result"]),
        "seconds": round(time.time() - started, 2),
    }


def _tick():
    try:
        if os.path.exists(INBOX):
            try:
                with open(INBOX, encoding="utf-8") as handle:
                    request = json.load(handle)
            finally:
                try:
                    os.remove(INBOX)
                except OSError:
                    pass
            code = request.get("code", "") or ""
            label = next((line for line in code.strip().splitlines() if line.strip()), "")
            _status("agent: " + label.strip()[:78])
            payload = {"id": request.get("id"), "code": code}
            payload.update(_execute(code))
            _write(payload)
            _log("[%s] %s  (%.2fs%s)"
                 % (time.strftime("%H:%M:%S"), label.strip()[:100], payload["seconds"],
                    ", ERROR" if payload["error"] else ""))
    except Exception:
        _log(traceback.format_exc())
        try:
            _write({"id": None, "error": traceback.format_exc()})
        except Exception:
            pass
    return INTERVAL


def main():
    os.makedirs(QUEUE, exist_ok=True)
    for stale in (INBOX, OUTBOX):
        if os.path.exists(stale):
            os.remove(stale)
    _log("--- live bridge started, Blender %s, file: %s"
         % (bpy.app.version_string, bpy.data.filepath or "(unsaved)"))
    bpy.app.timers.register(_tick, first_interval=0.3)
    _status("agent bridge ready")
    print("[live_bridge] ready: send snippets to " + INBOX)


if __name__ == "__main__":
    main()
