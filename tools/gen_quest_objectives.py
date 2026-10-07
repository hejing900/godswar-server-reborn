"""Generate the chain's kill objectives from the client's own quest data.

The counts and the target monsters are only in the quest text. Quest 520 says

    Kill 10 Dumb Wood Men and then go talk with [Village Leader]Stefanos.

and 520's Append block gives Exp:355 / TP:4, which is what StarterQuestChain
already carries - so the text is the same source the chain was built from. Quest
528 asks for two things at once ("Kill Addiya the Destroyer and destroy 8 Fake
Treasures") and 545 asks for three ("Destroy 30 Fluttering Swords, 30 Animated
Axes and 30 Wild Spears, and then report to Farmer Kozma"), so a quest can have
several objectives with their own counts.

The positions come from Quest.xml's CreatureMapID / CreatureMapPos and are
paired with the objectives in order. They are the fallback: the monster's
display name is what actually identifies the target.

Previously working objectives are retained unchanged. New objectives are read
by complete_quest_kill_rules from audited client fields. Unresolved target
bindings are listed explicitly and must never become automatic completions.
The legacy parse_objectives helper remains for reading explicit capture names;
its historical missing-number default is not a verified quantity.
"""

import os
import re
import sys
from pathlib import Path

QUEST_XML = r"D:\Godswar Origin\Localization\en_us\Settings\Sys\Quest.xml"
QUEST_TEXT_DIR = r"D:\Godswar Origin\Localization\en_us\Text\Quest"
QUEST_MONSTER = r"D:\Godswar Origin\Localization\en_us\Text\QuestMonster.dat"
CHAIN = (r"D:\Godswar-Reborn-main\src\Godswar.Server\Domain\World\Content"
         r"\StarterQuestChain.cs")
OUTPUT = (r"D:\Godswar-Reborn-main\src\Godswar.Server\Domain\World\Content"
          r"\StarterQuestObjectives.cs")

# Verbs a monster death can satisfy.
KILL_VERBS = ("kill", "destroy", "slay", "defeat", "eliminate", "exterminate")
# Anything the server cannot observe; such a clause is not an objective.
IGNORED_MARKERS = (
    "talk", "report", "speak", "visit", "meet", "find", "go to", "return",
    "deliver", "bring", "collect", "gather", "obtain", "use", "unfold",
    "explore", "escort", "protect",
)

COLOR = re.compile(r"\|c[0-9a-fA-F]{8}")
BRACKETS = re.compile(r"\[[^\]]*\]")
CLAUSE_SPLIT = re.compile(r",|;|\band\b|\bthen\b", re.IGNORECASE)
OBJECTIVE = re.compile(
    r"^\s*(?:(?P<verb>[A-Za-z]+)\s+)?(?:the\s+)?(?:(?P<count>\d+)\s+)?"
    r"(?P<target>[A-Za-z][^,;.]*?)\s*$")
# A clause with no verb lead: the target on its own, after a kill verb carried
# over from the previous clause.
TARGET_ONLY = re.compile(
    r"^\s*(?:the\s+)?(?:(?P<count>\d+)\s+)?(?P<target>[A-Za-z][^,;.]*?)\s*$")

OBJECTIVE_BLOCK = re.compile(r"Objectives\s*\{(?P<body>.*?)\}", re.DOTALL)

# A PvP objective names a player, not a monster: "Defeat a player Level 70 or
# higher" has no monster id and no frame that could ever count it.
PLAYER_TARGET = re.compile(r"\bplayer\b.*\blevel\b", re.IGNORECASE)

# Plurals no rule reaches. The quest text says "Wild Oxen" where the client's
# monster table says "Wild Ox".
IRREGULAR_PLURALS = {"men": "man", "oxen": "ox"}


