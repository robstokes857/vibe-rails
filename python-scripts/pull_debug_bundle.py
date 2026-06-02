"""
Pull a remote debug bundle from viberails.ai down to a local SQLite .db.

A beta user clicks "Send debug log" in the desktop app, which uploads an encrypted
bundle of one terminal session to viberails.ai. This script lists those bundles and
downloads one — the server decrypts it server-side and returns a plain SQLite database
that every other script here reads via its --db flag. No import/merge needed.

Auth: your VibeRails API key (the admin account). Resolved from, in order:
    --api-key, $VIBERAILS_API_KEY, then ~/.vibe_rails/settings.json (Settings.ApiKey).

Usage:
    python pull_debug_bundle.py --list
    python pull_debug_bundle.py --id 42                 # -> ./bundles/<session-id>.db
    python pull_debug_bundle.py --id 42 --out foo.db
    python pull_debug_bundle.py --list --url http://localhost:5057

Then debug it with the existing tools, e.g.:
    python decode_session.py --db bundles/<session-id>.db <session-id>
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_URL = "https://viberails.ai"
# The desktop app persists settings (incl. ApiKey) to settings.json (see
# VibeRails/Utils/Config.cs + PathConstants.SETTINGS_FILENAME). Older builds wrote
# state.json, so we still read it as a fallback.
SETTINGS_JSON = os.path.expanduser("~/.vibe_rails/settings.json")
LEGACY_STATE_JSON = os.path.expanduser("~/.vibe_rails/state.json")
DEFAULT_OUT_DIR = "bundles"


def resolve_api_key(cli_value: str | None) -> str:
    if cli_value:
        return cli_value
    env = os.environ.get("VIBERAILS_API_KEY")
    if env:
        return env
    for path in (SETTINGS_JSON, LEGACY_STATE_JSON):
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, json.JSONDecodeError):
            continue
        # Settings key casing has varied; accept either.
        for key in ("ApiKey", "apiKey"):
            if data.get(key):
                return data[key]
    sys.exit(
        "No API key found. Pass --api-key, set VIBERAILS_API_KEY, "
        f"or ensure {SETTINGS_JSON} has an ApiKey."
    )


def request(url: str, api_key: str) -> urllib.request.Request:
    req = urllib.request.Request(url)
    req.add_header("X-Api-Key", api_key)
    return req


def cmd_list(base_url: str, api_key: str) -> None:
    url = f"{base_url.rstrip('/')}/api/v1/debug-bundles"
    try:
        with urllib.request.urlopen(request(url, api_key)) as resp:
            bundles = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        sys.exit(f"List failed: HTTP {e.code} {e.reason}. "
                 f"(403 means this API key is not the admin account.)")
    except urllib.error.URLError as e:
        sys.exit(f"List failed: could not reach {base_url} ({e.reason}).")

    if not bundles:
        print("No debug bundles uploaded yet.")
        return

    print(f"{'ID':>4}  {'UPLOADED (UTC)':<20}  {'CLI':<8}  {'SIZE':>9}  {'EMAIL':<28}  SESSION / MESSAGE")
    print("-" * 110)
    for b in bundles:
        bundle_id = b.get("id")
        id_str = str(bundle_id) if bundle_id is not None else "?"
        size_kb = f"{(b.get('fileSizeBytes') or 0) / 1024:.0f} KB"
        uploaded = (b.get("uploadedAt") or "")[:19].replace("T", " ")
        email = (b.get("email") or "")[:28]
        session = b.get("sessionId") or ""
        message = (b.get("message") or "").replace("\n", " ")
        tail = session if not message else f"{session}  —  {message[:60]}"
        print(f"{id_str:>4}  {uploaded:<20}  {(b.get('cli') or ''):<8}  {size_kb:>9}  {email:<28}  {tail}")


def cmd_download(base_url: str, api_key: str, bundle_id: int, out: str | None) -> None:
    url = f"{base_url.rstrip('/')}/api/v1/debug-bundles/{bundle_id}/download"
    try:
        with urllib.request.urlopen(request(url, api_key)) as resp:
            data = resp.read()
            disposition = resp.headers.get("Content-Disposition", "")
    except urllib.error.HTTPError as e:
        sys.exit(f"Download failed: HTTP {e.code} {e.reason}. "
                 f"(403 means this API key is not the admin account; 404 means no such bundle.)")
    except urllib.error.URLError as e:
        sys.exit(f"Download failed: could not reach {base_url} ({e.reason}).")

    if out:
        out_path = out
        parent = os.path.dirname(out_path)
        if parent:
            os.makedirs(parent, exist_ok=True)
    else:
        # Prefer the server-provided "<session-id>.db" filename, but never trust it
        # as a path: strip any directory components (both separators) and reject
        # "."/".." so a crafted Content-Disposition can't escape ./bundles/.
        filename = f"bundle-{bundle_id}.db"
        marker = "filename="
        if marker in disposition:
            raw = disposition.split(marker, 1)[1].strip().strip('"')
            candidate = os.path.basename(raw.replace("\\", "/"))
            if candidate and candidate not in (".", ".."):
                filename = candidate
        os.makedirs(DEFAULT_OUT_DIR, exist_ok=True)
        out_path = os.path.join(DEFAULT_OUT_DIR, filename)

    with open(out_path, "wb") as f:
        f.write(data)

    print(f"Wrote {len(data):,} bytes to {out_path}")
    print(f"Next:  python decode_session.py --db {out_path} <session-id>")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    p.add_argument("--list", action="store_true", help="List all uploaded bundles (admin)")
    p.add_argument("--id", type=int, help="Download bundle by id (admin)")
    p.add_argument("--out", help=f"Output path (default: ./{DEFAULT_OUT_DIR}/<session-id>.db)")
    p.add_argument("--api-key", help="VibeRails API key (else $VIBERAILS_API_KEY or state.json)")
    p.add_argument("--url", default=DEFAULT_URL, help=f"Base URL (default: {DEFAULT_URL})")
    args = p.parse_args()

    if not args.list and args.id is None:
        p.error("pass --list or --id <n>")

    api_key = resolve_api_key(args.api_key)

    if args.list:
        cmd_list(args.url, api_key)
    if args.id is not None:
        cmd_download(args.url, api_key, args.id, args.out)


if __name__ == "__main__":
    main()
