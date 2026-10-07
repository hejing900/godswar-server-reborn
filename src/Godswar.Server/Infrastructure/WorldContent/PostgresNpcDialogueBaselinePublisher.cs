using System.Data;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record NpcDialoguePublicationResult(
    string Revision,
    string SpawnRevision,
    int TextCount,
    int ProfileCount,
    int RouteCount,
    int MenuEntryCount,
    string Source,
    bool Created);

internal static partial class PostgresNpcDialogueBaselinePublisher
{
    private const int PublicationLockNamespace = 1_193_657_936;
    private const int PublicationLockKey = 1_448_298_802;
    private const string Publisher = "server-baseline-v27-manifest-v1";

    public static string CurrentReleaseRevision =>
        WorldContentRevisionHasher.HashNpcDialogueRelease(
            new WorldContentFamilyRevision("npc-dialogues",
                NpcDialogueBaselineV27.ExpectedRevision,
                NpcDialogueBaselineV27.ExpectedHashedEntryCount),
            NpcDialogueBaselineV27.ExpectedSpawnRevision).Sha256;

    public static async Task<NpcDialoguePublicationResult>
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

    private static async Task<NpcDialoguePublicationResult>
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
        if (current is not null &&
            !IsSupportedPreviousRevision(current.Revision) &&
            current.Revision != CurrentReleaseRevision)
        {
            throw new InvalidDataException(
                "The published NPC dialogue revision is neither a reviewed " +
                "V1-V26 predecessor nor the reviewed dependency-bound release.");
        }

