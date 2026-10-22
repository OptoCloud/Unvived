"""Disassemble a range of a PE (x86 or x64), resolving import calls.

    py -I scripts/disasm.py <pe> <va hex> [length hex]
    py -I scripts/disasm.py <pe> <va hex> --until <substring>

Length defaults to 0x80. With --until, stops after the first instruction
whose text (including the resolved import name) contains <substring>.

Needs: pip install pefile capstone
"""
import sys

import capstone
import pefile


def main() -> None:
    args = sys.argv[1:]
    until = None
    if "--until" in args:
        i = args.index("--until")
        until = args[i + 1]
        del args[i:i + 2]
    pe = pefile.PE(args[0])
    base = pe.OPTIONAL_HEADER.ImageBase
    va = int(args[1], 16)
    length = int(args[2], 16) if len(args) > 2 else (0x800 if until else 0x80)
    data = pe.get_data(va - base, length)

    imports = {}
    for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
        for imp in entry.imports:
            name = imp.name.decode() if imp.name else f"#{imp.ordinal}"
            imports[imp.address] = f"{entry.dll.decode()}!{name}"

    x64 = pe.FILE_HEADER.Machine == 0x8664
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64 if x64 else capstone.CS_MODE_32)
    md.detail = True
    for ins in md.disasm(data, va):
        text = f"{ins.mnemonic} {ins.op_str}"
        note = ""
        for op in ins.operands if ins.id else []:
            if op.type == capstone.x86.X86_OP_MEM:
                target = None
                if x64 and op.mem.base == capstone.x86.X86_REG_RIP:
                    target = ins.address + ins.size + op.mem.disp
                elif not x64 and op.mem.base == 0:
                    target = op.mem.disp
                if target in imports:
                    note = f"  ; {imports[target]}"
        print(f"{ins.address:016x}  {text}{note}")
        if until and until in text + note:
            break


if __name__ == "__main__":
    main()
