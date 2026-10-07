namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The per-day quest count is durable: it lives in <c>character_quest_daily</c>,
/// is registered as a migration, and is written back on every capped hand-in.
/// </summary>
/// <remarks>
/// The restart half of the contract cannot be exercised in-process - it is the
/// table surviving the process that makes it true - so this reads the sources that
/// define it and pins the three things that would silently break it: the table not
/// being created, the migration not being registered, and the write not being an
/// upsert keyed on the day.
/// <para>
/// The day-comparison rule itself is checked live by
/// <see cref="QuestProtocolChecks"/>, because a stale day reading back as zero is
/// pure arithmetic and needs no database.
/// </para>
/// </remarks>
internal static partial class QuestProtocolChecks
{
    private static void CheckQuestDailyPersistenceRules()
    {
        var root = FindRepositoryRoot();
        var migrationPath = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "State",
            "DatabaseMigrations",
            "PostgresSchemaMigrationCatalog.CharacterQuestDaily.cs");
        Check.True(
            File.Exists(migrationPath),
            "the per-day quest table has a migration file");
        var migration = File.ReadAllText(migrationPath);
        Check.True(
            migration.Contains(
                "CREATE TABLE IF NOT EXISTS character_quest_daily",
                StringComparison.Ordinal),
            "the migration creates character_quest_daily");
        Check.True(
            migration.Contains("character_id integer NOT NULL", StringComparison.Ordinal) &&
            migration.Contains("quest_id integer NOT NULL", StringComparison.Ordinal) &&
            migration.Contains("reset_day date NOT NULL", StringComparison.Ordinal) &&
            migration.Contains(
                "completions integer NOT NULL DEFAULT 0",
                StringComparison.Ordinal),
            "the table keeps the character, the quest, the quota day and the count");
        Check.True(
            migration.Contains(
                "PRIMARY KEY (character_id, quest_id)",
                StringComparison.Ordinal),
            "one row per character and quest");
        Check.True(
            migration.Contains(
                "REFERENCES character_base(id) ON DELETE CASCADE",
                StringComparison.Ordinal),
            "the counts follow the character row");

        // Registered in the forward-only catalog, which is what runs it.
        var catalogPath = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "State",
            "DatabaseMigrations",
            "PostgresSchemaMigrationCatalog.cs");
        var catalog = File.ReadAllText(catalogPath);
        Check.True(
            catalog.Contains("CreateCharacterQuestDaily()", StringComparison.Ordinal),
            "the per-day quest migration is registered in the catalog");

        // The write is the hand-over's upsert: a row already stamped with today
        // keeps counting up, and a row from another day restarts at one.
        var storePath = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "State",
            "PostgresGameStore.QuestState.cs");
        var store = File.ReadAllText(storePath);
        Check.True(
            store.Contains(
                "INSERT INTO character_quest_daily",
                StringComparison.Ordinal),
            "the store writes the per-day counts");
        Check.True(
            store.Contains(
                "ON CONFLICT (character_id, quest_id)",
                StringComparison.Ordinal) &&
            store.Contains("reset_day = EXCLUDED.reset_day", StringComparison.Ordinal) &&
            store.Contains(
                "completions = EXCLUDED.completions",
                StringComparison.Ordinal),
            "the write upserts the day and the count together");
        Check.True(
            store.Contains(
                "SELECT daily.quest_id, daily.reset_day, daily.completions",
                StringComparison.Ordinal),
            "the store reads the day stamp back with the count");

        // The gate's own reads and writes are wired to it.
        var handlerPath = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.Quests.cs");
        var handler = File.ReadAllText(handlerPath);
        Check.True(
            handler.Contains(
                "LoadQuestDailyCompletionsAsync",
                StringComparison.Ordinal) &&
            handler.Contains(
                "SaveQuestDailyCompletionsAsync",
                StringComparison.Ordinal),
            "the quest handler loads and stores the per-day counts");

        // The day itself is server-local and rolls over at noon, in exactly one
        // place, so the gate and the store cannot drift apart.
        var dayPath = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "State",
            "QuestDailyState.cs");
        Check.True(
            File.Exists(dayPath),
            "the quota-day rule lives in its own file");
        var day = File.ReadAllText(dayPath);
        Check.True(
            day.Contains("RolloverHour = 12", StringComparison.Ordinal),
            "the quest day rolls over at 12:00 server time");
        Check.True(
            day.Contains("DateTimeOffset.Now", StringComparison.Ordinal),
            "the day is the server's local clock, not UTC");
    }
}
