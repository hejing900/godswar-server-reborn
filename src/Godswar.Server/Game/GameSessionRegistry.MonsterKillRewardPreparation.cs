using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal void RegisterPveMonsterKillRewardPreparer(
        ClientSession session,
        Func<MonsterDamageResult,
            Task<PreparedPveMonsterKillReward?>> preparer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preparer);
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
            {
                _sessions[session] = context with
                {
                    PreparePveMonsterKillReward = preparer
                };
            }
        }
    }

    internal void RegisterQuestPlayerKillRecorder(
        ClientSession session,
        Func<GameSessionContext, CancellationToken, Task> recorder)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
                _sessions[session] = context with { RecordQuestPlayerKill = recorder };
        }
    }
}
