using Godswar.Server.State;

namespace Godswar.Server.Application.World.Content;

/// <summary>One reward slot the GM tool owns for a quest.</summary>
internal sealed record QuestRewardSlotOverride(
    uint QuestId,
    int SlotIndex,
    uint ItemId,
    ItemGrantAttributes Attributes);

/// <summary>The four payout values the GM tool owns for a quest.</summary>
internal sealed record QuestRewardValueOverride(
    uint QuestId,
    int Experience,
    int TalentPoints,
    int Silver,
    int Gold);

/// <summary>What a hand-in pays: the quest's own values, or the GM's.</summary>
internal readonly record struct QuestRewardPayout(
    int Experience,
    int TalentPoints,
    int Silver,
    int Gold);

/// <summary>
/// The quest reward overrides the GM tool writes, read once at startup.
/// </summary>
/// <remarks>
/// The tables are database-owned like the monster loot ones, and this snapshot is
/// the runtime's only view of them: an edit takes effect on the next server start
/// and never mid-flight.
/// <para>
/// A quest with any slot row here has its whole menu replaced by those rows - the
/// slots left out are free - while a quest with no row keeps the captured menu it
/// shipped with. The values behave the same way, one row per quest.
/// </para>
/// </remarks>
internal sealed class QuestRewardContentSnapshot
{
    /// <summary>Slots one quest's reward menu can hold.</summary>
    public const int MaximumRewardSlots = 8;

    private readonly IReadOnlyDictionary<uint, IReadOnlyList<QuestRewardSlotOverride>>
        _slots;
    private readonly IReadOnlyDictionary<uint, QuestRewardValueOverride> _values;

    public QuestRewardContentSnapshot(
        IReadOnlyList<QuestRewardSlotOverride> slots,
        IReadOnlyList<QuestRewardValueOverride> values)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var slot in slots)
        {
            // Quality and star may legitimately be 0 (普通/0星): that is what the
            // GM tool means when the operator leaves those two boxes alone, and
            // character_items accepts quality 0..20. Only negative values are
            // nonsense here.
            if (slot.QuestId == 0 ||
                slot.SlotIndex is < 0 or >= MaximumRewardSlots ||
                slot.ItemId == 0 ||
                slot.Attributes.Quality < 0 ||
                slot.Attributes.Grade < 0)
            {
                throw new InvalidDataException(
                    $"Quest reward slot {slot.QuestId}/{slot.SlotIndex} is invalid.");
            }
        }

        foreach (var value in values)
        {
            if (value.QuestId == 0 ||
                value.Experience < 0 ||
                value.TalentPoints < 0 ||
                value.Silver < 0 ||
                value.Gold < 0)
            {
                throw new InvalidDataException(
                    $"Quest reward values for {value.QuestId} are invalid.");
            }
        }

        _slots = slots
            .GroupBy(static slot => slot.QuestId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<QuestRewardSlotOverride>)
                    [.. group.OrderBy(static slot => slot.SlotIndex)]);
        _values = values.ToDictionary(static value => value.QuestId);
    }

    /// <summary>No overrides at all, which is what an unloaded runtime sees.</summary>
    public static QuestRewardContentSnapshot Empty { get; } = new([], []);

    /// <summary>
    /// The reward menu a quest's frames should carry, when the GM tool replaced
    /// it.
    /// </summary>
    public bool TryGetSlots(
        uint questId,
        out IReadOnlyList<QuestRewardSlotOverride> slots) =>
        _slots.TryGetValue(questId, out slots!);

    /// <summary>The payout a quest's hand-in should pay, when the GM tool set it.</summary>
    public QuestRewardPayout Resolve(
        uint questId,
        QuestRewardPayout defaultValue) =>
        _values.TryGetValue(questId, out var value)
            ? new QuestRewardPayout(
                value.Experience,
                value.TalentPoints,
                value.Silver,
                value.Gold)
            : defaultValue;
}

internal static class QuestRewardContentCatalog
{
    private static QuestRewardContentSnapshot? _current;

    /// <summary>
    /// The installed overrides, or none when the runtime never installed any.
    /// </summary>
    /// <remarks>
    /// Unlike the monster loot catalog this one does not throw before install:
    /// the packet builders run in tooling and checks that never touch a database,
    /// and a quest with no overrides is the ordinary case anyway.
    /// </remarks>
    public static QuestRewardContentSnapshot Current =>
        Volatile.Read(ref _current) ?? QuestRewardContentSnapshot.Empty;

    public static void Install(QuestRewardContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
