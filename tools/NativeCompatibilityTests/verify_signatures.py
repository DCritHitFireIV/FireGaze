"""Read-only, offline unique-match check for the production CN native signatures."""
from pathlib import Path
import hashlib
import re
import struct
import sys


def rva_and_section(image: bytes, offset: int) -> tuple[int, str, bool]:
    pe = struct.unpack_from("<I", image, 0x3C)[0]
    if image[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Not a PE executable")
    sections = struct.unpack_from("<H", image, pe + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe + 20)[0]
    section_table = pe + 24 + optional_size
    for index in range(sections):
        section = section_table + index * 40
        name = image[section:section + 8].rstrip(b"\0").decode("ascii")
        rva, raw_size, raw_offset = struct.unpack_from("<III", image, section + 12)
        flags = struct.unpack_from("<I", image, section + 36)[0]
        if raw_offset <= offset < raw_offset + raw_size:
            return rva + offset - raw_offset, name, bool(flags & 0x20000000)
    raise ValueError(f"Match at file offset {offset:#x} is outside the PE sections")


base = Path(__file__).resolve().parents[2]
source = base / "src/FireGaze.LocalPreview/Runtime/ActorPreviewRuntime.NativeCompatibility.cs"
exe = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\ffxiv_dx11.exe")
image = exe.read_bytes()
patterns = re.findall(r'private const string (Character(?:Update|Disable)Signature) = "([^"]+)";', source.read_text(encoding="utf-8"))
assert len(patterns) == 2, "Both production signatures must be present"
print("Executable SHA256:", hashlib.sha256(image).hexdigest().upper())
for name, pattern in patterns:
    expression = b"".join(b"." if token == "??" else re.escape(bytes([int(token, 16)])) for token in pattern.split())
    matches = list(re.finditer(b"(?=(" + expression + b"))", image, re.S))
    assert len(matches) == 1, f"{name}: expected exactly one match, got {len(matches)}"
    rva, section, executable = rva_and_section(image, matches[0].start())
    assert executable, f"{name}: match is outside an executable section"
    print(f"{name}: matches=1 RVA={rva:#x} section={section}")
