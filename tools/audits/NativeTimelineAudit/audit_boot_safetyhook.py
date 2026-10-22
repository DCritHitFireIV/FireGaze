"""Read-only audit of the current Dalamud.Boot SafetyHook creation chain.

The audit does not load or execute the DLL. It checks the six exported helper
RVAs, the disabled-create forwarding chain, and the flag branch used by the
current NativeTimelineBackendContract. The output is evidence for the exact
Boot image pin; it is not a native hook installation test.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path

import capstone
import pefile


EXPECTED_SHA256 = "42522951B76905C3664A355EE773F2E42CD47F2C1B187766AE8F7BD456C4B367"
EXPECTED_EXPORTS = {
    "BootSafetyHookInlineCreate": 0x73690,
    "BootSafetyHookInlineDestroy": 0x73800,
    "BootSafetyHookInlineDisable": 0x73860,
    "BootSafetyHookInlineEnable": 0x73930,
    "BootSafetyHookInlineGetTrampoline": 0x73A00,
    "BootSafetyHookInlineIsEnabled": 0x73A10,
}
CREATION_SPANS = [(0x73690, 0x162), (0x1F920, 0x78), (0x1F770, 0x1B0)]


class AuditError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AuditError(message)


class Image:
    def __init__(self, raw: bytes):
        self.raw = raw
        self.pe = pefile.PE(data=raw, fast_load=False)
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True

    def at(self, rva: int, size: int) -> bytes:
        for section in self.pe.sections:
            if (section.Characteristics & 0x20000000 and section.VirtualAddress <= rva
                    and rva + size <= section.VirtualAddress + section.SizeOfRawData):
                offset = section.PointerToRawData + rva - section.VirtualAddress
                return self.raw[offset:offset + size]
        raise AuditError(f"RVA outside executable section: {rva:#x}")

    def rel(self, rva: int, opcode: int) -> int:
        require(self.at(rva, 1) == bytes([opcode]), f"wrong branch opcode at {rva:#x}")
        return rva + 5 + struct.unpack("<i", self.at(rva + 1, 4))[0]

    def short_rel(self, rva: int, opcode: int) -> int:
        require(self.at(rva, 1) == bytes([opcode]), f"wrong short branch opcode at {rva:#x}")
        return rva + 2 + struct.unpack("<b", self.at(rva + 1, 1))[0]

    def disassemble(self, rva: int, size: int) -> list[str]:
        return [f"{ins.address:06X} {ins.bytes.hex()} {ins.mnemonic} {ins.op_str}".rstrip()
                for ins in self.md.disasm(self.at(rva, size), rva)]


def audit(path: Path) -> dict:
    raw = path.read_bytes()
    sha = hashlib.sha256(raw).hexdigest().upper()
    require(sha == EXPECTED_SHA256, "current Dalamud.Boot identity changed")
    image = Image(raw)
    exports = {}
    for symbol in image.pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if symbol.name:
            name = symbol.name.decode("ascii", errors="replace")
            if name in EXPECTED_EXPORTS:
                exports[name] = symbol.address
    require(exports == EXPECTED_EXPORTS, "SafetyHook export RVA set changed")

    # The public create export places StartDisabled=1 in R9D before calling
    # the shared forwarding helper. The helper preserves that flag through the
    # underlying create routine and returns its owner pointer.
    require(image.at(0x736EC, 6) == bytes.fromhex("41b901000000"),
            "create export no longer passes StartDisabled=1 in R9D")
    require(image.rel(0x73700, 0xE8) == 0x1F920,
            "create export no longer calls the reviewed forwarding helper")
    require(image.at(0x1F929, 3) == bytes.fromhex("418bd9"),
            "forwarding helper no longer captures the flag")
    require(image.at(0x1F940, 4) == bytes.fromhex("895c2420"),
            "forwarding helper no longer forwards the flag to the create call")
    require(image.rel(0x1F950, 0xE8) == 0x1F770,
            "forwarding helper no longer calls the reviewed disabled factory")

    # The factory's bit-1 branch skips the native enable operation. This is
    # the only admission path used by the production staged Hook preparation.
    require(image.at(0x1F859, 4) == bytes.fromhex("f6456001"),
            "disabled factory flag test changed")
    require(image.short_rel(0x1F85D, 0x75) == 0x1F875,
            "disabled factory no longer bypasses native enable for bit 1")

    spans = []
    for rva, size in CREATION_SPANS:
        code = image.at(rva, size)
        spans.append({"rva": hex(rva), "size": size,
                      "sha256": hashlib.sha256(code).hexdigest().upper()})
    return {
        "schema": 1,
        "bootSha256": sha,
        "imageSize": image.pe.OPTIONAL_HEADER.SizeOfImage,
        "exports": {name: hex(rva) for name, rva in exports.items()},
        "creationSpans": spans,
        "startDisabled": True,
        "nativeExecution": False,
        "disassembly": {
            "create": image.disassemble(0x73690, 0x162),
            "forward": image.disassemble(0x1F920, 0x78),
            "factory": image.disassemble(0x1F770, 0x1B0),
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("boot", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = audit(args.boot)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "bootSha256": result["bootSha256"],
        "exports": result["exports"],
        "creationSpans": result["creationSpans"],
        "startDisabled": result["startDisabled"],
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
