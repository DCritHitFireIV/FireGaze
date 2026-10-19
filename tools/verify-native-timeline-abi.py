"""Read-only CN timeline ABI audit, separate from SDK metadata declarations.

No game/process access, patching, hook installation, or native invocation. The
selected executable must be the previously disassembled build. Metadata is
exported from the compiled NativeCompatibilityTests --timeline-abi-metadata.
Instruction offsets below are audit evidence, never runtime hook addresses.
"""
from pathlib import Path
import argparse
import copy
import hashlib
import json
import re
import struct

AUDITED_CLIENT = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9"


class AuditFailure(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise AuditFailure(message)


class Image:
    def __init__(self, raw):
        self.raw = raw
        pe = struct.unpack_from("<I", raw, 0x3C)[0]
        require(raw[pe:pe + 4] == b"PE\0\0", "not a PE image")
        count = struct.unpack_from("<H", raw, pe + 6)[0]
        optional = struct.unpack_from("<H", raw, pe + 20)[0]
        self.sections = []
        for index in range(count):
            start = pe + 24 + optional + index * 40
            rva, size, offset = struct.unpack_from("<III", raw, start + 12)
            flags = struct.unpack_from("<I", raw, start + 36)[0]
            if flags & 0x20000000:
                self.sections.append((rva, size, offset))

    def offset(self, rva, size):
        for base, count, offset in self.sections:
            if base <= rva and rva + size <= base + count:
                return offset + rva - base
        raise AuditFailure(f"RVA {rva:#x} is outside an executable section")

    def at(self, rva, size):
        start = self.offset(rva, size)
        return self.raw[start:start + size]

    def expect(self, rva, expected, label):
        require(self.at(rva, len(expected)) == expected, f"{label}: instruction evidence changed at {rva:#x}")

    def resolve(self, metadata):
        require(metadata["RelativeFollowOffsets"] == [1], "SDK relative-follow contract changed")
        expression = b"".join(b"." if token == "??" else re.escape(bytes([int(token, 16)]))
                              for token in metadata["Signature"].split())
        matches = [(base + match.start()) for base, size, offset in self.sections
                   for match in re.finditer(b"(?=(" + expression + b"))", self.raw[offset:offset + size], re.S)]
        require(len(matches) == 1, f"{metadata['Name']}: expected one SDK signature match, got {len(matches)}")
        return self.relative(matches[0], 0xE8, "SDK call signature")

    def relative(self, site, opcode, label):
        self.expect(site, bytes([opcode]), label)
        target = site + 5 + struct.unpack("<i", self.at(site + 1, 4))[0]
        self.offset(target, 1)
        return target


def audit_routes(image, metadata):
    functions = metadata["Functions"]
    require(len(functions) == 2, "expected exactly the two timeline boundaries")
    for function in functions:
        require(function["SdkReturn"] in ("Void", "Boolean"), "SDK timeline return contract requires a new audit")
        require(function["ArgumentsMatch"] is True, "production arguments differ from current SDK")
        require(function["ProductionReturn"] == "Boolean" and function["ProductionMarshalAs"] == "U1",
                "production delegate would lose the audited native AL return")
    by_name = {function["Name"].rsplit(".", 1)[-1]: function for function in functions}
    timeline = image.resolve(by_name["PlayTimeline"])
    action = image.resolve(by_name["PlayActionTimeline"])
    require(timeline == 0x606AB0 and action == 0x8BE410, "SDK resolves outside the reviewed functions")

    # The lookup-null branch retains RAX=0 through a restore-only epilogue.
    image.expect(timeline + 0x1F, bytes.fromhex("48 85 C0 75 10 48 8B 5C 24 30 48 8B 74 24 38 48 83 C4 20 5F C3"),
                 "timeline missing-row false return")
    timeline_leaf = image.relative(timeline + 0x4F, 0xE9, "timeline tail return")
    require(timeline_leaf == 0x606B10, "timeline tail target changed")
    image.expect(timeline_leaf + 0x25, bytes.fromhex("32 C0 48 81 C4 08 01 00 00 41 5E 5E 5B 5D C3"),
                 "timeline missing-state false return")
    image.expect(timeline_leaf + 0x23F, bytes.fromhex("32 C0 E9 92 01 00 00"), "timeline rejected-priority false return")
    scheduler = image.relative(timeline_leaf + 0x3D3, 0xE8, "timeline scheduler execution")
    require(scheduler == 0x6089B0, "timeline scheduler target changed")
    # The scheduler copies the actual play result to SIL, branches on AL, and
    # restores SIL to EAX before its final security-cookie/restore epilogue.
    image.expect(scheduler + 0x2D0, bytes.fromhex("0F B6 F0 84 C0 0F 84 03 01 00 00"), "scheduler accepted result")
    image.expect(scheduler + 0x3DA, bytes.fromhex("40 0F B6 C6 48 8B 4D EF 48 33 CC E8"), "scheduler return propagation")
    cookie_check = image.relative(scheduler + 0x3E5, 0xE8, "scheduler security cookie")
    # Unlike an arbitrary ABI call, this reviewed security-cookie success path
    # preserves RAX. Its failure path never returns normally.
    image.expect(cookie_check, bytes.fromhex("48 3B 0D 71 C5 B3 00 75 10 48 C1 C1 10 66 F7 C1 FF FF 75 01 C3"),
                 "security-cookie success preserves RAX")
    image.expect(scheduler + 0x3EA, bytes.fromhex("0F 28 B4 24 C0 00 00 00 48 81 C4 D8 00 00 00 41 5F 41 5E 41 5D 41 5C 5F 5E 5B 5D C3"),
                 "scheduler return epilogue")

    action_leaf = image.relative(action + 0x53, 0xE9, "action wrapper tail return")
    require(action_leaf == 0x606FD0, "action wrapper tail target changed")
    execution = image.relative(action_leaf + 0x90, 0xE8, "action execution leaf")
    require(execution == 0x608530, "action execution target changed")
    image.expect(action_leaf + 0x95, bytes.fromhex("48 8B 5C 24 40 48 8B 74 24 48 48 83 C4 30 5F C3"),
                 "action AL-preserving epilogue")
    image.expect(execution + 0x2B, bytes.fromhex("32 C0 48 81 C4 E8 00 00 00 41 5F 41 5C 5B 5D C3"),
                 "action missing-state false return")
    image.expect(execution + 0x419, bytes.fromhex("B0 01 44 0F 28 8C 24 90 00 00 00"), "action explicit success return")
    consumer = 0x6180FB
    require(image.relative(consumer, 0xE8, "native emote caller") == action, "native emote call target changed")
    image.expect(consumer + 5, bytes.fromhex("0F B6 F0 84 C0 74 2A"), "native emote caller consumes AL")
    return {"playTimeline": hex(timeline), "timelineLeaf": hex(timeline_leaf), "scheduler": hex(scheduler),
            "playActionTimeline": hex(action), "actionLeaf": hex(action_leaf), "actionExecution": hex(execution),
            "nativeEmoteConsumer": hex(consumer), "verified": "native AL execution result and caller; exact SDK arguments; production U1"}


def audit(raw, metadata):
    digest = hashlib.sha256(raw).hexdigest().upper()
    require(digest == AUDITED_CLIENT, "client SHA256 changed: obtain a new disassembly audit before accepting its ABI")
    result = audit_routes(Image(raw), metadata)
    result.update({"clientSha256": digest, "clientStructsSha256": metadata["ClientStructsSha256"],
                   "clientStructsVersion": metadata["ClientStructsVersion"],
                   "sdkDeclaresVoid": [f["Name"] for f in metadata["Functions"] if f["SdkReturn"] == "Void"],
                   "limitation": "Dalamud may warn about Boolean versus SDK Void; this audit does not suppress verification"})
    return result


def mutation_checks(raw, metadata):
    # Every counterexample must fail a specific instruction/metadata guard. The
    # normal CLI additionally requires the exact audited client SHA256 above.
    passed = 0
    image = Image(raw)
    for rva in (0x606AD0, 0x606AFF, 0x606B35, 0x606D4F, 0x606EE3, 0x608C80, 0x608D8A, 0x608D9A, 0x1D9E0E0,
                0x8BE463, 0x607060, 0x607065, 0x60855B, 0x608949, 0x618100):
        changed = bytearray(raw)
        changed[image.offset(rva, 1)] ^= 0x01
        try:
            audit_routes(Image(changed), metadata)
        except AuditFailure:
            passed += 1
        else:
            raise AuditFailure(f"mutation at {rva:#x} was not rejected")
    for key, value in (("ArgumentsMatch", False), ("ProductionReturn", "Void"),
                       ("ProductionMarshalAs", "Bool"), ("RelativeFollowOffsets", [2]), ("SdkReturn", "IntPtr")):
        changed = copy.deepcopy(metadata)
        changed["Functions"][0][key] = value
        try:
            audit_routes(image, changed)
        except AuditFailure:
            passed += 1
        else:
            raise AuditFailure(f"metadata mutation {key} was not rejected")
    return passed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("metadata", type=Path)
    parser.add_argument("--self-test", action="store_true", help="also reject 20 altered instruction/metadata counterexamples in memory")
    args = parser.parse_args()
    raw = args.executable.read_bytes()
    metadata = json.loads(args.metadata.read_text(encoding="utf-8-sig"))
    result = audit(raw, metadata)
    if args.self_test:
        result["rejectedMutations"] = mutation_checks(raw, metadata)
    print(json.dumps(result, ensure_ascii=False, indent=2))
