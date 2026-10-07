using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class QuestProtocolChecks
{
    private static readonly uint[] ConfirmedMainIds = Enumerable.Range(518, 48)
        .Where(id => id is not (535 or 536 or 557 or 558))
        .Select(id => (uint)id).ToArray();

    private static readonly uint[] ConfirmedDailyIds =
        [.. Enumerable.Range(69, 32).Concat(Enumerable.Range(342, 12))
            .Where(id => id is not (345 or 349))
            .Concat(Enumerable.Range(581, 46))
            .Select(id => (uint)id)];

    private static void CheckQuestGatingRules()
    {
        foreach (var (camp, offset) in new[]
        {
            (GameDefaults.SpartaCamp, 0u), (GameDefaults.AthensCamp, 1000u)
        })
        {
            var main = StarterQuestChain.Steps
                .Where(step => step.Camp == GameClientHandler.ChainCampFor(camp) && step.Segment == "1-38")
                .Select(step => step.QuestId).ToArray();
            Check.True(main.SequenceEqual(ConfirmedMainIds.Select(id => id + offset)),
                "each camp has exactly the confirmed 44 main-line quests");

            var character = new GameCharacter { Camp = camp, Level = 140 };
            var serial = main.Where(id => !StarterQuestChain.Find(id)!.Value.IsOptionalMainLine).ToArray();
            foreach (var expected in serial)
            {
                var regularOpen = GameClientHandler.AcceptableQuestIds(character)
                    .Where(id => StarterQuestChain.Find(id) is { Segment: "1-38", IsOptionalMainLine: false })
                    .ToArray();
                Check.True(regularOpen.SequenceEqual([expected]),
                    $"serial frontier advances to {expected} without doing optional branches");
                character.Quests.Add(new CharacterQuest { QuestId = expected });
                Check.True(!GameClientHandler.AcceptableQuestIds(character).Contains(expected),
                    "a carried main-line quest is not offered twice");
                var next = Array.IndexOf(serial, expected) + 1;
                if (next < serial.Length)
                {
                    Check.True(!GameClientHandler.AcceptableQuestIds(character).Contains(serial[next]),
                        "accepting a predecessor does not unlock its successor");
                }

                character.Quests.Clear();
                character.QuestCompletedIds = [.. character.QuestCompletedIds, expected];
            }

            Check.True(!GameClientHandler.AcceptableQuestIds(character)
                .Any(id => StarterQuestChain.Find(id) is { Segment: "1-38", IsOptionalMainLine: false }),
                "the serial chain ends after all 39 required steps");
            Check.Equal(5, GameClientHandler.AcceptableQuestIds(character)
                .Count(id => StarterQuestChain.Find(id) is { IsOptionalMainLine: true }),
                "the five skipped branches remain independently available");

            var before30 = ConfirmedMainIds.Where(id => id < 551).Select(id => id + offset).ToArray();
            var branchIds = Enumerable.Range(552, 5).Select(id => (uint)id + offset).ToArray();
            var branchCharacter = new GameCharacter
            {
                Camp = camp, Level = 32, QuestCompletedIds = before30
            };
            Check.True(branchIds.All(id => !GameClientHandler.AcceptableQuestIds(branchCharacter).Contains(id)),
                "all branches wait for completion of level-30 quest");
            Check.True(!GameClientHandler.AcceptableQuestIds(branchCharacter).Contains(559u + offset),
                "the level-32 quest also waits for level-30 completion");
            branchCharacter.QuestCompletedIds = [.. before30, 551u + offset];
            branchCharacter.Level = 30;
            Check.True(branchIds.All(id => !GameClientHandler.AcceptableQuestIds(branchCharacter).Contains(id)),
                "level 30 does not meet the five branches' minimum level");
            branchCharacter.Level = 31;
            Check.True(branchIds.All(id => GameClientHandler.AcceptableQuestIds(branchCharacter).Contains(id)),
                "all five level-31 branches open together");
            Check.True(!GameClientHandler.AcceptableQuestIds(branchCharacter).Contains(559u + offset),
                "level-32 main line still requires character level 32");
            branchCharacter.Level = 32;
            for (var mask = 0; mask < 32; mask++)
            {
                branchCharacter.QuestCompletedIds = [.. before30, 551u + offset,
                    .. branchIds.Where((_, index) => (mask & (1 << index)) != 0)];
                var open = GameClientHandler.AcceptableQuestIds(branchCharacter);
                Check.True(open.Contains(559u + offset),
                    $"level-32 main line ignores any subset of the five branches (mask {mask})");
                for (var index = 0; index < 5; index++)
                {
                    Check.Equal((mask & (1 << index)) == 0, open.Contains(branchIds[index]),
                        "each optional branch is one-time and independent");
                }
            }

            var first = StarterQuestChain.Find(518u + offset)!.Value;
            Check.True(!GameClientHandler.CanAcceptQuest(
                new GameCharacter { Camp = camp, Level = 0 }, first, QuestDailyState.Today()),
                "main line checks the minimum level");
            Check.True(!GameClientHandler.CanAcceptQuest(
                new GameCharacter { Camp = camp == GameDefaults.SpartaCamp
                    ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp, Level = 140 },
                first, QuestDailyState.Today()), "cross-camp acceptance is refused");
        }
        CheckIndependentMainLines();
    }

    private static void CheckQuestDailyQuotaRules()
    {
        var today = QuestDailyState.Today();
        var tomorrow = today.AddDays(1);
        foreach (var offset in new[] { 0u, 1000u })
        {
            foreach (var baseId in ConfirmedDailyIds)
            {
                var step = StarterQuestChain.Find(baseId + offset)!.Value;
                Check.True(step.IsDaily && step.MaxCompletionsPerDay == 1,
                    $"confirmed daily {step.QuestId} is independent and once per day");
                var character = new GameCharacter
                {
                    Camp = offset == 0 ? GameDefaults.SpartaCamp : GameDefaults.AthensCamp,
                    Level = step.RequiresExactLevel ? step.MinLevel : step.MinLevel - 5
                };
                Check.True(GameClientHandler.CanAcceptQuest(character, step, today),
                    "daily quest opens five levels ahead, without story progress");
                character.Level--;
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today),
                    "six levels ahead is outside the acceptance window");
                character.Level = step.RequiresExactLevel ? step.MinLevel + 1 : step.MinLevel + 9;
                Check.Equal(!step.RequiresExactLevel, GameClientHandler.CanAcceptQuest(character, step, today),
                    "nine levels above the quest is still eligible");
                character.Level++;
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today),
                    "ten levels above the quest is outside the acceptance window");
                character.Level = step.MinLevel;
                character.QuestCompletedIds = [step.QuestId];
                character.RecordQuestCompletion(step.QuestId, today);
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today),
                    "a daily completion blocks a second take on the same day");
                Check.True(GameClientHandler.CanAcceptQuest(character, step, tomorrow),
                    "completion history does not block a daily quest the next day");
                character.Quests.Add(new CharacterQuest { QuestId = step.QuestId });
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, tomorrow),
                    "a quest held across rollover is not offered again");
            }
        }

        foreach (var step in StarterQuestChain.Steps)
        {
            if (step.IsOneTimeUtility || (step.IsMainLine && !step.IsConfiguredDaily))
            {
                Check.True(!step.IsDaily && step.MaxCompletionsPerDay == 0,
                    "main-line classification takes priority over source color");
            }
            else if (step.Color == 2)
            {
                Check.True(step.IsDaily && step.MaxCompletionsPerDay == 1,
                    "every non-main Color=2 row is daily");
            }
        }

        foreach (var id in new uint[] { 535, 536, 557, 558, 328, 329, 110, 118 })
        {
            foreach (var offset in new[] { 0u, 1000u })
            {
                var step = StarterQuestChain.Find(id + offset)!.Value;
                Check.True(!step.IsMainLine, "previously excluded side quests are restored as side content");
                var character = new GameCharacter
                {
                    Camp = offset == 0 ? GameDefaults.SpartaCamp : GameDefaults.AthensCamp,
                    Level = step.IsOneTimeUtility ? step.MinLevel : step.MinLevel - 5
                };
                Check.True(GameClientHandler.CanAcceptQuest(character, step, today),
                    "side quests share the five-level lead boundary");
                character.Level = step.MinLevel + 10;
                Check.Equal(step.IsOneTimeUtility, GameClientHandler.CanAcceptQuest(character, step, today),
                    "side quests share the nine-level outgrowth boundary");
                character.Level = step.MinLevel;
                character.QuestCompletedIds = [step.QuestId];
                Check.Equal(step.MaxCompletionsPerDay > 0,
                    GameClientHandler.CanAcceptQuest(character, step, tomorrow),
                    "one-time side quests stay completed; capped quests can return");
            }
        }

        // Keep the existing quota-day policy; classification does not change the noon rollover.
        Check.Equal(new DateOnly(2026, 10, 6), QuestDailyState.DayOf(
            new DateTimeOffset(2026, 10, 7, 11, 59, 0, TimeSpan.Zero)), "before noon uses yesterday");
        Check.Equal(new DateOnly(2026, 10, 7), QuestDailyState.DayOf(
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)), "noon begins the next quota day");
        CheckQuestDailyPersistenceRules();
    }

    private static void CheckIndependentMainLines()
    {
        var today = QuestDailyState.Today();
        foreach (var (camp, offset) in new[]
        {
            (GameDefaults.SpartaCamp, 0u), (GameDefaults.AthensCamp, 1000u)
        })
        {
            var thermopylaeCharacter = new GameCharacter
            {
                Camp = camp, Level = 124,
                QuestCompletedIds = Enumerable.Range(371, 6).Select(id => (uint)id + offset).ToArray()
            };
            var secondChapter = StarterQuestChain.Find(377u + offset)!.Value;
            Check.Equal(376u + offset, secondChapter.PrerequisiteQuestId,
                "Thermopylae second chapter still requires first chapter completion");
            Check.True(!GameClientHandler.CanAcceptQuest(thermopylaeCharacter, secondChapter, today),
                "finishing quest 376 below level 125 does not publish quest 377");
            thermopylaeCharacter.Level = 125;
            Check.True(GameClientHandler.CanAcceptQuest(thermopylaeCharacter, secondChapter, today),
                "quest 377 opens at level 125 after completing quest 376");
            thermopylaeCharacter.QuestCompletedIds = [];
            Check.True(!GameClientHandler.CanAcceptQuest(thermopylaeCharacter, secondChapter, today),
                "level 125 alone does not bypass the first chapter");

            foreach (var (start, count) in new[] { (573, 8), (371, 12) })
            {
                var chain = Enumerable.Range(start, count).Select(id => (uint)id + offset).ToArray();
                var character = new GameCharacter { Camp = camp, Level = 140, QuestCompletedIds = [518u + offset] };
                var segment = StarterQuestChain.Find(chain[0])!.Value.Segment;
                Check.True(GameClientHandler.AcceptableQuestIds(character).Contains(chain[0]),
                    "a new main-line route opens without finishing the starter route");
                var first = StarterQuestChain.Find(chain[0])!.Value;
                character.Level = first.MinLevel - 1;
                Check.True(!GameClientHandler.CanAcceptQuest(character, first, today), "route start needs minimum level");
                character.Level = first.MinLevel;
                Check.True(GameClientHandler.CanAcceptQuest(character, first, today), "route start opens at minimum level");
                character.Level = 140;
                foreach (var expected in chain)
                {
                    var open = GameClientHandler.AcceptableQuestIds(character);
                    Check.True(open.Where(id => StarterQuestChain.Find(id)!.Value.Segment == segment)
                        .SequenceEqual([expected]), "only the current step is offered within its route");
                    Check.True(open.Contains(519u + offset), "another route's frontier stays available");
                    character.Quests.Add(new CharacterQuest { QuestId = expected });
                    Check.True(!GameClientHandler.AcceptableQuestIds(character)
                        .Any(id => StarterQuestChain.Find(id)!.Value.Segment == segment),
                        "accepting a route step does not unlock the next step");
                    character.Quests.Clear();
                    character.QuestCompletedIds = [.. character.QuestCompletedIds, expected];
                }
                Check.True(!GameClientHandler.AcceptableQuestIds(character)
                    .Any(id => StarterQuestChain.Find(id)!.Value.Segment == segment), "a completed route stays finished");
            }

            foreach (var step in StarterQuestChain.Steps.Where(step =>
                step.Camp == GameClientHandler.ChainCampFor(camp) && step.RequiresExactLevel))
            {
                Check.True(step.IsMainLine && step.IsDaily && step.PrerequisiteQuestId == 0,
                    "daily main-line content has no story prerequisites");
                var character = new GameCharacter { Camp = camp, Level = step.MinLevel };
                Check.True(GameClientHandler.CanAcceptQuest(character, step, today), "exact-level daily main line is independently available");
                character.Level--;
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today), "lower level does not publish daily main line");
                character.Level = step.MinLevel + 1;
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today), "higher level does not publish daily main line");
            }

            foreach (var step in StarterQuestChain.Steps.Where(step =>
                step.Camp == GameClientHandler.ChainCampFor(camp) && step.IsOneTimeUtility))
            {
                Check.True(!step.IsMainLine && !step.IsDaily && !step.IsRepeat && step.MaxCompletionsPerDay == 0,
                    "utility quests stay one-time even when source Color=2 or configured daily");
                var character = new GameCharacter { Camp = camp, Level = step.MinLevel - 1 };
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today), "utility needs its minimum level");
                character.Level++;
                Check.True(GameClientHandler.CanAcceptQuest(character, step, today), "utility opens at minimum level");
                character.Level = 200;
                Check.True(GameClientHandler.CanAcceptQuest(character, step, today), "utility has no level outgrowth limit");
                character.QuestCompletedIds = [step.QuestId];
                Check.True(!GameClientHandler.CanAcceptQuest(character, step, today.AddDays(1)), "utility can only be completed once");
            }
        }
    }
}
