"""Print a PE file's machine type, imported DLLs and exported names (stdlib only).

Usage: py -I scripts/pe_info.py <file.dll|file.exe> [--json]
"""
import json
import struct
import sys

MACHINES = {0x14C: "x86", 0x8664: "x64", 0xAA64: "arm64"}


def _sections(data, pe):
    count = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    base = pe + 24 + opt_size
    out = []
    for i in range(count):
        off = base + i * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        out.append((va, max(vsize, rsize), raw))
    return out


def _rva_to_off(sections, rva):
    for va, size, raw in sections:
        if va <= rva < va + size:
            return rva - va + raw
    return None


def _cstr(data, off):
    end = data.index(b"\0", off)
    return data[off:end].decode("ascii", "replace")


def pe_info(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"MZ":
        raise ValueError("not a PE file")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("bad PE signature")
    machine = struct.unpack_from("<H", data, pe + 4)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dirs = opt + (112 if magic == 0x20B else 96)
    clr_rva = struct.unpack_from("<I", data, dirs + 14 * 8)[0]
    sections = _sections(data, pe)

    exports = []
    exp_rva = struct.unpack_from("<I", data, dirs)[0]
    exp_off = _rva_to_off(sections, exp_rva) if exp_rva else None
    if exp_off is not None:
        n_names = struct.unpack_from("<I", data, exp_off + 24)[0]
        names_rva = struct.unpack_from("<I", data, exp_off + 32)[0]
        names_off = _rva_to_off(sections, names_rva)
        for i in range(n_names):
            name_rva = struct.unpack_from("<I", data, names_off + 4 * i)[0]
            exports.append(_cstr(data, _rva_to_off(sections, name_rva)))

    imports = []
    imp_rva = struct.unpack_from("<I", data, dirs + 8)[0]
    imp_off = _rva_to_off(sections, imp_rva) if imp_rva else None
    while imp_off is not None:
        name_rva = struct.unpack_from("<I", data, imp_off + 12)[0]
        if name_rva == 0:
            break
        imports.append(_cstr(data, _rva_to_off(sections, name_rva)))
        imp_off += 20

    return {
        "machine": MACHINES.get(machine, hex(machine)),
        "dotnet": clr_rva != 0,
        "imports": imports,
        "exports": exports,
    }


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    info = pe_info(sys.argv[1])
    if "--json" in sys.argv:
        print(json.dumps(info))
        return
    print(f"machine: {info['machine']}  .NET: {info['dotnet']}")
    print("imports:", ", ".join(info["imports"]) or "-")
    print(f"exports ({len(info['exports'])}):")
    for name in info["exports"]:
        print("  " + name)


if __name__ == "__main__":
    main()
