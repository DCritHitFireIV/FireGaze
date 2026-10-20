"""Read-only full-PE graph audit of proposed internal timeline boundaries.

No game process, native execution, writes to installed files, or hook backend.
RVA constants identify reviewed evidence only; they are not runtime addresses.
"""
from collections import deque
from pathlib import Path
import argparse
import hashlib
import json
import struct

import capstone
from capstone.x86 import X86_OP_IMM
import pefile


EXPECTED = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9"


class AuditError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise AuditError(message)


class Image:
    def __init__(self, raw):
        self.raw = raw
        self.pe = pefile.PE(data=raw, fast_load=True)
        self.pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXCEPTION"]])
        self.ranges = [(entry.struct.BeginAddress, entry.struct.EndAddress, entry) for entry in self.pe.DIRECTORY_ENTRY_EXCEPTION]
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True

    def at(self, rva, size):
        for section in self.pe.sections:
            if (section.Characteristics & 0x20000000 and section.VirtualAddress <= rva
                    and rva + size <= section.VirtualAddress + section.SizeOfRawData):
                offset = section.PointerToRawData + rva - section.VirtualAddress
                return self.raw[offset:offset + size]
        raise AuditError(f"outside executable section: {rva:#x}")

    def expect(self, rva, hex_bytes, label):
        expected = bytes.fromhex(hex_bytes)
        require(self.at(rva, len(expected)) == expected, f"{label}: changed at {rva:#x}")

    def rel(self, rva, opcode):
        require(self.at(rva, 1) == bytes([opcode]), f"wrong branch opcode at {rva:#x}")
        return rva + 5 + struct.unpack("<i", self.at(rva + 1, 4))[0]

    def containing(self, rva):
        entries = [entry for begin, end, entry in self.ranges if begin <= rva < end]
        require(len(entries) == 1, f"ambiguous/missing pdata at {rva:#x}")
        return entries[0]

    def graph(self, root):
        """Walk actual branch CFG including separated function ranges, not calls.

        Any direct branch outside the reviewed nearby function body is rejected,
        rather than silently including a guessed external tail-call contract.
        """
        require(self.containing(root).struct.BeginAddress == root, f"not a pdata function start: {root:#x}")
        queue = deque([root])
        seen = {}
        allowed = (root, {0x606B10: 0x606F4A, 0x606FD0: 0x607075, 0x8C0000: 0x8C00DA}[root])
        while queue:
            start = queue.popleft()
            cursor = start
            while cursor not in seen:
                require(allowed[0] <= cursor < allowed[1], f"unreviewed outgoing CFG target: {cursor:#x}")
                instruction = next(self.md.disasm(self.at(cursor, min(15, allowed[1] - cursor)), cursor, count=1), None)
                require(instruction is not None, f"undecodable reachable byte at {cursor:#x}")
                seen[cursor] = instruction
                nxt = cursor + instruction.size
                if instruction.group(capstone.CS_GRP_RET):
                    break
                if instruction.group(capstone.CS_GRP_JUMP):
                    require(len(instruction.operands) == 1 and instruction.operands[0].type == X86_OP_IMM,
                            f"unreviewed indirect jump at {cursor:#x}")
                    target = instruction.operands[0].imm
                    queue.append(target)
                    if instruction.mnemonic == "jmp":
                        break
                cursor = nxt
        entries = {self.containing(rva).struct.BeginAddress: self.containing(rva) for rva in seen}
        return {
            "root": hex(root), "reachableInstructions": len(seen),
            "pdataRanges": [{"begin": hex(entry.struct.BeginAddress), "end": hex(entry.struct.EndAddress),
                            "unwindFlags": entry.unwindinfo.Flags} for entry in entries.values()],
            "returns": [hex(rva) for rva, ins in seen.items() if ins.group(capstone.CS_GRP_RET)],
            "calls": [{"at": hex(rva), "target": ins.op_str} for rva, ins in seen.items() if ins.group(capstone.CS_GRP_CALL)],
            "instructions": [f"{rva:08X} {ins.bytes.hex():24} {ins.mnemonic} {ins.op_str}" for rva, ins in sorted(seen.items())],
            "reviewedSpanSha256": hashlib.sha256(self.at(*[allowed[0], allowed[1] - allowed[0]])).hexdigest().upper(),
        }


