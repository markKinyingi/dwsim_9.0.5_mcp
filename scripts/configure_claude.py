r"""
Adds (or updates) the "dwsim" entry in Claude Desktop's config so Claude
launches this MCP server. A timestamped backup of the old config is kept.

    python scripts\configure_claude.py                 # uses this Python + this folder's server.py
    python scripts\configure_claude.py --python C:\...\python.exe --server C:\...\server.py
    python scripts\configure_claude.py --dry-run       # show what would change

Fully quit Claude Desktop (including the system tray icon) and reopen it
afterwards.
"""

import argparse
import json
import os
import shutil
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--python", default=sys.executable)
    ap.add_argument("--server", default=os.path.join(ROOT, "server.py"))
    ap.add_argument("--name", default="dwsim")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    appdata = os.environ.get("APPDATA")
    if not appdata:
        sys.exit("APPDATA isn't set -- is this Windows?")
    config_path = os.path.join(appdata, "Claude", "claude_desktop_config.json")

    config = {}
    if os.path.exists(config_path):
        with open(config_path, encoding="utf-8") as f:
            text = f.read().strip()
        if text:
            try:
                config = json.loads(text)
            except ValueError as e:
                sys.exit(f"{config_path} isn't valid JSON ({e}); fix it by hand first.")

    entry = {"command": os.path.abspath(a.python), "args": [os.path.abspath(a.server)]}
    servers = config.setdefault("mcpServers", {})
    old = servers.get(a.name)
    if old == entry:
        print(f"Claude Desktop already launches '{a.name}' with these paths -- nothing to change.")
        return
    servers[a.name] = entry

    print(f"Config file: {config_path}")
    print(f"'{a.name}' entry {'updated' if old else 'added'}:")
    print(json.dumps({a.name: entry}, indent=2))
    if a.dry_run:
        print("(dry run -- nothing written)")
        return

    os.makedirs(os.path.dirname(config_path), exist_ok=True)
    if os.path.exists(config_path):
        backup = f"{config_path}.bak-{time.strftime('%Y%m%d-%H%M%S')}"
        shutil.copy2(config_path, backup)
        print(f"Backup of the previous config: {backup}")
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2)
    print("Done. Fully quit Claude Desktop (system tray too) and reopen it.")


if __name__ == "__main__":
    main()
