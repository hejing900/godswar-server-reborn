using System.Text.Json.Serialization;
using Godswar.Server.Application.Guilds;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.State;

internal sealed class GameCharacter
{
    private string _equipment = string.Empty;

    [JsonIgnore]
    internal object VitalsSync { get; } = new();

    [JsonIgnore]
    internal object ZodiacSync { get; } = new();

    public int Id { get; set; }

    public int AccountId { get; set; }

    [JsonIgnore]
    public RealmId RealmId { get; set; } = RealmId.Tempest;

    [JsonPropertyName("RealmId")]
    public int PersistedRealmId
    {
        get => RealmId.Value;
        set => RealmId = value > 0
            ? new RealmId(value)
            : RealmId.Tempest;
    }

    public short CharacterSlot { get; set; } =
        CharacterLifecyclePolicy.SingleCharacterSlot;

    public CharacterLifecycleState LifecycleState { get; set; } =
        CharacterLifecycleState.Active;

    public long LifecycleVersion { get; set; } = 1;

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset? RestoreUntil { get; set; }

    public DateTimeOffset? PurgeAfter { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Gender { get; set; }

    public byte Camp { get; set; } = GameDefaults.AthensCamp;

    public byte Profession { get; set; }

    public byte Hair { get; set; }

    public byte Face { get; set; }

    public byte Faith { get; set; } = 1;

    public byte ZodiacType { get; set; }

    public int ZodiacLuckyStatus { get; set; }

    public DateTimeOffset? ZodiacLuckyExpiresAt { get; set; }

    public byte ZodiacLevel { get; set; } = 1;

    public int ZodiacEnergy { get; set; }

    public int ZodiacEnergyRemainderX100 { get; set; }

    public DateOnly? ZodiacOnlineDay { get; set; }

    public long ZodiacOnlineDurationTicksToday { get; set; }

    public DateTimeOffset? ZodiacLastOnlineAt { get; set; }

    public DateOnly? ZodiacLastCompensationDay { get; set; }

    public int ZodiacAccumulatedExperienceX100 { get; set; }

    public int ZodiacAccumulatedTalentExperienceX100 { get; set; }

    public int[] ZodiacSkillGridLevels { get; set; } =
        ZodiacSkillGridCatalog.CreateEmptyLevels();

    public int[] ZodiacSkillGridSkillIds { get; set; } =
        ZodiacSkillGridCatalog.CreateEmptySkillIds();

    public byte CurrentMap { get; set; } = GameDefaults.AthensCapitalMap;

    public int Level { get; set; } = 1;

    public long Experience { get; set; }

    public bool FighterLevelSealed { get; set; }

    public int Silver { get; set; } = 10_000;

    // The legacy database calls premium gold "Stone". Keep the game-facing
    // name here and translate it at the PostgreSQL boundary.
    public int Gold { get; set; } = 10;

    public int BindingGold { get; set; }

    public int MedusaHonorPoints { get; set; }

    // Point Exchanger balances. These are in-memory only for now: the vendor
    // charges them, but no acquisition path exists yet, so they reset on
    // restart by design.
    public int ExchangePoint { get; set; }

    public int ExchangeMedal { get; set; }

    /// <summary>
    /// The quests the character has accepted and is working on.
    /// </summary>
    /// <remarks>
    /// A list, not a single id: the game lets a character carry several quests at
    /// once, and each one keeps its own objective progress.
    /// </remarks>
    public List<CharacterQuest> Quests { get; set; } = [];

    /// <summary>Quests the character has already handed in.</summary>
    public uint[] QuestCompletedIds { get; set; } = [];

    /// <summary>
    /// How often each repeatable quest has been completed in the current quest
    /// day, keyed by quest id.
    /// </summary>
    /// <remarks>
    /// The per-day cap lives here: a daily or guild row allows one completion a
    /// day and a repeat row three, and the main line is capped by its own
    /// completion record instead. The day itself rolls over at 12:00 server time
    /// - see <see cref="QuestDailyState"/> - so an entry stamped with another day
    /// reads back as zero. Kept in memory for the gate and written back through
    /// <c>character_quest_daily</c>, which is what makes a restart not reset it.
    /// </remarks>
    public Dictionary<uint, QuestDailyCount> QuestDailyCompletions { get; set; } = [];

    /// <summary>One repeatable quest's completion count and the day it is for.</summary>
    public readonly record struct QuestDailyCount(DateOnly Day, int Completions)
    {
        /// <summary>The completions that count towards <paramref name="today"/>.</summary>
        public int CompletedOn(DateOnly today) =>
            QuestDailyState.CompletionsToday(Day, Completions, today);
    }

    /// <summary>
    /// How often <paramref name="questId"/> has been completed in the quest day
    /// <paramref name="today"/>.
    /// </summary>
    public int QuestCompletionsOn(uint questId, DateOnly today) =>
        QuestDailyCompletions.TryGetValue(questId, out var count)
            ? count.CompletedOn(today)
            : 0;

    /// <summary>
    /// Records one more completion of <paramref name="questId"/> inside
    /// <paramref name="today"/>.
    /// </summary>
    /// <remarks>
    /// A stamp from an earlier day does not carry over: it restarts at one, which
    /// is the same thing the database upsert does.
    /// </remarks>
    public int RecordQuestCompletion(uint questId, DateOnly today)
    {
        var completed = QuestCompletionsOn(questId, today) + 1;
        QuestDailyCompletions[questId] = new QuestDailyCount(today, completed);
        return completed;
    }

    public long MedusaRewardRevision { get; set; }

    public uint SelectedTitleId { get; set; }

    public uint[] OwnedTitleIds { get; set; } = [];

    public long FactionCrierRevision { get; set; }

    public long OnlineAwardRevision { get; set; }

    public int MaxHp { get; set; } = 1500;

    public int MaxMp { get; set; } = 177;

    public int CurrentHp { get; set; } = 1500;

    public int CurrentMp { get; set; } = 177;

    public long VitalsRevision { get; set; }

    public long PositionRevision { get; set; }

    [JsonIgnore]
    public Guid CheckpointOwnerId { get; set; }

    [JsonIgnore]
    public long CheckpointOwnerGeneration { get; set; }

    public int TalentPoints { get; set; } = 10;

    public int TalentExperience { get; set; }

    public int HolySuitPoints { get; set; }

    public short WeaponRank { get; set; }

    public int WeaponAuraEffect { get; set; }

    public short ArmorRank { get; set; }

    public int ArmorAuraEffect { get; set; }

    // The stock client owns this preference in BagSet.xml and resends it on
    // login (opcode 10200). It affects only world appearance projection: the
    // equipped Fashion item and all authoritative item state remain intact.
    [JsonIgnore]
    public bool FashionHidden { get; set; }

    // The stock client owns the Fashion Effect preference in BagSet.xml and
    // resends it through opcode 10202. The native renderer uses this one flag
    // for both armor/body and held-weapon aura effects. It is presentation-only
    // and must never change the equipped items, their ranks, or persisted stats.
    [JsonIgnore]
    public bool EquipmentEffectsVisible { get; set; } = true;

    public float PositionX { get; set; } = GameDefaults.StartingPositionX;

    public float PositionZ { get; set; } = GameDefaults.StartingPositionZ;

    public string Equipment
    {
        get => _equipment;
        set
        {
            _equipment = value ?? string.Empty;
            ElementalEquipment = ElementalAttributeCatalog
                .CalculateEquippedProfile(ParseEquipment(_equipment));
        }
    }

    public string KitBag { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public CharacterStats? CalculatedStats { get; set; }

    /// <summary>
    /// The character's maximum health including every projection bonus.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxHp"/> is the live, effective ceiling: the projection applier
    /// writes the character's own value plus anything that raises it (the altar's
    /// HP ceiling, the elemental passive), so nothing here needs to add a bonus
    /// again. Only the persisted base is kept elsewhere - it lives in the
    /// character's own columns and in <see cref="CalculatedStats"/>.
    /// </remarks>
    [JsonIgnore]
    public int EffectiveMaxHp => MaxHp;

    /// <summary>
    /// The character's maximum mana including every projection bonus. See
    /// <see cref="EffectiveMaxHp"/>.
    /// </summary>
    [JsonIgnore]
    public int EffectiveMaxMp => MaxMp;

    [JsonIgnore]
    public ElementalEquipmentProfile ElementalEquipment { get; private set; } =
        ElementalAttributeCatalog.CalculateEquippedProfile([]);

    internal long MarkVitalsChanged()
    {
        VitalsRevision = checked(VitalsRevision + 1);
        return VitalsRevision;
    }

    internal void AddOwnedTitle(uint titleId)
    {
        if (titleId == 0 || OwnedTitleIds.Contains(titleId))
        {
            return;
        }

        OwnedTitleIds = [.. OwnedTitleIds, titleId];
        Array.Sort(OwnedTitleIds);
    }

    internal long MarkPositionChanged()
    {
        PositionRevision = checked(PositionRevision + 1);
        return PositionRevision;
    }

    private static IEnumerable<ElementalEquippedItem> ParseEquipment(
        string equipment) =>
        equipment.Split('#', StringSplitOptions.None)
            .Take(EquipmentSlots.Shield + 1)
            .Select((entry, slot) => new ElementalEquippedItem(
                slot,
                CompactItemEntry.Parse(entry)));
}
