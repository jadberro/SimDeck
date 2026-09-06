"""SimDeck desktop app.

Runs the hub, wraps the web UI in a native window, and puts an icon in the
Windows notification area.

Closing the window hides it - the hub keeps streaming to your panels. The
only way to actually stop is Quit from the tray menu. That is deliberate:
closing a window by reflex should never take the gauges down mid-flight.

    python app.py --source fsuipc

Needs:
    pip install pywebview pystray pillow

If pywebview is missing or the system has no webview runtime, the app falls
back to opening the dashboard in your default browser and runs as a
tray-only application. The tray behaviour is the same either way.
"""

import argparse
import os
import sys
import threading
import time
import webbrowser

import hub as hubmod
import webui
from sources import FsuipcLuaSource, MockSource

HERE = os.path.dirname(os.path.abspath(__file__))
URL = "http://127.0.0.1:%d" % webui.PORT

_hub = None
_window = None
_icon = None
_quitting = threading.Event()


# ---------------------------------------------------------------------------
# tray icon artwork
# ---------------------------------------------------------------------------

def make_icon(size=64, alert=False):
    """A tiny gauge glyph, drawn rather than shipped as a file."""
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 2
    ring = (150, 60, 60, 255) if alert else (95, 109, 118, 255)
    d.ellipse([pad, pad, size - pad, size - pad], fill=(14, 16, 19, 255),
              outline=ring, width=max(2, size // 16))
    # a pointer sitting at about two thirds of scale
    cx = cy = size / 2.0
    d.line([cx, cy, cx + size * 0.28, cy - size * 0.20],
           fill=(232, 232, 230, 255), width=max(2, size // 14))
    d.ellipse([cx - size * 0.06, cy - size * 0.06,
               cx + size * 0.06, cy + size * 0.06],
              fill=(232, 232, 230, 255))
    return img


# ---------------------------------------------------------------------------
# tray menu actions
# ---------------------------------------------------------------------------

def show_window(icon=None, item=None):
    if _window is not None:
        try:
            _window.show()
            _window.restore()
        except Exception:
            pass
    else:
        webbrowser.open(URL)


def open_in_browser(icon=None, item=None):
    webbrowser.open(URL)


def do_quit(icon=None, item=None):
    _quitting.set()
    if _icon is not None:
        try:
            _icon.stop()
        except Exception:
            pass
    if _window is not None:
        try:
            _window.destroy()
        except Exception:
            pass
    # the hub thread is a daemon, so the process exits from here


def status_text(_item=None):
    """Shown greyed at the top of the tray menu."""
    if _hub is None:
        return "Starting..."
    n = len(_hub.modules)
    src = "connected" if _hub.source.connected else "no data"
    return "%d module%s - %s" % (n, "" if n == 1 else "s", src)


def build_tray():
    import pystray
    menu = pystray.Menu(
        pystray.MenuItem(status_text, None, enabled=False),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Open dashboard", show_window, default=True),
        pystray.MenuItem("Open in browser", open_in_browser),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Quit SimDeck", do_quit),
    )
    return pystray.Icon("simdeck", make_icon(), "SimDeck", menu)


def tray_watcher():
    """Keep the tooltip and icon reflecting reality without a redraw loop."""
    last = None
    while not _quitting.is_set():
        time.sleep(2.0)
        if _icon is None or _hub is None:
            continue
        alert = not _hub.source.connected
        state = (alert, len(_hub.modules))
        if state != last:
            last = state
            try:
                _icon.icon = make_icon(alert=alert)
                _icon.title = "SimDeck - " + status_text()
            except Exception:
                pass


# ---------------------------------------------------------------------------

def start_hub(source_name, watchlist, auto_update):
    global _hub
    src = MockSource() if source_name == "mock" else FsuipcLuaSource(watchlist)
    _hub = hubmod.Hub(src, source_name, auto_update=auto_update)
    threading.Thread(target=_hub.run, daemon=True).start()
    # give the web server a moment before the window points at it
    for _ in range(50):
        if _hub is not None:
            time.sleep(0.05)
            break


def on_closing():
    """Hide instead of close. Returning False cancels the close."""
    if _quitting.is_set():
        return True
    try:
        _window.hide()
    except Exception:
        pass
    if _icon is not None:
        try:
            _icon.notify("Still running. Right-click the tray icon to quit.",
                         "SimDeck minimised")
        except Exception:
            pass          # notifications are best effort, never fatal
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", choices=["mock", "fsuipc"], default="mock")
    ap.add_argument("--no-auto-update", action="store_true")
    ap.add_argument("--minimised", action="store_true",
                    help="start hidden in the tray, for autostart")
    ap.add_argument("--watchlist",
                    default=os.path.join(HERE, "lua", "simdeck_lvars.txt"))
    args = ap.parse_args()

    start_hub(args.source, args.watchlist, not args.no_auto_update)

    global _icon, _window

    try:
        _icon = build_tray()
    except Exception as exc:
        print("[app] no tray available (%s) - running windowed only" % exc)
        _icon = None

    if _icon is not None:
        # pystray must not own the main thread; webview needs it on Windows
        threading.Thread(target=_icon.run, daemon=True).start()
        threading.Thread(target=tray_watcher, daemon=True).start()

    try:
        import webview
    except ImportError:
        print("[app] pywebview not installed - dashboard opens in your browser")
        if not args.minimised:
            webbrowser.open(URL)
        while not _quitting.is_set():
            time.sleep(0.5)
        return

    _window = webview.create_window(
        "SimDeck", URL,
        width=980, height=720, min_size=(720, 520),
        background_color="#0d1013",
        hidden=args.minimised,
    )
    _window.events.closing += on_closing

    try:
        webview.start()
    except Exception as exc:
        print("[app] webview failed (%s) - falling back to browser" % exc)
        if not args.minimised:
            webbrowser.open(URL)
        while not _quitting.is_set():
            time.sleep(0.5)


if __name__ == "__main__":
    main()
