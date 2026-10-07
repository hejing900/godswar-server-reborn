using Godswar.Server.Game;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        GetCharactersAsync(accountId, RealmId.Tempest, cancellationToken);

    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.account_id = @accountId
              AND cb.server_id = @realmId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        AddItemContentRevisionParameter(command);

        var characters = new List<GameCharacter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            characters.Add(ReadCharacter(reader));
        }

        return characters;
    }

    public Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        GetFirstCharacterAsync(accountId, RealmId.Tempest, cancellationToken);

    public async Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        await using var command = _dataSource.CreateCommand($"""
            SELECT {CharacterColumns}
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.account_id = @accountId
              AND cb.server_id = @realmId
              AND cb.lifecycle_state = 'active'
            ORDER BY cb.id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        AddItemContentRevisionParameter(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            PostgresCharacterRuntimeItemProjectionSql
                .CalculatedStatsForCharacter);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        AddItemContentRevisionParameter(command);
        AddGameplayContentRevisionParameter(command);
        AddPetLearnedSkillRevisionParameter(command);
        AddHolySpiritBalanceParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? CharacterStatsReader.Read(reader)
            : null;
    }

    public Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken = default) =>
        CreateCharacterAsync(
            accountId,
            RealmId.Tempest,
            character,
            cancellationToken);

    public async Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        RealmId realmId,
        GameCharacter character,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        var baseName = CleanCharacterName(character.Name);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidateName = attempt == 0 ? baseName : $"{baseName}{attempt + 1}";
            character.Name = candidateName.Length <= 32 ? candidateName : candidateName[..32];
            character.AccountId = accountId;
            character.RealmId = realmId;
            GameDefaults.InitializeStartingLocation(character);
            character.Equipment = GameDefaults.DefaultEquipment(character.Profession);
            character.CreatedUtc = DateTime.UtcNow;

            try
            {
                return await InsertCharacterAsync(character, cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
            }
        }

        character.Name = $"{baseName}{Guid.NewGuid():N}"[..32];
        character.Equipment = GameDefaults.DefaultEquipment(character.Profession);
        return await InsertCharacterAsync(character, cancellationToken);
    }

    private static GameCharacter ReadCharacter(NpgsqlDataReader reader)
    {
        return new GameCharacter
        {
            Id = reader.GetInt32(0),
            AccountId = reader.GetInt32(1),
            RealmId = new RealmId(reader.GetInt32(53)),
            Name = reader.GetString(2),
            Gender = reader.GetString(3) == "female" ? (byte)0 : (byte)1,
            Camp = (byte)reader.GetInt16(4),
            Profession = (byte)reader.GetInt16(5),
            Hair = (byte)reader.GetInt16(6),
            Face = (byte)reader.GetInt16(7),
            Faith = (byte)reader.GetInt16(8),
            CurrentMap = (byte)reader.GetInt16(9),
            Level = reader.GetInt32(10),
            MaxHp = reader.GetInt32(11),
            MaxMp = reader.GetInt32(12),
            CurrentHp = reader.GetInt32(13),
            CurrentMp = reader.GetInt32(14),
            PositionX = reader.GetFloat(15),
            PositionZ = reader.GetFloat(16),
            CreatedUtc = reader.GetDateTime(17).ToUniversalTime(),
            Equipment = reader.GetString(18),
            KitBag = reader.GetString(19),
            TalentPoints = reader.GetInt32(20),
            TalentExperience = reader.GetInt32(21),
            HolySuitPoints = reader.GetInt32(22),
            WeaponRank = reader.GetInt16(23),
            WeaponAuraEffect = reader.GetInt32(24),
            ArmorRank = reader.GetInt16(25),
            ArmorAuraEffect = reader.GetInt32(26),
            Experience = reader.GetInt64(27),
            VitalsRevision = reader.GetInt64(28),
            ZodiacType = (byte)reader.GetInt16(29),
            ZodiacLuckyStatus = reader.GetInt32(30),
            ZodiacLuckyExpiresAt = reader.IsDBNull(31)
                ? null
                : new DateTimeOffset(reader.GetDateTime(31).ToUniversalTime()),
            ZodiacLevel = (byte)reader.GetInt16(32),
            ZodiacEnergy = reader.GetInt32(33),
            ZodiacAccumulatedExperienceX100 = reader.GetInt32(34),
            ZodiacAccumulatedTalentExperienceX100 = reader.GetInt32(35),
            ZodiacEnergyRemainderX100 = reader.GetInt32(36),
            ZodiacOnlineDay = reader.IsDBNull(37)
                ? null
                : reader.GetFieldValue<DateOnly>(37),
            ZodiacOnlineDurationTicksToday = reader.GetInt64(38),
            ZodiacLastOnlineAt = reader.IsDBNull(39)
                ? null
                : new DateTimeOffset(reader.GetDateTime(39).ToUniversalTime()),
            ZodiacLastCompensationDay = reader.IsDBNull(40)
                ? null
                : reader.GetFieldValue<DateOnly>(40),
            Silver = reader.GetInt32(41),
            Gold = reader.GetInt32(42),
            ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    reader.GetFieldValue<int[]>(43)),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    reader.GetFieldValue<int[]>(44)),
            PositionRevision = reader.GetInt64(45),
            CharacterSlot = reader.GetInt16(46),
            LifecycleState = reader.GetString(47) == "active"
                ? CharacterLifecycleState.Active
                : CharacterLifecycleState.Deleted,
            LifecycleVersion = reader.GetInt64(48),
            DeletedAt = reader.IsDBNull(49)
                ? null
                : new DateTimeOffset(
                    reader.GetDateTime(49).ToUniversalTime()),
            RestoreUntil = reader.IsDBNull(50)
                ? null
                : new DateTimeOffset(
                    reader.GetDateTime(50).ToUniversalTime()),
            PurgeAfter = reader.IsDBNull(51)
                ? null
                : new DateTimeOffset(
                    reader.GetDateTime(51).ToUniversalTime()),
            FighterLevelSealed = reader.GetBoolean(52),
            BindingGold = reader.GetInt32(54),
            MedusaHonorPoints = reader.GetInt32(55),
            ExchangePoint = reader.GetInt32(56),
            ExchangeMedal = reader.GetInt32(57),
            Quests = ReadQuests(reader, 58, 59),
            QuestCompletedIds = Array.ConvertAll(
                reader.GetFieldValue<int[]>(60),
                v => (uint)v)
        };
    }

    /// <summary>
    /// Reads the carried quests from the parallel id/progress arrays.
    /// </summary>
    /// <remarks>
    /// Both arrays are ordered by quest id in SQL, so index i of one belongs with
    /// index i of the other.
    /// </remarks>
    private static List<CharacterQuest> ReadQuests(
        NpgsqlDataReader reader,
        int questIdOrdinal,
        int progressOrdinal)
    {
        var questIds = reader.GetFieldValue<int[]>(questIdOrdinal);
        var progresses = reader.GetFieldValue<long[]>(progressOrdinal);
        var quests = new List<CharacterQuest>(questIds.Length);
        for (var index = 0; index < questIds.Length; index++)
        {
            quests.Add(new CharacterQuest
            {
                QuestId = (uint)questIds[index],
                Progress = index < progresses.Length ? progresses[index] : 0
            });
        }

        return quests;
    }
}
