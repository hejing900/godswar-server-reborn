namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateCharacterQuestDaily() => new(
        "20261005_230_character_quest_daily",
        "Persist the per-day quest completion count",
        """
        CREATE TABLE IF NOT EXISTS character_quest_daily (
            character_id integer NOT NULL
                REFERENCES character_base(id) ON DELETE CASCADE,
            quest_id integer NOT NULL,
            reset_day date NOT NULL,
            completions integer NOT NULL DEFAULT 0
                CHECK (completions >= 0),
            PRIMARY KEY (character_id, quest_id)
        );

        CREATE INDEX IF NOT EXISTS ix_character_quest_daily_reset_day
            ON character_quest_daily (reset_day);
        """);
}
