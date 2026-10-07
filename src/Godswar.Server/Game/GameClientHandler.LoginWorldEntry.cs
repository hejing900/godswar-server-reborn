using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;
using Godswar.Server.Operations;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleGameLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        if (_account is not null)
        {
            _session.Disconnect();
            return;
        }

        var username = await ResolveGameLoginUsernameAsync(
            packet,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(username))
        {
            _session.Disconnect();
            return;
        }
        var boundPrincipal = _session.BoundGamePrincipal;
        if (boundPrincipal is not null)
        {
            if (!string.Equals(
                    username,
                    boundPrincipal.Username,
                    StringComparison.OrdinalIgnoreCase))
            {
                _session.Disconnect();
                return;
            }

            _account = await _accountDirectory.FindAccountByIdAsync(
                boundPrincipal.AccountId,
                cancellationToken);
            if (_account is null ||
                !string.Equals(
                    _account.Username,
                    boundPrincipal.Username,
                    StringComparison.Ordinal))
            {
                _session.Disconnect();
                return;
            }
        }
        else
        {
            if (_session.IsSecure ||
                _legacyAuthenticationAccess is null)
            {
                ServerProfileMetrics
                    .RecordLegacyAuthenticationAttempt(
                        "game",
                        "blocked");
                Console.Error.WriteLine(
                    "[security] rejected legacy authentication " +
                    "endpoint=game reason=profile");
                _session.Disconnect();
                return;
            }

            ServerProfileMetrics
                .RecordLegacyAuthenticationAttempt(
                    "game",
                    "allowed");
            _account = await _accountDirectory.FindAccountByUsernameAsync(
                username,
                cancellationToken);
            if (_account is null)
            {
                _session.Disconnect();
                return;
            }

            _session.MarkAuthenticated();
        }
        var hasGatewayAdmission =
            _session.GatewayWorldAdmission is not null;
        if (hasGatewayAdmission &&
            (!await RefreshCharacterSnapshotAsync(
                 "login",
                 cancellationToken) ||
             !ValidateGatewayAdmission()))
        {
            _session.Disconnect();
            return;
        }

        var replacement =
            _registry.ReplaceAccountSessionAndDetachWorld(
                _account.Id,
                _session);
        var replacedSession = replacement.ReplacedSession;
        _accountSessionRegistered = true;
        if (replacedSession is not null)
        {
            replacedSession.Disconnect();
            await PublishReplacedWorldRemovalAsync(
                replacement.DetachedWorld);
            if (_session.AllowsPayloadDiagnostics)
            {
                Console.WriteLine(
                    $"[game] replacing stale session account={_account.Username}");
            }
            try
            {
                await _registry.FinishProgressionBoostOnlineSessionAsync(
                    replacedSession,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A reconciliation checkpoint bounds any lost duration. A
                // transient persistence failure must never reject the new
                // account session or reproduce the switch-login crash.
                Console.WriteLine(
                    _session.AllowsPayloadDiagnostics
                        ? $"[status] stale-session boost tail deferred account={_account.Username}: {ex.Message}"
                        : "[status] stale-session boost tail deferred");
            }
        }
        if (!hasGatewayAdmission)
        {
            if (!await RefreshCharacterSnapshotAsync(
                    "login",
                    cancellationToken))
            {
                return;
            }

            PrepareUnavailableInstanceReconnectRecovery();
        }
        if (!_registry.IsCurrentAccountSession(
                _account.Id,
                _session))
        {
            _session.Disconnect();
            return;
        }

        if (_session.AllowsPayloadDiagnostics)
        {
            Console.WriteLine($"[game] accepted {_account.Username}");
        }

        await _session.SendAsync(PacketBuilder.AfterLogin(), cancellationToken, "AfterLogin");
        await SendCharacterPreviewAsync(cancellationToken);
    }

    private async Task SendCharacterPreviewAsync(CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        if (_account is null)
        {
            await _session.SendAsync(PacketBuilder.BlankUser(), cancellationToken, "BlankUser");
            return;
        }

        if (!_characterSnapshotLoaded)
        {
            RejectCharacterSnapshot(
                "preview",
                "snapshot_not_loaded");
            return;
        }

        await _session.SendAsync(
            _character is null ? PacketBuilder.BlankUser() : PacketBuilder.CharacterPreview(_character),
            cancellationToken,
            _character is null ? "BlankUser" : "CharacterPreview");
    }

    private async Task HandleCreateRoleAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        QuestFrameTrace.Append(
            "[create] entered account=" + (_account is not null) +
            " phase=" + IsCharacterSelectionLifecyclePhase +
            " secure=" + _session.IsSecure +
            " durable=" + (_characterLifecycleCommands is not null) +
            " operationId=" + (packet.ClientOperationId is not null),
            []);
        if (_account is null)
        {
            QuestFrameTrace.Append("[create] no account, disconnecting", []);
            _session.Disconnect();
            return;
        }

        if (!IsCharacterSelectionLifecyclePhase)
        {
            QuestFrameTrace.Append("[create] outside selection phase", []);
            await RejectOutsideSelectionLifecycleAsync(
                CommandFamily.CharacterCreate,
                packet,
                cancellationToken);
            return;
        }

        var payload = packet.Payload;
        var character = new GameCharacter
        {
            RealmId = _processRealmId,
            Name = PacketText.ReadFixedAscii(payload, 0, 32),
            Gender = ReadByte(payload, 32, 1),
            Camp = ReadByte(payload, 33, 1),
            Profession = ReadByte(payload, 34, 0),
            ZodiacType = ReadZodiacTypeFromCreationPayload(payload),
            Hair = ReadByte(payload, 36, 0),
            Face = ReadByte(payload, 37, 0),
            Faith = ReadByte(payload, 70, 1),
            Level = 1,
            CurrentHp = 1500,
            CurrentMp = 177,
            MaxHp = 1500,
            MaxMp = 177
        };
        await HandleCharacterCreateRequestAsync(
            packet,
            character,
            cancellationToken);
    }

    private async Task HandleDeleteRoleAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        if (_account is null)
        {
            _session.Disconnect();
            return;
        }

        if (!IsCharacterSelectionLifecyclePhase)
        {
            await RejectOutsideSelectionLifecycleAsync(
                CommandFamily.CharacterDelete,
                packet,
                cancellationToken);
            return;
        }

        var characterName = PacketText.ReadFixedAscii(packet.Payload, 32, 32);
        await HandleCharacterDeleteRequestAsync(
            packet,
            characterName,
            cancellationToken);
    }

    private async Task HandleEnterGameAsync(CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        if (_account is not null &&
            (!_characterSnapshotLoaded ||
             (_character is not null &&
              !_characterSnapshotBootstrapPending)))
        {
            if (!await RefreshCharacterSnapshotAsync(
                    "enter",
                    cancellationToken))
            {
                return;
            }
        }

        if (_character is null)
        {
            await _session.SendAsync(PacketBuilder.BlankUser(), cancellationToken, "BlankUser");
            return;
        }

        if (!await EnsureCheckpointOwnershipAsync(cancellationToken))
        {
            return;
        }
        // The reconnect joins the run's instance before the world bootstrap
        // resolves this session's own map runtime. A refused join leaves the
        // character in the capital, which is what the checkpoint below then
        // persists.
        TryEnterReconnectedInstance();
        if (!await PersistUnavailableInstanceReconnectRecoveryAsync(
                cancellationToken))
        {
            return;
        }

        var trainingDummyEntry =
            _registry.IsTrainingDummyCore(_character);
        if (trainingDummyEntry)
        {
            await RestoreEntryStateAsync(cancellationToken);
        }

        // A process crash can leave the durable Merge flag behind after its
        // owning session vanished. Clear it before EnterMain, the owned-pet
        // list, PlayerDetail, or any calculated-stat frame can expose a stale
        // hidden pet or temporary stat overlay to the new client.
        if (_characterLoadSnapshot is { } enterSnapshot)
        {
            await RecoverStalePetOwnerMergeOnLoginAsync(
                enterSnapshot.Pets,
                cancellationToken);
        }

        ResetPlayerMovementEcs();
        if (!trainingDummyEntry)
        {
            await RestoreEntryStateAsync(cancellationToken);
        }

        await RefillCarriedPetEnergyForLoginAsync(cancellationToken);

        var enterMain = PacketBuilder.EnterMain(_character);
        var kitBagDetailPages = PacketBuilder.KitBagDetailPages(_character);
        var kitBagSlotIndexes = PacketBuilder.KitBagSlotIndexes(_character);
        IReadOnlyList<SkillState> skillStates =
            _characterLoadSnapshot?.Skills ?? [];
        IReadOnlyList<TalentState> talentStates =
            _characterLoadSnapshot?.Talents ?? [];
        IReadOnlyList<PetBootstrapSnapshot> ownedPets =
            _characterLoadSnapshot?.Pets ?? [];
        var ownedPetList = PacketBuilder.OwnedPetList(
            RequirePetContent(),
            ownedPets,
            _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                PetShedCapacityPolicy.DefaultOpenedCellCount);
        Console.WriteLine(
            $"[game] enter name={_character.Name} profession={_character.Profession} level={_character.Level} honor={_character.MedusaHonorPoints} equipment={PacketBuilder.EnterEquipmentSummary(_character)} main={enterMain.Length} kitbagDetail={kitBagDetailPages.Length} kitbagIndex={kitBagSlotIndexes.Length} skills={skillStates.Count} talents={talentStates.Count} pets={ownedPets.Count}");

        await _session.SendAsync(enterMain, cancellationToken, "EnterMain");
        await _session.SendAsync(
            PacketBuilder.EnterUiBootstrap(),
            cancellationToken,
            "EnterUiBootstrap");
        // Published from the character's own quest state: a character holding no
        // quest advertises an empty snapshot, so the first quest no longer appears
        // on every login. It stays here, before the npc catalog is installed,
        // because that is the ordering the client expects; the giver and responder
        // come from the published map content instead.
        await SendQuestSnapshotAsync("login", cancellationToken);

        foreach (var packet in kitBagDetailPages)
        {
            await _session.SendAsync(packet, cancellationToken, "KitBagDetail");
        }

        foreach (var packet in kitBagSlotIndexes)
        {
            await _session.SendAsync(packet, cancellationToken, "KitBagSlotIndex");
        }

        await _session.SendAsync(
            ownedPetList,
            cancellationToken,
            "OwnedPetList");
        if (ownedPets.SingleOrDefault(
                static pet => pet.IsCarried) is { } carriedPet)
        {
            // Opcode 10237 selects the carried pet but does not publish its
            // durable energy. The stock client otherwise initializes the bar
            // as full until the first six-second heartbeat or a Merge result.
            await _session.SendAsync(
                PacketBuilder.PetEnergy(
                    carriedPet.CurrentEnergy,
                    carriedPet.MaximumEnergy),
                cancellationToken,
                "PetEnergyBootstrap");
        }
        await _session.SendAsync(
            PacketBuilder.MedusaDesignationInfo(
                _character.SelectedTitleId,
                _character.OwnedTitleIds),
            cancellationToken,
            "DesignationInfo");
        await _session.SendAsync(PacketBuilder.EnterComplete(), cancellationToken, "EnterComplete");
    }

    private async Task SendMapWorldObjectsAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[world] ignored ClientReady: no active character");
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        var mapContent = await _worldContent.ReadMapAsync(
            _character.CurrentMap,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var loadedNpcDefinitions = mapContent.Npcs;
        var compatibleNpcs = new List<NpcSpawnDefinition>(loadedNpcDefinitions.Count);
        foreach (var npc in loadedNpcDefinitions)
        {
            compatibleNpcs.Add(
                CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility(npc));
        }

        // The placements are applied to the whole map at once so the identities the
        // map ends up with stay unique: the catalog is installed into dictionaries
        // keyed by object and interaction id, and a duplicate there ends the session.
        var placedNpcs = CapturedNpcPlacementPolicy.ApplyToMap(compatibleNpcs);
        var npcDefinitions = new List<NpcSpawnDefinition>(placedNpcs.Count);
        foreach (var effectiveNpc in placedNpcs)
        {
            if (WorldObjectIds.IsReservedForPlayer(effectiveNpc.ObjectId) ||
                !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(
                    effectiveNpc.X,
                    effectiveNpc.Z,
                    out _))
            {
                Console.WriteLine(
                    $"[npc] skipped invalid world object map={_character.CurrentMap} object={effectiveNpc.ObjectId} key={effectiveNpc.NpcKey} x={effectiveNpc.X} z={effectiveNpc.Z}");
                continue;
            }

            npcDefinitions.Add(effectiveNpc);
        }

        // A Wonderland run owns its own NPCs - the eight island teleporters, the
        // entrance blackmarket actor and the eight treasure chests - and publishes
        // no map-207 rows, so they are injected here, before the roster is
        // published to the instance catalog and to the client.
        var beforeInstanceNpcs = npcDefinitions.Count;
        npcDefinitions =
        [
            .. _registry.AddWonderlandInstanceNpcs(_session, npcDefinitions)
        ];
        if (npcDefinitions.Count != beforeInstanceNpcs)
        {
            Console.WriteLine(
                "[npc] instance actors injected " +
                $"map={_character.CurrentMap} " +
                $"static={beforeInstanceNpcs} " +
                $"total={npcDefinitions.Count}");
        }

        // Operator-authored GM NPCs ride on top of the published roster, placed
        // before the catalog is published so they go through the same reserved-id,
        // grid and duplicate-key checks as every other actor.
        var gmNpcs = GmNpcDialoguePolicy.BuildPlacements(
            GmNpcOverrideCatalog.Current,
            _character.CurrentMap,
            compatibleNpcs);
        if (gmNpcs.Count > 0)
        {
            npcDefinitions.AddRange(gmNpcs);
        }

        var npcCatalog = await _registry.PublishMapNpcDefinitionsAsync(
            _character.CurrentMap,
            npcDefinitions,
            _session,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        npcDefinitions = npcCatalog.Definitions.ToList();
        InstallNpcCatalog(npcCatalog);

        var npcObjectIds = npcDefinitions
            .Select(npc => npc.ObjectId)
            .ToHashSet();

        // Operator-authored GM overrides ride on top of the immutable published
        // list: edits and disables first, then the brand-new points. Applied
        // before the validation loop below so a GM row goes through exactly the
        // same reserved-id, grid and NPC-collision filters as a published one.
        var loadedMonsterDefinitions = MonsterOverridePolicy.Apply(
            mapContent.Monsters,
            _character.CurrentMap);
        var monsterDefinitions = new List<CapturedMonsterSpawn>(loadedMonsterDefinitions.Count);
        foreach (var monster in loadedMonsterDefinitions)
        {
            try
            {
                monster.Validate(_character.CurrentMap);
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine(
                    $"[mob] skipped invalid captured spawn map={_character.CurrentMap} object={monster.ObjectId}: {ex.Message}");
                continue;
            }

            if (WorldObjectIds.IsReservedForPlayer(monster.ObjectId))
            {
                Console.WriteLine(
                    $"[mob] skipped reserved player object ID map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey}");
                continue;
            }

            if (!WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                    monster.AppearanceX,
                    monster.AppearanceZ,
                    out _))
            {
                Console.WriteLine(
                    $"[mob] skipped out-of-grid appearance map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey} x={monster.AppearanceX} z={monster.AppearanceZ}");
                continue;
            }

            if (npcObjectIds.Contains(monster.ObjectId))
            {
                Console.WriteLine(
                    $"[mob] skipped NPC object-ID collision map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey}");
                continue;
            }

            monsterDefinitions.Add(monster);
        }

        var monsterRuntimeInitializedAt = DateTimeOffset.UtcNow;
        WorldBossRespawnState? activeWorldBossRespawn = null;
        try
        {
            var respawn = await _worldBossRespawns.ReadActiveAsync(
                new WorldBossRespawnReadRequest(
                    _character.CurrentMap,
                    monsterRuntimeInitializedAt),
                cancellationToken);
            activeWorldBossRespawn = respawn is null
                ? null
                : FocusedGameplayProjectionCompatibility.ToLegacy(respawn);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] failed loading persisted respawn map={_character.CurrentMap}: {ex.Message}");
            if (_gameplayCatalogs.WorldBosses.TryGet(
                    _character.CurrentMap,
                    out var worldBoss))
            {
                // A database outage must never make a killed world boss reappear
                // early. Suppress it for this runtime and recover on restart.
                activeWorldBossRespawn = new WorldBossRespawnState(
                    _character.CurrentMap,
                    worldBoss.TemplateKey,
                    DateTimeOffset.MaxValue);
            }
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var runtimeMonsterCount = _registry.InitializeMapMonsters(
            _session,
            _character.CurrentMap,
            monsterDefinitions,
            monsterRuntimeInitializedAt,
            activeWorldBossRespawn);

        Console.WriteLine(
            $"[npc] loaded map definitions character={_character.Name} map={_character.CurrentMap} count={npcDefinitions.Count}");
        Console.WriteLine(
            runtimeMonsterCount > 0
                ? $"[mob] loaded shared map runtime character={_character.Name} map={_character.CurrentMap} count={runtimeMonsterCount}"
                : $"[mob] no captured map definitions character={_character.Name} map={_character.CurrentMap}");

        // Monster visibility state is map-owned. Register as non-ready before
        // the initial NPC/monster snapshot so the transition can commit while
        // this player remains hidden from all live world broadcasts.
        if (!_registered)
        {
            JoinCurrentWorld(worldReady: false);
            _registered = true;
        }

        await RefreshNearbyWorldObjectsAsync("initial", cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await SendMapPlayersAsync(cancellationToken);
    }

}
