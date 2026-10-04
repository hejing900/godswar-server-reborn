using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// An NPC whose whole window is owned by one client script: the server opens
    /// the function, and every step afterwards is a list of numbers the script
    /// draws.
    /// </summary>
    /// <param name="FunctionNumber">
    /// The <c>NPC_FLAG_SYS_*</c> value that selects the client script. It is
    /// advertised in the open packet's function list and echoed back untouched in
    /// every reply.
    /// </param>
    /// <param name="OpeningMenu">
    /// The numbers of the first page, answered when the client asks for the
    /// current page's entries (<c>subId == -1</c>).
    /// </param>
    /// <param name="Steps">
    /// What each number the client can send is answered with. Every value holds
    /// numbers of the page the click leads to, because a number only draws on the
    /// page whose branch it sits in. The table covers every button the dialogue
    /// draws, so a number outside it is not a button of this dialogue's and is left
    /// unanswered.
    /// </param>
    /// <param name="PageSelections">
    /// What a button <em>on</em> a page answers with, keyed by the page's own
    /// number and then by the chosen entry. The client keeps the page's number in
    /// <c>+20</c> and puts the entry it clicked in the first argument word, so a
    /// dialogue whose pages carry buttons of their own needs the pair to tell the
    /// two apart: the battlefield awarder answers <c>174</c> with
    /// <c>3001</c>-<c>3003</c> and then answers <c>3001</c> itself, so one flat
    /// table would re-send the page instead of the selection's result. Dialogues
    /// with a single level of buttons leave this empty.
    /// </param>
    internal readonly record struct ScriptedNpcDialogue(
        int FunctionNumber,
        IReadOnlyList<int> OpeningMenu,
        IReadOnlyDictionary<int, int[]> Steps,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int[]>>?
            PageSelections = null);

    /// <summary>
    /// The Mysterious Elder, who hands out Sheepskin Scrolls and pieces the Lost
    /// Book together.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunOldMan.lua</c> (function
    /// <c>NPC_FLAG_SYS_OLDMAN = 23</c>, which the client labels "Story of the Lost
    /// Book"). The intro is the page's only text branch and the four entries sit at
    /// <c>25,165</c>, <c>25,185</c>, <c>25,205</c> and <c>25,225</c>, so the whole
    /// menu fits in one reply. The script's own <c>2</c> entry is commented out in
    /// the client, so it is not sent.
    /// </remarks>
    private static readonly ScriptedNpcDialogue MysteriousElderDialogue = new(
        FunctionNumber: 23,
        OpeningMenu: [1000, 1, 3, 4, 5],
        Steps: new Dictionary<int, int[]>
        {
            // "*Get a Sheepskin Scroll for free." The scroll itself is not
            // granted, so the answer is the script's daily-limit line rather than
            // its success line.
            [1] = [505],

            // The three combining entries - chapters 1+2+3, chapters 4+5+6 and all
            // seven. Each is answered with the script's own "not enough of the
            // item to be exchanged", which is what a bag without the chapters
            // gets.
            [3] = [601],
            [4] = [601],
            [5] = [601]
        });

    /// <summary>
    /// The Profession Mentor, who teaches and unlearns the four professions.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunLifeSkill.lua</c> (function
    /// <c>NPC_FLAG_SYS_LIFESKILL = 64</c>). The client's own configuration names
    /// this NPC "Profession Mentor" in <c>NpcName.dat</c> and 专业导师 in
    /// <c>Settings/Sys/NPC.INI</c>.
    ///
    /// Page 1 is the four entries at <c>25,155</c>-<c>25,215</c>. Page 2 answers
    /// the learn entry with the profession overview and the four professions, the
    /// unlearn entry with its warning and its confirmation, and the other two with
    /// the script's information lines. Page 3 answers each profession with its own
    /// description and a learn button at <c>25,135</c>. The learn buttons are
    /// answered from page 4 with the script's missing-skillbook line, because no
    /// profession skillbook is granted or consumed here.
    /// </remarks>
    private static readonly ScriptedNpcDialogue ProfessionMentorDialogue = new(
        FunctionNumber: 64,
        OpeningMenu: [101, 102, 103, 104],
        Steps: new Dictionary<int, int[]>
        {
            // "*Learn Profession" - the overview text and the four professions.
            [101] = [201, 202, 203, 204, 205],

            // "*Unlearn Profession" - the warning and its confirmation.
            [102] = [206, 207],

            // "*Props Creation" - what each profession makes. The client also
            // raises its crafting window for this entry through its message
            // callback, which this server has no packet for.
            [103] = [208],

            // "*Professions Info" - the overview on its own, which the window's
            // own close button dismisses.
            [104] = [201],

            // The four professions, each with its description and a learn button.
            [202] = [301, 302],
            [203] = [303, 304],
            [204] = [305, 306],
            [205] = [307, 308],

            // The learn buttons. No skillbook is granted, so the script's own
            // missing-skillbook line is the answer.
            [302] = [402],
            [304] = [402],
            [306] = [402],
            [308] = [402],

            // The unlearn confirmation. Nothing is tracked as learned here, so the
            // script's own "you haven't learned anything" line is the answer.
            [207] = [309]
        });

    /// <summary>
    /// The Personal Helper, who enables batch use of consumables.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunBatch.lua</c> (function
    /// <c>NPC_FLAG_SYS_BATCH = 107</c>, which the client labels "BATCH"). The
    /// script's first page is one branch that prints the instruction and shows the
    /// item slot and the quantity field. The batch itself is not run here, so the
    /// page has no second step and every later answer is the opening menu again.
    /// </remarks>
    private static readonly ScriptedNpcDialogue PersonalHelperDialogue = new(
        FunctionNumber: 107,
        OpeningMenu: [100],
        Steps: new Dictionary<int, int[]>());

    /// <summary>
    /// The Event Transporters, who send players to Sicily and to the Trojan
    /// Expedition.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunTranmit.lua</c> (function
    /// <c>NPC_FLAG_SYS_TRANMIT = 1</c>). Both entries are named by the shipped
    /// content rather than inferred: <c>700</c> is <c>NF_L0_700</c> "*Teleport to
    /// Sicily", which quests 1292-1306 send players to, and <c>900</c> is
    /// <c>NF_L0_TR800</c> "*To the Trojan Expedition", which the weekday
    /// announcement sends players to. The transports themselves are not wired, so
    /// each entry is answered with the script's own line about the event: the
    /// missing-spell refusal for Sicily and the opening hours for Troy.
    ///
    /// The opening page is the capture's, not the script's order: the September
    /// 28 2026 session (c202c633, npc 5069 = Sparta_072 at local 02:01:43)
    /// advertised <c>[200, 700, 501, 600, 202]</c> - 诅咒之地二, 西西里岛,
    /// 多德卡尼斯群岛, 爱情岛猎苑区 and 商旅宝库 - so the two numbers this
    /// table used to open with are now two of five, and <c>900</c> is no longer
    /// advertised (it stays answerable, because the script still owns it).
    /// Clicking <c>200</c> answered <c>2001</c> "等级不符合要求不能传送" and
    /// clicking either <c>600</c> or <c>202</c> answered <c>1010</c>, the
    /// 商旅宝库 opening hours - both captured, both page two.
    /// </remarks>
    private static readonly ScriptedNpcDialogue EventTransporterDialogue = new(
        FunctionNumber: 1,
        OpeningMenu: [200, 700, 501, 600, 202],
        Steps: new Dictionary<int, int[]>
        {
            // Captured: the level refusal and the treasure-vault hours.
            [200] = [2001],
            [600] = [1010],
            [202] = [1010],

            // Transcribed from the script; not clicked in the capture.
            [700] = [2702],
            [701] = [2702],
            [900] = [2801]
        });

    /// <summary>
    /// The guild quest supervisor of both capitals.
    /// </summary>
    /// <remarks>
    /// Transcribed from page 1 of <c>NpcFunGuildQuest.lua</c> (function
    /// <c>NPC_FLAG_GUILDQUEST = 6</c>, which the client labels "Guild Quests").
    /// Page 1 prints <c>NF_L0_97</c> ("Please choose a quest for all guild
    /// members:") for every number and raises its two entries at <c>25,135</c> and
    /// <c>25,160</c>, so one reply carries both. The script's own <c>3</c> and
    /// <c>4</c> are aliases of those two buttons, so they are not sent. Issuing a
    /// guild quest is not run here, so both entries are answered with the script's
    /// own line for a player who may not issue one.
    ///
    /// The September 28 2026 capture (session c202c633, npc 5036 = Sparta_039 at
    /// local 02:06:24) settles the third number: the reference advertised
    /// <c>[1, 2, 5]</c> and answered a click on <c>1</c> with <c>1001</c>, the
    /// same refusal this table already sends. <c>5</c> is 战争物资运输; the
    /// client paints its label and then calls <c>Visible(false)</c> on the same
    /// button, so the reference shipped a hidden entry that draws nothing. It is
    /// advertised here for the same reason - the reply stays byte-faithful - and
    /// it was never clicked, so it owns no answer.
    /// </remarks>
    private static readonly ScriptedNpcDialogue GuildQuestSupervisorDialogue = new(
        FunctionNumber: 6,
        OpeningMenu: [1, 2, 5],
        Steps: new Dictionary<int, int[]>
        {
            [1] = [1001],
            [2] = [1001]
        });

    /// <summary>
    /// The guild altar supervisor of both capitals, who builds and upgrades the
    /// guild's buildings and takes donations.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunAltar.lua</c> (function
    /// <c>NPC_FLAG_SYS_ALTAR = 5</c>, which the client labels "Altar").
    ///
    /// The script reuses the same numbers on different pages - page one's
    /// <c>1</c>/<c>2</c>/<c>4</c> are the building types, page two's <c>1</c> is
    /// the Guild Footstone and page three's <c>1</c> is "Build New Building" - and a
    /// table keyed by the clicked number alone can hold only one meaning for each.
    /// Every reply below therefore offers only numbers that no earlier step of this
    /// dialogue uses, so the click that comes back can only mean one thing:
    ///
    ///   page 1  1/2/4      the three building types, at 25,135/155/175
    ///   page 2  5/6/3      basic buildings: Senior House at 25,135, Luxurious
    ///                      House at 25,155, then Super Guild Warehouse, whose
    ///                      branch sets no position at all and therefore takes the
    ///                      third button slot, which the window's own ladder places
    ///                      at 25,175 - the same spot the opening reply's
    ///                      <c>4</c> uses. The warehouse must come last: sent
    ///                      first it would sit on 25,135 and the Senior House
    ///                      would be drawn on top of it. The script gives 1/2/4
    ///                      away to page one, and 2 (the Common Guild Warehouse)
    ///                      has no position of its own either.
    ///   page 2  10-19      the ten god altars, 25,95-195 and 320,95-155
    ///   page 2  20-30      Advanced Altar 1-10 and the God Altar. The script sets
    ///                      no position for any of them, so they take the window's
    ///                      own slots in order - 25,135 to 25,235 and then 320,135
    ///                      to 320,215 - which is where the ladder in the client's
    ///                      NpcFun.lua puts slots one to eleven anyway.
    ///   page 3  200+N      the chosen building's own description: the script reads
    ///                      <c>SubID &gt;= 400</c> for ranks and keeps
    ///                      <c>201</c>-<c>206</c> for the basic buildings,
    ///                      <c>210</c>-<c>220</c> for the altars and
    ///                      <c>221</c>-<c>230</c> for the advanced ones, which is
    ///                      exactly page two's number plus 200.
    ///
    /// The description branches carry no <c>EndMessage</c>, so the window stays open
    /// on them. The guild's building economy is not run here, so the page-three
    /// action buttons (<c>1</c>/<c>2</c> share a position, <c>4</c>/<c>10</c> share
    /// another) and the page-four donation amounts are not sent: their outcomes are
    /// the script's <c>1000</c>-<c>1017</c> lines, which only draw from page five
    /// on and would need the guild gold, silver and contribution accounts to say
    /// anything true.
    /// </remarks>
    private static ScriptedNpcDialogue BuildGuildAltarDialogue()
    {
        var steps = new Dictionary<int, int[]>
        {
            [1] = [5, 6, 3],
            [2] = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19],
            [4] = [20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30],
            [3] = DescriptionStep(3),
            [5] = DescriptionStep(5),
            [6] = DescriptionStep(6)
        };
        for (var altar = 10; altar <= 30; altar++)
        {
            steps[altar] = DescriptionStep(altar);
        }

        return new ScriptedNpcDialogue(
            FunctionNumber: 5,
            OpeningMenu: [1, 2, 4],
            Steps: steps);
    }

    /// <summary>
    /// One building's page: the description alone, exactly as the spec records it.
    /// </summary>
    /// <remarks>
    /// The page's action buttons cannot be added here. The altar's script reuses
    /// its numbers on every page (page 1's <c>1</c> is the basic buildings, page
    /// 2's <c>1</c> is the guild footstone, page 3's <c>1</c> is "new building"),
    /// and the client does not reset button positions between replies, so acting
    /// on the description page needs the server to track which level the dialogue
    /// is on - which <c>docs/NPC对话链路技术文本.md</c> §2.11 records as beyond
    /// its specification and therefore not done. An attempt without that state
    /// drew the actions on top of each other and left them on stale slots
    /// (measured 2026-09-22).
    /// </remarks>
    /// <summary>
    /// One building's page: the description alone, which is what this table can
    /// say. The actions depend on whether the guild already has the building, so
    /// the page is answered from the guild's state instead
    /// (<c>TryHandleGuildAltarActionAsync</c>).
    /// </summary>
    private static int[] DescriptionStep(int building) => [200 + building];

    /// <summary>
    /// The entries the player has clicked to reach this request, in order.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-22 on the guild altar: the request carries the whole path in
    /// <c>+12</c>, <c>+16</c>, <c>+20</c> and <c>+24</c> - not "the previous entry
    /// plus the one clicked". One session grew exactly that way:
    /// <c>(1,-1,-1,-1)</c> for page one, <c>(1,6,-1,-1)</c> for the building on the
    /// page it opened, <c>(1,6,4,-1)</c> for the action on that building's page, and
    /// <c>(1,6,4,4)</c> for the amount on the page the action opened. The depth is
    /// how many of the four are present, the clicked entry is the last of them and
    /// its page is the one before it. Reading only the first two made every
    /// third-level click look like a second-level one, so acting on a building
    /// redrew the building page - and that page's numbers, taken one level deeper,
    /// are the client's worship prompts.
    /// </remarks>
    private static int[] DialogPath(GamePacket packet)
    {
        var payload = packet.Payload;
        var path = new List<int>(4);
        for (var offset = 12; offset <= 24 && offset + 4 <= payload.Length; offset += 4)
        {
            var value = System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(payload.Slice(offset, 4));
            if (value < 0)
            {
                break;
            }

            path.Add(value);
        }

        return [.. path];
    }

    /// <summary>Where a request carries the number typed into the input box.</summary>
    internal const int DialogAmountOffset = 0x38;

    /// <summary>
    /// The amount the player typed into the dialogue's own input box, or a
    /// non-positive value when the request carries none.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-23 on the guild altar: the click path stops at the client's
    /// confirm entry, and the number typed into the box the page draws
    /// (<c>NpcFun.xml</c>'s <c>BijouEditBox</c>, <c>MoneyEditBox</c> and
    /// <c>NumEditBox</c>) travels on its own word at <c>+0x38</c>. Across every
    /// captured submission that word was the request's only populated word past the
    /// path - 10, 123, 5000, 12345, 100000 and 123123 were each read there - while
    /// every request that carried no typed amount read -1 in it, which is what lets
    /// a non-positive value mean "nothing was typed".
    /// </remarks>
    internal static int DialogAmount(GamePacket packet)
    {
        var payload = packet.Payload;
        return payload.Length >= DialogAmountOffset + 4
            ? System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(payload.Slice(DialogAmountOffset, 4))
            : 0;
    }

    private static readonly ScriptedNpcDialogue GuildAltarDialogue =
        BuildGuildAltarDialogue();

    /// <summary>
    /// The guild member advisor of both capitals, who hands out the guild's
    /// double-experience and double-talent-point periods.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>NpcFunUionTime.lua</c> (function
    /// <c>NPC_FLAG_SYS_UNIONTIME = 8</c>, which the client's own table comments
    /// "guild welfare"). The NPC's description - "entitled to all the benefits of
    /// membership ... weekly prayer to double your EXP and talent points" - is
    /// answered only by this script's text (<c>NF_L0_UNION1</c> "*Double EXP
    /// Period", <c>NF_L0_UNION2</c> "*Double Talent Point Period",
    /// <c>NF_L0_UNION11</c>-<c>UNION30</c> the hourly claims).
    ///
    /// Page one's five buttons sit at 25,135/155/175/195/215, so one reply carries
    /// all of them. The claim entries open page two's hourly lists, and the inquiry
    /// is answered with the script's own level line, which it computes as
    /// <c>31 + (level + 1) * 500</c> - <c>531</c> reads the guild's double
    /// experience level as zero, which is what a server without guild levels can
    /// honestly report. Every claim ends on the script's own "insufficient time
    /// left to claim" line, and both period entries end on its "insufficient funds
    /// to upgrade the guild" line, because the guild gold, silver and level
    /// accounts those two would spend are not run here.
    /// </remarks>
    private static ScriptedNpcDialogue BuildGuildMemberAdvisorDialogue()
    {
        var steps = new Dictionary<int, int[]>
        {
            [1] = [300],
            [2] = [300],
            [3] = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
            [4] = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30],
            [5] = [531]
        };
        for (var hour = 11; hour <= 30; hour++)
        {
            steps[hour] = [352];
        }

        return new ScriptedNpcDialogue(
            FunctionNumber: 8,
            OpeningMenu: [1, 2, 3, 4, 5],
            Steps: steps);
    }

    private static readonly ScriptedNpcDialogue GuildMemberAdvisorDialogue =
        BuildGuildMemberAdvisorDialogue();

    /// <summary>
    /// Opens a scripted NPC's function menu by advertising every function it owns.
    /// </summary>
    private async Task SendScriptedNpcDialogueMenuAsync(
        NpcSpawnDefinition npc,
        Dictionary<int, ScriptedNpcDialogue> dialogues,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                [.. dialogues.Keys],
                npc.NpcKey),
            cancellationToken,
            "ScriptedNpcDialogueMenu");
        Console.WriteLine(
            $"[npc] scripted dialogue open npc={npc.InteractionId} " +
            $"key={npc.NpcKey} functions=[{string.Join(',', dialogues.Keys)}]");
    }

    /// <summary>
    /// Answers one step of a scripted NPC's dialogue by the number the client
    /// sent, and logs every word of the request so a selector that lands in an
    /// unexpected word can be seen in the server log.
    /// </summary>
    /// <remarks>
    /// The client names the function it is replying for through the dialog index, so an
    /// NPC advertising several functions is routed to the single entry owning that index.
    /// An index no entry owns is left unanswered rather than guessed at.
    /// </remarks>
    private async Task HandleScriptedNpcDialogueAsync(
        NpcSpawnDefinition npc,
        Dictionary<int, ScriptedNpcDialogue> dialogues,
        GamePacket packet,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        var payload = packet.Payload;
        var words = new List<string>();
        // Every word of the request: the typed amounts the dialogue's input boxes
        // carry are further in than the click path, so a bounded dump would hide
        // exactly the field being looked for.
        for (var index = 0; index * 4 + 4 <= payload.Length; index++)
        {
            words.Add(
                $"p{index * 4}=" +
                System.Buffers.Binary.BinaryPrimitives
                    .ReadInt32LittleEndian(payload.Slice(index * 4, 4)));
        }

        Console.WriteLine(
            $"[npc] scripted dialogue words npc={npc.InteractionId} " +
            $"key={npc.NpcKey} generic={subId} dialog={dialogIndex} " +
            $"len={packet.Length} {string.Join(' ', words)}");
        if (!dialogues.TryGetValue(dialogIndex, out var dialogue))
        {
            Console.WriteLine(
                $"[npc] scripted dialogue unknown function npc={npc.InteractionId} " +
                $"key={npc.NpcKey} dialog={dialogIndex}");
            return;
        }

        var selection = payload.Length >= 20
            ? System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(payload.Slice(16, 4))
            : subId;
        if (selection < 0)
        {
            selection = subId;
        }

        // The entry clicked on the page the last reply opened, if any. Read here
        // rather than beside its use because the payload is a span and cannot
        // survive the awaits below.
        var pageSelection = payload.Length >= 24
            ? System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(payload.Slice(20, 4))
            : -1;

        if (selection < 0)
        {
            // The client is asking for the current page's entries.
            await SendScriptedNpcDialogueReplyAsync(
                npc,
                dialogue,
                dialogIndex,
                selection,
                dialogue.OpeningMenu,
                cancellationToken);
            return;
        }

        if (await TryHandleGuildAltarActionAsync(
                npc,
                dialogIndex,
                DialogPath(packet),
                DialogAmount(packet),
                selection,
                cancellationToken))
        {
            return;
        }

        // A selection made on the page the last reply opened. The client keeps
        // that page's number in +20 - which is the `selection` above - and puts
        // the entry it clicked in the first argument word, so the pair is what
        // names the click. Checking it first is what keeps a number that is both
        // a page's answer and another page's button from being answered as the
        // wrong one.
        if (pageSelection >= 0 &&
            dialogue.PageSelections is { } pageSelections &&
            pageSelections.TryGetValue(selection, out var pageEntries) &&
            pageEntries.TryGetValue(pageSelection, out var pageReply))
        {
            await SendScriptedNpcDialogueReplyAsync(
                npc,
                dialogue,
                dialogIndex,
                pageSelection,
                pageReply,
                cancellationToken);
            return;
        }

        if (!dialogue.Steps.TryGetValue(selection, out var opened))
        {
            // Every button this dialogue draws is registered, so a number outside
            // the table cannot come from a button of its own. Nothing is sent: the
            // window keeps whatever it is showing.
            Console.WriteLine(
                $"[npc] scripted dialogue unregistered npc={npc.InteractionId} " +
                $"key={npc.NpcKey} selection={selection}");
            return;
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc,
            dialogue,
            dialogIndex,
            selection,
            opened,
            cancellationToken);
    }

    private async Task SendScriptedNpcDialogueReplyAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        IReadOnlyList<int> reply,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                dialogIndex,
                [.. reply]),
            cancellationToken,
            "ScriptedNpcDialogueReply");
        Console.WriteLine(
            $"[npc] scripted dialogue reply npc={npc.InteractionId} " +
            $"key={npc.NpcKey} function={dialogue.FunctionNumber} " +
            $"selection={selection} sent=[{string.Join(',', reply)}]");
    }
}
