"""Pinned disk-only constructor/final-reference/deleting destructor contract."""
from pathlib import Path
import argparse,hashlib,json,struct
import pefile
from capstone import Cs,CS_ARCH_X86,CS_MODE_64

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--exe',required=True,type=Path)
parser.add_argument('--sdk',required=True,type=Path)
parser.add_argument('--output',required=True,type=Path)
args=parser.parse_args()
OUT=args.output.resolve();OUT.mkdir(parents=True,exist_ok=True)
EXE=args.exe.resolve();SDK=args.sdk.resolve()
EXE_HASH="958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9"
SDK_HASH="5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778"
data=EXE.read_bytes();sdk=SDK.read_bytes()
assert hashlib.sha256(data).hexdigest().upper()==EXE_HASH,"EXE drift"
assert hashlib.sha256(sdk).hexdigest().upper()==SDK_HASH,"SDK drift"
pe=pefile.PE(data=data,fast_load=True);base=pe.OPTIONAL_HEADER.ImageBase
md=Cs(CS_ARCH_X86,CS_MODE_64);md.detail=True
functions={"Ctor":(0x268F20,0x268F3E),"ReleaseEntry":(0x268F50,0x268F57),
           "Dtor":(0x2EDFC0,0x2EE00D),"AddRef":(0x3356B0,0x3356BB),"ReleaseRef":(0x3356D0,0x335707)}
manifest={"liveGameTouched":False,"mode":"disk-only; no process access, native calls, or Hook installation",
          "exeSHA256":EXE_HASH,"sdkSHA256":SDK_HASH,"functions":{},"evidence":[]}
all_instructions={}
for name,(start,end) in functions.items():
    code=pe.get_data(start,end-start);ins=list(md.disasm(code,base+start))
    assert ins[-1].address+ins[-1].size==base+end,(name,hex(ins[-1].address-base),ins[-1].mnemonic,ins[-1].size,hex(end))
    assert pe.get_data(end,1)==b"\xcc",name
    all_instructions[name]={i.address-base:(i.mnemonic,i.op_str) for i in ins}
    manifest["functions"][name]={"rva":hex(start),"endRva":hex(end),"codeSHA256":hashlib.sha256(code).hexdigest().upper(),"codeHex":code.hex(' ').upper()}
    (OUT/(name+'.asm')).write_text('\n'.join(f'{i.address-base:#010x} {i.mnemonic:8} {i.op_str}'for i in ins)+'\n')
ctor=all_instructions['Ctor']
assert ctor[0x268F27]==('mov','dword ptr [rcx + 8], 1')
assert ctor[0x268F3A]==('mov','rax, rcx')
assert ctor[0x268F3D]==('ret','')
assert all_instructions['ReleaseEntry'][0x268F53]==('jmp','qword ptr [rax + 0x20]')
assert all_instructions['AddRef'][0x3356B5]==('lock xadd','dword ptr [rcx + 8], eax')
release=all_instructions['ReleaseRef']
for site,expected in {
    0x3356D9:('mov','eax, 0xffffffff'),0x3356DE:('lock xadd','dword ptr [rcx + 8], eax'),
    0x3356E3:('cmp','eax, 1'),0x3356E6:('jne',hex(base+0x335701)),
    0x3356EB:('call','qword ptr [rax + 8]'),0x3356F1:('mov','edx, 1'),
    0x3356FE:('jmp','qword ptr [rax]')}.items():assert release[site]==expected,(hex(site),release[site])
dtor=all_instructions['Dtor']
for site,expected in {0x2EDFD3:('test','dl, 1'),0x2EDFD8:('test','dl, 4'),
    0x2EDFE4:('mov','rdx, rbx'),0x2EDFF1:('mov','rax, rbx'),
    0x2EDFFF:('call',hex(base+0x5B200)),0x2EE004:('mov','rax, rbx')}.items():assert dtor[site]==expected,(hex(site),dtor[site])
table=struct.unpack('<5Q',pe.get_data(0x214A6C8,40))
assert table==tuple(base+rva for rva in [0x2EDFC0,0x268F50,0x3356B0,0x3356D0,0x2690B0])
for start,end,call,assign,configure in [(0x9CD6DC,0x9CD71D,0x9CD700,0x9CD714,0x9CD718),
    (0x9CEAE0,0x9CEB2E,0x9CEB11,0x9CEB25,0x9CEB29)]:
    ins={i.address-base:(i.mnemonic,i.op_str) for i in md.disasm(pe.get_data(start,end-start),base+start)}
    assert ins[call]==('call',hex(base+0x268F20))
    assert ins[configure]==('call',hex(base+0x269020))
    assert ins[assign][0]=='mov'and ins[assign][1].endswith('+ 0x18], rax')
manifest['contextVtableRva']='0x214A6C8';manifest['contextVtable']=[hex(x-base)for x in table]
manifest['evidence']=[
    'Ctor receives RCX and returns same context in RAX after refs=1; record generation only after Original returns.',
    'Configure is invoked only after view.context receives the constructor result; constructor tracking needs no stale view read.',
    'ReleaseRef performs atomic old-count decrement. Only old count==1 invokes virtual ReleaseEntry/Cleanup then Dtor(flags=1).',
    'Dtor pointer has nint(nint,byte) shape; flags1 and flags4 select native allocator paths; preserve all flags and RAX.',
    'Context can outlive view via other reference holders. Retire tracking on actual Dtor ingress, never on view Release alone.',
    'Generic Dtor is shared. Tracker must not dereference arbitrary receivers; unknown receiver invalidation is a no-op.'
]
manifest['limits']=[
    'Disk contracts are not live admission or a claim every bypass/free path is intercepted.',
    'Renderer reset, raw Manager counters, off-thread lost-slot retirement and GPU copy drain still require independent closure.',
    'No constructor is synthesized, no AddRef/ReleaseRef is invoked and no generic receiver is probed by the tracker.'
]
(OUT/'lifetime-disk-contract.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps({'hashesRevalidated':True,'functionCount':len(functions),'contextVtable':manifest['contextVtable'],
                  'liveGameTouched':False,'constructorABI':'nint(nint)','deletingDtorABI':'nint(nint,byte)'},indent=2))
