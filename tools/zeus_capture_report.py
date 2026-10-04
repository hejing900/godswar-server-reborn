"""Decode every captured Zeus event NPC dialogue (10067/10068/10069/10070).

Reads the capture proxy archive (postgres container godswar-postgres) and prints
one transcript per dialogue instance for the Zeus event NPCs:

    Athens_113 / Sparta_113  [Event]Zeus' Loyal Believer
    Athens_114 / Sparta_114  [Event]Praying Saint

Packet layouts are taken from docs/NPC对话链路技术文本.md and verified against
the frames this script prints; nothing here is inferred beyond the field offsets
that the doc already established.
"""
import struct
import subprocess
import sys

ZEUS_SCRIPTS = {"Athens_113", "Sparta_113", "Athens_114", "Sparta_114"}

QUERY = """
SELECT capture_session_id, connection_id, packet_sequence, captured_at,
       direction, opcode, encode(clear_bytes, 'hex')
FROM packet_transactions
WHERE opcode IN (10067, 10068, 10069, 10070)
ORDER BY capture_session_id, captured_at, packet_sequence;
"""


def query(sql):
    return subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "\x1f", "-c", sql],
        capture_output=True, text=True, check=True).stdout


def ints(data, offset):
    out = []
    while offset + 4 <= len(data):
        out.append(struct.unpack_from("<i", data, offset)[0])
        offset += 4
    return out


def script_name(data):
    raw = data[16:48]
    end = raw.find(b"\0")
    if end < 0:
        end = len(raw)
    return raw[:end].decode("ascii", "replace")


def unpack_ids(packed):
    out = []
    while packed:
        out.append(packed % 1000)
        packed //= 1000
    return out


def load():
    rows = []
    for line in query(QUERY).splitlines():
        if not line.strip():
            continue
        parts = line.split("\x1f")
        session, conn, seq, at, direction, opcode, hexed = parts
        rows.append({
            "session": session,
            "conn": conn,
            "seq": int(seq),
            "at": at,
            "dir": direction,
            "opcode": int(opcode),
            "data": bytes.fromhex(hexed),
        })
    return rows


def describe(row):
    data = row["data"]
    op = row["opcode"]
    if op == 10067:
        npc = struct.unpack_from("<I", data, 4)[0]
        if row["dir"] == "S2C":
            flags = struct.unpack_from("<I", data, 8)[0]
            packed = struct.unpack_from("<I", data, 12)[0]
            return (f"10067 S2C npc={npc} flags=0x{flags:X} "
                    f"list={unpack_ids(packed)} script={script_name(data)!r}")
        return f"10067 C2S npc={npc} raw={data[8:].hex()}"
    if op == 10068:
        return f"10068 C2S npc={struct.unpack_from('<I', data, 4)[0]}"
    if op == 10069:
        npc, dialog, subid, sel = struct.unpack_from("<IIii", data, 4)
        rest = ints(data, 20)
        return (f"10069 C2S npc={npc} dialog={dialog} subId={subid} "
                f"select={sel} params={rest}")
    if op == 10070:
        npc, dialog = struct.unpack_from("<II", data, 4)
        return (f"10070 S2C npc={npc} dialog={dialog} ids={ints(data, 12)}")
    return f"{op}"


def main():
    rows = load()
    # One dialogue instance runs from an S2C 10067 to the next S2C 10067 on the
    # same connection.
    groups = []
    current = None
    for row in rows:
        if row["opcode"] == 10067 and row["dir"] == "S2C":
            if current is not None:
                groups.append(current)
            current = None
            if script_name(row["data"]) in ZEUS_SCRIPTS:
                current = (row, [row])
            continue
        if current is None:
            continue
        open_row, frames = current
        if (row["session"] != open_row["session"]
                or row["conn"] != open_row["conn"]):
            continue
        frames.append(row)
    if current is not None:
        groups.append(current)

    print(f"dialogue instances: {len(groups)}")
    for number, (open_row, frames) in enumerate(groups, 1):
        npc = struct.unpack_from("<I", open_row["data"], 4)[0]
        print()
        print(f"=== #{number} {open_row['at']} npc={npc} "
              f"script={script_name(open_row['data'])} "
              f"session={open_row['session'][:8]} ===")
        for frame in frames:
            print(f"  {frame['at'][11:23]} {describe(frame)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
