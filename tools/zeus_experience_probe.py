"""Decode the experience packets of the captured sessions.

The 10031 packet is 13 bytes: length, opcode, gained experience at +4 and the
character's running total at +8.
"""
import struct
import subprocess

QUERY = """
SELECT capture_session_id, captured_at, encode(clear_bytes,'hex')
FROM packet_transactions
WHERE opcode = 10031
ORDER BY capture_session_id, captured_at;
"""


def main():
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "\x1f", "-c", QUERY],
        capture_output=True, text=True, check=True).stdout
    per_session = {}
    for line in out.splitlines():
        if not line.strip():
            continue
        session, when, hexed = line.split("\x1f")
        data = bytes.fromhex(hexed)
        if len(data) != 13:
            continue
        gained, current = struct.unpack_from("<ii", data, 4)
        per_session.setdefault(session, []).append((when, gained, current))

    for session, rows in per_session.items():
        print(f"== {session}  frames={len(rows)}")
        biggest = sorted(rows, key=lambda r: -abs(r[1]))[:6]
        for when, gained, current in biggest:
            print(f"   {when[11:23]} gained={gained:>12} current={current:>12}")

        # A running total that jumps by more than a thousand is a reward rather
        # than a kill.
        jumps = []
        previous = None
        for when, gained, current in rows:
            if previous is not None:
                delta = current - previous
                if delta > 1000 or delta < 0:
                    jumps.append((when, previous, current, delta))
            previous = current
        for when, before, after, delta in jumps[:10]:
            print(f"   JUMP {when[11:23]} {before} -> {after} delta={delta}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