def audit(raw, inventory, data):
    require(hashlib.sha256(raw).hexdigest().upper() == EXPECTED, "full PE identity changed")
    image = Image(raw)
    require(inventory["UnresolvedAddresses"] == 0 and inventory["AddressFields"] == 2570,
            "complete SDK address inventory was not resolved")
    for rva in (0x606B10, 0x606FD0):
        require(not any(entry["Rva"] is not None and int(entry["Rva"], 16) == rva for entry in inventory["Addresses"]),
                f"SDK address now registers internal entry {rva:#x}")
        require(not any(entry["Rva"] is not None and int(entry["Rva"], 16) == rva for entry in inventory["VerifierEntries"]),
                f"official verifier now registers internal entry {rva:#x}")
    # Wrapper root and register routing establish the real four-argument contract.
    image.expect(0x606ABF, "0F B7 DA 48 8B F1 8B CB 49 8B F8", "PlayTimeline saves id/sequence/original parameter")
    require(image.rel(0x606ACA, 0xE8) == 0x836D10, "row lookup changed")
    image.expect(0x606AE4, "4C 8B CF 4C 8B C0 0F B7 D3 48 8B CE", "resolved row goes to R8; optional parameter goes to R9")
    require(image.rel(0x606AFF, 0xE9) == 0x606B10, "resolved timeline tail target changed")
    image.expect(0x606B22, "48 83 B9 C8 01 00 00 00 4D 8B F0 0F B7 F2 48 8B D9", "leaf reads parent and preserves borrowed row")
    image.expect(0x606B35, "32 C0 48 81 C4 08 01 00 00 41 5E 5E 5B 5D C3", "missing native state returns AL zero")
    require(image.rel(0x606EE3, 0xE8) == 0x6089B0, "resolved timeline scheduler target changed")
    image.expect(0x608C80, "0F B6 F0 84 C0 0F 84 03 01 00 00", "scheduler consumes actual native execution AL")
    image.expect(0x608D8A, "40 0F B6 C6 48 8B 4D EF 48 33 CC E8", "scheduler propagates AL result")
    # Public action wrapper applies actor-specific replacement to BOTH arguments.
    require(image.rel(0x8BE42E, 0xE8) == 0x8C0000 and image.rel(0x8BE43C, 0xE8) == 0x8C0000,
            "intro and loop actor adjustments changed")
    image.expect(0x8BE441, "48 8D 4E 10 4C 8B CD 44 0F B7 C0 0F B7 D7", "action sequencer+0x10 and adjusted intro/loop/fourth arg")
    require(image.rel(0x8BE463, 0xE9) == 0x606FD0, "resolved action tail target changed")
    image.expect(0x606FE1, "66 89 91 50 01 00 00 8B C3 66 44 89 81 52 01 00 00 49 8B F1", "resolved action stores adjusted intro/loop and preserves R9")
    require(image.rel(0x607060, 0xE8) == 0x608530, "action executor changed")
    image.expect(0x607065, "48 8B 5C 24 40 48 8B 74 24 48 48 83 C4 30 5F C3", "action result AL epilogue")
    require(image.rel(0x6180FB, 0xE8) == 0x8BE410, "native emote caller changed")
    image.expect(0x618100, "0F B6 F0 84 C0 74 2A", "native emote caller consumes AL before posture transition")
    # The 'KillUpper' generated name is a schema label, not native semantics. In
    # this exact PE, row+6 indexes ActionTimelineReplace (sheet608), whose two
    # ushort columns replace an emote timeline for swimming/diving actors.
    image.expect(0x8C000F, "B8 E9 FD 00 00 0F B7 DA 48 8B F9 66 3B D0", "65001 sentinel handling")
    image.expect(0x8C001F, "0F B7 81 EA 02 00 00 BB 22 00 00 00", "sentinel fallback uses current override or idle34")
    image.expect(0x8C0055, "0F B6 B1 28 06 00 00 48 81 C1 F0 01 00 00", "actor flags and MoveController offset")
    require(image.rel(0x8C0063, 0xE8) == 0x181CA80, "actor IsDiving target changed")
    image.expect(0x8C0082, "0F B7 48 06 85 C9 74 3D", "timeline row+6 replacement index")
    require(image.rel(0x8C008A, 0xE8) == 0x836D80, "replacement table getter changed")
    image.expect(0x836D8E, "BA 60 02 00 00", "replacement getter sheet608")
    image.expect(0x8C0094, "0F B7 08 66 85 C9 74 2B 0F B7 50 02 66 85 D2", "both replacement columns nonzero")
    require(any(line.strip() == "ActionTimelineReplace,608" for line in data["RootMatches"]), "disk sqpack replacement table ID mismatch")
    timelines = {row["RowId"]: row for row in data["Timelines"]}
    replacements = {row["RowId"]: row for row in data["Replacements"]}
    adjusted = []
    for emote in data["Emotes"]:
        for timeline in emote["TimelineIds"]:
            row = timelines.get(timeline)
            if row and row["KillUpper"]:
                replacement = replacements.get(row["KillUpper"])
                require(replacement is not None, "emote replacement row missing")
                adjusted.append({"emote": emote["RowId"], "timeline": timeline, "replacement": replacement})
    require(any(row["emote"] == 143 and row["timeline"] == 4799 and row["replacement"]["Old"] == 5796 and row["replacement"]["New"] == 5798 for row in adjusted),
            "real diving/swimming sample for play-dead not found")
    graphs = [image.graph(root) for root in (0x606B10, 0x606FD0, 0x8C0000)]
    return {"clientSha256": EXPECTED, "graphs": graphs, "adjustedEmoteRows": adjusted,
            "ownership": "sequence/row/parameter are borrowed call-scoped pointers; pass original values once; never cache, write, or free row/parameter",
            "acceptedIdentity": "AL true plus unchanged source pointer/id/generation/local-binding/map/revision; only unique known timeline identity may update last-action; ambiguous known identity may end preview without inventing an emote",
            "sentinelPolicy": "0/1/3/34 and 65001 are not direct emote identity; never use stale EmoteController.EmoteId to fill missing identity",
            "limitation": "Full-PE/actual-SDK offline evidence only; no native Hook or game acceptance has been exercised."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    parser.add_argument("inventory", type=Path)
    parser.add_argument("data", type=Path)
    parser.add_argument("out", type=Path)
    args = parser.parse_args()
    document = audit(args.exe.read_bytes(), json.loads(args.inventory.read_text()), json.loads(args.data.read_text()))
    args.out.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"graphs": [{k: v for k, v in graph.items() if k != "instructions"} for graph in document["graphs"]],
                      "adjustedEmoteRows": document["adjustedEmoteRows"]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
