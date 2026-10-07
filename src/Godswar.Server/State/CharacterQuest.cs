namespace Godswar.Server.State;

/// <summary>
/// A quest the character has accepted, with the progress it has made.
/// </summary>
/// <remarks>
/// One row per quest, so a character can carry several at once. The objectives
/// themselves are content (<c>StarterQuestObjectives</c>), not state: only how
/// many of them are satisfied is per character.
/// </remarks>
internal sealed class CharacterQuest
{
    public required uint QuestId { get; init; }

    /// <summary>Objective entries satisfied so far.</summary>
    public long Progress { get; set; }

    public CharacterQuest Clone() => new()
    {
        QuestId = QuestId,
        Progress = Progress
    };
}

/// <summary>
/// The states a <c>character_quests</c> row can hold.
/// </summary>
internal static class CharacterQuestStatus
{
    /// <summary>Accepted and being worked on.</summary>
    public const short InProgress = 0;

    /// <summary>Handed in; kept so a finished quest is never offered again.</summary>
    public const short Completed = 1;
}
