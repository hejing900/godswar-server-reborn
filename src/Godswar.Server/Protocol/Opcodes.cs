namespace Godswar.Server.Protocol;

internal static class Opcodes
{
    public const ushort Login = 1;
    public const ushort ServerList = 3;
    public const ushort SelectServer = 4;
    public const ushort LoginReturnInfo = 6;
    public const ushort SendServer = 7;

    public const ushort LoginGameServer = 10000;
    public const ushort ResponseGameServer = 10001;
    public const ushort RoleInfo = 10002;
    public const ushort CreateRole = 10003;
    public const ushort DeleteRole = 10004;
    public const ushort GameServerReady = 10005;
    public const ushort EnterGame = 10006;
    public const ushort ClientReady = 10007;
    public const ushort GameServerInfo = 10008;
    public const ushort WalkBegin = 10013;
    public const ushort WalkEnd = 10014;
    // The native client overloads this opcode by object identity. A 28-byte
    // packet for another world object is its captured death notification,
    // while a 24-byte packet for the local player loads a new scene.
    public const ushort SceneChange = 10018;
    // The client revive request. The installed client sends
    // CReviveUI::Send_ReviveMsg as an exact 12-byte frame: +4 is the local
    // player object id and +8 is the revive type (0 stone, 1 money, 2 free,
    // per Localization/*/UI/XML/Revive.lua). Captured 2026-09-15 as
    // `0C002C278F04000002000000` immediately before the landing frame; see
    // docs/death-revive-capture-20260915.md. Earlier lineage captures carried
    // the same 12-byte body on 10019, which is also the server EnterMain
    // opcode, so both spellings are accepted.
    public const ushort Revive = 10028;
    public const ushort NativeRevive = 10028;
    public const ushort ReviveLegacy = 10019;
    public const ushort Kitbag = 10022;
    public const ushort Storage = 10023;
    // The installed Origin client maps its warehouse item snapshot handler to
    // MSG_STORAGE (10034). Opcode 10023 is the unrelated world-object marker
    // retained above; captured eight-byte 10023 frames are not warehouse data.
    public const ushort WarehouseSnapshot = 10034;
    public const ushort BasicAttack = 10026;
    public const ushort MonsterDrops = 10029;
    public const ushort Talk = 10035;
    // MSG_PYTHON_NOTE drives the stock client's on-screen announcement text.
    public const ushort PythonNote = 10038;
    public const ushort SkillCast = 10040;
    public const ushort PickupDrops = 10048;
    public const ushort UseOrEquip = 10049;
    public const ushort MoveItem = 10050;
    public const ushort BreakItem = 10051;
    public const ushort StorageItem = 10052;
    public const ushort Sell = 10053;
    // The installed client sells with 10060 rather than the known-but-unused
    // 10053. The request is four bytes: the bag page and the index inside that
    // page, so the authoritative slot is (page * 24) + index. It carries no
    // quantity because the client sells the whole addressed stack.
    public const ushort SellItem = 10060;
    public const ushort BagItemAction = 10056;
    // MSG_STORAGE_ITEM is bidirectional: the client requests a transfer and
    // the server echoes the same canonical 20-byte descriptor after commit.
    public const ushort WarehouseTransfer = 10059;
    // Quest protocol family, captured from the reference server on 2026-09-13.
    // Offsets below count from the start of the frame, so "+4" is the first
    // payload word after the 4-byte length+opcode header. 10082 and 10084 are
    // bidirectional and the two directions do NOT mean the same thing. The quest
    // id sits at +8 in the short requests and at +12 in the 10082/10083 pair,
    // which is worth checking against the capture before touching either:
    //   C2S 10081 (12)  +4 = 0, +8 = the quest the player clicked. This client
    //                   sends 10081 where the older reference capture sent the
    //                   first C2S 10082 below, so the server answers both alike.
    //   C2S 10082 (648) +4 = a client memory pointer, +8 = -1, +12 = the quest
    //   S2C 10082 (648) +4 = giver npc, +8 = responder npc, +12 = the quest,
    //                   +16 = record count, +68 = 72-byte records
    //   C2S 10084 (16)  +4 = 0, +8 = the quest being handed in
    //   S2C 10084 (12)  +4 = responder npc, +8 = the quest
    // 10083 is a per-scene quest query:
    //   C2S 10083 (20)  +4 = scene key (0x1AF720 for the newbie scene), +8 = the
    //                   quest the client is asking about, +12 = -1. The client in
    //                   this checkout does not send it.
    //   S2C 10083 (17)  +4 = the offered quest, +8 = responder npc, +12 = the
    //                   offered quest, +16 = 1. The scene key is NOT echoed.
    public const ushort QuestSelection = 10081;
    public const ushort QuestAction = 10082;
    public const ushort QuestSceneQuery = 10083;
    public const ushort QuestAccepted = 10084;
    // The installed client dispatch table maps 10090 to
    // MSG_PLAYER_ACCEPTQUESTS. Quest snapshots are character-specific and
    // must never be replayed from a captured login session.
    public const ushort PlayerAcceptedQuests = 10090;
    // 10091 is bidirectional-looking but only ever C2S, and one opcode carries two
    // client actions told apart by the payload word at +4:
    //   0x00810EFB  the pair that follows an accept. All 33 captured samples sit
    //               directly behind a C2S 10082.
    //   16, and once 4
    //               the quest window's "查找" (Inquire) button. None of the 35
    //               captured samples follows a 10082.
    // The reference answers both with the same 48-byte S2C 10092 - the character's
    // currently acceptable quests - which is empty right after an accept, and that
    // is why the accept-path captures show it all zero. Do not treat the zero
    // frame as the fixed shape; see docs/quest-lookup-panel-20261006.md.
    public const ushort QuestActionPair = 10091;
    //   S2C 10092 (48) +4 = count, +8 = 20 x u16 quest id. The reference sent the
    //   same 19 ids to four clicks in a row at 2026-10-06 16:45:40, for a level-57
    //   Athens character, and sent 0 once that character held the only quest it
    //   could still take.
    public const ushort QuestActionPairAck = 10092;
    // The quest window's third tab ("经验加成" / Increase EXP gain) carries the
    // 我要鉴定 ("Appraisal") button. The stock client sends a six-byte 10093
    // whose two payload bytes were zero in all four captured clicks, and the
    // reference server answers an eight-byte frame carrying one 32-bit value,
    // also zero in every capture. See
    // docs/quest-experience-appraisal-20260924.md.
    public const ushort QuestAppraisal = 10093;
    // 10076 = the follow-up quest's detail, 10086 = the completed hand-in,
    // 10077 = "quests this npc gives" (each with an available flag) and
    // 10080 = "quests this npc receives". All four are rebuilt from the chain.
    public const ushort QuestNextDetail = 10076;
    public const ushort QuestMarkerList = 10077;
    public const ushort QuestHandInList = 10080;
    public const ushort QuestHandInAck = 10086;
    // 10078 and 10079 are the global quest-mark lists: which npcs the client
    // should draw a quest mark over, so its quest-search panel can list the
    // quests it may still take without the player having to walk to every npc.
    // Both are a fixed 648 bytes with the same shape - +4 the count and then one
    // u32 npc interaction id each - and neither exists in the installed client's
    // own tables, so they are answered from the chain.
    //   S2C 10078  the npcs that have a quest the character may accept now
    //   S2C 10079  the npcs that take back a quest the character can hand in now
    // Captured 2026-10-06 13:42:25 (10078, one id: 5036 = Sparta_039) and
    // 2026-09-28 02:09:33 (the pair, both carrying 5176 at login).
    public const ushort QuestAvailableNpcList = 10078;
    public const ushort QuestHandInNpcList = 10079;
    public const ushort NpcDialogOpen = 10067;
    public const ushort NpcDialogPageRequest = 10068;
    public const ushort NpcFunctionAction = 10069;
    public const ushort NpcFunctionActionResponse = 10070;
    public const ushort NpcShopCatalog = 10071;
    public const ushort NpcShopPurchase = 10073;
    // The cash mall behind the client's function key. The client sends this
    // eight-byte request once when the mall is opened and the reference answers
    // with the fifteen-frame catalog; nothing about the mall travels over the
    // npc dialog opcodes.
    public const ushort MallCatalog = 10178;
    // Buying from that mall: twenty bytes, category, listing index, quantity and
    // item id. Captured live on 2026-09-14 23:29:48, not on the reference.
    public const ushort MallPurchase = 10180;
    // Equipment forging uses the same opcode for the client request and the
    // server result. The selection and cancel packets are separate messages.
    public const ushort ForgeStart = 10109;
    public const ushort ForgeSelection = 10110;
    public const ushort ForgeReplacementSelection = 10111;
    public const ushort ForgeReplacementAction = 10112;
    public const ushort ItemInfoRequest = 10114;
    public const ushort ForgeCancel = 10117;
    public const ushort PartyInvite = 10123;
    public const ushort PartyRequest = 10124;
    public const ushort PlayerNameInspectRequest = 10125;
    public const ushort PartyAccept = 10126;
    public const ushort PartyRemove = 10127;
    public const ushort PartyChangeLeader = 10128;
    public const ushort PartyDissolve = 10129;
    public const ushort PartyLeave = 10130;
    public const ushort PartyTip = 10131;
    public const ushort PartyReject = 10132;
    public const ushort PartyRefresh = 10133;
    public const ushort PartyDestroy = 10134;
    // The consortia window family, taken from the client's own receive
    // dispatcher: its byte table at 0x4ee900 maps each of these opcodes to a
    // case that pushes the matching MSG_CONSORTIA_* name. 10136 is the create
    // request the client sends (GuildRegistrarProtocol.CreateRequest).
    public const ushort ConsortiaCreateResponse = 10137;
    public const ushort ConsortiaBaseInfo = 10138;
    public const ushort ConsortiaUpdateBaseInfo = 10139;
    public const ushort ConsortiaUpdateBasePartInfo = 10140;
    public const ushort ConsortiaUpdatePlacardInfo = 10141;
    public const ushort ConsortiaUpdateBuildingInfo = 10142;
    public const ushort ConsortiaMemberList = 10143;
    public const ushort ConsortiaMemberOne = 10144;
    public const ushort ConsortiaInvite = 10145;
    public const ushort ConsortiaInviteConfirm = 10146;
    public const ushort ConsortiaDismiss = 10147;
    public const ushort ConsortiaResponse = 10148;
    public const ushort ConsortiaExit = 10149;
    public const ushort ConsortiaText = 10150;
    public const ushort ConsortiaDuty = 10151;
    public const ushort ConsortiaMemberDel = 10152;
    public const ushort ConsortiaNote = 10154;
    // The client's own "Refuse the application?" checkbox (Consortia.xml,
    // RejectRequest) sends its state here as a lone dword: measured 2026-10-02,
    // the guild window builds `length 8 / opcode 10155 / body = checked ? 1 : 0`
    // (Origin.exe 0x5409e0). The client's receive table has no case for it, so it
    // is a C2S-only opcode.
    public const ushort ConsortiaRefuseApplications = 10155;
    public const ushort ConsortiaElementList = 10157;
    public const ushort ConsortiaAltarInfo = 10162;
    // The guild window's own refresh request. The client sends it with an empty
    // four-byte body every time the window is opened; its name lives in the
    // client's receive table as an unhandled slot, so the number is what the
    // observed traffic identifies.
    public const ushort ConsortiaInfoRequest = 10179;
    public const ushort ServerNote = 10169;
    public const ushort DesignationInfo = 10196;
    public const ushort DesignationSelection = 10198;
    // Cast interruption is bidirectional in the native protocol. Both the
    // client report and the authoritative server notification use the same
    // eight-byte frame: length, opcode, and caster ID in the receiver's
    // object namespace (0x1448 for self; authoritative world ID for viewers).
    public const ushort SkillCastInterrupt = 10171;
    public const ushort PlayerInspectRequest = 10191;
    // The native Gear Mentor sends one of these whenever an item is inserted
    // into or removed from its three operation controls. The 12-byte payload
    // carries bag page, page slot, and a one-byte selected flag.
    public const ushort GearEnhancerItemSelection = 10193;
    // Native 10200 is overloaded: it participates in login/map-detail
    // readiness and carries the Fashion Show checkbox in its final DWORD.
    public const ushort PlayerDetailRequest = 10200;
    // Native Fashion Effect sends a 16-byte request. The server publishes a
    // 12-byte per-avatar effect-visibility projection on the same opcode.
    public const ushort FashionEffectVisibility = 10202;
    public const ushort RepetitionNotice = 10216;
    public const ushort RepetitionResponse = 10217;
    public const ushort RepetitionInstanceMembers = 10218;
    public const ushort RepetitionLeave = 10221;
    public const ushort RepetitionQueueState = 10222;
    public const ushort RepetitionInvitation = 10224;
    public const ushort RepetitionCompletionState = 10227;
    public const ushort RepetitionFightInfo = 10229;
    public const ushort RepetitionReward = 10230;
    public const ushort RepetitionReset = 10231;
    public const ushort RepetitionSync = 10232;
    // The active repetition panel sends a six-byte action frame. Its first
    // payload byte is the action (zero is the Terminate button); the stock
    // client leaves the final byte uninitialized, so it is never authoritative.
    public const ushort RepetitionPanelAction = 10313;
    public const ushort PetCaptureRequest = 10252;
    public const ushort MonsterClaimState = 10322;
    public const ushort PlayerInspectVisualRequest = 10279;
    public const ushort PetTakeRequest = 10239;
    // Permanently discards an owned pet. Captured on the reference server at
    // 2026-09-24 23:57:38 as an eight-byte frame carrying only the pet id; the
    // server answered with pet-operation result code 3.
    public const ushort PetDeleteRequest = 10238;
    public const ushort PetCallOutRequest = 10240;
    public const ushort PetRecallRequest = 10241;
    public const ushort PetOperationResult = 10244;
    public const ushort PetCareState = 10245;
    public const ushort PetExperience = 10261;
    public const ushort PetToPetMergeRequest = 10268;
    public const ushort PetToPetMergeResult = 10269;
    public const ushort PetSoulContractRequest = 10270;
    public const ushort PetSoulContractResult = 10271;
    public const ushort PetRebirthRequest = 10272;
    public const ushort PetRebirthResult = 10273;
    // Header-only request emitted by the stock client's innate Merge action.
    // The request carries no pet, item, slot, or stat data; the server resolves
    // every input from the authenticated character's authoritative state.
    public const ushort PetOwnerMergeRequest = 10274;
    // Native pet-unite lifecycle projections recovered independently from
    // the installed client. Both are fixed eight-byte server-to-client frames.
    public const ushort PetOwnerMergeStarted = 10275;
    // Current energy for the locally carried pet. The stock client uses a
    // fixed 0..1800 scale even though durable state is normalized separately.
    public const ushort PetEnergy = 10278;
    public const ushort PetOwnerMergeEnded = 10282;
    public const ushort PackedPetDetailRequest = 10283;
    public const ushort PackedPetDetailResponse = 10284;
    public const ushort PetLevelUpgradeRequest = 10285;
    public const ushort PetLevelUpgrade = 10286;
    public const ushort Zodiac = 10297;
    public const ushort Walk = 10194;
    public const ushort ServerTimeRequest = 10311;
    public const ushort UiHeartbeat = 10312;
    // Mounted reuse of the Riding skill takes this native player-state path
    // instead of sending a second ordinary SkillCast request. Action 6 is the
    // Ride cancellation observed in the installed client.
    public const ushort PlayerStateAction = 10320;
    public const ushort PlayerInspectFollowup = 10342;
    public const ushort EnterUiReady = 10357;
    // Captured S2C silver grant: a 16-byte frame whose leading dword is the
    // grant kind (25 for a money bag), then the player object id and the
    // amount. Captured 2026-09-15 as `10007428190000002502000010270000`
    // (id 145492: kind 25, player 549, 10000 silver) immediately after the
    // 10040 cast of item skill 4600. The second observed kind, 49, belongs to
    // the talent stone (skill 4630) and is not implemented.
    public const ushort BagSilverGrant = 10356;
    public const ushort Ping = 10015;

