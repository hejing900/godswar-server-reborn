"""Decode one 10166 status packet by opcode and time window."""
import struct
import subprocess
import sys

session, start, end = sys.argv[1], sys.argv[2], sys.argv[3]
query = (
    "select encode(clear_bytes,'hex') from packet_transactions "
    f"where capture_session_id='{session}' and opcode=10166 "
    f"and captured_at between '{start}+00' and '{end}+00';"
)
out = subprocess.run(
    ["docker", "exec", "godswar-postgres", "psql", "-U", "godswar",
     "-d", "godswar", "-t", "-A", "-c", query],
    capture_output=True, text=True, check=True).stdout
for line in out.splitlines():
    if not line.strip():
        continue
    data = bytes.fromhex(line)
    print("name        ", data[8:40].split(b"\0")[0].decode("ascii", "replace"))
    for offset, label in ((84, "u84"), (88, "u88"), (96, "exp"), (100, "level"),
                          (104, "hp"), (108, "mp"), (228, "talent")):
        print(f"{label:<12}+{offset:<4}", struct.unpack_from("<i", data, offset)[0])
