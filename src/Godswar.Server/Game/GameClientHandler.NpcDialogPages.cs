using System.Buffers.Binary;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// How many click slots a <c>10069</c> carries from payload offset 12. The
    /// fixed block the client only fills on the request that opens the window
    /// starts right after them.
    /// </summary>
    /// <remarks>
    /// Captured frames are always 92 bytes: payload offset 12 to 36 are the click
    /// slots, and offset 40 onwards is the block the opening request carries
    /// (<c>2026-09-27 18:54:42.334</c> has slots 0–6 at <c>-1</c> and the block
    /// filled from offset 44; <c>2026-10-04 01:02:15.933</c> has slots 0 and 1
    /// filled and the block at <c>-1</c>).
    /// </remarks>
    private const int NpcFunctionClickSlotCount = 7;

    /// <summary>
    /// The page a scripted NPC dialogue is on.
    /// </summary>
    /// <remarks>
    /// The client passes its own page counter to the script as the <c>Index</c>
    /// argument, and that counter is <b>not</b> the position of the function number
    /// in the packed list: it starts at one when the window opens and advances once
    /// for every <c>10070</c> the client receives. The capture settles it twice —
    /// the follower (<c>list=[26]</c>, one entry) answers its second and third
    /// replies with numbers that only exist on pages two and three of
    /// <c>NpcFunZeus.lua</c> and the player then clicks buttons only those pages
    /// draw; and the saint-of-lost-books case at <c>Athens_074</c> answers a
    /// second-page function number with the <c>NpcFunLostBook</c> first page's
    /// <c>101, 1, 2</c>.
    /// <para>
    /// The consequence for the server is that a reply may only carry numbers that
    /// exist on page <c>replies + 1</c>, and that a click the server leaves
    /// unanswered leaves the counter behind for every later step.
    /// </para>
    /// </remarks>
    private readonly NpcDialogPages _npcDialogPages = new();

    /// <summary>
    /// The newest click a <c>10069</c> reports.
    /// </summary>
    /// <remarks>
    /// The client appends one slot per entry it has already sent inside the open
    /// dialogue, so the number the player just clicked is the <b>last</b> slot that
    /// is not <c>-1</c>, not the first. Captured 2026-09-27 18:54:47.238: slots are
    /// <c>[1, 174, 3002]</c> after three clicks, and the reference server answered
    /// the third one. Reading slot zero instead repeats the previous answer and
    /// freezes the window, which is what the thirteen-step capture never shows.
    /// </remarks>
    private static int ResolveNpcFunctionSelection(int subId, int[] args)
    {
        var selection = subId;
        var limit = Math.Min(args.Length, NpcFunctionClickSlotCount - 1);
        for (var index = 0; index < limit; index++)
        {
            if (args[index] >= 0)
            {
                selection = args[index];
            }
        }
        return selection;
    }

    /// <summary>
    /// The newest click of a raw <c>10069</c> payload, for the routes that read the
    /// payload themselves instead of going through
    /// <see cref="TryReadNpcFunctionAction"/>.
    /// </summary>
    private static int ResolveNpcFunctionSelection(ReadOnlySpan<byte> payload)
    {
        var subId = payload.Length >= 16
            ? BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4))
            : -1;
        var count = Math.Max(0, (payload.Length - 16) / 4);
        var args = new int[count];
        for (var index = 0; index < count; index++)
        {
            args[index] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16 + (index * 4), 4));
        }
        return ResolveNpcFunctionSelection(subId, args);
    }

    /// <summary>
    /// One scripted dialogue's page counter. A handler asks for the page it is
    /// about to answer on before it writes the reply and books it afterwards, so
    /// the counter and the numbers sent can never drift apart.
    /// </summary>
    internal sealed class NpcDialogPages
    {
        private uint _npcId;
        private int _replies;

        /// <summary>Starts a fresh dialogue for one NPC, resetting the counter.</summary>
        public void Opened(uint npcId)
        {
            _npcId = npcId;
            _replies = 0;
        }

        /// <summary>The page the next reply will be drawn on.</summary>
        public int NextPage(uint npcId) => _npcId == npcId ? _replies + 1 : 1;

        /// <summary>Books one reply, which is what advances the page.</summary>
        public void Recorded(uint npcId)
        {
            if (_npcId != npcId)
            {
                _npcId = npcId;
                _replies = 1;
                return;
            }
            _replies++;
        }

        /// <summary>How many replies the open dialogue has already had.</summary>
        public int Replies(uint npcId) => _npcId == npcId ? _replies : 0;

        /// <summary>Forgets the dialogue, so the next click starts at page one.</summary>
        public void Closed()
        {
            _npcId = 0;
            _replies = 0;
        }
    }
}
