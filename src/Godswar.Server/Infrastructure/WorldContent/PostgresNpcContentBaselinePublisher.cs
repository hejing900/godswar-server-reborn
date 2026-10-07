using System.Data;
using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record NpcContentPublicationResult(
    string Revision,
    int EntryCount,
    string Source,
    bool Created);

internal static class PostgresNpcContentBaselinePublisher
{
    private const int PublicationLockNamespace = 1_193_657_936;
    private const int PublicationLockKey = 1_448_298_801;
    private const string Publisher = "server-baseline-v11";

    public static async Task<NpcContentPublicationResult>
        EnsurePublishedAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        return await EnsurePublishedOnceAsync(
            dataSource,
            cancellationToken);
    }

    private static async Task<NpcContentPublicationResult>
        EnsurePublishedOnceAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await AcquirePublicationLockAsync(
            connection,
            transaction,
            cancellationToken);
        var current = await ReadCurrentPublicationAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is not null && string.Equals(
                current.Revision,
                NpcContentBaselineV11.ExpectedRevision,
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return current with { Created = false };
        }
        if (current is not null &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV1.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV2.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV3.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV4.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV5.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV6.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV7.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV8.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV9.ExpectedRevision,
                StringComparison.Ordinal) &&
            !string.Equals(
                current.Revision,
                NpcContentBaselineV10.ExpectedRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The published NPC revision is neither a reviewed V1-V10 " +
                "predecessor nor the reviewed V11 release.");
        }

        var mapIds = await ReadMapIdsAsync(
            connection,
            transaction,
            cancellationToken);
        var definitions = NpcContentBaselineV11.LoadDefinitions();
        var canonical = await ValidateAndCanonicalizeAsync(
            mapIds,
            definitions,
            cancellationToken);
        var revision = WorldContentRevisionHasher.HashNpcs(canonical);
        if (revision.EntryCount !=
                NpcContentBaselineV11.ExpectedEntryCount ||
            !string.Equals(
                revision.Sha256,
                NpcContentBaselineV11.ExpectedRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed NPC baseline failed publication validation.");
        }

        var releaseCreated = await InsertReleaseAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        if (releaseCreated)
        {
            await InsertDefinitionsAsync(
                connection,
                transaction,
                revision.Sha256,
                canonical,
                cancellationToken);
        }

        await VerifyStoredReleaseAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await using (var publish = new NpgsqlCommand(
                         """
                         INSERT INTO npc_content_publication (
                             family,
                             revision,
                             published_at,
                             publisher
                         )
                         VALUES (
                             'npcs',
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
                         transaction))
        {
            publish.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision.Sha256);
            publish.Parameters.AddWithValue(
                "publisher",
                NpgsqlDbType.Varchar,
                Publisher);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The NPC baseline publication pointer was not created.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new NpcContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            NpcContentBaselineV7.Source,
            Created: true);
    }

    private static async Task AcquirePublicationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@namespace, @key);",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "namespace",
            NpgsqlDbType.Integer,
            PublicationLockNamespace);
        command.Parameters.AddWithValue(
            "key",
            NpgsqlDbType.Integer,
            PublicationLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpcContentPublicationResult?>
        ReadCurrentPublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.entry_count,
                   release.source
            FROM npc_content_publication publication
            JOIN npc_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npcs';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NpcContentPublicationResult(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            Created: false);
    }

    private static async Task<short[]> ReadMapIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mapIds = new List<short>();
        await using var command = new NpgsqlCommand(
            "SELECT map_id FROM map_templates ORDER BY map_id;",
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mapIds.Add(reader.GetInt16(0));
        }

        return mapIds.ToArray();
    }

    private static async Task<NpcSpawnDefinition[]>
        ValidateAndCanonicalizeAsync(
            IReadOnlyList<short> mapIds,
            IReadOnlyList<NpcSpawnDefinition> definitions,
            CancellationToken cancellationToken)
    {
        var validationReader = PinnedWorldContentReader.Create(
            "npc-baseline-validation-v1",
            mapIds,
            definitions,
            [],
            []);
        var canonical = new List<NpcSpawnDefinition>(
            validationReader.Manifest.Npcs.EntryCount);
        foreach (var mapId in mapIds.Order())
        {
            var map = await validationReader.ReadMapAsync(
                mapId,
                cancellationToken);
            canonical.AddRange(map.Npcs);
        }

        return canonical.ToArray();
    }

    private static async Task<bool> InsertReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_content_revisions (
                revision,
                entry_count,
                source
            )
            VALUES (
                @revision,
                @entry_count,
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
            "entry_count",
            NpgsqlDbType.Integer,
            revision.EntryCount);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            NpcContentBaselineV7.Source);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<NpcSpawnDefinition> definitions,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO npc_spawn_definitions (
                revision,
                map_id,
                scene_key,
                npc_key,
                template_key,
                object_id,
                pos_x,
                pos_z,
                interaction_id,
                appearance_type,
                facing,
                detail_10077,
                detail_10080
            )
            VALUES (
                @revision,
                @map_id,
                @scene_key,
                @npc_key,
                @template_key,
                @object_id,
                @pos_x,
                @pos_z,
                @interaction_id,
                @appearance_type,
                @facing,
                @detail_10077,
                @detail_10080
            );
            """,
            connection,
            transaction);
        foreach (var definition in definitions)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision);
            command.Parameters.AddWithValue(
                "map_id",
                NpgsqlDbType.Smallint,
                definition.MapId);
            command.Parameters.AddWithValue(
                "scene_key",
                NpgsqlDbType.Varchar,
                definition.SceneKey);
            command.Parameters.AddWithValue(
                "npc_key",
                NpgsqlDbType.Varchar,
                definition.NpcKey);
            command.Parameters.AddWithValue(
                "template_key",
                NpgsqlDbType.Varchar,
                definition.TemplateKey);
            command.Parameters.AddWithValue(
                "object_id",
                NpgsqlDbType.Bigint,
                checked((long)definition.ObjectId));
            command.Parameters.AddWithValue(
                "pos_x",
                NpgsqlDbType.Real,
                definition.X);
            command.Parameters.AddWithValue(
                "pos_z",
                NpgsqlDbType.Real,
                definition.Z);
            command.Parameters.AddWithValue(
                "interaction_id",
                NpgsqlDbType.Bigint,
                checked((long)definition.InteractionId));
            command.Parameters.AddWithValue(
                "appearance_type",
                NpgsqlDbType.Bigint,
                checked((long)definition.AppearanceType));
            command.Parameters.AddWithValue(
                "facing",
                NpgsqlDbType.Real,
                definition.Facing);
            command.Parameters.AddWithValue(
                "detail_10077",
                NpgsqlDbType.Bytea,
                definition.Detail10077);
            command.Parameters.AddWithValue(
                "detail_10080",
                NpgsqlDbType.Bytea,
                definition.Detail10080);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "An NPC baseline definition was not inserted.");
            }
        }
    }

    private static async Task VerifyStoredReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision expected,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT entry_count,
                   source,
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_spawn_definitions definitions
                       WHERE definitions.revision = release.revision
                   )
            FROM npc_content_revisions release
            WHERE revision = @revision;
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
            reader.GetInt32(0) != expected.EntryCount ||
            !string.Equals(
                reader.GetString(1),
                NpcContentBaselineV7.Source,
                StringComparison.Ordinal) ||
            reader.GetInt32(2) != expected.EntryCount)
        {
            throw new InvalidDataException(
                "The stored NPC baseline release failed verification.");
        }
    }
}
