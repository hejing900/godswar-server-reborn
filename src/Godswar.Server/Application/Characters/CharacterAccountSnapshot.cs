using System.Collections.Immutable;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Characters;

internal sealed record CharacterAccountSnapshot(
    int ContractVersion,
    int AccountId,
    string ProviderSnapshotToken,
    DateTimeOffset ReadAtUtc,
    CharacterSlotPolicy SlotPolicy,
    CharacterLoadSnapshot? Character)
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;
}

internal sealed record CharacterLoadSnapshot(
    CharacterIdentitySnapshot Identity,
    CharacterAppearanceSnapshot Appearance,
    CharacterLocationSnapshot Location,
    CharacterProgressionSnapshot Progression,
    CharacterVitalsSnapshot Vitals,
    CharacterWalletSnapshot Wallet,
    CharacterLoadoutSnapshot Loadout,
    CharacterZodiacSnapshot Zodiac,
    CharacterCalculatedStatsSnapshot CalculatedStats,
    ImmutableArray<CharacterSkillSnapshot> Skills,
    ImmutableArray<CharacterTalentSnapshot> Talents,
    CharacterPetShedSnapshot PetShed,
    ImmutableArray<CharacterPetSnapshot> Pets,
    ImmutableArray<CharacterProgressionBoostSnapshot> PersonalBoosts);

internal sealed record CharacterPetShedSnapshot(
    short OpenedCellCount,
    long Revision);

internal sealed record CharacterIdentitySnapshot(
    int CharacterId,
    int AccountId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    short CharacterSlot = 0,
    long LifecycleVersion = 1)
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;

    public long FactionCrierRevision { get; init; }

    public long OnlineAwardRevision { get; init; }

    /// <summary>
    /// The quests the character carries, in quest id order.
    /// </summary>
    /// <remarks>
    /// Part of the login snapshot because the accepted-quest list is published
    /// from it: a snapshot that leaves this empty drops every quest on relog and
    /// the client falls back to offering the first quest again.
    /// </remarks>
    public ImmutableArray<LoadedQuestSnapshot> Quests { get; init; } = [];

    /// <summary>Quests the character has already handed in.</summary>
    public ImmutableArray<uint> QuestCompletedIds { get; init; } = [];
}

/// <summary>One accepted quest as the login snapshot carries it.</summary>
internal readonly record struct LoadedQuestSnapshot(uint QuestId, long Progress);

internal sealed record CharacterAppearanceSnapshot(
    byte Gender,
    byte Camp,
    byte Profession,
    byte Hair,
    byte Face,
    byte Faith,
    uint SelectedTitleId = 0)
{
    public ImmutableArray<uint> OwnedTitleIds { get; init; } = [];
}

internal sealed record CharacterLocationSnapshot(
    byte CurrentMap,
    float PositionX,
    float PositionZ,
    long PositionRevision);

internal sealed record CharacterProgressionSnapshot(
    int Level,
    long Experience,
    int TalentPoints,
    int TalentExperience,
    int HolySuitPoints,
    bool FighterLevelSealed,
    long Revision = 0);

internal static class CharacterProgressionSnapshotRules
{
    public const int MaximumCharacterLevel = 200;
}

internal sealed record CharacterVitalsSnapshot(
    int BaseMaxHp,
    int BaseMaxMp,
    int PersistedCurrentHp,
    int PersistedCurrentMp,
    long Revision);

internal sealed record CharacterWalletSnapshot(
    int Silver,
    int Gold,
    int BindingGold = 0,
    int MedusaHonorPoints = 0,
    long MedusaRewardRevision = 0,
    int ExchangePoint = 0,
    int ExchangeMedal = 0);

internal sealed record CharacterLoadoutSnapshot(
    string Equipment,
    string KitBag,
    short WeaponRank,
    int WeaponAuraEffect,
    short ArmorRank,
    int ArmorAuraEffect,
    long InventoryRevision = 0);

internal sealed record CharacterZodiacSnapshot(
    byte Type,
    int LuckyStatus,
    DateTimeOffset? LuckyExpiresAtUtc,
    byte Level,
    int Energy,
    int EnergyRemainderX100,
    DateOnly? OnlineDay,
    long OnlineDurationTicksToday,
    DateTimeOffset? LastOnlineAtUtc,
    DateOnly? LastCompensationDay,
    int AccumulatedExperienceX100,
    int AccumulatedTalentExperienceX100,
    ImmutableArray<int> SkillGridLevels,
    ImmutableArray<int> SkillGridSkillIds);

internal sealed record CharacterSkillSnapshot(
    int SkillId,
    int Level);

internal sealed record CharacterTalentSnapshot(
    int TalentId,
    int Rank,
    int DisplayValue,
    int NextCost);

internal sealed record CharacterProgressionBoostSnapshot(
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    int Priority,
    DateTimeOffset ActivatedAtUtc,
    long? RemainingOnlineTicks,
    string Source);
