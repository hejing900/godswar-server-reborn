using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleNpcFunctionActionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId: null,
                "no_active_character",
                cancellationToken);
            Console.WriteLine("[npc] function action ignored: no active character");
            return;
        }

        if (!TryReadNpcFunctionAction(packet.Payload, out var npcId, out var dialogIndex, out var subId, out var args))
        {
            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId: null,
                "malformed_function_action",
                cancellationToken);
            Console.WriteLine("[npc] function action ignored: payload does not match captured NPC function shape");
            return;
        }

        // A Wonderland run's own actors own their actions too: the teleporter and
        // blackmarket dialogs and the treasure-chest claims. Both handlers refuse
        // anything that is not one of those actors, and only on map 207.
        if (await TryHandleWonderlandTransportActionAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken) ||
            await TryHandleWonderlandChestClaimAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken))
        {
            return;
        }

        if (await TryHandleNonCanonicalSecureGearMentorPacketAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken))
        {
            return;
        }

        // The Wishing Pool has to be answered before the capital mall: the same
        // capital npc pair carries both services, and the mall branch claims the
        // shared dialog indices without answering them.
        if (IsWishingPool(npcId))
        {
            // The newest click, not the first slot: the client appends one slot per
            // entry it has already sent, so a second click sits in slot one (see
            // ResolveNpcFunctionSelection).
            await HandleWishingPoolActionAsync(
                npcId,
                dialogIndex,
                ResolveNpcFunctionSelection(subId, args),
                cancellationToken);
            return;
        }

        if (await TryHandleMallFunctionActionAsync(
                npcId,
                dialogIndex,
                subId,
                cancellationToken))
        {
            return;
        }

        HolyStoneWireIntent? secureHolyStoneIntent = null;
        HolyStoneWireIntent? rawHolyStoneIntent = null;
        if (packet.ClientOperationId.HasValue &&
            _session.IsSecure &&
            HolyStoneProtocol.IsEndpoint(npcId, dialogIndex) &&
            HolyStoneProtocol.TryResolveBoundaryOperation(
                subId,
                out var secureHolyStoneOperation))
        {
            var exactMutation = HolyStoneProtocol.TryReadMutation(
                packet,
                out var exactNpcId,
                out var exactDialogIndex,
                out var exactIntent);
            if (!exactMutation &&
                secureHolyStoneOperation ==
                    HolyStoneCommandOperation.Upgrade &&
                HolyStoneProtocol.IsExactUpgradeBoundary(packet))
            {
                exactNpcId = npcId;
                exactDialogIndex = dialogIndex;
                exactIntent = HolyStoneProtocol.PendingUpgradeIntent();
                exactMutation = true;
            }
            else if (!exactMutation &&
                     secureHolyStoneOperation ==
                        HolyStoneCommandOperation.ImplementSpirit &&
                     HolyStoneProtocol.IsExactImplementSpiritBoundary(packet))
            {
                exactNpcId = npcId;
                exactDialogIndex = dialogIndex;
                exactIntent =
                    HolyStoneProtocol.PendingImplementSpiritIntent();
                exactMutation = true;
            }
            else if (!exactMutation &&
                     secureHolyStoneOperation ==
                        HolyStoneCommandOperation.Combine &&
                     !HolyStoneProtocol.IsExactPageNavigation(packet) &&
                     HolyStoneProtocol.IsExactCombinationBoundary(packet))
            {
                exactNpcId = npcId;
                exactDialogIndex = dialogIndex;
                exactIntent = HolyStoneProtocol.PendingCombinationIntent();
                exactMutation = true;
            }

            if (!exactMutation ||
                exactNpcId != npcId ||
                exactDialogIndex != dialogIndex ||
                exactIntent.Operation != secureHolyStoneOperation)
            {
                await RejectMalformedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    secureHolyStoneOperation,
                    "packet_shape_mismatch",
                    cancellationToken);
                return;
            }

            secureHolyStoneIntent = exactIntent;
        }
        else if (!packet.ClientOperationId.HasValue &&
                 _session.IsSecure &&
                 HolyStoneProtocol.IsEndpoint(npcId, dialogIndex) &&
                 HolyStoneProtocol.IsMutationSubId(subId) &&
                 !HolyStoneProtocol.IsExactPageNavigation(packet))
        {
            await RejectUnidentifiedSecureHolyStoneAsync(
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return;
        }
        else if (!_session.IsSecure &&
                 HolyStoneProtocol.IsEndpoint(npcId, dialogIndex))
        {
            var rawClassification = await ClassifyRawHolyStoneBoundaryAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            if (!rawClassification.Accepted)
            {
                return;
            }

            rawHolyStoneIntent = rawClassification.Intent;
        }

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                if (await TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        cancellationToken))
                {
                    return;
                }

                await RejectUnroutedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    "npc_not_authoritative_for_map",
                    cancellationToken);
                return;
            }

            if (await TryResolveSecureHolySuitOutsideRouteAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    subId,
                    "npc_not_authoritative_for_map",
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                    packet,
                    subId,
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                    packet,
                    npcId,
                    subId,
                    cancellationToken))
            {
                return;
            }

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "npc_not_authoritative_for_map",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId) ??
                    ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex);
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        // The Zeus gift endpoints have no dialogue route: the installed client owns
        // the whole function in NpcFunZeus.lua and only needs the server to answer
        // its menu and selection traffic. Handled before route resolution so it
        // leaves the versioned dialogue baseline untouched.
        if (IsZeusGiftEndpoint(npc))
        {
            await HandleZeusGiftDialogueAsync(
                npc,
                dialogIndex,
                ResolveNpcFunctionSelection(subId, args),
                args,
                cancellationToken);
            return;
        }

        // The scripted NPCs own their whole window in client scripts and carry no
        // dialogue route, so they are answered here as well. The registry lives in
        // ScriptedNpcDialogueCatalog.Routing.cs, which is also what the open packet
        // consults.
        if (ResolveScriptedNpcDialogues(npc) is { } scriptedDialogue)
        {
            await HandleScriptedNpcDialogueAsync(
                npc,
                scriptedDialogue,
                packet,
                dialogIndex,
                subId,
                cancellationToken);
            return;
        }

        var route = await ResolveNpcDialogueRouteAsync(
            npc,
            dialogIndex,
            cancellationToken);
        if (route is null)
        {
            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                if (await TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        cancellationToken))
                {
                    return;
                }

                await RejectUnroutedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    "dialogue_route_mismatch",
                    cancellationToken);
                return;
            }

            if (await TryResolveSecureHolySuitOutsideRouteAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    subId,
                    "dialogue_route_mismatch",
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                    packet,
                    subId,
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                    packet,
                    npcId,
                    subId,
                    cancellationToken))
            {
                return;
            }

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "dialogue_route_mismatch",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId) ??
                    ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex);
            Console.WriteLine(
                $"[npc] function action rejected npc={npcId} " +
                $"dialog={dialogIndex} subId={subId}");
            return;
        }

        if (await TryHandleNpcInitialActionAsync(
                packet, npc, route, npcId, dialogIndex, subId, args,
                cancellationToken))
        {
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.GearMentor)
        {
            if (GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    packet.ClientOperationId,
                    cancellationToken);
                return;
            }

            if (subId == GearEnhancerProtocol.CombineGemPiecesMenuSubId)
            {
                // Stock action 9 aliases operation 201 only on this page.
                if (IsCombineGemPiecesConfirmAlias(
                        subId,
                        _gearMentorOperationPageSubId,
                        packet.ClientOperationId.HasValue))
                {
                    await HandleGearMentorTransactionAsync(
                        npcId,
                        GearEnhancerProtocol.CombineGemPiecesActionSubId,
                        args,
                        packet.ClientOperationId,
                        cancellationToken);
                    return;
                }

                ClearGearEnhancerSelection();
                _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    operation: null,
                    expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
                _gearMentorOperationPageSubId = GearEnhancerProtocol.CombineGemPiecesActionSubId;
                await _session.SendAsync(
                    GearEnhancerProtocol.BuildGemPieceCombinationPageResponse(npcId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] gem-piece combination page character={_character.Name} npc={npcId}");
                return;
            }

            if (GearEnhancerProtocol.IsGearMentorTransactionSubId(subId))
            {
                await HandleGearMentorTransactionAsync(
                    npcId,
                    subId,
                    args,
                    packet.ClientOperationId,
                    cancellationToken);
                return;
            }

            if (GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId))
            {
                ClearGearEnhancerSelection();
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        GearEnhancerProtocol.TemporarilyDisabledResultSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] unsupported original operation npc={npcId} subId={subId} response={GearEnhancerProtocol.TemporarilyDisabledResultSubId}");
            }
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.OriginEnhancer)
        {
            if (GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    packet.ClientOperationId,
                    cancellationToken);
            }
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.HolyStone)
        {
            if (!packet.ClientOperationId.HasValue &&
                rawHolyStoneIntent is null &&
                HolyStoneProtocol.TryGetPageResponseSubIds(
                    subId,
                    args,
                    out var pageSubIds))
            {
                PrepareHolyStoneSelectionContext(
                    npcId,
                    dialogIndex,
                    subId);
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        pageSubIds),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                return;
            }

            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                if (!TryResolveAdvancedDrillSelections(
                        npcId,
                        dialogIndex,
                        args,
                        holyStoneIntent,
                        out holyStoneIntent) ||
                    !TryResolveUpgradeSelections(
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        out holyStoneIntent) ||
                    !TryResolveImplementSpiritSelections(
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        out holyStoneIntent) ||
                    !TryResolveCombinationSelections(
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        out holyStoneIntent))
                {
                    if ((holyStoneIntent.Operation is
                            HolyStoneCommandOperation.Upgrade or
                            HolyStoneCommandOperation.Combine or
                            HolyStoneCommandOperation.ImplementSpirit) &&
                        await TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
                            packet,
                            npcId,
                            dialogIndex,
                            holyStoneIntent,
                            cancellationToken))
                    {
                        return;
                    }

                    await RejectMalformedSecureHolyStoneAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        holyStoneIntent.Operation,
                        "item_selection_context_mismatch",
                        cancellationToken);
                    return;
                }

                await HandleDurableHolyStoneAsync(
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    packet.ClientOperationId!.Value,
                    cancellationToken);
                return;
            }

            if (rawHolyStoneIntent is { } rawIntent)
            {
                if (!TryResolveAdvancedDrillSelections(
                        npcId,
                        dialogIndex,
                        args,
                        rawIntent,
                        out rawIntent) ||
                    !TryResolveRawUpgradeSelections(
                        npcId,
                        dialogIndex,
                        rawIntent,
                        out rawIntent) ||
                    !TryResolveRawImplementSpiritSelections(
                        npcId,
                        dialogIndex,
                        rawIntent,
                        out rawIntent) ||
                    !TryResolveRawCombinationSelections(
                        npcId,
                        dialogIndex,
                        rawIntent,
                        out rawIntent))
                {
                    await _session.SendAsync(
                        PacketBuilder.NpcFunctionActionResponse(
                            npcId,
                            dialogIndex,
                            HolyStoneNativeResults.WrongSelectionSubId),
                        cancellationToken,
                        "NpcFunctionActionResponse");
                    return;
                }

                rawHolyStoneIntent = rawIntent;
            }

            // An invalid UUID-bearing command never reaches the legacy path.
            if (packet.ClientOperationId.HasValue &&
                _session.IsSecure)
            {
                Console.WriteLine(
                    "[holy-stone] preserved unsupported secure packet " +
                    $"npc={npcId} dialog={dialogIndex} subId={subId}");
                return;
            }
        }

        if (route.Behavior == NpcDialogueBehavior.HolySuitDesign)
        {
            await HandleHolySuitDesignAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return;
        }

        if (await TryHandleNpcFeatureDialogueAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                args,
                cancellationToken))
        {
            return;
        }

        if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                packet,
                npcId,
                subId,
                cancellationToken))
        {
            return;
        }

        if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                packet,
                subId,
                cancellationToken))
        {
            return;
        }

        if (await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
            "valuable_command_wrong_npc_behavior",
            cancellationToken,
            ResolveSecureGearMentorCommandFamily(subId) ??
                ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex))
        {
            return;
        }

        if (route.Behavior != NpcDialogueBehavior.HolyStone)
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        await HandleLegacyHolyStoneAsync(
            npcId,
            route.DialogIndex,
            subId,
            args,
            rawHolyStoneIntent,
            cancellationToken);
    }

}
