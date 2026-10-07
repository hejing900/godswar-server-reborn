namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateQuestWideProgress() => new(
        "20261007_232_quest_wide_progress",
        "Preserve four quest counters above 255 kills",
        """
        ALTER TABLE character_quests ALTER COLUMN progress TYPE bigint;
        -- Convert existing four eight-bit counters to four sixteen-bit counters.
        -- Completed rows keep zero; existing accepted quests retain every counter.
        UPDATE character_quests SET progress =
            (progress & 255) |
            (((progress >> 8) & 255) << 16) |
            (((progress >> 16) & 255) << 32) |
            (((progress >> 24) & 255) << 48);
        """);
}
