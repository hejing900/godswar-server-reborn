using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    private static async Task<IReadOnlyList<CharacterCoreRow>>
        ReadCoreCharactersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            RealmId realmId,
            string itemContentRevision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            CoreCharacterQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue(
            "itemContentRevision",
            itemContentRevision);
        var rows = new List<CharacterCoreRow>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadCoreCharacter(reader));
            if (rows.Count > 2)
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.BoundsExceeded,
                    "Character slot query exceeded its two-row guard.");
            }
        }

        return rows;
    }

    private static CharacterCoreRow ReadCoreCharacter(
        NpgsqlDataReader reader)
    {
        var identity = new CharacterIdentitySnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            ToUtcOffset(reader.GetDateTime(17)),
            reader.GetInt16(46),
            reader.GetInt64(47))
        {
            RealmId = new RealmId(reader.GetInt32(53)),
            FactionCrierRevision = reader.GetInt64(55),
            OnlineAwardRevision = reader.GetInt64(56),
            Quests = ReadQuestSnapshots(reader, 63, 64),
            QuestCompletedIds = ImmutableArray.CreateRange(
                reader.GetFieldValue<int[]>(65).Select(static id => (uint)id))
        };
        return new CharacterCoreRow(
            identity,
            new CharacterAppearanceSnapshot(
                reader.GetString(3) == "female" ? (byte)0 : (byte)1,
                ToByte(reader.GetInt16(4), "camp"),
                ToByte(reader.GetInt16(5), "profession"),
                ToByte(reader.GetInt16(6), "hair"),
                ToByte(reader.GetInt16(7), "face"),
                ToByte(reader.GetInt16(8), "faith"),
                checked((uint)reader.GetInt32(59)))
            {
                OwnedTitleIds = ImmutableArray.CreateRange(
                    reader.GetFieldValue<int[]>(60)
                        .Select(static titleId => checked((uint)titleId)))
            },
            new CharacterLocationSnapshot(
                ToByte(reader.GetInt16(9), "map"),
                reader.GetFloat(15),
                reader.GetFloat(16),
                reader.GetInt64(45)),
            new CharacterProgressionSnapshot(
                reader.GetInt32(10),
                reader.GetInt64(27),
                reader.GetInt32(20),
                reader.GetInt32(21),
                reader.GetInt32(22),
                reader.GetBoolean(50),
                reader.GetInt64(48)),
            new CharacterVitalsSnapshot(
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt64(28)),
            new CharacterWalletSnapshot(
                reader.GetInt32(41),
                reader.GetInt32(42),
                reader.GetInt32(54),
                reader.GetInt32(57),
                reader.GetInt64(58),
                reader.GetInt32(61),
                reader.GetInt32(62)),
            new CharacterLoadoutSnapshot(
                reader.GetString(18),
                reader.GetString(19),
                reader.GetInt16(23),
                reader.GetInt32(24),
                reader.GetInt16(25),
                reader.GetInt32(26),
                reader.GetInt64(49)),
            new CharacterZodiacSnapshot(
                ToByte(reader.GetInt16(29), "zodiac type"),
                reader.GetInt32(30),
                reader.IsDBNull(31)
                    ? null
                    : ToUtcOffset(reader.GetDateTime(31)),
                ToByte(reader.GetInt16(32), "zodiac level"),
                reader.GetInt32(33),
                reader.GetInt32(36),
                reader.IsDBNull(37)
                    ? null
                    : reader.GetFieldValue<DateOnly>(37),
                reader.GetInt64(38),
                reader.IsDBNull(39)
                    ? null
                    : ToUtcOffset(reader.GetDateTime(39)),
                reader.IsDBNull(40)
                    ? null
                    : reader.GetFieldValue<DateOnly>(40),
                reader.GetInt32(34),
                reader.GetInt32(35),
                ImmutableArray.CreateRange(
                    reader.GetFieldValue<int[]>(43)),
                ImmutableArray.CreateRange(
                    reader.GetFieldValue<int[]>(44))),
            new CharacterPetShedSnapshot(
                reader.GetInt16(51),
                reader.GetInt64(52)));
    }

    /// <summary>
    /// Reads the carried quests from the parallel id and progress arrays.
    /// </summary>
    /// <remarks>
    /// Both arrays are ordered by quest id in SQL, so index i of one belongs with
    /// index i of the other.
    /// </remarks>
    private static ImmutableArray<LoadedQuestSnapshot> ReadQuestSnapshots(
        NpgsqlDataReader reader,
        int questIdOrdinal,
        int progressOrdinal)
    {
        var questIds = reader.GetFieldValue<int[]>(questIdOrdinal);
        var progresses = reader.GetFieldValue<long[]>(progressOrdinal);
        var quests = ImmutableArray.CreateBuilder<LoadedQuestSnapshot>(
            questIds.Length);
        for (var index = 0; index < questIds.Length; index++)
        {
            quests.Add(new LoadedQuestSnapshot(
                (uint)questIds[index],
                index < progresses.Length ? progresses[index] : 0));
        }

        return quests.MoveToImmutable();
    }

    private static byte ToByte(short value, string field)    {
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidDataException(
                $"Character {field} is outside the byte range.");
        }

        return (byte)value;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(value.ToUniversalTime());

    private sealed record CharacterCoreRow(
        CharacterIdentitySnapshot Identity,
        CharacterAppearanceSnapshot Appearance,
        CharacterLocationSnapshot Location,
        CharacterProgressionSnapshot Progression,
        CharacterVitalsSnapshot Vitals,
        CharacterWalletSnapshot Wallet,
        CharacterLoadoutSnapshot Loadout,
        CharacterZodiacSnapshot Zodiac,
        CharacterPetShedSnapshot PetShed)
    {
        public CharacterLoadSnapshot ToSnapshot(
            CharacterRelatedReadResult related) =>
            new(
                Identity,
                Appearance,
                Location,
                Progression,
                Vitals,
                Wallet,
                Loadout,
                Zodiac,
                related.CalculatedStats,
                related.Skills,
                related.Talents,
                PetShed,
                related.Pets,
                related.PersonalBoosts);
    }

    private static readonly string CoreCharacterQuery =
        $"""
        SELECT
            cb.id,
            cb.account_id,
            cb.name,
            cb.gender,
            cb.camp,
            cb.profession,
            cb.hair_style,
            COALESCE(cb.face_shap, 0),
            cb.belief,
            cb."Map",
            cb.fighter_job_lv,
            cb."MaxHP",
            cb."MaxMP",
            cb."curHP",
            cb."curMP",
            cb."Pos_X",
            cb."Pos_Z",
            cb."Register_time",
            COALESCE(equipment_projection.equip, ''),
            COALESCE(kitbag_projection.kitbag_1, ''),
            cb."SkillPoint",
            cb."SkillExp",
            COALESCE(cb.holy_suit_points, 0),
            item_rank_projection.weapon_rank,
            item_rank_projection.weapon_aura_effect,
            item_rank_projection.armor_rank,
            item_rank_projection.armor_aura_effect,
            cb.fighter_job_exp,
            cb.vitals_revision,
            cb.zodiac_type,
            cb.zodiac_lucky_status,
            cb.zodiac_lucky_expires_at,
            cb.zodiac_level,
            cb.zodiac_energy,
            cb.zodiac_accumulated_exp_x100,
            cb.zodiac_accumulated_talent_exp_x100,
            cb.zodiac_energy_remainder_x100,
            cb.zodiac_online_day,
            cb.zodiac_online_duration_ticks,
            cb.zodiac_last_online_at,
            cb.zodiac_last_compensation_day,
            cb."Money",
            cb."Stone",
            ARRAY(
                SELECT COALESCE(grid.level, 0)::integer
                FROM generate_series(0, 15) AS requested_grid(grid_index)
                LEFT JOIN character_zodiac_skill_grids grid
                  ON grid.user_id = cb.id
                 AND grid.grid_index = requested_grid.grid_index
                ORDER BY requested_grid.grid_index
            ),
            ARRAY(
                SELECT COALESCE(grid.selected_skill_id, -1)
                FROM generate_series(0, 15) AS requested_grid(grid_index)
                LEFT JOIN character_zodiac_skill_grids grid
                  ON grid.user_id = cb.id
                 AND grid.grid_index = requested_grid.grid_index
                ORDER BY requested_grid.grid_index
            ),
            cb.position_revision,
            cb.character_slot,
            cb.lifecycle_version,
            cb.progression_reward_revision,
            cb.inventory_revision,
            cb.fighter_level_sealed,
            cb.pet_shed_capacity,
            cb.pet_shed_revision,
            cb.server_id,
            cb."BindingGold",
            cb.faction_crier_revision,
            cb.online_award_revision,
            cb.medusa_honor_points,
            cb.medusa_reward_revision,
            cb.selected_title_id,
            ARRAY(
                SELECT ownership.title_id
                FROM character_title_ownership ownership
                WHERE ownership.character_id = cb.id
                UNION
                SELECT ownership.title_id
                FROM atlantis_character_title_ownership ownership
                WHERE ownership.character_id = cb.id
                ORDER BY title_id
            ),
            COALESCE(cb.exchange_point, 0),
            COALESCE(cb.exchange_medal, 0),
            ARRAY(
                SELECT quest.quest_id
                FROM character_quests quest
                WHERE quest.character_id = cb.id AND quest.state = 0
                ORDER BY quest.quest_id
            ),
            ARRAY(
                SELECT quest.progress
                FROM character_quests quest
                WHERE quest.character_id = cb.id AND quest.state = 0
                ORDER BY quest.quest_id
            ),
            ARRAY(
                SELECT quest.quest_id
                FROM character_quests quest
                WHERE quest.character_id = cb.id AND quest.state = 1
                ORDER BY quest.quest_id
            )
        FROM character_base cb
        {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
        WHERE cb.account_id = @accountId
          AND cb.server_id = @realmId
          AND cb.lifecycle_state = 'active'
        ORDER BY cb.id
        LIMIT 2;
        """;
}
