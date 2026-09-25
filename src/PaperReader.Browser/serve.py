#!/usr/bin/env python3
"""Serves the published web app (dist/web/wwwroot) on http://localhost:8080.

usage: serve.py [folder] [port]
The microphone needs a secure page: localhost counts as one; elsewhere serve it over HTTPS.
"""
import http.server
import os
import sys

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "dist", "web", "wwwroot")
port = int(sys.argv[2]) if len(sys.argv) > 2 else 8080


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".wasm": "application/wasm",
        ".js": "text/javascript",
        ".mjs": "text/javascript",
        ".json": "application/json",
        ".dat": "application/octet-stream",
        ".blat": "application/octet-stream",
        ".svg": "image/svg+xml",
    }

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=root, **kwargs)

    def end_headers(self):
        # the app is updated in place: always check for a newer copy
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()


print(f"Paper Reader: http://localhost:{port}/  (serving {os.path.abspath(root)})")
http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
