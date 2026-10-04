"""Probe captured NPC function dialogues (10067 / 10069 / 10070) in the proxy archive.

Reads the capture proxy's postgres archive and prints the traffic of the stock
client's NPC function window. Modes:

    list                 one line per dialogue instance: script, packed function
                         list and the replies it produced
    timeline <npcId>     interleaved clicks and replies, reduced to the values
                         that are not -1
    slots <npcId>        every int32 slot of each C2S 10069, so the click array
                         and the fixed tail block can be told apart
    deep [minReplies]    instances with at least that many replies, page by page

The field offsets come from docs/NPC对话链路技术文档.md and are re-checked by
the output: +4 npc object id, +8 dialog index, +12 dialog index repeated, +16
the click array, then a fixed block the client only fills on the opening
request.
"""
import argparse
import struct
import subprocess

FRAME_OPCODES = (10067, 10069, 10070)

SQL = """
SELECT capture_session_id, connection_id, captured_at, direction, opcode,
       encode(clear_bytes, 'hex')
FROM packet_transactions
WHERE opcode IN (10067, 10069, 10070)
ORDER BY capture_session_id, captured_at;
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
    return raw[:end if end >= 0 else len(raw)].decode("ascii", "replace")


def unpack_ids(packed):
    out = []
    while packed:
        out.append(packed % 1000)
        packed //= 1000
    return out


def load():
    rows = []
    for line in query(SQL).splitlines():
        if not line.strip():
            continue
        session, conn, at, direction, opcode, hexed = line.split("\x1f")
        rows.append({"session": session, "conn": conn, "at": at,
                     "dir": direction, "opcode": int(opcode),
                     "data": bytes.fromhex(hexed)})
    return rows


def instances(rows):
    """Split the stream into dialogue instances, one per S2C 10067."""
    out = []
    current = None
    for row in rows:
        if row["opcode"] == 10067 and row["dir"] == "S2C":
            if current:
                out.append(current)
            current = {
                "at": row["at"], "session": row["session"],
                "sessionKey": row["session"][:8],
                "conn": row["conn"], "npc": struct.unpack_from("<I", row["data"], 4)[0],
                "flags": struct.unpack_from("<I", row["data"], 8)[0],
                "list": unpack_ids(struct.unpack_from("<I", row["data"], 12)[0]),
                "script": script_name(row["data"]), "steps": []}
            continue
        if current is None:
            continue
        if row["conn"] != current["conn"] or row["session"] != current["session"]:
            continue
        if row["opcode"] == 10069:
            dialog = struct.unpack_from("<i", row["data"], 8)[0]
            current["steps"].append(
                ("click", dialog, ints(row["data"], 16)))
        elif row["opcode"] == 10070:
            dialog = struct.unpack_from("<i", row["data"], 8)[0]
            current["steps"].append(("reply", dialog, ints(row["data"], 12)))
    if current:
        out.append(current)
    return out


def mode_list(rows):
    for inst in instances(rows):
        replies = [s for s in inst["steps"] if s[0] == "reply"]
        print(f"{inst['at'][:23]} {inst['script']:<12} npc={inst['npc']:<6} "
              f"flags=0x{inst['flags']:X} list={inst['list']} "
              f"replies={len(replies)}")

def mode_timeline(rows, npc):
    for inst in instances(rows):
        if inst["npc"] != npc:
            continue
        print(f"\n--- {inst['at']} {inst['script']} list={inst['list']} ---")
        for kind, dialog, values in inst["steps"]:
            shown = [v for v in values if v != -1]
            if kind == "click":
                print(f"  click  dialog={dialog:<4} values={shown}")
            else:
                print(f"  reply  dialog={dialog:<4} ids={values}")


def mode_slots(rows, npc):
    for row in rows:
        if row["opcode"] != 10069:
            continue
        data = row["data"]
        if struct.unpack_from("<I", data, 4)[0] != npc:
            continue
        dialog = struct.unpack_from("<i", data, 8)[0]
        filled = [f"[{i}]={v}" for i, v in enumerate(ints(data, 16)) if v != -1]
        print(f"{row['at'][11:23]} dialog={dialog} len={len(data)}")
        print(f"    {' '.join(filled) if filled else '(all -1)'}")


def mode_deep(rows, minimum):
    found = 0
    for inst in instances(rows):
        replies = [s for s in inst["steps"] if s[0] == "reply"]
        if len(replies) < minimum:
            continue
        found += 1
        print(f"\n--- {inst['at']} {inst['script']} replies={len(replies)} ---")
        page = 0
        for kind, dialog, values in inst["steps"]:
            if kind == "click":
                shown = [v for v in values if v != -1]
                print(f"    click  values={shown}")
            else:
                page += 1
                print(f"    page{page}  ids={values}")
    print(f"\ninstances with >= {minimum} replies: {found}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["list", "timeline", "slots", "deep"])
    parser.add_argument("npc", nargs="?", type=int, default=0)
    parser.add_argument("--minimum", type=int, default=4)
    args = parser.parse_args()

    rows = load()
    if args.mode == "list":
        mode_list(rows)
    elif args.mode == "timeline":
        mode_timeline(rows, args.npc)
    elif args.mode == "slots":
        mode_slots(rows, args.npc)
    else:
        mode_deep(rows, args.minimum)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
