using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal static bool MatchesReviewedQuestTarget(QuestReviewedBatch.Rule rule,
        QuestReviewedBatch.Target target, uint map, string? monsterName, string? templateKey)
    {
        if (!rule.Maps.Contains(map)) return false;
        if (target.AnyMonster) return true;
        if (templateKey is not null && target.Templates.Contains(templateKey, StringComparer.OrdinalIgnoreCase))
            return true;
        if (target.Names.Any(name => StarterQuestObjectives.SameName(name, monsterName))) return true;
        // Resolve localized names through the same client model section, without
        // choosing a numeric monster ID from nearby spawns.
        return templateKey is not null &&
            QuestReviewedBatch.ChineseNamesByTemplate.TryGetValue(templateKey, out var chineseNames) &&
            chineseNames.Any(name => target.Names.Any(expected => StarterQuestObjectives.SameName(expected, name)));
    }

    internal static bool RecordReviewedQuestKill(CharacterQuest quest, uint map,
        string? monsterName, string? templateKey)
    {
        if (!QuestReviewedBatch.ByQuestId.TryGetValue(quest.QuestId, out var rule) || rule.Collection)
            return false;
        for (var slot = 0; slot < rule.Targets.Length; slot++)
        {
            var target = rule.Targets[slot];
            var current = StarterQuestObjectives.Counter(quest.Progress, slot);
            if (current >= target.Required || !MatchesReviewedQuestTarget(rule, target, map, monsterName, templateKey))
                continue;
            quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, slot, current + 1);
            return true;
        }
        return false;
    }

    private Task SendReviewedQuestKillNoticeAsync(CharacterQuest quest, CancellationToken cancellationToken)
    {
        if (!QuestReviewedBatch.ByQuestId.TryGetValue(quest.QuestId, out var rule) || rule.Collection)
            return Task.CompletedTask;
        var progress = string.Join("；", rule.Targets.Select((target, slot) =>
            $"{target.Label} {StarterQuestObjectives.Counter(quest.Progress, slot)}/{target.Required}"));
        var title = QuestProgressTexts.Titles.GetValueOrDefault(quest.QuestId, "击杀任务");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return _session.SendAsync(PacketBuilder.PersonalNotice(Encoding.GetEncoding(936).GetBytes($"{title}：{progress}")),
            cancellationToken, "QuestReviewedKillNotice");
    }
}
