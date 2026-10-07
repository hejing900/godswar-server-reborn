"""Generate the catalog from client data using the user-confirmed quest tables.

Independent serial main lines: the original 1-38 chain, Thermopylae 371-382,
and Thebes 573-580. Thebes 581-626 is independently available daily main-line
content at exactly the quest level. Selected utility quests are minimum-level,
one-time content. Original UIQuestSort/Color/levels remain source metadata.
"""

import os
import re
import sys

QUEST_XML = r"D:\Godswar Origin\Localization\zh_cn\Settings\Sys\Quest.xml"
TEXT_DIR = r"D:\Godswar Origin\Localization\en_us\Text\Quest"
PUBLISHED_NPCS = r"D:\Godswar Origin\npc-translation\published-npcs.txt"
OUTPUT = r"D:\Godswar-Reborn-main\src\Godswar.Server\Domain\World\Content\StarterQuestChain.cs"
SPARTA_CAMP = "Sparta"
ATHENS_CAMP = "Athens"
SPARTA_REGIONS = ("Sparta_", "Peloponnese_", "Nemea_", "Argolis_", "Derveni_")
ATHENS_REGIONS = ("Athens_", "Marathon_", "Parnitha_", "Megara_", "Plataea_")
SCROLL_GIVER = "QuestScroll"
MIRROR = 1000
STARTER_MAIN_IDS = tuple(i for i in range(518, 566) if i not in (535, 536, 557, 558))
THERMOPYLAE_IDS = tuple(range(371, 383))
THEBES_INTRO_IDS = tuple(range(573, 581))
THEBES_DAILY_IDS = tuple(range(581, 627))
MAIN_IDS = (*STARTER_MAIN_IDS, *THERMOPYLAE_IDS, *THEBES_INTRO_IDS, *THEBES_DAILY_IDS)
BRANCH_IDS = frozenset(range(552, 557))
SERIAL_IDS = tuple(i for i in STARTER_MAIN_IDS if i not in BRANCH_IDS)
# Verified NPC exploration/bounty/trial and feature-introduction quest IDs.
# Scroll acceptance is a separate item flow and is not an NPC feature quest.
ONE_TIME_UTILITY_IDS = frozenset((*range(118, 123), *range(214, 223), 139,
                                328, 329, 345, 349, 383, 468, 472, 473,
                                535, 536, 557, 558))
DAILY_IDS = frozenset((*range(69, 101), *range(342, 354),
                       *THEBES_DAILY_IDS)) - ONE_TIME_UTILITY_IDS
PREREQUISITES = {quest_id: previous for previous, quest_id
                 in zip(SERIAL_IDS, SERIAL_IDS[1:])}
PREREQUISITES.update({quest_id: 551 for quest_id in BRANCH_IDS})
for chain in (THERMOPYLAE_IDS, THEBES_INTRO_IDS):
    PREREQUISITES.update({quest_id: previous for previous, quest_id
                         in zip(chain, chain[1:])})
REWARD = re.compile(r"(Exp|TP|Silver|Gold|BindGold)\s*:\s*(-?\d+)", re.IGNORECASE)
APPEND = re.compile(r"Append\s*\{(?P<body>.*?)\}", re.DOTALL)
END_TEXT = re.compile(r"EndText\s*\{(?P<body>.*?)\}", re.DOTALL)


