"""Scan every captured frame for large 32-bit values.

Dialog ids and item ids sit in the same wire words as rewards, so a raw byte
search is not enough: this walks the payload at its own four-byte alignment and
reports the values a reward could plausibly be.
"""
import collections
import struct
import subprocess
import sys

LOW = int(sys.argv[1]) if len(sys.argv) > 1 else 100_000
HIGH = int(sys.argv[2]) if len(sys.argv) > 2 else 5_000_000

QUERY = """
SELECT capture_session_id, captured_at, direction, opcode, encode(clear_bytes,'hex')
FROM packet_transactions
WHERE direction = 'S2C' AND actual_length BETWEEN 8 AND 400
ORDER BY capture_session_id, captured_at;
"""


def main():
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "|", "-c", QUERY],
        capture_output=True, text=True, check=True).stdout
    found = collections.defaultdict(list)
    frames = 0
    for line in out.splitlines():
        if not line.strip():
            continue
        session, when, direction, opcode, hexed = line.split("|", 4)
        data = bytes.fromhex(hexed)
        frames += 1
        for offset in range(4, len(data) - 3, 4):
            value = struct.unpack_from("<I", data, offset)[0]
            if LOW <= value <= HIGH:
                found[value].append((when[11:23], opcode, offset, session[:8]))

    print(f"frames scanned: {frames}   distinct large values: {len(found)}")
    for value in sorted(found, key=lambda v: -len(found[v]))[:40]:
        rows = found[value]
        print(f"{value:>12} x{len(rows):<5} first={rows[0][0]} opcode={rows[0][1]} "
              f"offset={rows[0][2]} sessions={sorted({r[3] for r in rows})}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
