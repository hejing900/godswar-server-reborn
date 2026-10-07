using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresNpcDialogueBaselinePublisher
{
    private static async Task<bool> InsertReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision revision,
        string spawnRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_revisions (
                revision,
                spawn_revision,
                text_count,
                profile_count,
                route_count,
                menu_entry_count,
                source
            )
            VALUES (
                @revision,
                @spawn_revision,
                @text_count,
                @profile_count,
                @route_count,
                @menu_entry_count,
                @source
            )
            ON CONFLICT (revision) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision.Sha256);
        command.Parameters.AddWithValue(
            "spawn_revision",
            NpgsqlDbType.Varchar,
            spawnRevision);
        command.Parameters.AddWithValue(
            "text_count",
            NpgsqlDbType.Integer,
            NpcDialogueBaselineV27.ExpectedTextCount);
        command.Parameters.AddWithValue(
            "profile_count",
            NpgsqlDbType.Integer,
            NpcDialogueBaselineV27.ExpectedProfileCount);
        command.Parameters.AddWithValue(
            "route_count",
            NpgsqlDbType.Integer,
            NpcDialogueBaselineV27.ExpectedRouteCount);
        command.Parameters.AddWithValue(
            "menu_entry_count",
            NpgsqlDbType.Integer,
            NpcDialogueBaselineV27.ExpectedMenuEntryCount);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            NpcDialogueBaselineV27.Source);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertTextsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<NpcTextDefinition> texts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_texts (
                revision,
                npc_key,
                scene_key,
                display_name,
                description
            )
            VALUES (
                @revision,
                @npc_key,
                @scene_key,
                @display_name,
                @description
            );
            """,
            connection,
            transaction);
        foreach (var text in texts)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision);
            command.Parameters.AddWithValue(
                "npc_key",
                NpgsqlDbType.Varchar,
                text.NpcKey);
            command.Parameters.AddWithValue(
                "scene_key",
                NpgsqlDbType.Varchar,
                text.SceneKey);
            command.Parameters.AddWithValue(
                "display_name",
                NpgsqlDbType.Varchar,
                text.DisplayName);
            command.Parameters.AddWithValue(
                "description",
                NpgsqlDbType.Text,
                text.Description);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "An NPC dialogue text row was not inserted.");
            }
        }
    }

    private static async Task InsertProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO npc_dialogue_profiles (
                             revision,
                             profile_key,
                             dialog_index,
                             behavior,
                             initial_request_sub_id
                         )
                         VALUES (
                             @revision,
                             @profile_key,
                             @dialog_index,
                             @behavior,
                             @initial_request_sub_id
                         );
                         """,
                         connection,
                         transaction))
        {
            foreach (var profile in NpcDialogueBaselineV27.Profiles)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue(
                    "revision",
                    NpgsqlDbType.Varchar,
                    revision);
                command.Parameters.AddWithValue(
                    "profile_key",
                    NpgsqlDbType.Varchar,
                    profile.ProfileKey);
                command.Parameters.AddWithValue(
                    "dialog_index",
                    NpgsqlDbType.Integer,
                    profile.DialogIndex);
                command.Parameters.AddWithValue(
                    "behavior",
                    NpgsqlDbType.Smallint,
                    checked((short)profile.Behavior));
                command.Parameters.AddWithValue(
                    "initial_request_sub_id",
                    NpgsqlDbType.Integer,
                    profile.InitialRequestSubId);
                if (await command.ExecuteNonQueryAsync(
                        cancellationToken) != 1)
                {
                    throw new InvalidDataException(
                        "An NPC dialogue profile was not inserted.");
                }
            }
        }

        await using var entryCommand = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_profile_entries (
                revision,
                profile_key,
                menu_order,
                sub_id
            )
            VALUES (
                @revision,
                @profile_key,
                @menu_order,
                @sub_id
            );
            """,
            connection,
            transaction);
        foreach (var profile in NpcDialogueBaselineV27.Profiles)
        {
            for (var index = 0;
                 index < profile.InitialMenuSubIds.Length;
                 index++)
            {
                entryCommand.Parameters.Clear();
                entryCommand.Parameters.AddWithValue(
                    "revision",
                    NpgsqlDbType.Varchar,
                    revision);
                entryCommand.Parameters.AddWithValue(
                    "profile_key",
                    NpgsqlDbType.Varchar,
                    profile.ProfileKey);
                entryCommand.Parameters.AddWithValue(
                    "menu_order",
                    NpgsqlDbType.Smallint,
                    checked((short)index));
                entryCommand.Parameters.AddWithValue(
                    "sub_id",
                    NpgsqlDbType.Integer,
                    profile.InitialMenuSubIds[index]);
                if (await entryCommand.ExecuteNonQueryAsync(
                        cancellationToken) != 1)
                {
                    throw new InvalidDataException(
                        "An NPC dialogue menu entry was not inserted.");
                }
            }
        }
    }

    private static async Task InsertBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_bindings (
                revision,
                npc_key,
                client_script_key,
                profile_key,
                route_order
            )
            VALUES (
                @revision,
                @npc_key,
                @client_script_key,
                @profile_key,
                @route_order
            );
            """,
            connection,
            transaction);
        foreach (var binding in NpcDialogueBaselineV27.Bindings)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision);
            command.Parameters.AddWithValue(
                "npc_key",
                NpgsqlDbType.Varchar,
                binding.NpcKey);
            command.Parameters.AddWithValue(
                "client_script_key",
                NpgsqlDbType.Varchar,
                binding.ClientScriptKey);
            command.Parameters.AddWithValue(
                "profile_key",
                NpgsqlDbType.Varchar,
                binding.ProfileKey);
            command.Parameters.AddWithValue(
                "route_order",
                NpgsqlDbType.Smallint,
                checked((short)binding.RouteOrder));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "An NPC dialogue binding was not inserted.");
            }
        }
    }

    private static async Task VerifyStoredReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision expected,
        string spawnRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT release.spawn_revision,
                   release.text_count,
                   release.profile_count,
                   release.route_count,
                   release.menu_entry_count,
                   release.source,
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_texts
                       WHERE revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profiles
                       WHERE revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_bindings
                       WHERE revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profile_entries
                       WHERE revision = release.revision
                   )
            FROM npc_dialogue_revisions release
            WHERE release.revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            expected.Sha256);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(
                reader.GetString(0),
                spawnRevision,
                StringComparison.Ordinal) ||
            reader.GetInt32(1) !=
                NpcDialogueBaselineV27.ExpectedTextCount ||
            reader.GetInt32(2) !=
                NpcDialogueBaselineV27.ExpectedProfileCount ||
            reader.GetInt32(3) !=
                NpcDialogueBaselineV27.ExpectedRouteCount ||
            reader.GetInt32(4) !=
                NpcDialogueBaselineV27.ExpectedMenuEntryCount ||
            !string.Equals(
                reader.GetString(5),
                NpcDialogueBaselineV27.Source,
                StringComparison.Ordinal) ||
            reader.GetInt32(6) !=
                NpcDialogueBaselineV27.ExpectedTextCount ||
            reader.GetInt32(7) !=
                NpcDialogueBaselineV27.ExpectedProfileCount ||
            reader.GetInt32(8) !=
                NpcDialogueBaselineV27.ExpectedRouteCount ||
            reader.GetInt32(9) !=
                NpcDialogueBaselineV27.ExpectedMenuEntryCount)
        {
            throw new InvalidDataException(
                "The stored NPC dialogue release failed verification.");
        }
    }

    private static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_dialogue_publication (
                family,
                revision,
                published_at,
                publisher
            )
            VALUES (
                'npc-dialogues',
                @revision,
                now(),
                @publisher
            )
            ON CONFLICT (family) DO UPDATE
            SET revision = EXCLUDED.revision,
                published_at = EXCLUDED.published_at,
                publisher = EXCLUDED.publisher;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        command.Parameters.AddWithValue(
            "publisher",
            NpgsqlDbType.Varchar,
            Publisher);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The NPC dialogue publication pointer was not created.");
        }
    }
}
