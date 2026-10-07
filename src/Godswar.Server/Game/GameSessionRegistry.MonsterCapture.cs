using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryCaptureQuestMonster(ClientSession session, MonsterRuntimeSnapshot expected,
        DateTimeOffset now, out MonsterDamageResult result)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
                session.IsDisconnected || context.MapId >= 200 ||
                context.MapId != expected.Definition.MapId || context.Character.CurrentHp <= 0 ||
                context.Character.CurrentMap != context.MapId || !context.Ownership.IsValid ||
                !IsCurrentAccountSession(context.AccountId, session, context.Ownership) ||
                !context.Character.Quests.Any(q => GameClientHandler.RecordQuestCapture(q.Clone(), expected)) ||
                !WorldInstances.TryFind(context.WorldInstanceId, out var runtime))
            { result = default!; return false; }
            var attempt = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                var captured = map.TryCaptureQuestMonster(expected, now, out var value);
                return (Captured: captured, Value: value);
            });
            result = attempt.Value;
            return attempt.Captured;
        }
    }
    internal bool TryCaptureMonster(
        ClientSession routingSession,
        MonsterRuntimeSnapshot expected,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(routingSession);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Definition.MapId == 205)
        {
            return TryCaptureAtlantisPet(routingSession, expected, now, out result);
        }
        if (expected.Definition.MapId is < byte.MinValue or > byte.MaxValue ||
            !TryResolveWorldInstance(
                checked((byte)expected.Definition.MapId),
                routingSession,
                out var runtime))
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var captured = map.TryCaptureMonster(
                    expected,
                    now,
                    out var value);
                return (Captured: captured, Value: value);
            });
        result = attempt.Value;
        return attempt.Captured;
    }
}
