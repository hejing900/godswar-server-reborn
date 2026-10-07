"""Generate the chain's collect-item objectives from the client's own quest data.

A quest's Objectives text can name two kinds of objective:

    Kill 15 Little Snakes and collect 5 Snake Tails. Then report to ...

The kill clause is what StarterQuestObjectives carries. The collect clause names
an item from the client's own Text/QuestItem.dat, and the server writes it into
the objective area's item pair - the item id and how many are wanted - which is a
separate pair of fields from the parallel monster/count arrays:

    frame      item id   count
    10082        +24      +40
    10076/10081  +20      +36

Without that pair the client draws only the kill objective, so a quest that asks
for two things shows one. The offsets and the id space are the reference
server's own; the three samples that pin them are

    quest 526  collect 5 Snake Tails   -> 400  5
    quest 1526 collect 5 Honeycombs    -> 300  5
    quest 1528 collect 8 Snake Fangs   -> 301  8

Ids come from QuestItem.dat by name, never from arithmetic. A name the table
holds once is determined. A name it holds twice (Snake Fangs is 301 and 403,
Wolf Fangs is 307 and 407) is broken by camp, because the client ships the two
camps' newbie item sets in parallel blocks: Athens 300..307, Sparta 400..407.
Quest 1528, an Athens quest, confirms the block rule with 301 over 403.

A clause whose item cannot be resolved is dropped rather than guessed at, so the
table only ever holds an id the client's own table names.
"""

import os
import re
import sys

QUEST_TEXT_DIR = r"D:\Godswar Origin\Localization\en_us\Text\Quest"
ITEM_DAT = r"D:\Godswar Origin\Localization\en_us\Text\QuestItem.dat"
CHAIN = (r"D:\Godswar-Reborn-main\src\Godswar.Server\Domain\World\Content"
         r"\StarterQuestChain.cs")
OUTPUT = (r"D:\Godswar-Reborn-main\src\Godswar.Server\Domain\World\Content"
          r"\StarterQuestCollectObjectives.cs")

OBJECTIVE_BLOCK = re.compile(r"Objectives\s*\{(?P<body>.*?)\}", re.DOTALL)
COLOR = re.compile(r"\|c[0-9a-fA-F]{8}")
BRACKETS = re.compile(r"\[[^\]]*\]")
CLAUSE_SPLIT = re.compile(r",|;|\band\b|\bthen\b", re.IGNORECASE)
COLLECT = re.compile(
    r"^\s*(?:collect|gather|obtain|retrieve|recover|bring|get|take|find)\s+"
    r"(?:(?P<count>\d+)\s+)?(?P<item>.+?)\s*$", re.IGNORECASE)
# A clause that sends the player somewhere is a delivery, not a collection.
DELIVERY = re.compile(r"\b(?:to|for)\s+(?:\[|[A-Z])", re.UNICODE)
CHAIN_ROW = re.compile(r'new\((\d+),\s*(\w+)Camp,')

# The two camps' newbie item blocks, which hold the same names twice.
ATHENS_BLOCK = range(300, 308)
SPARTA_BLOCK = range(400, 408)


def read_text(path):
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xff\xfe"):
        return raw.decode("utf-16-le", errors="replace")
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw.decode("utf-8-sig", errors="replace")
    return raw.decode("utf-8", errors="replace")


def quest_items():
    """name (lowercased, singular) -> sorted list of ids."""
    table = {}
    for line in read_text(ITEM_DAT).splitlines():
        parts = line.split("\t")
        if len(parts) < 2 or not parts[0].strip().isdigit():
            continue
        name = parts[1].strip().lower()
        if not name:
            continue
        table.setdefault(name, []).append(int(parts[0]))
    return table


def singular(name):
    name = name.strip(" .")
    if name.endswith("ies") and len(name) > 3:
        return name[:-3] + "y"
    if name.endswith("s") and not name.endswith("ss"):
        return name[:-1]
    return name


def resolve(item_text, table):
    """Longest QuestItem name contained in the clause, or None."""
    words = re.findall(r"[A-Za-z']+", item_text)
    best = None
    for start in range(len(words)):
        for end in range(len(words), start, -1):
            candidate = " ".join(words[start:end]).lower()
            for key in (candidate, singular(candidate)):
                if key in table:
                    if best is None or end - start > best[0]:
                        best = (end - start, key)
                    break
    if best is None:
        return None
    return best[1]


def pick(ids, camp):
    if len(ids) == 1:
        return ids[0], "table"
    athens = [i for i in ids if i in ATHENS_BLOCK]
    sparta = [i for i in ids if i in SPARTA_BLOCK]
    if camp == "Athens" and len(athens) == 1:
        return athens[0], "camp-block"
    if camp == "Sparta" and len(sparta) == 1:
        return sparta[0], "camp-block"
    return None, "ambiguous"


def chain():
    rows = []
    with open(CHAIN, "r", encoding="utf-8") as handle:
        for line in handle:
            m = CHAIN_ROW.search(line)
            if m:
                rows.append((int(m.group(1)), m.group(2)))
    return rows


# Clauses the wording rules do not reach, each taken from a captured frame
# rather than from the text. They are the evidence that fixes the offsets and
# the id space, so they are pinned here as well as in the checks.
VERIFIED = {
    # 2026-10-04 09:01:29  S2C 10081  +20 = 123  +36 = 10
    1146: (123, 10),
    # 2026-10-04 09:00:49  S2C 10090  descriptor +32 = 303  +48 = 10
    1557: (303, 10),
}


