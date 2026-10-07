namespace Godswar.Server.State;

/// <summary>Compatibility projection contract; SQL belongs to the feature adapter.</summary>
internal interface ICapitalShopPurchaseStore
{
    Task<CapitalShopPurchaseResult> PurchaseCapitalShopItemAsync(
        int accountId,
        int characterId,
        Guid purchaseId,
        CapitalShopOffer offer,
        int quantity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CapitalShopPurchaseResult(
            CapitalShopPurchaseStatus.UnsupportedItem,
            Character: null,
            CurrencyBalance: 0));

    Task<CapitalShopSaleResult> SellCapitalShopItemAsync(
        int accountId,
        int characterId,
        Guid saleId,
        int sourceSlot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CapitalShopSaleResult.Rejected(
            CapitalShopSaleStatus.UnsupportedItem));

}

/// <summary>Idempotent loot and summoned-pet reward supplements.</summary>
internal interface IMonsterRewardExtrasStore
{
    Task<PetMonsterExperienceResult> ApplyPetMonsterKillExperienceAsync(
        int accountId,
        int characterId,
        Guid deathEventId,
        int experience,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PetMonsterExperienceResult(
            PetMonsterExperienceStatus.NoSummonedPet,
            deathEventId,
            0,
            PetId: null,
            TotalExperience: null,
            PetRevision: null));

    Task<MonsterLootPickupResult> PickupMonsterLootAsync(
        int accountId,
        int characterId,
        Guid deathEventId,
        int lootIndex,
        uint itemId,
        int quantity,
        bool? boundOnPickup = null,
        ItemGrantAttributes? attributes = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonsterLootPickupResult(
            MonsterLootPickupStatus.Unsupported,
            Character: null));

    /// <summary>
    /// Writes a quest reward item into the character's kit bag, once per reward
    /// slot.
    /// </summary>
    /// <remarks>
    /// The client pays the slot it was offered and then announces the item on
    /// opcode 10056; this is the write that makes the item survive a relog.
    /// </remarks>
    Task<QuestRewardItemGrantResult> GrantQuestRewardItemAsync(
        int accountId,
        int characterId,
        uint questId,
        int slotIndex,
        uint itemId,
        int quantity,
        ItemGrantAttributes attributes,
        CancellationToken cancellationToken = default,
        Guid rewardClaimId = default) =>
        Task.FromResult(new QuestRewardItemGrantResult(
            QuestRewardItemGrantStatus.Unsupported,
            Character: null));

}

internal sealed class UnsupportedGameplayFeatures :
    ICapitalShopPurchaseStore,
    IMonsterRewardExtrasStore,
    ILelantineFarmPointsStore
{
    public static readonly UnsupportedGameplayFeatures Instance = new();
    private UnsupportedGameplayFeatures() { }
}
