using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.World;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server;

internal sealed class ServerRuntimeBootstrapContext
{
    private readonly ServerOptions _options;
    private readonly IWorldContentReader _world;
    private readonly GameplayItemContent _items;
    private readonly PinnedPetContentCatalog _pets;
    private readonly PinnedPetOwnerMergeContentCatalog _ownerMerge;
    private readonly PinnedPetLearnedSkillContentCatalog _learnedSkills;
    private readonly HolySpiritBalanceSnapshot _holySpiritBalance;
    private readonly WarehouseExpansionPolicySnapshot _expansionPolicy;
    private readonly MonsterRewardPolicySnapshot _monsterRewardPolicy;
    private readonly ServerDailyNpcBalances _dailyNpcs;

    private ServerRuntimeBootstrapContext(
        ServerOptions options,
        IWorldContentReader world,
        GameplayItemContent items,
        PinnedPetContentCatalog pets,
        PinnedPetOwnerMergeContentCatalog ownerMerge,
        PinnedPetLearnedSkillContentCatalog learnedSkills,
        HolySpiritBalanceSnapshot holySpiritBalance,
        WarehouseExpansionPolicySnapshot expansionPolicy,
        MonsterRewardPolicySnapshot monsterRewardPolicy,
        ServerDailyNpcBalances dailyNpcs)
    {
        _options = options;
        _world = world;
        _items = items;
        _pets = pets;
        _ownerMerge = ownerMerge;
        _learnedSkills = learnedSkills;
        _holySpiritBalance = holySpiritBalance;
        _expansionPolicy = expansionPolicy;
        _monsterRewardPolicy = monsterRewardPolicy;
        _dailyNpcs = dailyNpcs;
    }

    public static async Task<ServerRuntimeBootstrapContext> LoadAsync(
        ServerOptions options,
        IWorldContentReader world,
        GameplayItemContent items,
        PinnedPetContentCatalog pets,
        PinnedPetOwnerMergeContentCatalog ownerMerge,
        PinnedPetLearnedSkillContentCatalog learnedSkills,
        HolySpiritBalanceSnapshot holySpiritBalance,
        ServerDailyNpcBalances dailyNpcs,
        CancellationToken cancellationToken = default)
    {
        var policy = await ServerRuntimeContentComposition
            .LoadWarehouseExpansionPolicyAsync(
                options,
                items,
                cancellationToken);
        var medusaRewards = await PostgresMedusaRewardPolicySnapshotReader
            .LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var medusaMonsters = await
            PostgresMedusaMonsterContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var monsterRewardPolicy = await
            PostgresMonsterRewardPolicySnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var monsterLoot = await
            PostgresMonsterLootContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var questRewards = await
            PostgresQuestRewardContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var monsterOverrides = await
            PostgresMonsterOverrideSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        var gmNpcs = await PostgresGmNpcSnapshotReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
        MedusaRewardPolicyCatalog.Install(medusaRewards);
        MedusaMonsterContentCatalog.Install(medusaMonsters);
        MonsterLootContentCatalog.Install(monsterLoot);
        QuestRewardContentCatalog.Install(questRewards);
        MonsterOverrideCatalog.Install(monsterOverrides);
        GmNpcOverrideCatalog.Install(gmNpcs);
        Console.WriteLine(
            $"[monster-overrides] loaded GM spawns={monsterOverrides.Spawns.Count} " +
            $"edits={monsterOverrides.Edits.Count} " +
            $"template-attributes={monsterOverrides.TemplateAttributes.Count}");
        Console.WriteLine(
            $"[gm-npc] loaded GM placements={gmNpcs.Spawns.Count} " +
            $"dialogues={gmNpcs.Dialogues.Count}");
        return new(
            options,
            world,
            items,
            pets,
            ownerMerge,
            learnedSkills,
            holySpiritBalance,
            policy,
            monsterRewardPolicy,
            dailyNpcs);
    }

    public MonsterRewardPolicySnapshot MonsterRewardPolicy =>
        _monsterRewardPolicy;

    public PostgresApplicationDataRuntime CreateApplicationData(
        RealmCalendar realmCalendar) =>
        ServerRuntimeContentComposition.CreateApplicationData(
            _options,
            _world,
            _items,
            _pets,
            _ownerMerge,
            _learnedSkills,
            _holySpiritBalance,
            _expansionPolicy,
            _dailyNpcs,
            realmCalendar);

    public ValueTask<ServerCoordinationComposition>
        CreateCoordinationAsync(
        RealmCalendarCatalog realmCalendars,
        CancellationToken cancellationToken) =>
        ServerRuntimeContentComposition.CreateCoordinationAsync(
            _options,
            _world,
            _items,
            _pets,
            _ownerMerge,
            _learnedSkills,
            _holySpiritBalance,
            _expansionPolicy,
            _monsterRewardPolicy,
            _dailyNpcs,
            realmCalendars,
            cancellationToken);
}
