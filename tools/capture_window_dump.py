"""Dump every captured frame in a time window, decoded by opcode name."""
import struct
import subprocess
import sys

SQL = """
SELECT captured_at, connection_id, direction, opcode, opcode_name,
       encode(clear_bytes,'hex')
FROM packet_transactions
WHERE captured_at >= '{start}' AND captured_at <= '{end}'
ORDER BY captured_at;
"""


def ints(data, offset):
    out = []
    while offset + 4 <= len(data):
        out.append(struct.unpack_from("<i", data, offset)[0])
        offset += 4
    return out


def main():
    start, end = sys.argv[1], sys.argv[2]
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "\x1f", "-c",
         SQL.format(start=start, end=end)],
        capture_output=True, text=True, check=True).stdout
    for line in out.splitlines():
        if not line.strip():
            continue
        when, conn, direction, opcode, name, hexed = line.split("\x1f")
        data = bytes.fromhex(hexed)
        npc = struct.unpack_from("<I", data, 4)[0] if len(data) >= 8 else 0
        if opcode in ("10067", "10069", "10070"):
            head = ints(data, 8)
            print(f"{when[11:23]} {conn[:8]} {direction} {opcode:<6} npc={npc} {head}")
        else:
            print(f"{when[11:23]} {conn[:8]} {direction} {opcode:<6} npc={npc} "
                  f"{name} len={len(data)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
