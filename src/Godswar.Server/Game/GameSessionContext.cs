using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record GameSessionContext(
    ClientSession Session,
    int AccountId,
    int CharacterId,
    string CharacterName,
    RealmId RealmId,
    WorldInstanceId WorldInstanceId,
    byte MapId,
    uint ObjectId,
    GameCharacter Character,
    bool WorldReady,
    long WorldRevision)
{
    private readonly bool _worldReady = WorldReady;

    // Preserve authored scene readiness in the record while terminal session
    // state immediately revokes every reader's live-world eligibility. Physical
    // membership cleanup can then wait for outstanding delivery leases safely.
    public bool WorldReady
    {
        get => _worldReady && !Session.IsDisconnected;
        init => _worldReady = value;
    }

    /// <summary>
    /// Registry-local membership lineage. Routine character revisions keep
    /// this value; join/rejoin/instance transfer always advances it.
    /// </summary>
    public long WorldMembershipEpoch { get; init; }

    public PlayerOwnershipFence Ownership { get; init; }

    public bool PetOwnerMergeActive { get; init; }

    public PetAptitude PetOwnerMergeAptitude { get; init; }

    public short PetOwnerMergeCompletedRebirths { get; init; }

    /// <summary>
    /// The pet currently visible beside this player in the world. This is an
    /// AOI snapshot only; recalled and owner-merged pets have no separate
    /// world model.
    /// </summary>
    public PetBootstrapSnapshot? SummonedPet { get; init; }

    public Func<MonsterDamageResult,
        Task<PreparedPveMonsterKillReward?>>?
        PreparePveMonsterKillReward { get; init; }

    public Func<GameSessionContext, CancellationToken, Task>?
        RecordQuestPlayerKill { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(CharacterName)
        ? $"character:{CharacterId}"
        : CharacterName;
}

internal sealed class PreparedPveMonsterKillReward
{
    private readonly Func<CancellationToken, Task> _publish;

    public PreparedPveMonsterKillReward(
        Func<CancellationToken, Task> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(CancellationToken cancellationToken) =>
        _publish(cancellationToken);
}
