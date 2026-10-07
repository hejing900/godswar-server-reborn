namespace Godswar.Server.State;

/// <summary>
/// The quest quota day: which day a completion belongs to, and whether a row may
/// be taken again today.
/// </summary>
/// <remarks>
/// The day is the server's own local day, and it rolls over at <b>12:00</b>, not
/// at midnight: a completion at 11:59 belongs to the previous day, and the same
/// counter is back to zero from 12:00 onwards.
/// <para>
/// The count is durable - it lives in <c>character_quest_daily</c> - so a restart
/// does not reset it. Only the day changing does.
/// </para>
/// </remarks>
internal static class QuestDailyState
{
    /// <summary>The hour the quest day rolls over at, server-local.</summary>
    internal const int RolloverHour = 12;

    /// <summary>
    /// The quota day <paramref name="serverLocalNow"/> falls in.
    /// </summary>
    /// <remarks>
    /// Before 12:00 the day is still the previous calendar day, which is what
    /// makes "12:00 is the reset" true without a scheduler: the key simply
    /// changes, and a row keyed to the old day reads back as zero completions.
    /// </remarks>
    internal static DateOnly DayOf(DateTimeOffset serverLocalNow) =>
        DateOnly.FromDateTime(
            serverLocalNow.Hour < RolloverHour
                ? serverLocalNow.Date.AddDays(-1)
                : serverLocalNow.Date);

    /// <summary>The quota day now, by server-local time.</summary>
    internal static DateOnly Today() => DayOf(DateTimeOffset.Now);

    /// <summary>
    /// How many times the row has been completed in the quota day
    /// <paramref name="today"/>.
    /// </summary>
    /// <remarks>
    /// A stamp from another day is not a completion today: the count reads as
    /// zero without anything having to clear it, which is what makes the reset
    /// free and restart-proof.
    /// </remarks>
    internal static int CompletionsToday(
        DateOnly? storedDay,
        int storedCompletions,
        DateOnly today) =>
        storedDay == today && storedCompletions > 0 ? storedCompletions : 0;

    /// <summary>
    /// True when the row may still be taken, given how often it was completed
    /// today and how often it may be.
    /// </summary>
    /// <param name="completedToday">Completions inside the current quota day.</param>
    /// <param name="maximumPerDay">
    /// The row's per-day cap, or zero when it carries none.
    /// </param>
    internal static bool AllowsCompletion(int completedToday, int maximumPerDay) =>
        maximumPerDay <= 0 || completedToday < maximumPerDay;
}