def main():
    table = quest_items()
    rows = []
    notes = []
    for quest_id, camp in chain():
        path = os.path.join(QUEST_TEXT_DIR, "%d.dat" % quest_id)
        if not os.path.exists(path):
            continue
        block = OBJECTIVE_BLOCK.search(read_text(path))
        if not block:
            continue
        body = block.group("body").replace("\r", " ").replace("\n", " ")
        body = COLOR.sub("", body)
        for clause in CLAUSE_SPLIT.split(body):
            clause = clause.strip(" .\t")
            if not clause:
                continue
            m = COLLECT.match(clause)
            if not m:
                continue
            item_text = BRACKETS.sub("", m.group("item")).strip()
            if DELIVERY.search(item_text):
                continue
            key = resolve(item_text, table)
            if key is None:
                notes.append((quest_id, camp, clause, "no QuestItem name"))
                continue
            item_id, how = pick(table[key], camp)
            if item_id is None:
                notes.append((quest_id, camp, clause,
                              "ambiguous %s" % table[key]))
                continue
            count = int(m.group("count")) if m.group("count") else 1
            rows.append((quest_id, camp, item_id, count, key, how, clause))

    # A clause the wording rules miss but a capture settles, e.g. 1557's
    # "back 10 Deer Antlers to [Props]Thoas" reads as a delivery.
    emitted = {row[0] for row in rows}
    for quest_id, (item_id, count) in sorted(VERIFIED.items()):
        if quest_id in emitted:
            continue
        camp = next((c for q, c in chain() if q == quest_id), "?")
        rows.append((quest_id, camp, item_id, count, "",
                     "captured", "captured frame"))

    rows.sort()
    lines = []
    lines.append("namespace Godswar.Server.Domain.World.Content;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// The item a quest asks the player to bring back, and how many.")
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append("/// A quest's objectives are not all kills: the client's own text also")
    lines.append("/// names an item (\"Kill 15 Little Snakes and collect 5 Snake Tails\").")
    lines.append("/// That objective is a separate pair of fields from the parallel")
    lines.append("/// monster/count arrays, so it needs its own table:")
    lines.append("/// <list type=\"bullet\">")
    lines.append("/// <item><c>10082</c> carries the item id at <c>+24</c> and the count")
    lines.append("/// at <c>+40</c>.</item>")
    lines.append("/// <item><c>10076</c> and <c>10081</c> carry the same pair four bytes")
    lines.append("/// earlier, at <c>+20</c> and <c>+36</c>.</item>")
    lines.append("/// </list>")
    lines.append("/// Leaving the pair zero is what makes such a quest draw only its kill")
    lines.append("/// objective.")
    lines.append("/// <para>")
    lines.append("/// Generated by <c>tools/gen_quest_collect_objectives.py</c>; do not edit")
    lines.append("/// by hand. The ids are the client's own <c>Text/QuestItem.dat</c> names,")
    lines.append("/// and every entry is named in the comment beside it so a wrong one can")
    lines.append("/// be spotted without the client's tables to hand.")
    lines.append("/// </para>")
    lines.append("/// </remarks>")
    lines.append("internal static class StarterQuestCollectObjectives")
    lines.append("{")
    lines.append("    /// <summary>One collected item and how many are wanted.</summary>")
    lines.append("    internal readonly record struct CollectObjective(")
    lines.append("        uint ItemId,")
    lines.append("        int Required);")
    lines.append("")
    lines.append("    public static IReadOnlyDictionary<uint, CollectObjective> ByQuestId")
    lines.append("        { get; } = new Dictionary<uint, CollectObjective>")
    lines.append("        {")
    for quest_id, camp, item_id, count, key, how, clause in rows:
        lines.append("            // %s: \"%s\"%s"
                     % (camp, clause.strip(),
                        "" if how == "table" else "  (id by camp block)"))
        lines.append("            [%du] = new CollectObjective(%du, %d),"
                     % (quest_id, item_id, count))
    lines.append("        };")
    lines.append("")
    lines.append("    /// <summary>The collect objective a quest names, if it names one.</summary>")
    lines.append("    public static bool TryGet(uint questId, out CollectObjective objective)")
    lines.append("    {")
    lines.append("        if (ByQuestId.TryGetValue(questId, out var found))")
    lines.append("        {")
    lines.append("            objective = found;")
    lines.append("            return true;")
    lines.append("        }")
    lines.append("")
    lines.append("        objective = default;")
    lines.append("        return false;")
    lines.append("    }")
    lines.append("}")
    lines.append("")

    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))

    print("entries emitted: %d" % len(rows))
    print("dropped clauses: %d" % len(notes))
    print()
    print("%-7s %-8s %-6s %-6s %s" % ("quest", "camp", "item", "count", "clause"))
    for quest_id, camp, item_id, count, key, how, clause in rows:
        print("%-7d %-8s %-6d %-6d %s%s"
              % (quest_id, camp, item_id, count, clause.strip(),
                 "" if how == "table" else "   [camp block]"))
    if notes:
        print()
        print("=== dropped ===")
        for quest_id, camp, clause, why in notes:
            print("  %-6d %-8s %-60s %s" % (quest_id, camp, clause[:60], why))


if __name__ == "__main__":
    sys.exit(main())
