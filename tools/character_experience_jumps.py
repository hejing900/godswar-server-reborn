"""List every character's experience jumps from the 10166 status packets.

A 10166 frame carries the character's name, level, current experience (+96) and
talent points (+228). Grouping the frames by session and name turns them into a
timeline, and the jumps in it are the rewards and kills that character received.
"""
import struct
import subprocess

QUERY = """
SELECT capture_session_id, captured_at, encode(clear_bytes,'hex')
FROM packet_transactions
WHERE opcode = 10166 AND actual_length = 236
ORDER BY capture_session_id, captured_at;
"""


def main():
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "|", "-c", QUERY],
        capture_output=True, text=True, check=True).stdout
    frames = {}
    for line in out.splitlines():
        if not line.strip():
            continue
        session, when, hexed = line.split("|", 2)
        data = bytes.fromhex(hexed)
        name = data[8:40].split(b"\0")[0].decode("ascii", "replace")
        level = struct.unpack_from("<i", data, 100)[0]
        exp = struct.unpack_from("<i", data, 96)[0]
        talent = struct.unpack_from("<i", data, 228)[0]
        frames.setdefault((session, name), []).append((when, level, exp, talent))

    print("characters with more than one status frame:")
    for (session, name), rows in sorted(frames.items(), key=lambda kv: -len(kv[1])):
        if len(rows) < 3:
            continue
        levels = sorted({r[1] for r in rows})
        print(f"  {name:<16} {session[:8]} frames={len(rows):<4} levels={levels}")

    print()
    print("jumps of 10,000 experience or more:")
    for (session, name), rows in frames.items():
        for before, after in zip(rows, rows[1:]):
            delta = after[2] - before[2]
            leveled = after[1] != before[1]
            if abs(delta) >= 10_000 or (leveled and delta > 0):
                print(f"  {after[0][11:23]} {session[:8]} {name:<16} "
                      f"lv {before[1]}->{after[1]} exp {before[2]}->{after[2]} "
                      f"delta={delta} talent {before[3]}->{after[3]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