    public static string Name(ushort opcode)
    {
        return opcode switch
        {
            Login => nameof(Login),
            ServerList => nameof(ServerList),
            SelectServer => nameof(SelectServer),
            LoginReturnInfo => nameof(LoginReturnInfo),
            SendServer => nameof(SendServer),
            LoginGameServer => nameof(LoginGameServer),
            ResponseGameServer => nameof(ResponseGameServer),
            RoleInfo => nameof(RoleInfo),
            CreateRole => nameof(CreateRole),
            DeleteRole => nameof(DeleteRole),
            GameServerReady => nameof(GameServerReady),
            EnterGame => nameof(EnterGame),
            ClientReady => nameof(ClientReady),
            GameServerInfo => nameof(GameServerInfo),
            WalkBegin => nameof(WalkBegin),
            WalkEnd => nameof(WalkEnd),
            SceneChange => nameof(SceneChange),
            Revive => nameof(Revive),
            ReviveLegacy => nameof(ReviveLegacy),
            Kitbag => nameof(Kitbag),
            Storage => nameof(Storage),
            WarehouseSnapshot => nameof(WarehouseSnapshot),
            BasicAttack => nameof(BasicAttack),
            MonsterDrops => nameof(MonsterDrops),
            Ping => nameof(Ping),
            Talk => nameof(Talk),
            PythonNote => nameof(PythonNote),
            SkillCast => nameof(SkillCast),
            PickupDrops => nameof(PickupDrops),
            UseOrEquip => nameof(UseOrEquip),
            MoveItem => nameof(MoveItem),
            BreakItem => "EquipmentItemEquipRequest",
            StorageItem => nameof(StorageItem),
            Sell => nameof(Sell),
            SellItem => nameof(SellItem),
            BagItemAction => nameof(BagItemAction),
            WarehouseTransfer => nameof(WarehouseTransfer),
            PlayerAcceptedQuests => nameof(PlayerAcceptedQuests),
            QuestAction => nameof(QuestAction),
            QuestSelection => nameof(QuestSelection),
            QuestSceneQuery => nameof(QuestSceneQuery),
            QuestAccepted => nameof(QuestAccepted),
            QuestActionPair => nameof(QuestActionPair),
            QuestActionPairAck => nameof(QuestActionPairAck),
            QuestAppraisal => nameof(QuestAppraisal),
            QuestNextDetail => nameof(QuestNextDetail),
            QuestMarkerList => nameof(QuestMarkerList),
            QuestHandInList => nameof(QuestHandInList),
            QuestHandInAck => nameof(QuestHandInAck),
            NpcDialogOpen => nameof(NpcDialogOpen),
            NpcDialogPageRequest => nameof(NpcDialogPageRequest),
            NpcFunctionAction => nameof(NpcFunctionAction),
            NpcFunctionActionResponse => nameof(NpcFunctionActionResponse),
            NpcShopCatalog => nameof(NpcShopCatalog),
            NpcShopPurchase => nameof(NpcShopPurchase),
            MallCatalog => nameof(MallCatalog),
            MallPurchase => nameof(MallPurchase),
            ForgeStart => nameof(ForgeStart),
            ForgeSelection => nameof(ForgeSelection),
            ForgeReplacementSelection => nameof(ForgeReplacementSelection),
            ForgeReplacementAction => nameof(ForgeReplacementAction),
            ItemInfoRequest => nameof(ItemInfoRequest),
            ForgeCancel => nameof(ForgeCancel),
            PartyInvite => nameof(PartyInvite),
            PartyRequest => nameof(PartyRequest),
            PlayerNameInspectRequest => nameof(PlayerNameInspectRequest),
            PartyAccept => nameof(PartyAccept),
            PartyRemove => nameof(PartyRemove),
            PartyChangeLeader => nameof(PartyChangeLeader),
            PartyDissolve => nameof(PartyDissolve),
            PartyLeave => nameof(PartyLeave),
            PartyTip => nameof(PartyTip),
            PartyReject => nameof(PartyReject),
            PartyRefresh => nameof(PartyRefresh),
            PartyDestroy => nameof(PartyDestroy),
            ServerNote => nameof(ServerNote),
            DesignationInfo => nameof(DesignationInfo),
            DesignationSelection => nameof(DesignationSelection),
            SkillCastInterrupt => nameof(SkillCastInterrupt),
            PlayerInspectRequest => nameof(PlayerInspectRequest),
            GearEnhancerItemSelection => nameof(GearEnhancerItemSelection),
            PlayerDetailRequest => nameof(PlayerDetailRequest),
            FashionEffectVisibility => nameof(FashionEffectVisibility),
            RepetitionNotice => nameof(RepetitionNotice),
            RepetitionResponse => nameof(RepetitionResponse),
            RepetitionInstanceMembers => nameof(RepetitionInstanceMembers),
            RepetitionLeave => nameof(RepetitionLeave),
            RepetitionInvitation => nameof(RepetitionInvitation),
            RepetitionCompletionState => nameof(RepetitionCompletionState),
            RepetitionFightInfo => nameof(RepetitionFightInfo),
            RepetitionReward => nameof(RepetitionReward),
            RepetitionReset => nameof(RepetitionReset),
            RepetitionSync => nameof(RepetitionSync),
            RepetitionPanelAction => nameof(RepetitionPanelAction),
            PetCaptureRequest => nameof(PetCaptureRequest),
            MonsterClaimState => nameof(MonsterClaimState),
            PlayerInspectVisualRequest => nameof(PlayerInspectVisualRequest),
            PetTakeRequest => nameof(PetTakeRequest),
            PetDeleteRequest => nameof(PetDeleteRequest),
            PetCallOutRequest => nameof(PetCallOutRequest),
            PetRecallRequest => nameof(PetRecallRequest),
            PetOperationResult => nameof(PetOperationResult),
            PetExperience => nameof(PetExperience),
            PetToPetMergeRequest => nameof(PetToPetMergeRequest),
            PetToPetMergeResult => nameof(PetToPetMergeResult),
            PetSoulContractRequest => nameof(PetSoulContractRequest),
            PetSoulContractResult => nameof(PetSoulContractResult),
            PetRebirthRequest => nameof(PetRebirthRequest),
            PetRebirthResult => nameof(PetRebirthResult),
            PetOwnerMergeRequest => nameof(PetOwnerMergeRequest),
            PetOwnerMergeStarted => nameof(PetOwnerMergeStarted),
            PetEnergy => nameof(PetEnergy),
            PetOwnerMergeEnded => nameof(PetOwnerMergeEnded),
            PackedPetDetailRequest => nameof(PackedPetDetailRequest),
            PackedPetDetailResponse => nameof(PackedPetDetailResponse),
            PetLevelUpgradeRequest => nameof(PetLevelUpgradeRequest),
            PetLevelUpgrade => nameof(PetLevelUpgrade),
            PetCareState => nameof(PetCareState),
            Zodiac => nameof(Zodiac),
            Walk => nameof(Walk),
            ServerTimeRequest => nameof(ServerTimeRequest),
            UiHeartbeat => nameof(UiHeartbeat),
            PlayerStateAction => nameof(PlayerStateAction),
            PlayerInspectFollowup => nameof(PlayerInspectFollowup),
            EnterUiReady => nameof(EnterUiReady),
            BagSilverGrant => nameof(BagSilverGrant),
            10192 => "ClientMovementOrLoad",
            _ => "Unknown"
        };
    }
}
