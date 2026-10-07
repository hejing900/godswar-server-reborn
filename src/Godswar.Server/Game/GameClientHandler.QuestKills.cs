using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Credits a kill to any carried quest whose objective it satisfies.
    /// </summary>
    /// <remarks>
    /// The objectives come from the client's own quest text, which names both the
    /// target and how many of it the quest wants - "Kill 10 Dumb Wood Men". A kill
    /// is matched on the monster's display name, so only the right kind counts,
    /// and the position the client shows is the fallback for a name that cannot be
    /// resolved.
    /// <para>
    /// One kill credits at most one objective, and each objective keeps its own
    /// counter, so a quest with three targets needs all three and killing one kind
    /// past its count does not stand in for another.
    /// </para>
    /// </remarks>
    private async Task RecordQuestKillAsync(
        MonsterDamageResult damageResult,
        CancellationToken cancellationToken)
    {
        if (_character is null || _character.Quests.Count == 0 || !damageResult.Killed ||
            damageResult.Monster.Definition.MapId < 0) return;
        var monster = damageResult.Monster;
        var map = checked((uint)monster.Definition.MapId);
        var before = _character.Quests.ToDictionary(q => q.QuestId, q => q.Progress);
        var changes = new List<(uint Quest, uint Monster, bool Met)>();
        foreach (var quest in _character.Quests)
        {
            var wasMet = AreQuestRequirementsSatisfied(_character, quest);
            var targetId = 0u;
            var objectives = StarterQuestObjectives.For(quest.QuestId);
            var reviewedKill = QuestReviewedBatch.ByQuestId.TryGetValue(quest.QuestId, out var reviewed) && !reviewed.Collection;
            if (reviewed is { Collection: true, PreserveKillQuota: false }) objectives = [];
            if (reviewedKill)
                RecordReviewedQuestKill(quest, map, monster.Definition.DisplayName, monster.Definition.TemplateKey);
            for (var slot = 0; !reviewedKill && slot < objectives.Count; slot++)
            {
                var objective = objectives[slot];
                var count = StarterQuestObjectives.Counter(quest.Progress, slot);
                if (count >= objective.Required || !StarterQuestObjectives.Matches(objective, map,
                    monster.Definition.DisplayName, monster.HomeX, monster.HomeZ)) continue;
                quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, slot, count + 1);
                targetId = objective.MonsterId;
                break;
            }
            // Drops continue after the kill quota is full, until the item quota
            // is also full. Each actual death gets one independent 15% roll.
            RecordQuestCollection(quest, map, monster.Definition.DisplayName,
                monster.HomeX, monster.HomeZ, Random.Shared.NextDouble, monster.Definition.TemplateKey);
            if (quest.Progress != before[quest.QuestId])
                changes.Add((quest.QuestId, targetId,
                    !wasMet && AreQuestRequirementsSatisfied(_character, quest)));
        }
        if (changes.Count == 0) return;
        try { await SaveQuestStateAsync(cancellationToken); }
        catch
        {
            foreach (var quest in _character.Quests) quest.Progress = before[quest.QuestId];
            throw;
        }
        foreach (var change in changes)
        {
            if (change.Monster != 0)
                await SendQuestObjectiveProgressAsync(change.Quest, change.Monster, cancellationToken);
            else
                await SendReviewedQuestKillNoticeAsync(_character.Quests.First(q => q.QuestId == change.Quest), cancellationToken);
            if (change.Met) await SendQuestObjectivesMetAsync(change.Quest, cancellationToken);
            var quest = _character.Quests.First(q => q.QuestId == change.Quest);
            if (StarterQuestObjectives.Counter(quest.Progress, QuestCollectCounterSlot) !=
                StarterQuestObjectives.Counter(before[quest.QuestId], QuestCollectCounterSlot))
                await SendQuestCollectionNoticeAsync(quest, cancellationToken);
        }
        await SendQuestSnapshotAsync("kill-and-collection-progress", cancellationToken);
        if (changes.Any(change => change.Met)) await SendQuestNpcMarksAsync(cancellationToken);
    }

    /// <summary>
    /// Tells the client that one of a quest's objectives moved on.
    /// </summary>
    /// <remarks>
    /// The reference server sends opcode 10087 for every counted kill - quest, the
    /// npc the quest belongs to, a step of one and the target monster id - and that
    /// is what makes the quest window count up until the quest shows as finished.
    /// Without it the window has no progress to show.
    /// </remarks>
    private async Task SendQuestObjectiveProgressAsync(
        uint questId,
        uint monsterId,
        CancellationToken cancellationToken)
    {
        if (StarterQuestChain.Find(questId) is not { } step)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.QuestObjectiveProgress(
                questId,
                ResolveQuestNpcId(step.GiverKey),
                monsterId),
            cancellationToken,
            "QuestObjectiveProgress",
            framed: false);
    }

    /// <summary>
    /// Tells the client that a quest's objectives are met and it can be handed in.
    /// </summary>
    /// <remarks>
    /// This is the 10084 frame the talk quests get with their accept answer. A kill
    /// quest gets it once the last required kill lands, which is what moves the
    /// quest window from "in progress" to "finished".
    /// </remarks>
    private async Task SendQuestObjectivesMetAsync(
        uint questId,
        CancellationToken cancellationToken)
    {
        if (StarterQuestChain.Find(questId) is not { } step)
        {
            return;
        }

        // A responder that will not resolve is never published: the client looks
        // the npc up and a frame naming zero crashes it. Skipping leaves the quest
        // handable at the responder once the map content resolves again.
        var responderNpcId = ResolveQuestNpcId(step.ResponderKey);
        if (responderNpcId == 0)
        {
            Console.Error.WriteLine(
                $"[quest] skipped objectives-met without a responder " +
                $"character={_character?.Name ?? "(none)"} quest={questId} " +
                $"responder={step.ResponderKey}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.QuestConfirm(
                responderNpcId,
                questId),
            cancellationToken,
            "QuestObjectivesMet",
            framed: false);
    }
}
