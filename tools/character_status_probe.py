"""Decode the 10166 player status packets of one character across the captures.

Offsets are the ones documented in docs/player-status-update-10166.md. Fields the
document leaves unknown are printed too, because the experience and talent a
reward writes have to be somewhere in this packet.
"""
import struct
import subprocess
import sys

FIELDS = [
    (100, "i", "level"),
    (104, "i", "hp"),
    (108, "i", "mp"),
    (144, "i", "max_hp"),
    (148, "i", "max_mp"),
    (84, "i", "u84"),
    (88, "i", "u88"),
    (96, "i", "u96"),
    (112, "i", "u112"),
    (116, "i", "u116"),
    (212, "i", "u212"),
    (216, "i", "u216"),
    (220, "i", "u220"),
    (224, "i", "u224"),
    (228, "i", "talent_points"),
]

QUERY = """
SELECT capture_session_id, captured_at, encode(clear_bytes,'hex')
FROM packet_transactions
WHERE opcode = 10166
ORDER BY capture_session_id, captured_at;
"""


def main():
    wanted = sys.argv[1] if len(sys.argv) > 1 else "2v41e12"
    out = subprocess.run(
        ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
         "-d", "godswar", "-t", "-A", "-F", "|", "-c", QUERY],
        capture_output=True, text=True, check=True).stdout
    previous = {}
    session_previous = {}
    seen = 0
    for line in out.splitlines():
        if not line.strip():
            continue
        session, when, hexed = line.split("|", 2)
        data = bytes.fromhex(hexed)
        name = data[8:40].split(b"\0")[0].decode("ascii", "replace")
        if name != wanted:
            continue
        seen += 1
        values = {label: struct.unpack_from("<" + fmt, data, off)[0]
                  for off, fmt, label in FIELDS}
        changes = []
        baseline = session_previous.get(session, {})
        for label, value in values.items():
            if label in baseline and baseline[label] != value:
                changes.append(f"{label} {baseline[label]}->{value}")
        session_previous[session] = dict(values)
        marker = "  CHANGED " + ", ".join(changes) if changes else ""
        print(f"{when[11:23]} {session[:8]} lv={values['level']:<4} hp={values['hp']:<8} "
              f"talent={values['talent_points']:<8} u224={values['u224']:<12}"
              f"u112={values['u112']:<12} u116={values['u116']}{marker}")
    print(f"frames for {wanted}: {seen}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
