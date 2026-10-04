r"""
Removes personal metadata that DWSIM stores inside .dwxmz files, so they
can be shared publicly: the saved file path (which contains your Windows
user name), the simulation author (COMPUTER\user), the backup file name,
the messages log, and any other C:\Users\<name>\... paths.

    python scripts\sanitize_dwxmz.py file1.dwxmz [file2.dwxmz ...]
    python scripts\sanitize_dwxmz.py examples          # every .dwxmz in a folder

Files are rewritten in place; the simulation itself is untouched.
"""

import os
import re
import sys
import tempfile
import zipfile

_BLANK_TAGS = ("FilePath", "SimulationAuthor", "BackupFileName")
_USER_PATH = re.compile(r"[A-Za-z]:\\Users\\[^\\<>\"]+\\[^<>\"]*")


def _clean_xml(text: str) -> str:
    for tag in _BLANK_TAGS:
        text = re.sub(rf"<{tag}>.*?</{tag}>", f"<{tag}></{tag}>", text, flags=re.S)
    text = re.sub(r"<MessagesLog>.*?</MessagesLog>", "<MessagesLog />", text, flags=re.S)
    return _USER_PATH.sub("", text)


def sanitize(path: str) -> bool:
    """Rewrite one .dwxmz in place. Returns True if anything changed."""
    with zipfile.ZipFile(path) as src:
        entries = [(info, src.read(info.filename)) for info in src.infolist()]
    changed = False
    cleaned = []
    for info, data in entries:
        if info.filename.lower().endswith(".xml"):
            text = data.decode("utf-8")
            new = _clean_xml(text)
            if new != text:
                changed = True
                data = new.encode("utf-8")
        cleaned.append((info, data))
    if not changed:
        return False
    fd, tmp = tempfile.mkstemp(suffix=".dwxmz", dir=os.path.dirname(os.path.abspath(path)))
    os.close(fd)
    try:
        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as dst:
            for info, data in cleaned:
                dst.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return True


def main(args):
    if not args:
        print(__doc__)
        return 1
    files = []
    for a in args:
        if os.path.isdir(a):
            files += [os.path.join(a, f) for f in sorted(os.listdir(a)) if f.lower().endswith(".dwxmz")]
        else:
            files.append(a)
    for f in files:
        print(f"{'cleaned ' if sanitize(f) else 'already clean'}  {f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