def chain_ids():
    ids = []
    with open(CHAIN, "r", encoding="utf-8") as handle:
        for line in handle:
            match = re.match(r"\s*new\((\d+),", line)
            if match:
                ids.append(int(match.group(1)))
    if not ids:
        raise SystemExit("no chain rows found")
    return ids


def quest_rows():
    rows = {}
    with open(QUEST_XML, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = re.match(r'\s*<Quest(\d+)\s', line)
            if not match:
                continue
            rows[int(match.group(1))] = dict(
                re.findall(r'(\w+)="([^"]*)"', line))
    return rows


def positions(attributes):
    maps = [value for value in attributes.get("CreatureMapID", "").split(",")
            if value]
    coords = [value for value in
              attributes.get("CreatureMapPos", "").split(",") if value]
    pairs = []
    if not maps or len(coords) % 2 != 0:
        return pairs
    for index in range(0, len(coords), 2):
        target = min(index // 2, len(maps) - 1)
        pairs.append((int(maps[target]),
                      float(coords[index]),
                      float(coords[index + 1])))
    return pairs


def read_text(path):
    """Read a client text file whose encoding varies from file to file."""
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xff\xfe"):
        return raw.decode("utf-16-le", errors="replace")
    if raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16-be", errors="replace")
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw.decode("utf-8-sig", errors="replace")
    for encoding in ("utf-8", "gb18030"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    return raw.decode("gb18030", errors="replace")


def objectives_text(quest_id):
    path = os.path.join(QUEST_TEXT_DIR, f"{quest_id}.dat")
    if not os.path.exists(path):
        return None
    match = OBJECTIVE_BLOCK.search(read_text(path))
    return match.group("body") if match else None


def parse_objectives(text, monsters=None):
    """Pull (target, count) pairs out of an Objectives block.

    A clause with no kill verb of its own is only taken as a target when the
    client's own monster table recognises it, which is what separates the real
    name in "Defeat the strongest boss, Scorpion Lord Selket!" from the
    instructions and appositives that sentence-splitting also produces
    ("claim your reward from Aithra", "leader of the invading Persian army").
    Without the table those clauses are dropped rather than guessed at.
    """
    plain = COLOR.sub("", text).replace("\r", " ").replace("\n", " ")
    found = []
    verb = None
    for clause in CLAUSE_SPLIT.split(plain):
        clause = clause.strip(" .\t")
        if not clause:
            continue
        lowered = clause.lower()
        if any(marker in lowered for marker in IGNORED_MARKERS):
            continue
        match = OBJECTIVE.match(clause)
        if not match:
            continue
        clause_verb = (match.group("verb") or "").lower()
        if clause_verb:
            if clause_verb not in KILL_VERBS:
                # "Defeat the strongest boss, Scorpion Lord Selket!": the second
                # clause starts with a word that is not a verb at all, so the
                # whole clause is the target and the verb carries over from the
                # first one. Without this the sentence's real target is dropped.
                fallback = TARGET_ONLY.match(clause)
                if fallback is None or verb is None or monsters is None:
                    continue
                count = (int(fallback.group("count"))
                         if fallback.group("count") else 1)
                target = BRACKETS.sub("", fallback.group("target")).strip()
                if resolve_monster(target, monsters)[0] == 0:
                    continue
            else:
                verb = clause_verb
                count = int(match.group("count")) if match.group("count") else 1
                target = BRACKETS.sub("", match.group("target")).strip()
        elif verb is None:
            continue
        else:
            count = int(match.group("count")) if match.group("count") else 1
            target = BRACKETS.sub("", match.group("target")).strip()
        # "Kill enough Mountain Lions" and "Kill some Snakes" name a quantity in
        # words; the count is what the frame needs, not those words. "the" can
        # also arrive glued to the name when colour markup sat between them
        # ("Destroy the|cffd7dd80Purple Winged Demons").
        for filler in ("enough ", "some ", "the ", "a ", "an "):
            if target.lower().startswith(filler):
                target = target[len(filler):].strip()
        if (target.lower().startswith("the") and len(target) > 3
                and target[3].isupper()):
            target = target[3:].strip()
        # "Kill the Dumb Wood Men in the Sparta Starting Area" names a place as
        # well as a target; the place is not part of the monster's name.
        for marker in (" in the ", " at the ", " near the ", " in ", " at "):
            index = target.lower().find(marker)
            if index > 0:
                target = target[:index].strip()
                break
        if not target or count <= 0:
            continue
        if PLAYER_TARGET.search(target):
            # "Defeat a player Level 70 or higher" is a PvP objective: the
            # target is another character, so there is no monster to count and
            # no frame that could ever move it.
            continue
        found.append((target, count))
    return found


def emit(generated):
    lines = []
    lines.append("using System.Text;")
    lines.append("using System.Text.RegularExpressions;")
    lines.append("")
    lines.append("namespace Godswar.Server.Domain.World.Content;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// One thing a quest asks the player to kill, and how many of them.")
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append("/// <paramref name=\"MonsterId\"/> is the client's own id from")
    lines.append("/// Text/QuestMonster.dat, which is what the frames that drive the quest")
    lines.append("/// window carry. The map and coordinates are the position the client")
    lines.append("/// shows for the target, used when the name cannot be resolved.")
    lines.append("/// <para>")
    lines.append("/// <paramref name=\"Match\"/> is the name the client's monster table")
    lines.append("/// gives that id, and is only set when the quest text calls the target")
    lines.append("/// something else - quest 528 asks for eight \"Fake Treasures\" while the")
    lines.append("/// world carries them as \"Juno's Box\" (id 1023). A kill is credited on")
    lines.append("/// this name when there is one, otherwise on the quest text.")
    lines.append("/// </para>")
    lines.append("/// </remarks>")
    lines.append("internal readonly record struct QuestObjective(")
    lines.append("    string Target,")
    lines.append("    string Match,")
    lines.append("    int Required,")
    lines.append("    uint MonsterId,")
    lines.append("    uint MapId,")
    lines.append("    float X,")
    lines.append("    float Z);")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// The newbie chain's kill objectives, read from the client's own quest")
    lines.append("/// text (Text/Quest/&lt;id&gt;.dat) and Quest.xml.")
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append("/// Generated by <c>tools/gen_quest_objectives.py</c>; do not edit by")
    lines.append("/// hand. A quest that is absent here has no objective this server can")
    lines.append("/// verify - a plain talk quest, or one that only asks for items - and is")
    lines.append("/// therefore handed in as soon as the player asks.")
    lines.append("/// <para>")
    lines.append("/// Progress is stored as up to four counters packed into the single")
    lines.append("/// progress integer of the character's quest row, because a quest can")
    lines.append("/// name up to three targets.")
    lines.append("/// </para>")
    lines.append("/// </remarks>")
    lines.append("internal static partial class StarterQuestObjectives")
    lines.append("{")
    lines.append("    /// <summary>Bits one objective counter occupies.</summary>")
    lines.append("    public const int CounterBits = 16;")
    lines.append("")
    lines.append("    /// <summary>How many objectives one quest can track.</summary>")
    lines.append("    public const int CounterSlots = 4;")
    lines.append("")
    lines.append("    /// <summary>Largest count a slot can hold.</summary>")
    lines.append("    public const int CounterMaximum = short.MaxValue;")
    lines.append("")
    lines.append("    /// <summary>How close a kill has to be when the name is unknown.</summary>")
    lines.append("    public const float MatchRadius = 40.0f;")
    lines.append("")
    lines.append("    public static IReadOnlyDictionary<uint, QuestObjective[]> ByQuestId")
    lines.append("        { get; } = new Dictionary<uint, QuestObjective[]>")
    lines.append("        {")
    for quest_id, objectives in generated:
        entries = ", ".join(
            f'new QuestObjective("{target}", "{match}", {count}, '
            f'{monster_id}u, {map_id}u, {x}f, {z}f)'
            for target, match, count, monster_id, map_id, x, z in objectives)
        lines.append(f"            [{quest_id}u] = [{entries}],")
    lines.append("        };")
    lines.append("")
    lines.append("    /// <summary>The objectives of a quest, empty when it has none.</summary>")
    lines.append("    public static IReadOnlyList<QuestObjective> For(uint questId) =>")
    lines.append("        ByQuestId.TryGetValue(questId, out var objectives)")
    lines.append("            ? objectives")
    lines.append("            : [];")
    lines.append("")
    lines.append("    /// <summary>Reads one objective's counter out of the packed progress.</summary>")
    lines.append("    public static int Counter(long progress, int slot) =>")
    lines.append("        (int)((progress >> (slot * CounterBits)) & CounterMaximum);")
    lines.append("")
    lines.append("    /// <summary>Writes one objective's counter back into the progress.</summary>")
    lines.append("    public static long WithCounter(long progress, int slot, int value)")
    lines.append("    {")
    lines.append("        var clamped = Math.Clamp(value, 0, CounterMaximum);")
    lines.append("        var shift = slot * CounterBits;")
    lines.append("        return (progress & ~((long)ushort.MaxValue << shift)) |")
    lines.append("            ((long)clamped << shift);")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>True when every objective has been satisfied.</summary>")
    lines.append("    public static bool IsSatisfied(")
    lines.append("        IReadOnlyList<QuestObjective> objectives,")
    lines.append("        long progress)")
    lines.append("    {")
    lines.append("        for (var slot = 0; slot < objectives.Count; slot++)")
    lines.append("        {")
    lines.append("            if (Counter(progress, slot) < objectives[slot].Required)")
    lines.append("            {")
    lines.append("                return false;")
    lines.append("            }")
    lines.append("        }")
    lines.append("")
    lines.append("        return true;")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>True when a killed monster satisfies this objective.</summary>")
    lines.append("    public static bool Matches(")
    lines.append("        QuestObjective objective,")
    lines.append("        uint mapId,")
    lines.append("        string? monsterName,")
    lines.append("        float x,")
    lines.append("        float z)")
    lines.append("    {")
    lines.append("        if (objective.MapId != mapId)")
    lines.append("        {")
    lines.append("            return false;")
    lines.append("        }")
    lines.append("")
    lines.append("        if (Needle(NameOf(objective)) is { Length: > 0 } &&")
    lines.append("            Needle(monsterName) is { Length: > 0 })")
    lines.append("        {")
    lines.append("            // The quest text is sloppy about plurals - \"Woodland")
    lines.append("            // Wolves\" for the client's \"Woodland Wolf\" - so the two")
    lines.append("            // names are compared through every shape the plural can")
    lines.append("            // stand for, not just the folded text.")
    lines.append("            return SameName(NameOf(objective), monsterName);")
    lines.append("        }")
    lines.append("")
    lines.append("        var dx = objective.X - x;")
    lines.append("        var dz = objective.Z - z;")
    lines.append("        return (dx * dx) + (dz * dz) <= MatchRadius * MatchRadius;")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>")
    lines.append("    /// The name a kill has to carry to credit this objective: the client's")
    lines.append("    /// own name for the target when the quest text calls it something else.")
    lines.append("    /// </summary>")
    lines.append("    public static string NameOf(QuestObjective objective) =>")
    lines.append("        string.IsNullOrWhiteSpace(objective.Match)")
    lines.append("            ? objective.Target")
    lines.append("            : objective.Match;")
    lines.append("")
    lines.append("    /// <summary>")
    lines.append("    /// Reduces a display name to letters and digits so plurals and colour")
    lines.append("    /// markup do not stop a match.")
    lines.append("    /// </summary>")
    lines.append("    /// <remarks>")
    lines.append("    /// The quest text says \"Kill 10 Dumb Wood Men\" while the monster is")
    lines.append("    /// called \"Dumb Wood Man\", and 521 says \"Wooden Puppets\" for the")
    lines.append("    /// monster \"Wooden Puppet\", so a trailing plural is dropped and the")
    lines.append("    /// irregular \"men\" is folded onto \"man\".")
    lines.append("    /// </remarks>")
    lines.append("    internal static string Needle(string? value)")
    lines.append("    {")
    lines.append("        if (string.IsNullOrWhiteSpace(value))")
    lines.append("        {")
    lines.append("            return string.Empty;")
    lines.append("        }")
    lines.append("")
    lines.append("        var plain = ColorMarkup().Replace(value, string.Empty);")
    lines.append("        var builder = new StringBuilder(plain.Length);")
    lines.append("        var separator = false;")
    lines.append("        foreach (var character in plain)")
    lines.append("        {")
    lines.append("            if (char.IsLetterOrDigit(character))")
    lines.append("            {")
    lines.append("                builder.Append(char.ToLowerInvariant(character));")
    lines.append("                separator = true;")
    lines.append("            }")
    lines.append("            else if (separator)")
    lines.append("            {")
    lines.append("                builder.Append(' ');")
    lines.append("                separator = false;")
    lines.append("            }")
    lines.append("        }")
    lines.append("")
    lines.append("        var words = new List<string>();")
    lines.append("        foreach (var word in builder.ToString().Split(")
    lines.append("                     ' ', StringSplitOptions.RemoveEmptyEntries))")
    lines.append("        {")
    lines.append("            words.Add(word switch")
    lines.append("            {")
    lines.append("                \"men\" => \"man\",")
    lines.append("                _ when word.Length > 3 && word.EndsWith('s') =>")
    lines.append("                    word[..^1],")
    lines.append("                _ => word")
    lines.append("            });")
    lines.append("        }")
    lines.append("")
    lines.append("        return string.Join(' ', words);")
    lines.append("    }")
    lines.append("")
    lines.append("    [GeneratedRegex(")
    lines.append("        @\"\\|c[0-9a-fA-F]{8}\",")
    lines.append("        RegexOptions.CultureInvariant)]")
    lines.append("    private static partial Regex ColorMarkup();")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def monster_ids():
    """id -> name, from the client's own quest monster table."""
    table = {}
    for line in read_text(QUEST_MONSTER).splitlines():
        parts = line.rstrip("\n").split("\t")
        if len(parts) < 2 or not parts[0].strip().isdigit():
            continue
        table[int(parts[0].strip())] = parts[1].strip()
    if not table:
        raise SystemExit(f"no monsters parsed from {QUEST_MONSTER}")
    return table


def needle(value):
    """Reduce a name to letters and digits so plurals do not stop a match."""
    words = []
    for word in re.sub(r"[^0-9A-Za-z]+", " ", value).lower().split():
        if word == "men":
            words.append("man")
        elif len(word) > 3 and word.endswith("s"):
            words.append(word[:-1])
        else:
            words.append(word)
    return " ".join(words)


# Names the quest text uses that the client's monster table does not carry. The
# world content at the objective position is what actually stands there: quest
# 528 asks for "8 Fake Treasures" at (71,191) on map 0, and the only monsters
# within a few units of that spot are ten "Juno's Box" - which the monster table
# lists as 1023.
TARGET_ALIASES = {
    "fake treasure": "Juno's Box",
}


def fold_head_word(word):
    """A singular guess for a word that is not the target's head noun."""
    if word in IRREGULAR_PLURALS:
        return IRREGULAR_PLURALS[word]
    if word.endswith("men") and len(word) > 3:
        return word[:-3] + "man"
    if word.endswith("ies") and len(word) > 3:
        return word[:-3] + "y"
    if word.endswith("ves") and len(word) > 3:
        return word[:-3] + "f"
    if word.endswith("s") and not word.endswith("ss") and len(word) > 3:
        return word[:-1]
    return word


def shapes(value):
    """Every needle a quest target may be written as.
    The quest text is sloppy about plurals - "Woodland Wolves", "Persian Spies",
    "Plains Wolves" - while the client's monster table holds the singular, so the
    plural is unfolded here instead of guessed at on the table side. Folding only
    the last word and dropping a trailing "s" from the others is enough for every
    name in the chain; anything looser starts matching the wrong monster.
    """
    raw = re.sub(r"[^0-9A-Za-z]+", " ", value).lower().split()
    if not raw:
        return set()
    head = [fold_head_word(word) for word in raw[:-1]]

    word = raw[-1]
    stems = {word}
    if word.endswith("s"):
        stems.add(word[:-1])
    if word.endswith("es"):
        stems.add(word[:-2])
    if word.endswith("ies"):
        stems.add(word[:-3] + "y")
    if word.endswith("ves"):
        stems.add(word[:-3] + "f")
    if word == "men":
        stems.add("man")
    if word in IRREGULAR_PLURALS:
        # "Wild Oxen" is the quest's word for the client's "Wild Ox".
        stems.add(IRREGULAR_PLURALS[word])
    if word.endswith("men") and len(word) > 3:
        # "Spearmen" is the plural of "Spearman", not of "Spearmen".
        stems.add(word[:-3] + "man")

    found = {needle(" ".join(head + [stem])) for stem in stems}
    found.add(needle(value))
    return {value for value in found if value}


def squashed(values):
    """The same names with every space removed, for names the table runs together."""
    return {value.replace(" ", "") for value in values}


def contains_words(haystack, needle):
    """True when every word of needle appears consecutively in haystack.

    Deliberately not a character-level substring: quest 535 says "claim rewards
    from the board", and "boa" sits inside "board", so a character match credits
    the kill to a Boa. Whole words keep the legitimate longer phrases working
    ("Little Snakes outside the Spartan Starting Area" still contains "Little
    Snake") without inventing monsters out of ordinary prose.
    """
    hay = haystack.split()
    pin = needle.split()
    if not pin or len(pin) > len(hay):
        return False
    for start in range(len(hay) - len(pin) + 1):
        if hay[start:start + len(pin)] == pin:
            return True
    return False


def resolve_monster(name, table):
    """The client's monster-table entry for a quest target: (id, name or '')."""
    wanted = shapes(TARGET_ALIASES.get(needle(name), name))
    for monster_id, monster_name in table.items():
        if needle(monster_name) in wanted:
            return monster_id, monster_name
    for monster_id, monster_name in table.items():
        actual = needle(monster_name)
        if actual and any(contains_words(shape, actual) or
                          contains_words(actual, shape)
                          for shape in wanted):
            return monster_id, monster_name
    # A few names are only written apart in the quest text: the table carries
    # "Young RedDragon" for "Young Red Dragons".
    joined = squashed(wanted)
    for monster_id, monster_name in table.items():
        if squashed([needle(monster_name)]) & joined:
            return monster_id, monster_name
    return 0, ""


def main():
    # The retained rules preserve existing playable tasks. New rules come only
    # from audited client goal fields, with quantity provenance recorded.
    generated = []
    from complete_quest_kill_rules import complete
    generated, unresolved = complete(generated, sys.modules[__name__])
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(emit(generated))
    pending = Path(OUTPUT).with_name('QuestUnresolvedRequirements.Generated.cs')
    pending.write_text('// Generated by tools/gen_quest_objectives.py.\n'
        'namespace Godswar.Server.Domain.World.Content;\n'
        'internal static class QuestUnresolvedRequirements\n{\n'
        '    internal static readonly HashSet<uint> QuestIds = [' +
        ', '.join(f'{q}u' for q in unresolved) + '];\n}\n', encoding='utf-8')
    print(f"wrote {OUTPUT} with {len(generated)} quest(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
