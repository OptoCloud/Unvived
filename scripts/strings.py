"""Print printable ASCII and UTF-16LE strings in a file (like `strings`).

Usage: py -I scripts/strings.py [-n MIN] <file>...
Each line is `<offset hex> <a|u> <text>`.
"""
import re
import sys


def main():
    args = sys.argv[1:]
    n = 5
    if args[:1] == ["-n"]:
        n, args = int(args[1]), args[2:]
    ascii_re = re.compile(rb"[\x20-\x7e]{%d,}" % n)
    utf16_re = re.compile(rb"(?:[\x20-\x7e]\x00){%d,}" % n)
    for path in args:
        data = open(path, "rb").read()
        hits = [(m.start(), "a", m.group().decode()) for m in ascii_re.finditer(data)]
        hits += [(m.start(), "u", m.group().decode("utf-16le")) for m in utf16_re.finditer(data)]
        for off, kind, text in sorted(hits):
            print(f"{off:08x} {kind} {text}")


if __name__ == "__main__":
    main()
