namespace Godswar.Server.ProtocolChecks;

internal readonly record struct LegacyStoreCallAllowance(
    string Path,
    string Member,
    int Count);

internal readonly record struct ReferenceAllowance(
    string Path,
    int Count);

internal sealed record DataBoundaryBaselineSnapshot(
    IReadOnlyList<LegacyStoreCallAllowance> StoreCalls,
    IReadOnlyList<ReferenceAllowance> StoreFieldReferences,
    IReadOnlyList<ReferenceAllowance> StoreParameterReferences,
    IReadOnlyList<ReferenceAllowance> StoreTypeReferences,
    IReadOnlyList<ReferenceAllowance> LegacyNpgsqlReferences,
    IReadOnlyList<ReferenceAllowance> StateToGameUsings,
    IReadOnlyList<string> GameStoreMethods);

internal static class DataBoundaryArchitectureBaseline
{
    public static readonly LegacyStoreCallAllowance[] StoreCalls =
    [
        new(
            "Game/GameClientHandler.CharacterCheckpoints.cs",
            "SaveCharacterPositionAsync",
            1),
        new(
            "Game/GameClientHandler.CharacterCheckpoints.cs",
            "SaveCharacterVitalsAsync",
            1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "AddDeveloperMountAsync", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "AddForgingMaterialAsync", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "ClearKitBagAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "DeleteKitBagItemAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "MoveKitBagItemAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "MoveKitBagToEquipmentAsync", 1),
        new("Game/GameClientHandler.Forging.cs", "ForgeEquipmentAsync", 1),
        new("Game/GameClientHandler.GearEnhancer.cs", "EnhanceGearAsync", 1),
        new("Game/GameClientHandler.GearMentor.cs", "ProcessGearMentorAsync", 1),
        new("Game/GameClientHandler.InventoryActions.cs", "MoveEquipmentToKitBagAsync", 1),
        new("Game/GameClientHandler.InventoryActions.cs", "UpgradeTalentAsync", 1),
        new(
            "Game/GameClientHandler.DurableCharacterLifecycle.cs",
            "CreateCharacterAsync",
            1),
        new(
            "Game/GameClientHandler.DurableCharacterLifecycle.cs",
            "DeleteCharacterAsync",
            1),
        new(
            "Game/GameClientHandler.LegacyHolyStone.cs",
            "ApplyWeaponHolyStoneAsync",
            1),
        new("Game/GameClientHandler.Progression.cs", "ApplyMonsterKillRewardAsync", 1),
        new("Game/GameClientHandler.ZeusGift.cs", "ApplyMonsterKillRewardAsync", 1),
        new(
            "Game/GameSessionRegistry.CharacterCheckpoints.cs",
            "SaveCharacterVitalsAsync",
            1),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "ActivateZodiacSkillGridAsync",
            2),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "UpgradeZodiacSkillGridAsync",
            2),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "SelectZodiacSkillGridAsync",
            2),
    ];

    public static readonly ReferenceAllowance[] StoreTypeReferences =
    [
        new("GameClientHandlerFactory.cs", 1),
        new("Game/GameClientHandler.Construction.cs", 1),
        new("Game/GameClientHandler.cs", 1),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.PlayerRuntimeEcs.cs", 1),
        new("Program.cs", 1)
    ];

    public static readonly ReferenceAllowance[] StoreFieldReferences =
    [
        new("Game/GameClientHandler.CharacterCheckpoints.cs", 2),
        new("Game/GameClientHandler.Construction.cs", 1),
        new("Game/GameClientHandler.cs", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", 3),
        new("Game/GameClientHandler.Equipment.cs", 3),
        new("Game/GameClientHandler.Forging.cs", 1),
        new("Game/GameClientHandler.GearEnhancer.cs", 1),
        new("Game/GameClientHandler.GearMentor.cs", 1),
        new("Game/GameClientHandler.InventoryActions.cs", 2),
        new("Game/GameClientHandler.DurableCharacterLifecycle.cs", 2),
        new("Game/GameClientHandler.LegacyHolyStone.cs", 1),
        new("Game/GameClientHandler.Progression.cs", 1),
        new("Game/GameClientHandler.Quests.cs", 5),
        new("Game/GameClientHandler.ZeusGift.cs", 1),
        new("Game/GameSessionRegistry.CharacterCheckpoints.cs", 2),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.MountStatus.cs", 1),
        new("Game/GameSessionRegistry.PlayerStatusMutations.cs", 1),
        new("Game/GameSessionRegistry.PlayerStatusPublishing.cs", 1),
        new("Game/GameSessionRegistry.ZodiacSkillGrids.cs", 9)
    ];

    public static readonly ReferenceAllowance[] StoreParameterReferences =
    [
        new("GameClientHandlerFactory.cs", 2),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.PlayerRuntimeEcs.cs", 2),
        new("Program.cs", 3),
        new("Security/Authentication/AccountAuthenticationService.cs", 1)
    ];

    public static readonly ReferenceAllowance[] LegacyNpgsqlReferences =
    [
        new("Operations/ControlledHostValidationCommand.cs", 2),
        new("State/DatabaseMigrations/PostgresSchemaMigrationRunner.cs", 17),
        new("State/PostgresGameStore.Characters.cs", 2),
        new("State/PostgresGameStore.CharacterLookup.cs", 3),
        new("State/PostgresGameStore.Characters.Persistence.cs", 12),
        new("State/PostgresGameStore.Crafting.cs", 7),
        new("State/PostgresGameStore.cs", 4),
        new("State/PostgresGameStore.Inventory.Grants.cs", 9),
        new("State/PostgresGameStore.Inventory.HolyStones.cs", 11),
        new("State/PostgresGameStore.Inventory.Movement.cs", 12),
        new("State/PostgresGameStore.Inventory.Persistence.cs", 24),
        new("State/PostgresGameStore.Inventory.Projection.cs", 23),
        new("State/PostgresGameStore.PetEggs.Audit.cs", 4),
        new("State/PostgresGameStore.PetEggs.cs", 10),
        new("State/PostgresGameStore.PetLevel.cs", 16),
        new("State/PostgresGameStore.PetLevelStats.cs", 8),
        new("State/PostgresGameStore.PetPresence.cs", 18),
        new("State/PostgresGameStore.PetPresenceAudit.cs", 10),
        new("State/PostgresGameStore.Pets.cs", 6),
        new("State/PostgresGameStore.Progression.cs", 4),
        new("State/PostgresGameStore.SkillsAndTalents.cs", 7),
        new("State/PostgresGameStore.ZodiacSkillGrids.cs", 20)
    ];

    public static readonly ReferenceAllowance[] StateToGameUsings =
    [
        new("State/PostgresGameStore.Characters.cs", 1),
        new("State/PostgresGameStore.Characters.Persistence.cs", 1),
        new("State/PostgresGameStore.Crafting.cs", 1),
        new("State/PostgresGameStore.cs", 1),
        new("State/PostgresGameStore.Inventory.Grants.cs", 1),
        new("State/PostgresGameStore.Inventory.HolyStones.cs", 1),
        new("State/PostgresGameStore.Inventory.Movement.cs", 1),
        new("State/PostgresGameStore.Inventory.Persistence.cs", 1),
        new("State/PostgresGameStore.Inventory.Projection.cs", 1),
        new("State/PostgresGameStore.PetEggs.Audit.cs", 1),
        new("State/PostgresGameStore.PetEggs.cs", 1),
        new("State/PostgresGameStore.Progression.cs", 1),
        new("State/PostgresGameStore.SkillsAndTalents.cs", 1),
    ];

    public static readonly string[] GameStoreMethods =
    [
        "ActivateZodiacSkillGridAsync",
        "AddDeveloperMountAsync",
        "AddForgingMaterialAsync",
        "ApplyMonsterKillRewardAsync",
        "ApplyWeaponHolyStoneAsync",
        "ClearKitBagAsync",
        "CreateCharacterAsync",
        "CreateCharacterAsync",
        "DeleteCharacterAsync",
        "DeleteCharacterAsync",
        "DeleteKitBagItemAsync",
        "EnhanceGearAsync",
        "EnsureSeedDataAsync",
        "ForgeEquipmentAsync",
        "GetCharactersAsync",
        "GetCharactersAsync",
        "GetFirstCharacterAsync",
        "GetFirstCharacterAsync",
        "GetTalentStatesAsync",
        "MoveEquipmentToKitBagAsync",
        "MoveKitBagItemAsync",
        "MoveKitBagToEquipmentAsync",
        "ProcessGearMentorAsync",
        "SaveCharacterPositionAsync",
        "SaveCharacterVitalsAsync",
        "SelectZodiacSkillGridAsync",
        "UpgradeTalentAsync",
        "UpgradeZodiacSkillGridAsync"
    ];

    public const string GameStoreSignatureSha256 =
        "9D188033C31713B4CD5F561F5FE0F17F5F9F6A974B3140E329A330CCA17DF204";

    public static DataBoundaryBaselineSnapshot Snapshot { get; } =
        new(
            StoreCalls,
            StoreFieldReferences,
            StoreParameterReferences,
            StoreTypeReferences,
            LegacyNpgsqlReferences,
            StateToGameUsings,
            GameStoreMethods);
}
