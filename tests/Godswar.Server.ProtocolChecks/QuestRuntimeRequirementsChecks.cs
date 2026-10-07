using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class QuestRuntimeRequirementsChecks
{
    public const string CheckName = "Quest runtime collection exploration capture and wide progress";

    public static Task RunAsync()
    {
        long wide = 0;
        var counts = new[] { 300, 500, 1000, 5000 };
        for (var slot = 0; slot < 4; slot++)
            wide = StarterQuestObjectives.WithCounter(wide, slot, counts[slot]);
        Check.True(wide > int.MaxValue && wide > 0, "wide state fits a positive database bigint");
        for (var slot = 0; slot < 4; slot++)
            Check.Equal(counts[slot], StarterQuestObjectives.Counter(wide, slot), "independent high counters");
        var restored = new CharacterQuest { QuestId = 1649, Progress = wide }.Clone();
        Check.Equal(5000, StarterQuestObjectives.Counter(restored.Progress, 3), "clone preserves high slot");

        var character = new GameCharacter { Level = 140 };
        var collect = new CharacterQuest { QuestId = 526,
            Progress = StarterQuestObjectives.WithCounter(0, 0, 15) };
        Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, collect),
            "kill quota alone does not complete a combined collection quest");
        var target = StarterQuestObjectives.For(526)[0];
        var rolls = 0;
        double Roll(double value) { rolls++; return value; }
        Check.True(!GameClientHandler.RecordQuestCollection(collect, target.MapId + 1,
            StarterQuestObjectives.NameOf(target), target.X, target.Z, () => Roll(0)),
            "wrong map cannot grant a quest item");
        Check.True(!GameClientHandler.RecordQuestCollection(collect, target.MapId,
            "Dumb Wood Man", target.X, target.Z, () => Roll(0)), "wrong species cannot grant a quest item");
        Check.Equal(0, rolls, "unmatched deaths do not draw a drop");
        Check.True(!GameClientHandler.RecordQuestCollection(collect, target.MapId,
            StarterQuestObjectives.NameOf(target), target.X, target.Z, () => Roll(0.15)),
            "exact 15 percent boundary does not drop");
        for (var count = 0; count < 5; count++)
            Check.True(GameClientHandler.RecordQuestCollection(collect, target.MapId,
                StarterQuestObjectives.NameOf(target), target.X, target.Z, () => Roll(0.149999)),
                "matching kills can finish item quota after kill quota is full");
        Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, collect), "both quotas permit hand-in");
        var before = rolls;
        Check.True(!GameClientHandler.RecordQuestCollection(collect, target.MapId,
            StarterQuestObjectives.NameOf(target), target.X, target.Z, () => Roll(0)), "full item quota stays capped");
        Check.Equal(before, rolls, "full quota avoids another random draw");

        // "Clear X and obtain Y" describes the source of a collection. It
        // does not create an independent kill quota without an explicit count.
        foreach (var id in new[] { 147u, 1147u })
        {
            Check.Equal(0, StarterQuestObjectives.For(id).Count,
                "jewel collection has no invented independent kill quota");
            var requirement = StarterQuestCollectObjectives.ByQuestId[id];
            Check.Equal(12, requirement.Required, "Chinese Details explicitly asks for twelve jewels");
            var held = new CharacterQuest { QuestId = id };
            var source = QuestRuntimeRequirements.Collections[id].Monsters.Single();
            var drawCount = 0;
            double JewelRoll() { drawCount++; return 0; }
            Check.True(!GameClientHandler.RecordQuestCollection(held, source.MapId,
                "Unrelated Monster", source.X, source.Z, JewelRoll),
                "another species on the same map cannot grant jewels");
            Check.Equal(0, drawCount, "unrelated kills cannot draw a quest drop");
            for (var itemCount = 0; itemCount < 11; itemCount++)
                Check.True(GameClientHandler.RecordQuestCollection(held, source.MapId,
                    StarterQuestObjectives.NameOf(source), source.X, source.Z, JewelRoll),
                    "a matching killed monster can grant one jewel");
            Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, held),
                "eleven jewels cannot complete twelve");
            Check.True(GameClientHandler.RecordQuestCollection(held, source.MapId,
                StarterQuestObjectives.NameOf(source), source.X, source.Z, JewelRoll),
                "the twelfth matching drop completes the collection");
            Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, held),
                "twelve jewels permit hand-in without an invented kill count");
            Check.Equal(0, StarterQuestObjectives.Counter(held.Progress, 0),
                "collection source kills do not create a separate kill counter");
        }
        foreach (var id in new[] { 149u, 1149u })
        {
            var held = new CharacterQuest { QuestId = id };
            var source = QuestRuntimeRequirements.Collections[id].Monsters.Single();
            Check.Equal(1117u, source.MonsterId, "rug sources bind the verified Milier Guardsman entry");
            Check.Equal(0, StarterQuestObjectives.For(id).Count, "rugs do not invent a kill quota");
            Check.True(GameClientHandler.RecordQuestCollection(held, source.MapId,
                StarterQuestObjectives.NameOf(source), source.X, source.Z, () => 0),
                "Milier Guardsman death can grant the rug");
            Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, held),
                "one rug completes the explicitly singular English Details requirement");
        }
        var guild = StarterQuestObjectives.For(1211).Single();
        Check.Equal(1456u, guild.MonsterId, "Monster_New.ini binds the named One-eyed Ogre");
        Check.Equal(35, guild.Required, "missing guild quantity uses the documented level-29 task 1550 peer");
        var guildHeld = new CharacterQuest { QuestId = 1211,
            Progress = StarterQuestObjectives.WithCounter(0, 0, 34) };
        Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, guildHeld),
            "guild kill task still needs the complete kill quota");
        guildHeld.Progress = StarterQuestObjectives.WithCounter(guildHeld.Progress, 0, 35);
        Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, guildHeld),
            "guild kill task can finish without inventing a collection requirement");

        var exploreRow = QuestAdditionalRequirements.ByQuestId.First(row => row.Value.Exploration.Length > 0);
        var explore = new CharacterQuest { QuestId = exploreRow.Key };
        var point = exploreRow.Value.Exploration[0];
        character.CurrentMap = checked((byte)point.Map);
        character.PositionX = point.X + GameClientHandler.QuestExploreRadius + 0.1f;
        character.PositionZ = point.Z;
        Check.True(!GameClientHandler.RecordQuestExploration(character, explore), "far exploration is incomplete");
        character.PositionX = point.X;
        Check.True(GameClientHandler.RecordQuestExploration(character, explore), "arrival records exploration");
        var progress = explore.Progress;
        GameClientHandler.RecordQuestExploration(character, explore);
        Check.Equal(progress, explore.Progress, "standing at the same point does not count twice");

        foreach (var id in new[] { 538u, 1538u })
        {
            var capture = new CharacterQuest { QuestId = id };
            Check.Equal(2, QuestRuntimeRequirements.Captures[id].Length, "pet tutorial has both capture targets");
            Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, capture), "capture is not a talk quest");
            capture.Progress = StarterQuestObjectives.WithCounter(0, 0, 1);
            Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, capture), "one capture cannot replace the other");
            capture.Progress = StarterQuestObjectives.WithCounter(capture.Progress, 1, 1);
            Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, capture), "both captures permit hand-in");
        }
        Check.True(!QuestRuntimeRequirements.Captures.ContainsKey(423),
            "Chinese quest 423 explicitly requires kills, not English capture wording");
        var bird = StarterQuestObjectives.For(423);
        Check.Equal(1, bird.Count, "quest 423 has one explicit Chinese monster goal");
        Check.Equal(1110u, bird[0].MonsterId, "quest 423 binds the client little-weird-bird entry");
        Check.Equal(30, bird[0].Required, "quest 423 uses the explicit Chinese quantity thirty");
        foreach (var id in new[] { 187u, 1187u })
            Check.Equal(1, QuestRuntimeRequirements.Captures[id][0].Required,
                "capture quantity one is verified in the full Chinese QuestText");
        var high = new CharacterQuest { QuestId = 1649 };
        high.Progress = StarterQuestObjectives.WithCounter(0, 0, 4999);
        Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, high), "4999 cannot complete 5000");
        high.Progress = StarterQuestObjectives.WithCounter(0, 0, 5000);
        Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, high), "5000 can complete 5000");
        CheckReviewedBatch(character);
        return Task.CompletedTask;
    }

    private static void CheckReviewedBatch(GameCharacter character)
    {
        Check.Equal(38, QuestReviewedBatch.ByQuestId.Count, "all thirty-eight reviewed tasks have executable rules");
        foreach (var (id, rule) in QuestReviewedBatch.ByQuestId)
        {
            var held = new CharacterQuest { QuestId = id };
            Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, held), "unworked reviewed quest cannot hand in");
            if (rule.Collection)
            {
                var required = StarterQuestCollectObjectives.ByQuestId[id].Required;
                var source = rule.Targets[0];
                Check.True(!GameClientHandler.RecordQuestCollection(held, rule.Maps[0] + 500u,
                    source.Names[0], 0, 0, () => 0), "wrong map cannot grant a reviewed collection");
                for (var i = 0; i < required; i++)
                    Check.True(GameClientHandler.RecordQuestCollection(held, rule.Maps[0],
                        source.Names[0], 0, 0, () => 0), "each matching successful drop grants exactly one count");
                Check.Equal(required, StarterQuestObjectives.Counter(held.Progress, GameClientHandler.QuestCollectCounterSlot),
                    "collection quota recorded for every reviewed collection");
                if (rule.PreserveKillQuota)
                {
                    Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, held), "original kill quota is preserved");
                    var goals = StarterQuestObjectives.For(id);
                    for (var slot = 0; slot < goals.Count; slot++)
                        held.Progress = StarterQuestObjectives.WithCounter(held.Progress, slot, goals[slot].Required);
                }
            }
            else
            {
                Check.True(!GameClientHandler.RecordReviewedQuestKill(held, rule.Maps[0] + 500u,
                    rule.Targets[0].Names[0], null), "wrong map cannot grant a reviewed kill");
                for (var slot = 0; slot < rule.Targets.Length; slot++)
                {
                    var target = rule.Targets[slot];
                    for (var i = 0; i < target.Required; i++)
                        Check.True(GameClientHandler.RecordReviewedQuestKill(held, rule.Maps[0], target.Names[0], null),
                            "matching actual-death facts can complete each target separately");
                    if (slot + 1 < rule.Targets.Length)
                        Check.True(!GameClientHandler.AreQuestRequirementsSatisfied(character, held), "one species cannot replace the next species");
                }
            }
            Check.True(GameClientHandler.AreQuestRequirementsSatisfied(character, held), "all reviewed requirements permit hand-in");
        }
        foreach (var id in new[] { 607u, 1607u })
        {
            var held = new CharacterQuest { QuestId = id };
            var target = QuestReviewedBatch.ByQuestId[id].Targets.Single();
            Check.Equal(100, target.Required, "map kill quota references the same-level existing 637/1637 quota");
            Check.True(!GameClientHandler.RecordReviewedQuestKill(held, 6, "Undead Dark Mage", null), "Mycenae kills do not credit Thebes");
            Check.True(GameClientHandler.RecordReviewedQuestKill(held, 9, "Undead Dark Mage", null), "first Thebes species counts");
            Check.True(GameClientHandler.RecordReviewedQuestKill(held, 9, "Undead Raider", null), "another Thebes species shares the same counter");
            Check.Equal(2, StarterQuestObjectives.Counter(held.Progress, 0), "various monsters means a cumulative map goal");
        }
    }
}
