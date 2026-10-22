"""Pinned, disk-only count/resize/render-target ingress audit; no process access."""
from argparse import ArgumentParser
from bisect import bisect_right
from functools import cache
from pathlib import Path
import hashlib
import json
import re
import struct

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

parser = ArgumentParser(description=__doc__)
parser.add_argument("--exe", required=True, type=Path)
parser.add_argument("--sdk", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
exe_hash = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9"
sdk_hash = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778"
data = args.exe.read_bytes()
assert hashlib.sha256(data).hexdigest().upper() == exe_hash, "EXE drift"
assert hashlib.sha256(args.sdk.read_bytes()).hexdigest().upper() == sdk_hash, "SDK drift"
pe = pefile.PE(data=data)
base = pe.OPTIONAL_HEADER.ImageBase
md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
ranges = sorted((x.struct.BeginAddress, x.struct.EndAddress, x.struct.UnwindData)
                for x in pe.DIRECTORY_ENTRY_EXCEPTION)
starts = [x[0] for x in ranges]


def owner(rva):
    index = bisect_right(starts, rva) - 1
    return ranges[index] if index >= 0 and ranges[index][0] <= rva < ranges[index][1] else None


@cache
def root(item):
    if item is None:
        return None
    seen = set()
    while item not in seen:
        seen.add(item)
        header = pe.get_data(item[2], 4)
        if not (header[0] >> 3) & 4:
            return item
        offset = 4 + ((header[2] + 1) & ~1) * 2
        item = struct.unpack("<III", pe.get_data(item[2] + offset, 12))
    raise AssertionError("unwind cycle")


groups = {}
for item in ranges:
    groups.setdefault(root(item), []).append(item)


@cache
def instructions(item):
    assert item is not None, "unbounded leaf must have a separate explicit CFG contract"
    return tuple(instruction for start, end, _ in groups[item]
                 for instruction in md.disasm(pe.get_data(start, end-start), base+start))


def dump(rva, name):
    item = root(owner(rva))
    lines = []
    if item is None:
        sequence = []
        for instruction in md.disasm(pe.get_data(rva, 256), base+rva):
            if instruction.mnemonic == "int3":
                break
            sequence.append(instruction)
        assert sequence and sequence[-1].mnemonic in ("ret", "jmp"), (name, "leaf terminator")
        end = sequence[-1].address + sequence[-1].size
        assert pe.get_data(end-base, 1) == b"\xcc"
        assert all(base+rva <= i.operands[0].imm < end for i in sequence[:-1] if i.mnemonic.startswith("j"))
        assert sequence[-1].mnemonic != "jmp" or sequence[-1].operands[0].type == 2
        assert not any(i.mnemonic == "call" for i in sequence)
        lines = ["; complete leaf CFG; RET or explicit final tail JMP followed by INT3; all other jumps internal"]
        lines += [f"{i.address-base:#010x} {i.mnemonic:8} {i.op_str}" for i in sequence]
        (out / (name + ".asm")).write_text("\n".join(lines) + "\n")
        return {"rva": hex(rva), "root": None, "endRva": hex(end-base), "leafCFGBounded": True}
    for start, end, unwind in groups[item]:
        lines.append(f"; fragment {start:#x}..{end:#x}; unwind {unwind:#x}; root {item[0]:#x}")
        lines += [f"{i.address-base:#010x} {i.mnemonic:8} {i.op_str}"
                  for i in md.disasm(pe.get_data(start, end-start), base+start)]
    (out / (name + ".asm")).write_text("\n".join(lines) + "\n")
    return {"rva": hex(rva), "root": hex(item[0]),
            "fragments": [[hex(x) for x in piece] for piece in groups[item]]}


def unique_pattern(pattern, offset=0, relative=False):
    expression = b"".join(b"." if value == "??" else re.escape(bytes([int(value, 16)]))
                          for value in pattern.split())
    matches = list(re.finditer(expression, data, re.DOTALL))
    assert len(matches) == 1, (pattern, len(matches))
    rva = pe.get_rva_from_offset(matches[0].start())
    target = rva + offset + 4 + struct.unpack("<i", pe.get_data(rva+offset, 4))[0] if relative else rva
    return target, rva


# Exact strings were read from the pinned SDK metadata. Whole-SDK identity is required above.
patterns = {
    "Callback.Execute": ("E8 ?? ?? ?? ?? 48 8B 5B ?? 48 8B 4B", 1, True),
    "Callback.Add": ("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 48 8B F1 49 8B E8 48 83 C1", 0, False),
    "Callback.Remove": ("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 8B FA 48 83 C1 ?? FF 15", 0, False),
    "RenderTarget.GetCharaViewTexture": ("E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B D0 FF D3", 1, True),
    "Device.Instance": ("48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 80 7B 08 00", 3, True),
}
targets = {}
for name, (pattern, offset, relative) in patterns.items():
    rva, match = unique_pattern(pattern, offset, relative)
    targets[name] = {"rva": hex(rva), "patternRva": hex(match), "pattern": pattern, "callers": []}
    if name != "Device.Instance":
        targets[name].update(dump(rva, name))
for name, rva in {"Device.ResizeDestroyCandidate": 0x222970, "Device.ResizeCreateCandidate": 0x222870,
                  "RenderTarget.Ctor": 0x2D4D20, "RenderTarget.Initialize": 0x2D5120,
                  "Device.Register28": 0x226410, "Device.Register30": 0x226480,
                  "Device.Register40": 0x2264F0, "Device.Register48": 0x226560,
                  "RenderTarget.Callback28": 0x2DB600, "RenderTarget.Callback30": 0x2DB630,
                  "RenderTarget.Callback40": 0x2DCCB0, "RenderTarget.Callback48": 0x2DCCD0,
                  "RenderTarget.ResizeCleanup": 0x2DBB90, "RenderTarget.ResizeRecreate": 0x2DBFE0}.items():
    targets[name] = dict(dump(rva, name), callers=[], pointerRefs=[])

by_target = {int(value["rva"], 16): name for name, value in targets.items() if name != "Device.Instance"}
known_leaves = [(int(value["rva"], 16), int(value["endRva"], 16)) for value in targets.values()
                if value.get("leafCFGBounded")]
rt_global = 0x28FCF08
rt_global_refs = []
for section in pe.sections:
    if not section.Characteristics & 0x20000000:
        raw = section.get_data()
        for target, name in by_target.items():
            needle = struct.pack("<Q", base+target)
            cursor = 0
            while True:
                offset = raw.find(needle, cursor)
                if offset < 0:
                    break
                cursor = offset+1
                targets[name].setdefault("pointerRefs", []).append(hex(section.VirtualAddress+offset))
        continue
    raw = section.get_data()
    for prefix in [bytes.fromhex(value) for value in ["48 89 05", "48 89 0D", "48 89 1D", "48 8B 05", "48 8B 0D", "48 8B 1D", "48 8B 35"]]:
        cursor = 0
        while True:
            offset = raw.find(prefix, cursor)
            if offset < 0:
                break
            cursor = offset+1
            if offset+7 > len(raw):
                continue
            site = section.VirtualAddress+offset
            destination = site+7+struct.unpack_from("<i", raw, offset+3)[0]
            if destination != rt_global:
                continue
            item = root(owner(site))
            if item is None:
                continue
            instruction = next((i for i in instructions(item) if i.address == base+site
                                and i.mnemonic == "mov"), None)
            if instruction is not None:
                rt_global_refs.append({"site": hex(site), "root": hex(item[0]), "instruction": instruction.mnemonic+" "+instruction.op_str})
                dump(item[0], f"RTGlobal.{item[0]:X}")
    for opcode, mnemonic in [(b"\xe8", "call"), (b"\xe9", "jmp")]:
        cursor = 0
        while True:
            offset = raw.find(opcode, cursor)
            if offset < 0:
                break
            cursor = offset + 1
            if offset + 5 > len(raw):
                continue
            site = section.VirtualAddress + offset
            target = site + 5 + struct.unpack_from("<i", raw, offset+1)[0]
            if target not in by_target:
                continue
            item = root(owner(site))
            leaf = next(((start, end) for start, end in known_leaves if start <= site < end), None)
            if item is None and leaf is None:
                continue  # Unknown no-unwind entry is not evidence of an instruction boundary.
            sequence = instructions(item) if item else tuple(md.disasm(pe.get_data(leaf[0], leaf[1]-leaf[0]), base+leaf[0]))
            index = next((n for n, i in enumerate(sequence) if i.address == base+site
                          and i.mnemonic == mnemonic and i.op_str == hex(base+target)), None)
            if index is None:
                continue
            name = by_target[target]
            caller_root = item[0] if item else leaf[0]
            targets[name]["callers"].append({"site": hex(site), "root": hex(caller_root),
                "mnemonic": mnemonic, "before": [f"{i.address-base:#x} {i.mnemonic} {i.op_str}"
                for i in sequence[max(0, index-8):index+2]]})
            dump(caller_root, f"Caller.{caller_root:X}")

counts = {}
for name, start, end in [("CountAdd", 0x2BA770, 0x2BA77F), ("CountSub", 0x2BA790, 0x2BA7B7)]:
    body = pe.get_data(start, end-start)
    sequence = list(md.disasm(body, base+start))
    assert sequence[-1].mnemonic == "ret" and sequence[-1].address+sequence[-1].size == base+end
    assert pe.get_data(end, 1) == b"\xcc" and data.count(body) == 1
    assert all(base+start <= i.operands[0].imm < base+end for i in sequence if i.mnemonic.startswith("j"))
    counts[name] = {"rva": hex(start), "endRva": hex(end), "codeSHA256": hashlib.sha256(body).hexdigest().upper(),
                    "body": [f"{i.address-base:#x} {i.mnemonic} {i.op_str}" for i in sequence]}

# Bind each observed ingress to actual instructions, including bypasses of the SDK callback runner.
def exact(site, mnemonic, operands):
    decoded = next(md.disasm(pe.get_data(site, 16), base+site))
    assert (decoded.mnemonic, decoded.op_str) == (mnemonic, operands), (hex(site), decoded.mnemonic, decoded.op_str)


for site, mnemonic, operands in [
    (0x2264F0, "mov", "rcx, qword ptr [rcx + 0x40]"),
    (0x2264F4, "jmp", hex(base+0x236650)),
    (0x226560, "mov", "rcx, qword ptr [rcx + 0x48]"),
    (0x226564, "jmp", hex(base+0x236650)),
    (0x2D51E8, "lea", "rdx, [rip + 0x7ac1]"),
    (0x2D51F2, "call", hex(base+0x2264F0)),
    (0x2D5200, "lea", "rdx, [rip + 0x7ac9]"),
    (0x2D520A, "call", hex(base+0x226560)),
    (0x2DCCBB, "call", hex(base+0x2DBB90)),
    (0x2DCCD7, "jmp", hex(base+0x2DBFE0)),
    (0x226C67, "mov", "rdi, qword ptr [rsi + 0x40]"),
    (0x226C91, "call", "rax"),
    (0x226F07, "mov", "rdi, qword ptr [rsi + 0x40]"),
    (0x226F31, "call", "rax"),
    (0x226FEA, "mov", "rdi, qword ptr [rsi + 0x48]"),
    (0x22701C, "call", "rax"),
]:
    exact(site, mnemonic, operands)
native_post_tick = dump(0x2266C0, "Device.NativePostTickCandidate")

manifest = {"liveGameTouched": False, "mode": "disk only; no calls, process reads, or hooks",
            "exeSHA256": exe_hash, "sdkSHA256": sdk_hash, "targets": targets, "counts": counts,
            "renderTargetGlobal": {"rva": hex(rt_global), "validatedMovReferences": rt_global_refs},
            "resizeIngress": {"deviceNativePostTick": native_post_tick,
                "destroyCallbacks": {"deviceField": "0x40", "callbackRva": "0x2dccb0", "rtCleanupRva": "0x2dbb90"},
                "createCallbacks": {"deviceField": "0x48", "callbackRva": "0x2dccd0", "rtRecreateRva": "0x2dbfe0"},
                "sdkExecuteCallbacksIsBypassed": True,
                "validatedInlinedDestroyCalls": ["0x226c91", "0x226f31"],
                "validatedInlinedCreateCalls": ["0x22701c"]},
            "limits": ["No direct caller is not proof of no indirect or third-party caller.",
                       "SDK OnResizeDestroy/Create names are leads; call order and receiver are audited separately.",
                       "Disk identity and static contracts do not issue live consumer/slot admission."]}
(out / "ingress-disk-contract.json").write_text(json.dumps(manifest, indent=2)+"\n")
print(json.dumps({"hashesRevalidated": True, "liveGameTouched": False,
                  "targets": {name: {"rva": item["rva"], "callers": len(item["callers"])}
                              for name, item in targets.items()}}, indent=2))
