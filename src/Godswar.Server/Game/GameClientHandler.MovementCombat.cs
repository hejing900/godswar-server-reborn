using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool RejectDeadLegacyMovement(GamePacket packet)
    {
        if (_character is not { CurrentHp: <= 0 })
        {
            return false;
        }

        Console.WriteLine(
            $"[world] ignored legacy movement from dead character={_character.Name} opcode={packet.Opcode}");
        return true;
    }

    private async Task<bool> HandleWalkAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }
        if (_session.IsRealtimeMovementActive)
        {
            await RejectLegacyWalkAfterRealtimeCutoverAsync(
                cancellationToken);
            return false;
        }

        var movementAcceptedAt = DateTimeOffset.UtcNow;
        if (!IsElementalMovementAllowed(movementAcceptedAt))
        {
            return false;
        }

        var updated = _registry.PlayerRuntimeMode ==
            PlayerRuntimeMode.Ecs
            ? UpdateCharacterPositionFromWalkEcs(
                packet,
                out var movement)
            : UpdateCharacterPositionFromWalk(
                packet,
                out movement);
        if (!updated)
        {
            return false;
        }

        CommitAcceptedElementalMovement(
            movement,
            movementAcceptedAt);

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.Movement,
            cancellationToken);

        if (await TryBeginMapTransitionAsync(
                movement,
                cancellationToken))
        {
            // The source map receives an explicit removal during the
            // transition. Never broadcast the triggering walk into either
            // the old or the still-hidden destination world.
            return false;
        }

        await RefreshNearbyWorldObjectsAsync("walk", cancellationToken);
        await RecordQuestExplorationAsync(cancellationToken);
        await PublishPartyPositionRefreshAsync(cancellationToken);
        await PersistCharacterPositionAsync(force: false, cancellationToken);

        return true;
    }

    private async Task HandleReviveAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ReviveTrace.Log("REVIVE-HANDLER entered");
        if (_character is null)
        {
            ReviveTrace.Log("REVIVE-HANDLER rejected: character null");
            Console.WriteLine("[revive] ignored request before character enter");
            return;
        }

        if (!ReviveRequest.TryParse(packet.Buffer, out var request))
        {
            Console.WriteLine($"[revive] ignored malformed request len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        ReviveTrace.Log(
            $"REVIVE-PARSED obj={request.PlayerObjectId} " +
            $"type={request.ReviveType} local={LocalPlayerObjectId} " +
            $"hp={_character.CurrentHp} map={_character.CurrentMap} " +
            $"pos={_character.PositionX:F2},{_character.PositionZ:F2}");

        if (request.PlayerObjectId != LocalPlayerObjectId)
        {
            ReviveTrace.Log("REVIVE-REJECT spoofed");
            Console.WriteLine(
                $"[revive] ignored spoofed player object character={_character.Name} request-object={request.PlayerObjectId} expected-object={LocalPlayerObjectId}");
            return;
        }

        if (request.ReviveType != ReviveRequest.FreeReviveType)
        {
            ReviveTrace.Log($"REVIVE-REJECT unsupported type={request.ReviveType}");
            Console.WriteLine(
                $"[revive] ignored unsupported type character={_character.Name} requested-type={request.ReviveType}");
            return;
        }

        if (_character.CurrentHp > 0)
        {
            ReviveTrace.Log($"REVIVE-REJECT living hp={_character.CurrentHp}");
            Console.WriteLine($"[revive] ignored request for living character={_character.Name}");
            return;
        }

        ReviveTrace.Log("REVIVE-ACCEPT");

        // A Wonderland death revives inside the run at the entrance island. The
        // generic path below would restore the character's saved entry state,
        // which is the capital, and tear the run down with it.
        if (await TryHandleWonderlandReviveAsync(cancellationToken))
        {
            return;
        }

        // 港湾遇袭 revives inside its own run for the same reason.
        if (await TryHandleHarborAttackReviveAsync(cancellationToken))
        {
            return;
        }

        if (!_registry.TryGetPlayerLifeRevision(
                _session,
                out _))
        {
            Console.WriteLine(
                $"[revive] rejected missing life authority " +
                $"character={_character.Name}");
            return;
        }
        var revivedLifeRevision = _registry.AdvancePlayerLifeRevision(_session);
        if (revivedLifeRevision < 0)
        {
            return;
        }
        try
        {
            await _registry.SetPersistentRuntimeStatusAndPublishAsync(
                _session,
                MountCatalog.RuntimeStatusKind,
                statusId: 0,
                priority: 0,
                beneficial: false,
                movementSpeedBonus: 0f,
                active: false,
                DateTimeOffset.UtcNow,
                "mount-revive",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[mount] failed publishing revive dismount character={_character.Name} life={revivedLifeRevision}: {ex.Message}");
        }

        var previousMap = _character.CurrentMap;
        ReviveTrace.Log($"REVIVE-BEFORE-ENTRY map={previousMap} pos={_character.PositionX:F2},{_character.PositionZ:F2} hp={_character.CurrentHp}/{_character.MaxHp}");

        // The reference revive response opens by dropping the dead player's
        // fight state - captured immediately before the landing frame as
        // `100029278F0400000000000000000000` - so the client leaves the combat
        // HUD state before the scene reloads.
        await _session.SendAsync(
            PacketBuilder.ObjectFightState(
                request.PlayerObjectId,
                engaged: false),
            cancellationToken,
            "PlayerReviveFightStateReset");

        if (_worldPresenceAnnounced)
        {
            await BroadcastPlayerLeaveAsync(cancellationToken);
        }

        if (_registered)
        {
            _registry.Remove(_session, preservePlayerStatus: true);
            _registered = false;
        }

        _worldPresenceAnnounced = false;
        _clientReadyReceived = false;
        _playerDetailSent = false;
        _enterUiReadyReceived = false;
        _postEnterBootstrapSent = false;
        ClearLocalNpcCatalog();
        _nextBasicAttackAt = DateTimeOffset.MinValue;

        // Type 2 is the only capture-proven free-revival path. Currency-backed
        // in-place revival remains unsupported until its native contract and
        // settlement rules are proven.
        await RestoreEntryStateAsync(cancellationToken);
        ReviveTrace.Log($"REVIVE-AFTER-RESTORE map={_character.CurrentMap} pos={_character.PositionX:F2},{_character.PositionZ:F2} hp={_character.CurrentHp}/{_character.MaxHp}");
        await HandleEnterGameAsync(cancellationToken);
        ReviveTrace.Log($"REVIVE-AFTER-ENTER map={_character.CurrentMap} pos={_character.PositionX:F2},{_character.PositionZ:F2} hp={_character.CurrentHp}/{_character.MaxHp}");
        Console.WriteLine(
            $"[revive] free revival character={_character.Name} request-object={request.PlayerObjectId} requested-type={request.ReviveType} map={previousMap}->{_character.CurrentMap} hp={_character.CurrentHp}/{_character.MaxHp} mp={_character.CurrentMp}/{_character.MaxMp}");
    }

    /// <summary>
    /// Reference revive strategy: a free revive puts the character back on the
    /// map it died on, at that map's own revive point. The landing is per map
    /// (the captured Athens revive is x = 20, z = -100, which is nowhere near
    /// the camp coordinate), so a map without a captured point keeps the camp
    /// capital fallback and traces it instead of guessing a coordinate.
    /// </summary>
    private static void ApplyFreeRevivalLanding(GameCharacter character)
    {
        var deathMap = character.CurrentMap;
        if (ReviveLandingCatalog.TryResolve(deathMap, out var landing))
        {
            GameDefaults.NormalizeCamp(character);
            character.CurrentMap = landing.MapId;
            character.PositionX = landing.X;
            character.PositionZ = landing.Z;
            ReviveTrace.Log(
                $"REVIVE-LANDING death-map={deathMap} " +
                $"map={landing.MapId} pos={landing.X:F2},{landing.Z:F2}");
            return;
        }

        ReviveTrace.Log(
            $"REVIVE-LANDING-FALLBACK death-map={deathMap} capital");
        GameDefaults.InitializeStartingLocation(character);
    }

    private async Task RestoreFreeRevivalStateAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var medusaRevival = TryRestoreMedusaRevivalState(_character);
        if (!medusaRevival)
        {
            ApplyFreeRevivalLanding(_character);
        }

        // 复活会把角色从战斗中挪走,所以所有正在以它为目标的怪物都必须
        // 清掉仇恨并走回自己的刷新点。否则挑战者还没收到恢复后的血量帧,
        // 就会被同一只怪重新接战。
        _registry.ClearMonsterAggroForCharacter(
            _character.CurrentMap,
            _session,
            _character.Id,
            DateTimeOffset.UtcNow);

        _character.MarkPositionChanged();
        lock (_character.VitalsSync)
        {
            // A dungeon death keeps the run alive, so the challenger returns
            // with a real fighting chance rather than the capital's tenth.
            var restoredPercent = medusaRevival ? 30 : 10;
            _character.CurrentHp = Math.Max(
                1,
                checked(_character.MaxHp * restoredPercent / 100));
            _character.CurrentMp = Math.Max(
                0,
                checked(_character.MaxMp * restoredPercent / 100));
            _character.MarkVitalsChanged();
        }
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;

        if (!await PersistPositionCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The revival position checkpoint was not durable.");
        }
        if (!await PersistVitalsCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The revival vitals checkpoint was not durable.");
        }
    }

    /// <summary>
    /// <see cref="MedusaPlayerDeathRule.RespawnAtInstanceBeginning"/> keeps a
    /// dead challenger inside the run: the capital round-trip would drop the
    /// party membership and the remaining attempt. The landing point is the
    /// same captured first-entry anchor the run starts on.
    /// </summary>
    private static bool TryRestoreMedusaRevivalState(GameCharacter character)
    {
        if (!DynamicDungeonContentMapPolicy.IsMedusaMap(
                character.CurrentMap))
        {
            return false;
        }

        if (!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry",
                out var entrance))
        {
            return false;
        }

        character.PositionX = entrance.X;
        character.PositionZ = entrance.Z;
        return true;
    }

    private async Task HandleBasicAttackAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[attack] ignored basic attack before character enter");
            return;
        }

        if (!IsHostileStatusBasicAttackAllowed(DateTimeOffset.UtcNow))
        {
            return;
        }

        if (await TryHandlePvpBasicAttackAsync(
                packet,
                cancellationToken))
        {
            return;
        }

        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await HandleBasicAttackEcsAsync(
                packet,
                cancellationToken);
            return;
        }

        if (_character.CurrentHp <= 0)
        {
            Console.WriteLine($"[attack] ignored basic attack from dead character={_character.Name}");
            return;
        }

        if (!BasicAttackRequest.TryParse(packet.Buffer, out var attack))
        {
            Console.WriteLine($"[attack] ignored malformed basic attack len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (attack.AttackerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected spoofed attacker character={_character.Name} supplied={attack.AttackerObjectId} expected={LocalPlayerObjectId}");
            return;
        }

        if (!_registry.TryCapturePlayerMonsterTarget(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                out var target,
                out var combatAuthority) ||
            !_registry.IsMonsterVisibleTo(
                _session,
                attack.TargetObjectId,
                target.SpawnGeneration) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine($"[attack] rejected unavailable monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                _character.PositionX,
                _character.PositionZ,
                attack.AttackerX,
                attack.AttackerZ,
                out var attackX,
                out var attackZ))
        {
            Console.WriteLine(
                $"[attack] rejected mismatched position character={_character.Name} server={_character.PositionX:F2},{_character.PositionZ:F2} reported={attack.AttackerX:F2},{attack.AttackerZ:F2}");
            return;
        }

        if (!MonsterCombatResolver.IsWithinBasicAttackRange(
                attackX,
                attackZ,
                target.X,
                target.Z,
                MonsterCombatResolver.ResolvePlayerBasicAttackRange(
                    target.Definition,
                    _gameplayCatalogs.MonsterCombatRanges,
                    CharacterStats.FromCharacter(_character)
                    .BasicAttackRange)))
        {
            Console.WriteLine(
                $"[attack] rejected out-of-range monster character={_character.Name} target={attack.TargetObjectId} player={attackX:F2},{attackZ:F2} monster={target.X:F2},{target.Z:F2}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_registry.GetPlayerSkillCastControl(_session, now) ==
            PlayerSkillCastControl.Stunned)
        {
            Console.WriteLine(
                $"[attack] rejected stunned character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (now < _nextBasicAttackAt)
        {
            Console.WriteLine($"[attack] rejected cooldown character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "basic_attack_resolution"))
        {
            Console.WriteLine(
                $"[attack] rejected stale ownership character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        using var elementalAuthority =
            CapturePveElementalCommitAuthority(_character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[attack] rejected stale elemental authority character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        var admittedCombatRevision =
            NextAdmittedLegacyCombatRevision();
        var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            _character.Id,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            (ulong)admittedCombatRevision);
        var targetCombat = _gameplayCatalogs.MonsterCombatProfiles
            .Resolve(target.Definition)
            .ToTargetStats();
        targetCombat = _registry.AdjustPveMonsterTargetStats(
            _session,
            target,
            now,
            targetCombat);
        var runtimeCombatModifiers =
            _registry.GetRuntimeStatusAggregate(_session, now);
        var resolution = MonsterCombatResolver.ResolvePlayerBasicAttack(
            _character,
            targetCombat,
            eventId,
            runtimeModifiers: runtimeCombatModifiers);
        resolution = _registry.AdjustPveOutgoingResolution(
            _session,
            _character,
            target,
            CombatEventProvenance.DirectBasicAttack,
            now,
            resolution,
            checked((ulong)admittedCombatRevision));
        var attackStats = CharacterStats.FromCharacter(_character);
        var cooldown = PlayerCombatRules.ResolveBasicAttackCooldown(
            attackStats.BasicAttackIntervalMilliseconds);
        _nextBasicAttackAt = now + cooldown;
        var attackSelector = _character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        if (!resolution.Hit)
        {
            await InterruptPendingSkillCastAsync(
                SkillCastInterruptionReason.Replaced,
                cancellationToken);
            var selfMiss = PacketBuilder.PhysicalDamage(
                LocalPlayerObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                resolution.CapturedDamageValue,
                result: attackSelector,
                damageType: (byte)resolution.Outcome);
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                selfMiss,
                target.SpawnGeneration,
                cancellationToken,
                "BasicAttackMissSelf");
            var missViewers = await _registry.BroadcastToMonsterViewersAsync(
                _character.CurrentMap,
                attack.TargetObjectId,
                PacketBuilder.PhysicalDamage(
                    CurrentPlayerObjectId,
                    0f,
                    0f,
                    0f,
                    attack.TargetObjectId,
                    resolution.CapturedDamageValue,
                    result: attackSelector,
                    damageType: (byte)resolution.Outcome),
                cancellationToken,
                _session,
                "BasicAttackMissWorld",
                expectedSpawnGeneration: target.SpawnGeneration);
            Console.WriteLine(
                $"[attack] miss character={_character.Name} target={attack.TargetObjectId} event={eventId} hit={resolution.Rolls.HitRollBasisPoints}/{resolution.Rolls.HitChanceBasisPoints} viewers={missViewers}");
            return;
        }

        if (!_registry.TryCommitPlayerMonsterDamageGuarded(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                target.RuntimeInstanceId,
                _character.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                combatAuthority,
                now,
                resolution,
                out var damageCommit) ||
            damageCommit.DamageResult is not { } damageResult ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            Console.WriteLine($"[attack] rejected stale monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }
        resolution = damageCommit.Resolution;

        var lifeAbsorption = CommitPveLifeAbsorption(
            _character,
            [new PveCommittedMonsterDamage(
                resolution.EventId,
                damageResult.ObjectId,
                damageResult.Monster.SpawnGeneration,
                damageResult.BeforeHealth - damageResult.AfterHealth)]);
        var elementalCommit = CommitPveElementalHit(
            elementalAuthority,
            CombatEventProvenance.DirectBasicAttack,
            resolution,
            damageResult);
        var pendingReward = damageResult.Killed
            ? await PrepareClaimedMonsterKillRewardAsync(damageResult)
            : null;
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);
        await _registry.PublishMonsterClaimStateAsync(
            _session,
            _character.CurrentMap,
            damageResult,
            cancellationToken);
        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.Replaced,
            cancellationToken);
        var selfPacket = PacketBuilder.PhysicalDamage(
            LocalPlayerObjectId,
            0f,
            0f,
            0f,
            attack.TargetObjectId,
            resolution.CapturedDamageValue,
            result: damageResult.Killed ? (byte)5 : attackSelector,
            damageType: (byte)resolution.Outcome);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                selfPacket,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "BasicAttackSelf");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[attack] caster notification failed character={_character.Name} target={attack.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = CurrentPlayerObjectId;
        var viewers = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            attack.TargetObjectId,
            PacketBuilder.PhysicalDamage(
                worldObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                resolution.CapturedDamageValue,
                result: damageResult.Killed ? (byte)5 : attackSelector,
                damageType: (byte)resolution.Outcome),
            cancellationToken,
            _session,
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

        await PublishPveLifeAbsorptionAsync(
            _character,
            lifeAbsorption,
            cancellationToken);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

        if (pendingReward is not null)
        {
            await pendingReward.PublishAsync(cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={_character.Name} target={attack.TargetObjectId} event={eventId} outcome={resolution.Outcome} resolved={resolution.Damage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} first-hit={damageResult.FirstHitCharacterId} caster-notified={casterNotified} viewers={viewers}");
    }

}