        var spawnRevision = await ReadCurrentSpawnRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (!string.Equals(
                spawnRevision.Revision,
                NpcDialogueBaselineV27.ExpectedSpawnRevision,
                StringComparison.Ordinal) ||
            spawnRevision.EntryCount !=
                NpcDialogueBaselineV27.ExpectedTextCount)
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline does not target the " +
                "currently published NPC spawn revision.");
        }

        if (current is not null && current.Revision == CurrentReleaseRevision)
        {
            // A sealed publication must remain independent of mutable seed
            // tables after its initial construction.
            var published = new WorldContentFamilyRevision("npc-dialogues",
                current.Revision, NpcDialogueBaselineV27.ExpectedHashedEntryCount);
            await VerifyStoredReleaseAsync(connection, transaction, published,
                spawnRevision.Revision, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current with { Created = false };
        }

        var texts = NpcDialogueBaselineV27.ApplyTextOverrides(
            await ReadOfficialNpcTextsAsync(
                connection,
                transaction,
                spawnRevision.Revision,
                cancellationToken));
        var routes = NpcDialogueBaselineV27.CreateRoutes();
        var payload = ValidateBaseline(texts, routes);
        var revision = WorldContentRevisionHasher.HashNpcDialogueRelease(
            payload, spawnRevision.Revision);

        var releaseCreated = await InsertReleaseAsync(
            connection,
            transaction,
            revision,
            spawnRevision.Revision,
            cancellationToken);
        if (releaseCreated)
        {
            await InsertTextsAsync(
                connection,
                transaction,
                revision.Sha256,
                texts,
                cancellationToken);
            await InsertProfilesAsync(
                connection,
                transaction,
                revision.Sha256,
                cancellationToken);
            await InsertBindingsAsync(
                connection,
                transaction,
                revision.Sha256,
                cancellationToken);
        }

        await VerifyStoredReleaseAsync(
            connection,
            transaction,
            revision,
            spawnRevision.Revision,
            cancellationToken);
        await PublishAsync(
            connection,
            transaction,
            revision.Sha256,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CreateResult(
            revision.Sha256,
            spawnRevision.Revision,
            Created: true);
    }

    private static WorldContentFamilyRevision ValidateBaseline(
        IReadOnlyList<NpcTextDefinition> texts,
        IReadOnlyList<NpcDialogueRouteDefinition> routes)
    {
        if (texts.Count != NpcDialogueBaselineV27.ExpectedTextCount ||
            routes.Count != NpcDialogueBaselineV27.ExpectedRouteCount ||
            NpcDialogueBaselineV27.Profiles.Length !=
                NpcDialogueBaselineV27.ExpectedProfileCount ||
            NpcDialogueBaselineV27.Profiles.Sum(
                static profile => profile.InitialMenuSubIds.Length) !=
                NpcDialogueBaselineV27.ExpectedMenuEntryCount)
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline has unexpected counts.");
        }

        var textKeys = new HashSet<string>(StringComparer.Ordinal);
        string? previousTextKey = null;
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text.NpcKey) ||
                string.IsNullOrWhiteSpace(text.SceneKey) ||
                string.IsNullOrWhiteSpace(text.DisplayName) ||
                string.IsNullOrWhiteSpace(text.Description) ||
                !textKeys.Add(text.NpcKey) ||
                (previousTextKey is not null &&
                 StringComparer.Ordinal.Compare(
                     previousTextKey,
                     text.NpcKey) >= 0))
            {
                throw new InvalidDataException(
                    "The official NPC dialogue text set is malformed.");
            }

            previousTextKey = text.NpcKey;
        }

        var routeKeys = new HashSet<(string NpcKey, int RouteOrder)>();
        foreach (var route in routes)
        {
            if (!textKeys.Contains(route.NpcKey) ||
                !routeKeys.Add((route.NpcKey, route.RouteOrder)) ||
                !DuelArenaCapturedTransportProtocol.IsAllowedClientScriptKey(
                    route.NpcKey,
                    route.ClientScriptKey) ||
                route.InitialMenuSubIds.IsDefaultOrEmpty ||
                route.InitialMenuSubIds.Distinct().Count() !=
                    route.InitialMenuSubIds.Length)
            {
                throw new InvalidDataException(
                    "The reviewed NPC dialogue route set is malformed.");
            }
        }

        foreach (var npcRoutes in routes.GroupBy(
                     static route => route.NpcKey,
                     StringComparer.Ordinal))
        {
            var expectedOrder = 0;
            var dialogIndices = new HashSet<int>();
            if (npcRoutes.Any(route =>
                    route.RouteOrder != expectedOrder++ ||
                    !dialogIndices.Add(route.DialogIndex)))
            {
                throw new InvalidDataException(
                    "The reviewed NPC dialogue routes are non-contiguous " +
                    "or duplicate a client dialog endpoint.");
            }
        }

        var revision =
            WorldContentRevisionHasher.HashNpcDialogues(texts, routes);
        if (revision.EntryCount !=
                NpcDialogueBaselineV27.ExpectedHashedEntryCount ||
            !string.Equals(
                revision.Sha256,
                NpcDialogueBaselineV27.ExpectedRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed NPC dialogue baseline failed golden " +
                $"revision validation: {revision.Sha256}.");
        }

        return revision;
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

    private static async Task<NpcDialoguePublicationResult?>
        ReadCurrentPublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.spawn_revision,
                   release.text_count,
                   release.profile_count,
                   release.route_count,
                   release.menu_entry_count,
                   release.source
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npc-dialogues';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NpcDialoguePublicationResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            Created: false);
    }

    private static async Task<(string Revision, int EntryCount)>
        ReadCurrentSpawnRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision, release.entry_count
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
            throw new InvalidDataException(
                "NPC dialogue publication requires an official NPC spawn " +
                "publication.");
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<NpcTextDefinition[]>
        ReadOfficialNpcTextsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string spawnRevision,
            CancellationToken cancellationToken)
    {
        var texts = new List<NpcTextDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT text.npc_key,
                   text.scene_key,
                   text.display_name,
                   text.description
            FROM npc_spawn_definitions spawn
            JOIN npc_text_templates text
              ON text.npc_key = spawn.npc_key
            WHERE spawn.revision = @spawn_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "spawn_revision",
            NpgsqlDbType.Varchar,
            spawnRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            texts.Add(new NpcTextDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return texts
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static NpcDialoguePublicationResult CreateResult(
        string revision,
        string spawnRevision,
        bool Created) =>
        new(
            revision,
            spawnRevision,
            NpcDialogueBaselineV27.ExpectedTextCount,
            NpcDialogueBaselineV27.ExpectedProfileCount,
            NpcDialogueBaselineV27.ExpectedRouteCount,
            NpcDialogueBaselineV27.ExpectedMenuEntryCount,
            NpcDialogueBaselineV27.Source,
            Created);

    private static bool IsSupportedPreviousRevision(string revision) =>
        string.Equals(
            revision,
            NpcDialogueBaselineV1.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV2.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV3.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV4.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV5.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV6.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV7.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV8.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV9.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV10.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV11.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV12.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV13.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV14.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV15.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV16.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV17.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV18.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV19.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV20.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV21.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV21.ExpectedRevision,
                    NpcDialogueBaselineV21.ExpectedHashedEntryCount),
                NpcDialogueBaselineV21.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            NpcDialogueBaselineV22.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV22.ExpectedRevision,
                    NpcDialogueBaselineV22.ExpectedHashedEntryCount),
                NpcDialogueBaselineV22.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal) ||
        // V23 was the release actually stored in the database before the farm
        // release existed, in both its raw form and its dependency-bound form.
        // Leaving V23 out of this list makes the publisher refuse to start
        // against any database that already holds it.
        string.Equals(
            revision,
            NpcDialogueBaselineV23.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV23.ExpectedRevision,
                    NpcDialogueBaselineV23.ExpectedHashedEntryCount),
                NpcDialogueBaselineV23.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal) ||
        // The reviewed V24 release, in its raw form and its dependency-bound
        // form. A database published from the build before this one holds the
        // latter, so it has to stay readable as a predecessor.
        string.Equals(
            revision,
            NpcDialogueBaselineV24.ExpectedRevision,
            StringComparison.Ordinal) ||
        // The first V24 publication carried the farm window's seven-entry root
        // menu before the capture's own four-entry page replaced it. That
        // revision is still what a database published before this change holds,
        // so it has to stay readable as a predecessor.
        string.Equals(
            revision,
            "79FD9053BA332B3EB671D2751FD03768266038907B9503CFE7DF1D923BFDA340",
            StringComparison.Ordinal) ||
        // The second V24 publication, which moved the farm's shop npcs off the
        // dialogue routes. A database published from the build before this one
        // still holds it.
        string.Equals(
            revision,
            "6B0D5A359A6AC77726F515D9FE1F82F860F63B621140E12F83E52D2F5BDFCE2D",
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV24.ExpectedRevision,
                    NpcDialogueBaselineV24.ExpectedHashedEntryCount),
                NpcDialogueBaselineV24.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal) ||
        // The reviewed V25 release, in its raw form and its dependency-bound
        // form. V25 was bound to the V9 spawn release, so a database published
        // from the build before this one holds the latter; both stay readable as
        // predecessors of the V26 Cursed Land release.
        string.Equals(
            revision,
            NpcDialogueBaselineV25.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV25.ExpectedRevision,
                    NpcDialogueBaselineV25.ExpectedHashedEntryCount),
                NpcDialogueBaselineV25.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal) ||
        // The reviewed V26 Cursed Land release, in its raw form and its
        // dependency-bound form. V26 was bound to the V10 spawn release, so the
        // database published before this build holds the latter; both stay
        // readable as predecessors of the V27 quest-actor release.
        string.Equals(
            revision,
            NpcDialogueBaselineV26.ExpectedRevision,
            StringComparison.Ordinal) ||
        string.Equals(
            revision,
            WorldContentRevisionHasher.HashNpcDialogueRelease(
                new WorldContentFamilyRevision("npc-dialogues",
                    NpcDialogueBaselineV26.ExpectedRevision,
                    NpcDialogueBaselineV26.ExpectedHashedEntryCount),
                NpcDialogueBaselineV26.ExpectedSpawnRevision).Sha256,
            StringComparison.Ordinal);
}
