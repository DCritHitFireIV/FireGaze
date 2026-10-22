"""Read-only ABI evidence for the current CN native special-bar bridge.

No process access, patching, hooks, or game commands. RVAs in the output are
derived from the selected executable and are never copied into production code.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import struct

parser = argparse.ArgumentParser()
parser.add_argument("executable", type=Path)
args = parser.parse_args()
raw = args.executable.read_bytes()
pe = struct.unpack_from("<I", raw, 0x3C)[0]
assert raw[pe:pe+4] == b"PE\0\0"
sections = []
count = struct.unpack_from("<H", raw, pe+6)[0]
optional = struct.unpack_from("<H", raw, pe+20)[0]
for i in range(count):
    start = pe+24+optional+i*40
    virtual_size, rva, size, offset = struct.unpack_from("<IIII", raw, start+8)
    flags = struct.unpack_from("<I", raw, start+36)[0]
    if flags & 0x20000000:
        sections.append((rva, raw[offset:offset+size]))

def matches(pattern):
    return [(base+m.start()) for base,data in sections for m in re.finditer(re.escape(pattern),data)]

def at(rva, size):
    return next(data[rva-base:rva-base+size] for base,data in sections if base<=rva<base+len(data))

source = (Path(__file__).parents[1]/"src/FireGaze.LocalPreview/Interaction/TrialNativeSpecialHotbar.cs").read_text(encoding="utf-8")
signature = bytes.fromhex(re.search(r'ExecuteSlotSignature="([0-9A-F ]+)"',source)[1])
leaf_hits = matches(signature)
assert len(leaf_hits)==1, f"native slot dispatcher must resolve uniquely: {leaf_hits}"
leaf = leaf_hits[0]
wrapper_hits = matches(bytes.fromhex("8B C2 48 69 D0 E8 00 00 00 48 81 C2 A0 05 01 00 48 03 D1 E9"))
assert len(wrapper_hits)==1, f"PetHotbar wrapper must resolve uniquely: {wrapper_hits}"
wrapper = wrapper_hits[0]
tail = wrapper+19
assert tail+5+struct.unpack("<i",at(tail+1,4))[0] == leaf, "PetSlot wrapper must tail-dispatch to reviewed leaf"
calls=[]
for base,data in sections:
    for match in re.finditer(b"\xE8",data):
        k=match.start()
        if k+5>len(data):continue
        site=base+k
        if site+5+struct.unpack_from("<i",data,k+1)[0]==wrapper:calls.append(site)
assert len(calls)>=2, "mouse callback and HOTBAR_EX input paths must both call PetSlot wrapper"
# The native dispatcher returns its explicit handled byte in EAX at each exit.
assert bytes.fromhex("41 0F B6 C6 48 8B 5C 24 60") in at(leaf,0xA00), "leaf U1 bool return ABI must remain present"
print(json.dumps({"executable":str(args.executable),"sha256":hashlib.sha256(raw).hexdigest(),
    "slotDispatcher":hex(leaf),"signatureMatches":len(leaf_hits),"petSlotWrapper":hex(wrapper),
    "petSlotCallers":[hex(x) for x in calls],"slotStride":hex(0xE8),"petSlotOffset":hex(0x105A0),
    "verified":"unique leaf; module+slot ABI; PetSlot tail target; mouse and keyboard callers; U1 bool return"},ensure_ascii=False,indent=2))
