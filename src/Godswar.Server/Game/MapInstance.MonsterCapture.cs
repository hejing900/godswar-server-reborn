namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    // Quest capability is validated by the registry; ordinary instance capture
    // retains its own Medusa/Atlantis admission rules below.
    internal bool TryCaptureQuestMonster(MonsterRuntimeSnapshot expected,
        DateTimeOffset now, out MonsterDamageResult result)
    {
        if (MapId >= 200 || expected.Definition.MapId != MapId)
        { result = default!; return false; }
        lock (_monsterRuntimeGate) return TryCaptureMonsterCore(expected, now, out result);
    }
    internal bool TryCaptureMonster(
        MonsterRuntimeSnapshot expected,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (MapId == 205)
        {
            return TryCaptureAtlantisPet(expected, now, out result);
        }
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null)
            {
                result = default!;
                return false;
            }

            lock (_monsterRuntimeGate)
            {
                return TryCaptureMonsterCore(expected, now, out result);
            }
        }
    }

    // Caller holds the encounter guard and monster gate.
    private bool TryCaptureMonsterCore(MonsterRuntimeSnapshot expected,
        DateTimeOffset now, out MonsterDamageResult result)
    {
        if (_monsterRuntime is null ||
            !_monsterRuntime.TryGetSnapshot(
                expected.ObjectId,
                out var current) ||
            current.RuntimeInstanceId !=
                expected.RuntimeInstanceId ||
            current.SpawnGeneration !=
                expected.SpawnGeneration ||
            current.HealthRevision != expected.HealthRevision ||
            !current.IsAlive ||
            !current.IsSpawned ||
            current.CurrentHealth == 0 ||
            !_monsterRuntime.TryApplyDamage(
                current.ObjectId,
                current.CurrentHealth,
                attackerCharacterId: null,
                current.SpawnGeneration,
                now,
                out result) ||
            !result.Killed)
        {
            result = default!;
            return false;
        }

        return true;
    }
}
