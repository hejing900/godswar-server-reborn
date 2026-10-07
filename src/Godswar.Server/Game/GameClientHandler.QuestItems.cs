using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private int FindQuestScrollSlot(uint questId)
    {
        if (_character is null) return -1;
        for (var slot = 0; slot < 96; slot++)
        {
            var item = KitBagSlots.GetItem(_character.KitBag, slot);
            if (item.Stack > 0 && QuestRuntimeRequirements.ScrollItems.TryGetValue(item.Id, out var bound) &&
                bound == questId) return slot;
        }
        return -1;
    }

    private static string ConsumeQuestBagProjection(string bag, int slot)
    {
        var item = KitBagSlots.GetItem(bag, slot);
        return item.Stack == 1 ? KitBagSlots.ClearSlot(bag, slot) :
            KitBagSlots.SetSlot(bag, slot, (item with { Stack = checked((short)(item.Stack - 1)) }).ToCompactString());
    }

    private async Task<bool> TryOpenQuestScrollAsync(int slot, CancellationToken cancellationToken)
    {
        if (_character is null) return false;
        var item = KitBagSlots.GetItem(_character.KitBag, slot);
        if (!QuestRuntimeRequirements.ScrollItems.TryGetValue(item.Id, out var questId)) return false;
        if (StarterQuestChain.Find(questId) is not { IsQuestScroll: true } step || item.Stack <= 0 ||
            !CanAcceptQuest(_character, step, QuestDailyState.Today(), fromScroll: true)) return true;
        _offeredQuestId = questId;
        await AcceptQuestAsync(questId, cancellationToken, commit: false);
        return true;
    }

    internal static bool RecordQuestCapture(CharacterQuest quest, MonsterRuntimeSnapshot target)
    {
        if (!QuestRuntimeRequirements.Captures.TryGetValue(quest.QuestId, out var targets)) return false;
        for (var slot = 0; slot < targets.Length; slot++)
        {
            var current = StarterQuestObjectives.Counter(quest.Progress, slot);
            if (current >= targets[slot].Required || !StarterQuestObjectives.Matches(targets[slot],
                checked((uint)target.Definition.MapId), target.Definition.DisplayName, target.HomeX, target.HomeZ))
                continue;
            quest.Progress = StarterQuestObjectives.WithCounter(quest.Progress, slot, current + 1);
            return true;
        }
        return false;
    }

    private async Task<bool> TryHandleQuestCaptureAsync(PetCaptureRequest request, CancellationToken cancellationToken)
    {
        if (_character is not { } character || _account is null || character.CurrentHp <= 0 ||
            !_registry.TryGetMonsterSnapshot(_session, character.CurrentMap, request.TargetObjectId, out var target) ||
            !target.IsAlive || !target.IsSpawned ||
            !_registry.IsMonsterVisibleTo(_session, target.ObjectId, target.SpawnGeneration) ||
            !IsWithinPetCaptureRange(character, target)) return false;
        var quests = character.Quests.Where(quest => RecordQuestCapture(quest.Clone(), target)).ToArray();
        if (quests.Length == 0) return false;
        var net = KitBagSlots.GetItem(character.KitBag, request.KitBagSlot);
        if (net.Id != MysteriousTuckNetItemId || net.Stack <= 0) return true;
        var control = ResolvePlayerSkillCastControl(DateTimeOffset.UtcNow);
        if (control != PlayerSkillCastControl.None)
        {
            await SendBlockedSkillCastNoticeAsync(control, cancellationToken);
            return true;
        }
        var started = await TryBeginPendingSkillCastAsync(
            PetCaptureSkillId, PetCaptureCastTime, "quest_capture",
            async token => await _session.SendAsync(PacketBuilder.MonsterSkillCastVisual(
                LocalPlayerObjectId, target.ObjectId, PetCaptureSkillId,
                character.PositionX, character.PositionZ, target.X, target.Z), token, "QuestCaptureCast"),
            async token =>
            {
                try
                {
                    var currentNet = KitBagSlots.GetItem(character.KitBag, request.KitBagSlot);
                    if (_character != character || currentNet.Id != MysteriousTuckNetItemId || currentNet.Stack <= 0 ||
                        !IsWithinPetCaptureRange(character, target)) return;
                    var before = character.Quests.Where(quest => RecordQuestCapture(quest.Clone(), target)).Select(q => q.Clone()).ToArray();
                    var after = before.Select(q => q.Clone()).ToArray();
                    foreach (var quest in after) RecordQuestCapture(quest, target);
                    if (after.Length == 0 || !_registry.TryCaptureQuestMonster(_session, target,
                        DateTimeOffset.UtcNow, out var captured)) return;
                    await _registry.BroadcastToCurrentWorldInstanceAsync(_session,
                        PacketBuilder.RemoveWorldObjects(captured.ObjectId), token, includeRoutingSession: true,
                        label: "QuestCapturedRemove");
                    if (!await _store.ConsumeQuestBagItemAsync(_account.Id, character.Id, request.KitBagSlot,
                        currentNet.Id, currentNet.Stack, before, after, cancellationToken: token)) return;
                    foreach (var update in after)
                        character.Quests.First(q => q.QuestId == update.QuestId).Progress = update.Progress;
                    character.KitBag = ConsumeQuestBagProjection(character.KitBag, request.KitBagSlot);
                    await SendKitBagRefreshAsync(token);
                    await SendQuestSnapshotAsync("capture-progress", token);
                    await SendLoginQuestProgressAsync(token);
                    await SendQuestNpcMarksAsync(token);
                }
                finally { await SendPetCaptureCastEndAsync(false, CancellationToken.None); }
            }, cancellationToken);
        if (!started) await SendPetCaptureCastEndAsync(false, cancellationToken);
        return true;
    }
}
