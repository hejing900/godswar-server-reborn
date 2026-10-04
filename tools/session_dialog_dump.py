"""Decode the NPC function dialog frames of one capture session.

Usage:
    python tools/session_dialog_dump.py <capture_session_id> [npcId ...]
"""
import struct
import subprocess
import sys

SQL = """
SELECT captured_at, direction, opcode, encode(clear_bytes,'hex')
FROM packet_transactions
WHERE capture_session_id = '{session}' AND opcode IN (10067, 10068, 10069, 10070)
ORDER BY packet_sequence;
"""


def ints(data, offset):
    out = []
    while offset + 4 <= len(data):
        out.append(struct.unpack_from("<i", data, offset)[0])
        offset += 4
    return out


def script_name(data):
    raw = data[16:48]
    end = raw.find(b"\0")
    return raw[:end if end >= 0 else len(raw)].decode("ascii", "replace")


def main():
    session = sys.argv[1]
    wanted = {int(a) for a in sys.argv[2:]}
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "\x1f", "-c", SQL.format(session=session)],
        capture_output=True, text=True, check=True).stdout
    for line in out.splitlines():
        if not line.strip():
            continue
        when, direction, opcode, hexed = line.split("\x1f")
        data = bytes.fromhex(hexed)
        npc = struct.unpack_from("<I", data, 4)[0]
        if wanted and npc not in wanted:
            continue
        if opcode == "10067":
            if direction == "S2C":
                flags = struct.unpack_from("<I", data, 8)[0]
                packed = struct.unpack_from("<I", data, 12)[0]
                ids = []
                while packed:
                    ids.append(packed % 1000)
                    packed //= 1000
                print(f"{when[11:23]} S2C 10067 npc={npc} flags=0x{flags:X} "
                      f"list={ids} script={script_name(data)}")
            else:
                print(f"{when[11:23]} C2S 10067 npc={npc} {ints(data, 8)}")
        elif opcode == "10068":
            print(f"{when[11:23]} C2S 10068 npc={npc}")
        elif opcode == "10069":
            print(f"{when[11:23]} C2S 10069 npc={npc} "
                  f"dialog={struct.unpack_from('<i', data, 8)[0]} args={ints(data, 12)}")
        else:
            print(f"{when[11:23]} S2C 10070 npc={npc} "
                  f"dialog={struct.unpack_from('<i', data, 8)[0]} ids={ints(data, 12)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