def read_text(quest_id):
    path = os.path.join(TEXT_DIR, f"{quest_id}.dat")
    if not os.path.exists(path):
        return None
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xff\xfe"):
        return raw.decode("utf-16-le", errors="replace")
    if raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16-be", errors="replace")
    for encoding in ("utf-8-sig", "utf-8", "gb18030"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    return raw.decode("gb18030", errors="replace")


def rewards(quest_id):
    text = read_text(quest_id)
    block = (APPEND.search(text) or END_TEXT.search(text)) if text else None
    values = {"exp": 0, "tp": 0, "silver": 0, "gold": 0}
    if block:
        for key, value in REWARD.findall(block.group("body")):
            if key.lower() in values:
                values[key.lower()] = int(value)
    return tuple(values[key] for key in ("exp", "tp", "silver", "gold"))


def quest_rows():
    rows = {}
    with open(QUEST_XML, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = re.match(r'\s*<Quest(\d+)\s', line)
            if match:
                rows[int(match.group(1))] = dict(re.findall(r'(\w+)="([^"]*)"', line))
    return rows


def published_npcs():
    if not os.path.exists(PUBLISHED_NPCS):
        return set()
    with open(PUBLISHED_NPCS, "r", encoding="utf-8-sig") as handle:
        return {line.strip() for line in handle if line.strip()}


def camp_of(quest_id, row):
    keys = [row.get("GiverName", ""), row.get("ResponderName", "")]
    if any(key.startswith(ATHENS_REGIONS) for key in keys):
        return ATHENS_CAMP
    if any(key.startswith(SPARTA_REGIONS) for key in keys):
        return SPARTA_CAMP
    return ATHENS_CAMP if quest_id > MIRROR else SPARTA_CAMP


def as_int(value, default=0):
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def base_id(quest_id):
    return quest_id - MIRROR if quest_id > MIRROR else quest_id


def is_main_line(quest_id, row):
    return base_id(quest_id) in MAIN_IDS


def main_line_segment(quest_id):
    quest_id = base_id(quest_id)
    if quest_id in STARTER_MAIN_IDS:
        return "1-38"
    if quest_id in THERMOPYLAE_IDS:
        return "Thermopylae"
    if quest_id in THEBES_INTRO_IDS:
        return "Thebes-130"
    if quest_id in THEBES_DAILY_IDS:
        return "Thebes-daily"
    return ""


def select(rows, npcs):
    ordered = {SPARTA_CAMP: [], ATHENS_CAMP: []}
    skipped = []
    for quest_id, row in rows.items():
        giver = row.get("GiverName", "")
        responder = row.get("ResponderName", "")
        scroll = giver == SCROLL_GIVER
        level = as_int(row.get("MinLevel"))
        ceiling = as_int(row.get("MaxLevel"))
        reason = None
        if level == ceiling == 200:
            reason = "the never-offered 200/200 copy"
        elif "_" not in giver and not scroll:
            reason = "no npc or scroll giver"
        elif npcs and ((not scroll and giver not in npcs) or
                       (responder and "_" in responder and responder not in npcs)):
            reason = "its npc is not published"
        if reason:
            skipped.append((quest_id, reason))
            continue
        camp = camp_of(quest_id, row)
        main = is_main_line(quest_id, row)
        previous = PREREQUISITES.get(base_id(quest_id), 0) if main else 0
        if previous and quest_id > MIRROR:
            previous += MIRROR
        # Keep the original field indexes used by the catalog inspection tools.
        ordered[camp].append((quest_id, camp, level, ceiling, quest_id,
                              giver, responder, as_int(row.get("UIQuestSort")),
                              as_int(row.get("Color")), main,
                              main_line_segment(quest_id), scroll, previous,
                              base_id(quest_id) in DAILY_IDS,
                              as_int(row.get("GiverMapID"), -1),
                              as_int(row.get("ResponderMapID"), -1),
                              base_id(quest_id) in ONE_TIME_UTILITY_IDS,
                              base_id(quest_id) in THEBES_DAILY_IDS))
    for items in ordered.values():
        items.sort(key=lambda item: (item[2], item[0]))
    selected_ids = {item[0] for items in ordered.values() for item in items}
    required_ids = {i + offset for i in (*MAIN_IDS, *DAILY_IDS, *ONE_TIME_UTILITY_IDS)
                    for offset in (0, MIRROR)}
    missing = required_ids - selected_ids
    if missing:
        raise ValueError(f"Confirmed quests missing from catalog: {sorted(missing)}")
    return ordered, skipped


def emit(ordered):
    lines = ['''namespace Godswar.Server.Domain.World.Content;

/// <summary>Client quest data with the user-confirmed main-line and daily rules.</summary>
/// <remarks>Generated by tools/gen_sparta_quest_chain.py; do not edit by hand.
/// Original UIQuestSort/Color/MinLevel/MaxLevel remain client metadata.
/// Serial main lines have separate segments; daily main line has no prerequisites.
/// </remarks>
internal static class StarterQuestChain
{
    public const string SpartaCamp = "Sparta";
    public const string AthensCamp = "Athens";
    public const string ScrollGiverKey = "QuestScroll";
    public const int SortStory = 0;
    public const int SortRepeat = 1;
    public const int SortCity = 2;
    public const int SortDaily = 3;
    public const int SortGuild = 4;

    internal readonly record struct Step(
        uint QuestId,
        string Camp,
        string GiverKey,
        string ResponderKey,
        int Experience,
        int TalentPoints,
        int Silver,
        int Gold,
        int MinLevel,
        int MaxLevel,
        bool IsMainLine,
        int UIQuestSort,
        int Color,
        string Segment,
        bool IsQuestScroll,
        uint PrerequisiteQuestId = 0,
        bool IsConfiguredDaily = false,
        short GiverMapId = -1,
        short ResponderMapId = -1,
        bool IsOneTimeUtility = false,
        bool RequiresExactLevel = false)
    {
        public bool HasSegment => Segment.Length != 0;
        public bool IsOptionalMainLine => IsMainLine &&
            QuestId is >= 552 and <= 556 or >= 1552 and <= 1556;
        public bool IsDaily => !IsOneTimeUtility &&
            (IsConfiguredDaily || (!IsMainLine &&
             (Color == 2 || UIQuestSort == SortDaily || UIQuestSort == SortGuild)));
        public bool IsRepeat => !IsMainLine && !IsOneTimeUtility && !IsDaily &&
            UIQuestSort == SortRepeat && !IsQuestScroll;
        public int MaxCompletionsPerDay => IsDaily ? 1 : IsRepeat ? 3 : 0;
        public bool IsLevelOutgrowthLimited => RequiresExactLevel || (!IsMainLine && !IsOneTimeUtility);
        public bool IsNineLevelLimited => !RequiresExactLevel && !IsMainLine && !IsOneTimeUtility;
    }

    public static IReadOnlyList<Step> Steps { get; } =
    [''']
    for camp in (SPARTA_CAMP, ATHENS_CAMP):
        items = ordered[camp]
        lines.append(f"        // {camp}: {len(items)} quests, {sum(item[9] for item in items)} main-line.")
        for (quest_id, _, level, ceiling, _, giver, responder, sort, color,
             main, segment, scroll, previous, daily, giver_map, responder_map,
             utility, exact_level) in items:
            exp, tp, silver, gold = rewards(quest_id)
            segment_literal = f'"{segment}"' if segment else "string.Empty"
            flag = lambda value: "true" if value else "false"
            lines.append(
                f'        new({quest_id}, {camp}Camp, "{giver}", "{responder}", '
                f'{exp}, {tp}, {silver}, {gold}, {level}, {ceiling}, {flag(main)}, '
                f'{sort}, {color}, {segment_literal}, {flag(scroll)}, {previous}, '
                f'{flag(daily)}, {giver_map}, {responder_map}, '
                f'{flag(utility)}, {flag(exact_level)}),')
    lines.append('''    ];

    public static Step? Find(uint questId)
    {
        foreach (var step in Steps)
        {
            if (step.QuestId == questId)
            {
                return step;
            }
        }

        return null;
    }
}
''')
    return "\n".join(lines)


def main():
    ordered, skipped = select(quest_rows(), published_npcs())
    out = sys.argv[1] if len(sys.argv) > 1 else OUTPUT
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(emit(ordered))
    print(f"wrote {out}")
    for camp, items in ordered.items():
        print(f"{camp}: {len(items)} quests; main={sum(item[9] for item in items)}; "
              f"confirmed daily={sum(item[13] for item in items)}")
    print(f"Skipped {len(skipped)} source rows")
    return 0


if __name__ == "__main__":
    sys.exit(main())
