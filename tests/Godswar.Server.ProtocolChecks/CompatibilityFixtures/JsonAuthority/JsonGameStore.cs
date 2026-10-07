using System.Collections.Concurrent;
using System.Text.Json;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.ProtocolChecks;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore :
    IGameStore,
    ICharacterCheckpointStore,
    ICharacterSnapshotReader,
    ICharacterRuntimeProjectionReader,
    IOwnedPetSnapshotReader,
    ISealedPetSnapshotReader,
    IExperienceBoostStateReader,
    IWorldBossAreaControlStore,
    IWorldBossRespawnReader,
    IAccountCredentialStore,
    IAccountDirectory,
    IAccountPresenceWriter,
    ILegacyAccountLoginStore,
    ISemanticGatewayCharacterRouteReader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _statePath;
    private readonly SemaphoreSlim _lock;

    public JsonGameStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _statePath = Path.GetFullPath(Path.Combine(dataPath, "state.json"));
        _lock = PathLocks.GetOrAdd(_statePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = File.Exists(_statePath)
                ? await LoadUnsafeAsync(cancellationToken)
                : new GameDatabase();
            foreach (var boost in db.CharacterExperienceBoosts)
            {
                CharacterBoostOnlineDuration.RestoreLegacyGrant(boost);
            }

            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MarkAccountOfflineAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return;
            }

            character.CurrentMap = currentMap;
            character.PositionX = positionX;
            character.PositionZ = positionZ;
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null ||
                vitalsRevision <= character.VitalsRevision)
            {
                return;
            }

            character.CurrentHp = Math.Clamp(
                currentHp,
                0,
                Math.Max(1, character.MaxHp));
            character.CurrentMp = Math.Clamp(
                currentMp,
                0,
                Math.Max(0, character.MaxMp));
            character.VitalsRevision = vitalsRevision;
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCharacterQuestStateAsync(
        int accountId,
        int characterId,
        IReadOnlyList<CharacterQuest> quests,
        IReadOnlyList<uint> completedQuestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(completedQuestIds);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(
                c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return;
            }

            character.Quests = [.. quests.Select(static quest => quest.Clone())];
            character.QuestCompletedIds = [.. completedQuestIds];
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount>>
        LoadQuestDailyCompletionsAsync(
            int characterId,
            CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.Id == characterId);
            return character is null
                ? new Dictionary<uint, GameCharacter.QuestDailyCount>()
                : new Dictionary<uint, GameCharacter.QuestDailyCount>(
                    character.QuestDailyCompletions);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveQuestDailyCompletionsAsync(
        int accountId,
        int characterId,
        IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount> counts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(
                c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return;
            }

            foreach (var (questId, count) in counts)
            {
                character.QuestDailyCompletions[questId] = count;
            }

            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterWalletResult?> GrantQuestCurrencyAsync(
        int accountId,
        int characterId,
        int silver,
        int gold,
        CancellationToken cancellationToken = default)
    {
        silver = Math.Max(0, silver);
        gold = Math.Max(0, gold);
        if (silver == 0 && gold == 0)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(
                c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            character.Silver += silver;
            character.Gold += gold;
            await SaveUnsafeAsync(db, cancellationToken);
            return new CharacterWalletResult(character.Silver, character.Gold);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default)
    {
        experience = Math.Max(0, experience);
        talentExperience = Math.Max(0, talentExperience);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var previousLevel = character.Level;
            var fighterProgression = PlayerExperienceCatalog.Apply(
                character.Level,
                character.Experience,
                experience);
            character.Level = fighterProgression.Level;
            character.Experience = fighterProgression.Experience;
            var accumulatedTalentExperience = checked(character.TalentExperience + talentExperience);
            var gainedTalentPoints = accumulatedTalentExperience / 100;
            character.TalentExperience = accumulatedTalentExperience % 100;
            character.TalentPoints = checked(character.TalentPoints + gainedTalentPoints);
            await SaveUnsafeAsync(db, cancellationToken);

            return new CharacterProgressionResult(
                fighterProgression.ExperienceGained,
                previousLevel,
                character.Level,
                character.Experience,
                PlayerExperienceCatalog.GetNextLevelExperience(character.Level),
                fighterProgression.LevelUps,
                talentExperience,
                character.TalentExperience,
                gainedTalentPoints,
                character.TalentPoints);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (!db.Characters.Any(character =>
                    character.Id == characterId &&
                    character.AccountId == accountId &&
                    character.LifecycleState ==
                        CharacterLifecycleState.Active))
            {
                return ExperienceBoostState.Empty;
            }

            foreach (var boost in db.CharacterExperienceBoosts)
            {
                CharacterBoostOnlineDuration.RestoreLegacyGrant(boost);
            }

            var boosts = db.CharacterExperienceBoosts
                .Where(boost =>
                    boost.CharacterId == characterId &&
                    boost.ActivatedAt <= now &&
                    (!boost.RemainingOnlineTicks.HasValue ||
                     CharacterBoostOnlineDuration.RemainingTicks(boost) > 0))
                .GroupBy(boost => boost.Kind)
                .Select(group => group
                    .OrderByDescending(boost => boost.Priority)
                    .ThenByDescending(boost => boost.BonusBasisPoints)
                    .First())
                .Select(boost => new ActiveExperienceBoost(
                    boost.StatusId,
                    boost.Kind,
                    boost.BonusBasisPoints,
                    boost.Priority,
                    CharacterBoostOnlineDuration.EffectiveExpiry(boost, now),
                    boost.Source))
                .ToList();

            var account = db.Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is not null &&
                account.DonatorTier != DonatorTier.None &&
                (account.DonatorExpiresAt is null ||
                 account.DonatorExpiresAt > now))
            {
                boosts.Add(new ActiveExperienceBoost(
                    DonatorBenefits.StatusId(account.DonatorTier),
                    ExperienceBoostKinds.Donator,
                    DonatorBenefits.ExperienceBonusBasisPoints(
                        account.DonatorTier),
                    (int)account.DonatorTier,
                    account.DonatorExpiresAt,
                    $"donator:{account.DonatorTier.ToString().ToLowerInvariant()}"));
            }

            var areaControl = db.FactionAreaExperienceControls.FirstOrDefault(control =>
                control.MapId == mapId &&
                control.ControllingCamp == camp &&
                control.ActivatedAt <= now &&
                control.ExpiresAt > now &&
                GameplayContentTestFixtures.Runtime.WorldBosses.IsWorldBoss(
                    mapId,
                    control.BossTemplateKey));
            if (areaControl is not null)
            {
                boosts.Add(new ActiveExperienceBoost(
                    ExperienceStatusIds.FactionAreaExperience,
                    ExperienceBoostKinds.FactionArea,
                    areaControl.BonusBasisPoints,
                    1,
                    areaControl.ExpiresAt,
                    $"world-boss:{areaControl.BossTemplateKey}"));
            }

            return new ExperienceBoostState(boosts.OrderBy(boost => boost.Kind).ToArray());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FactionAreaExperienceControl?> ActivateWorldBossAreaAsync(
        short mapId,
        string bossTemplateKey,
        byte controllingCamp,
        DateTimeOffset killedAt,
        string deathToken,
        CancellationToken cancellationToken = default)
    {
        if (controllingCamp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            string.IsNullOrWhiteSpace(deathToken) ||
            !GameplayContentTestFixtures.Runtime.WorldBosses.IsWorldBoss(
                mapId,
                bossTemplateKey))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var existing = db.FactionAreaExperienceControls.FirstOrDefault(control => control.MapId == mapId);
            if (existing is not null &&
                string.Equals(existing.DeathToken, deathToken, StringComparison.Ordinal))
            {
                return null;
            }

            var control = existing ?? new FactionAreaExperienceControl { MapId = checked((byte)mapId) };
            control.ControllingCamp = controllingCamp;
            control.BossTemplateKey = bossTemplateKey;
            control.DeathToken = deathToken;
            control.BonusBasisPoints = 2_500;
            control.ActivatedAt = killedAt;
            control.ExpiresAt = killedAt +
                GameplayContentTestFixtures.Runtime.WorldBosses
                    .ResolveRespawnInterval(
                        mapId,
                        bossTemplateKey,
                        WorldBossCatalog.DefaultRespawnInterval);
            if (existing is null)
            {
                db.FactionAreaExperienceControls.Add(control);
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return control;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorldBossRespawnState?> GetActiveWorldBossRespawnAsync(
        short mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var control = db.FactionAreaExperienceControls.FirstOrDefault(candidate =>
                candidate.MapId == mapId &&
                candidate.ExpiresAt > now &&
                GameplayContentTestFixtures.Runtime.WorldBosses.IsWorldBoss(
                    mapId,
                    candidate.BossTemplateKey));
            return control is null
                ? null
                : new WorldBossRespawnState(mapId, control.BossTemplateKey, control.ExpiresAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<ExperienceBoostSnapshot> IExperienceBoostStateReader.ReadAsync(
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken)
    {
        ExperienceBoostContract.ValidateRequest(request);
        var state = await GetExperienceBoostStateAsync(
            request.AccountId,
            request.CharacterId,
            request.Camp,
            checked((byte)request.MapId),
            request.ReadAtUtc,
            cancellationToken);
        return FocusedGameplayProjectionCompatibility.ToApplication(
            state,
            request.ReadAtUtc);
    }

    async Task<WorldBossAreaActivationResult>
        IWorldBossAreaControlStore.ActivateAsync(
            WorldBossAreaActivation activation,
            CancellationToken cancellationToken)
    {
        if (!WorldBossPersistenceContract.IsValid(activation))
        {
            return WorldBossAreaActivationResult.Invalid();
        }

        if (!GameplayContentTestFixtures.Runtime.WorldBosses.IsWorldBoss(
                activation.MapId,
                activation.BossTemplateKey))
        {
            return WorldBossAreaActivationResult.NotConfigured();
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var database = await LoadUnsafeAsync(cancellationToken);
            var tokenOwner = database.FactionAreaExperienceControls
                .FirstOrDefault(control => string.Equals(
                    control.DeathToken,
                    activation.DeathToken,
                    StringComparison.Ordinal));
            if (tokenOwner is not null)
            {
                return tokenOwner.MapId == activation.MapId
                    ? WorldBossAreaActivationResult.Duplicate(
                        FocusedGameplayProjectionCompatibility.ToApplication(
                            tokenOwner))
                    : WorldBossAreaActivationResult.Invalid();
            }

            var existing = database.FactionAreaExperienceControls
                .FirstOrDefault(control =>
                    control.MapId == activation.MapId);
            if (existing is not null)
            {
                var current =
                    FocusedGameplayProjectionCompatibility.ToApplication(
                        existing);
                if (existing.ActivatedAt >= activation.KilledAtUtc)
                {
                    return WorldBossAreaActivationResult.Stale(current);
                }
            }

            var control = existing ?? new FactionAreaExperienceControl
            {
                MapId = checked((byte)activation.MapId)
            };
            control.ControllingCamp = activation.ControllingCamp;
            control.BossTemplateKey = activation.BossTemplateKey;
            control.DeathToken = activation.DeathToken;
            control.BonusBasisPoints = 2_500;
            control.ActivatedAt = activation.KilledAtUtc;
            control.ExpiresAt = activation.KilledAtUtc +
                GameplayContentTestFixtures.Runtime.WorldBosses
                    .ResolveRespawnInterval(
                        activation.MapId,
                        activation.BossTemplateKey,
                        WorldBossCatalog.DefaultRespawnInterval);
            if (existing is null)
            {
                database.FactionAreaExperienceControls.Add(control);
            }

            await SaveUnsafeAsync(database, cancellationToken);
            return WorldBossAreaActivationResult.Committed(
                FocusedGameplayProjectionCompatibility.ToApplication(
                    control));
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<WorldBossRespawnSnapshot?>
        IWorldBossRespawnReader.ReadActiveAsync(
            WorldBossRespawnReadRequest request,
            CancellationToken cancellationToken)
    {
        WorldBossPersistenceContract.Validate(request);
        var respawn = await GetActiveWorldBossRespawnAsync(
            request.MapId,
            request.ReadAtUtc,
            cancellationToken);
        if (respawn is null)
        {
            return null;
        }

        var mapped = new WorldBossRespawnSnapshot(
            respawn.MapId,
            respawn.BossTemplateKey,
            respawn.RespawnAt);
        WorldBossPersistenceContract.Validate(mapped);
        return mapped;
    }

}
