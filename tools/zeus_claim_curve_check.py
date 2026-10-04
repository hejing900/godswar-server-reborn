"""Compare the captured experience grants against the level experience table.

The table lives in src/Godswar.Server/State/PlayerExperienceCatalog.cs: entry n is
the fighter experience needed to advance from level n.
"""
import re
import pathlib

SOURCE = pathlib.Path("src/Godswar.Server/State/PlayerExperienceCatalog.cs")
text = SOURCE.read_text(encoding="utf-8")
block = text.split("NextLevelExperience =", 1)[1].split("];", 1)[0]
table = [int(value) for value in re.findall(r"\d+", block)]
print(f"table entries: {len(table)}  (entry n = experience from level n)")

for level in (56, 57, 86, 87, 88):
    if level <= len(table):
        print(f"  level {level:<4} needs {table[level - 1]:>10}")

user = 293_700
other = 630_000
print()
print(f"user's claim at level 56 : {user}")
print(f"r3idel's jump at level 87: {other}")
print(f"  ratio other/user            = {other / user:.4f}")
for level_a, level_b in ((56, 87), (57, 88), (56, 86)):
    a, b = table[level_a - 1], table[level_b - 1]
    print(f"  ratio table[{level_a}]/table[{level_b}] = {b / a:.4f}   "
          f"({a} -> {b})")
print()
for level, amount in ((56, user), (87, other)):
    entry = table[level - 1]
    print(f"  level {level}: amount/table[level]   = {amount / entry:.4f}"
          f"   amount/table[level+1] = {amount / table[level]:.4f}")
