#!/usr/bin/env python3
"""
make_pck.py — Create a minimal Godot 4.3 PCK file.

Usage: python3 make_pck.py <manifest_json> <output_pck>

The generated PCK embeds res://mod_manifest.json so the STS2 mod loader
can find and validate the mod manifest from inside the PCK file.

PCK format v2 (Godot 4.x):
  0x00: magic b'GDPC'
  0x04: format_version u32 = 2
  0x08: godot_major u32 = 4
  0x0c: godot_minor u32 = 3
  0x10: godot_patch u32 = 0
  0x14: flags u32 = 0
  0x18: file_base u64 (absolute offset where file data starts)
  0x20: reserved (16 * u32 = 64 bytes, all zeros)
  0x60: file_count u32
  0x64: directory entries (one per file):
          path_len u32  (byte count of path string, padded to 4-byte alignment)
          path bytes    (UTF-8, null-terminated, padded)
          file_offset u64  (relative to file_base)
          file_size u64
          md5 bytes[16]
          flags u32
  then: file data at file_base
"""
import struct
import hashlib
import sys
import os


def pad4(n: int) -> int:
    return (n + 3) & ~3


def make_pck(manifest_content: bytes, output_path: str) -> None:
    path_str = b'res://mod_manifest.json\x00'
    # Pad path string to 4-byte boundary
    path_padded = path_str + b'\x00' * (pad4(len(path_str)) - len(path_str))

    # Compute MD5 of file content
    md5 = hashlib.md5(manifest_content).digest()

    # Size of one directory entry:
    #   4 (path_len field) + len(path_padded) + 8 (offset) + 8 (size) + 16 (md5) + 4 (flags)
    dir_entry_size = 4 + len(path_padded) + 8 + 8 + 16 + 4

    # Header ends at 0x64 + dir_entry_size; align file_base to 16 bytes
    header_end = 0x64 + dir_entry_size
    file_base = (header_end + 15) & ~15

    buf = bytearray()

    # --- Fixed header ---
    buf += b'GDPC'                       # magic
    buf += struct.pack('<I', 2)          # format version
    buf += struct.pack('<I', 4)          # godot major
    buf += struct.pack('<I', 3)          # godot minor
    buf += struct.pack('<I', 0)          # godot patch
    buf += struct.pack('<I', 0)          # flags
    buf += struct.pack('<Q', file_base)  # file_base

    assert len(buf) == 0x20, f'Expected 0x20, got {len(buf):#x}'

    buf += b'\x00' * 64  # 16 reserved u32s

    assert len(buf) == 0x60, f'Expected 0x60, got {len(buf):#x}'

    # --- File count ---
    buf += struct.pack('<I', 1)

    assert len(buf) == 0x64, f'Expected 0x64, got {len(buf):#x}'

    # --- Directory entry ---
    buf += struct.pack('<I', len(path_padded))
    buf += path_padded
    buf += struct.pack('<Q', 0)                      # file_offset (relative to file_base)
    buf += struct.pack('<Q', len(manifest_content))  # file_size
    buf += md5
    buf += struct.pack('<I', 0)                      # flags

    # Pad to file_base
    if len(buf) < file_base:
        buf += b'\x00' * (file_base - len(buf))

    assert len(buf) == file_base, f'Expected buf len={file_base}, got {len(buf)}'

    # --- File data ---
    buf += manifest_content

    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    with open(output_path, 'wb') as f:
        f.write(buf)

    print(f'[make_pck] {output_path}: {len(buf)} bytes '
          f'(file_base={file_base:#x}, manifest={len(manifest_content)} bytes, md5={md5.hex()})')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        print(f'Usage: {sys.argv[0]} <manifest_json> <output_pck>', file=sys.stderr)
        sys.exit(1)

    manifest_file = sys.argv[1]
    output_pck = sys.argv[2]

    with open(manifest_file, 'rb') as f:
        content = f.read()

    make_pck(content, output_pck)
