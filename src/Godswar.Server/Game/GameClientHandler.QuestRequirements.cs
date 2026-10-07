using System.Collections.Concurrent;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // Emulator policy: a character must approach a published exploration point
    // within ten world units. Coordinates and maps come directly from Quest.xml.
    internal const float QuestExploreRadius = 10f;
    internal const int QuestCollectCounterSlot = 3;
    internal const double QuestCollectDropChance = 0.15;

    private Task SendQuestCollectionNoticeAsync(CharacterQuest quest, CancellationToken cancellationToken)
    {
        if (!StarterQuestCollectObjectives.TryGet(quest.QuestId, out var collect)) return Task.CompletedTask;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var title = QuestProgressTexts.Titles.GetValueOrDefault(quest.QuestId, "收集任务");
        var text = $"{title}：{collect.DisplayName} {StarterQuestObjectives.Counter(quest.Progress, QuestCollectCounterSlot)}/{collect.Required}";
        return _session.SendAsync(PacketBuilder.PersonalNotice(Encoding.GetEncoding(936).GetBytes(text)),
            cancellationToken, "QuestCollectionNotice");
    }

    internal static IReadOnlyList<QuestObjective> DisplayQuestObjectives(uint questId)
    {
        if (QuestReviewedBatch.ByQuestId.TryGetValue(questId, out var reviewed) && !reviewed.Collection)
            return reviewed.Targets.All(target => target.NativeId != 0)
                ? reviewed.Targets.Select(target => new QuestObjective(target.Label, target.Label,
                    target.Required, target.NativeId, reviewed.Maps[0], 0, 0)).ToArray()
                : [];
        if (reviewed is { Collection: true, PreserveKillQuota: false }) return [];
        return QuestRuntimeRequirements.Captures.TryGetValue(questId, out var captures)
            ? captures : StarterQuestObjectives.For(questId);
    }

    internal static bool AreQuestRequirementsSatisfied(GameCharacter character, CharacterQuest quest)
    {
        var reviewedKill = QuestReviewedBatch.ByQuestId.TryGetValue(quest.QuestId, out var reviewed) && !reviewed.Collection;
        if (QuestUnresolvedRequirements.QuestIds.Contains(quest.QuestId) && !reviewedKill &&
            !StarterQuestCollectObjectives.TryGet(quest.QuestId, out _)) return false;
        if (reviewedKill)
        {
            for (var slot = 0; slot < reviewed!.Targets.Length; slot++)
                if (StarterQuestObjectives.Counter(quest.Progress, slot) < reviewed.Targets[slot].Required)
                    return false;
        }
        else if (reviewed is not { Collection: true, PreserveKillQuota: false } &&
            !StarterQuestObjectives.IsSatisfied(StarterQuestObjectives.For(quest.QuestId), quest.Progress))
        {
            return false;
        }
        if (StarterQuestCollectObjectives.TryGet(quest.QuestId, out var collect) &&
            StarterQuestObjectives.Counter(quest.Progress, QuestCollectCounterSlot) < collect.Required)
        {
            return false;
        }
        if (QuestRuntimeRequirements.Captures.TryGetValue(quest.QuestId, out var captures) &&
            (captures.Length == 0 || !StarterQuestObjectives.IsSatisfied(captures, quest.Progress)))
            return false;
        if (!QuestAdditionalRequirements.ByQuestId.TryGetValue(quest.QuestId, out var requirement))
        {
            return true;
        }
        if (character.Level < requirement.HandInMinimumLevel)
        {
            return false;
        }
        for (var slot = 0; slot < requirement.Exploration.Length; slot++)
        {
            if (StarterQuestObjectives.Counter(quest.Progress, slot) == 0)
            {
                return false;
            }
        }
        return StarterQuestObjectives.Counter(quest.Progress, 0) >= requirement.PlayerKills;
    }

    internal static bool RecordQuestCollection(CharacterQuest quest, uint map, string? monsterName,
        float x, float z, Func<double> roll, string? templateKey = null)
    {
        if (!StarterQuestCollectObjectives.TryGet(quest.QuestId, out var collect)) return false;
        var current = StarterQuestObjectives.Counter(quest.Progress, QuestCollectCounterSlot);
        if (current >= collect.Required) return false;
        var targets = StarterQuestObjectives.For(quest.QuestId);
        var matched = targets.Any(target => StarterQuestObjectives.Matches(target, map, monsterName, x, z));
        if (QuestRuntimeRequirements.Collections.TryGetValue(quest.QuestId, out var source))
            matched = source.Monsters.Length > 0
                ? source.Monsters.Any(target => StarterQuestObjectives.Matches(target, map, monsterName, x, z))
                : matched;
        if (QuestReviewedBatch.ByQuestId.TryGetValue(quest.QuestId, out var reviewed) && reviewed.Collection)
            matched = reviewed.Targets.Any(target => MatchesReviewedQuestTarget(reviewed, target, map, monsterName, templateKey));
        if (!matched || roll() >= QuestCollectDropChance) return false;
        quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, QuestCollectCounterSlot, current + 1);
        return true;
    }

    internal static bool RecordQuestExploration(GameCharacter character, CharacterQuest quest)
    {
        if (!QuestAdditionalRequirements.ByQuestId.TryGetValue(quest.QuestId, out var requirement))
        {
            return false;
        }
        var changed = false;
        for (var slot = 0; slot < requirement.Exploration.Length; slot++)
        {
            var point = requirement.Exploration[slot];
            var dx = point.X - character.PositionX;
            var dz = point.Z - character.PositionZ;
            if (point.Map == character.CurrentMap &&
                dx * dx + dz * dz <= QuestExploreRadius * QuestExploreRadius &&
                StarterQuestObjectives.Counter(quest.Progress, slot) == 0)
            {
                quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, slot, 1);
                changed = true;
            }
        }
        return changed;
    }

    private async Task RecordQuestExplorationAsync(CancellationToken cancellationToken)
    {
        if (_character is null || _character.CurrentHp <= 0)
        {
            return;
        }
        var changed = false;
        foreach (var quest in _character.Quests)
        {
            if (!RecordQuestExploration(_character, quest))
            {
                continue;
            }
            changed = true;
        }
        if (changed)
        {
            await SaveQuestStateAsync(cancellationToken);
            await SendQuestSnapshotAsync("exploration-progress", cancellationToken);
            await SendLoginQuestProgressAsync(cancellationToken);
            await SendQuestNpcMarksAsync(cancellationToken);
        }
    }

    private readonly ConcurrentQueue<QuestPlayerDeath> _questPlayerDeaths = new();
    private readonly record struct QuestPlayerDeath(int KillerId, int VictimId, byte Camp,
        int Level, int KillerMap, int VictimMap);

    // Combat publishers can run while a packet already owns the character gate.
    // Queue immutable facts; the packet loop applies them under that same gate.
    private Task RecordQuestPlayerKillAsync(GameSessionContext victim, CancellationToken cancellationToken)
    {
        if (_character is { } killer && !_session.IsDisconnected)
            _questPlayerDeaths.Enqueue(new(killer.Id, victim.CharacterId, victim.Character.Camp,
                victim.Character.Level, killer.CurrentMap, victim.MapId));
        return Task.CompletedTask;
    }

    private async Task DrainQuestPlayerDeathsAsync(CancellationToken cancellationToken)
    {
        while (_questPlayerDeaths.TryDequeue(out var victim))
            await ApplyQuestPlayerDeathAsync(victim, cancellationToken);
    }

    private async Task ApplyQuestPlayerDeathAsync(QuestPlayerDeath victim, CancellationToken cancellationToken)
    {
        if (_character is null || victim.KillerId != _character.Id || victim.VictimId == _character.Id ||
            victim.Camp == _character.Camp)
        {
            return;
        }
        var changed = false;
        foreach (var quest in _character.Quests)
        {
            if (!QuestAdditionalRequirements.ByQuestId.TryGetValue(quest.QuestId, out var requirement) ||
                requirement.PlayerKills == 0 || requirement.PlayerMap != victim.KillerMap ||
                victim.VictimMap != victim.KillerMap || victim.Level < requirement.PlayerMinimumLevel)
            {
                continue;
            }
            var current = StarterQuestObjectives.Counter(quest.Progress, 0);
            if (current >= requirement.PlayerKills) continue;
            quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, 0, current + 1);
            changed = true;
        }
        if (changed)
        {
            await SaveQuestStateAsync(cancellationToken);
            await SendQuestSnapshotAsync("player-kill-progress", cancellationToken);
            await SendLoginQuestProgressAsync(cancellationToken);
            await SendQuestNpcMarksAsync(cancellationToken);
        }
    }
}
