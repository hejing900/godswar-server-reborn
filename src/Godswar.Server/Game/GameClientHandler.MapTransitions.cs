using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const float MapPortalTriggerRadius = 6f;
    private static readonly TimeSpan DefaultMapTransitionReadyTimeout =
        TimeSpan.FromSeconds(60);

    private readonly TimeSpan _mapTransitionReadyTimeout;
    private PendingMapTransition? _pendingMapTransition;
    private Task? _mapTransitionTimeoutTask;

    private bool IsMapTransitionPending =>
        _pendingMapTransition is not null;

    private static bool IsAllowedDuringMapTransition(ushort opcode) =>
        opcode is
            Opcodes.ClientReady or
            Opcodes.PlayerDetailRequest or
            Opcodes.Ping or
            Opcodes.UiHeartbeat;

    private async Task<bool> TryBeginMapTransitionAsync(
        AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not null)
        {
            return true;
        }

        if (await TryApplyMedusaIslandTraversalAsync(
                movement,
                cancellationToken))
        {
            return true;
        }

        if (_character is null ||
            movement.MapId != _character.CurrentMap ||
            !MapTraversalDetector.TryDetectAndResolve(
                _gameplayCatalogs.MapTraversal,
                movement,
                MapPortalTriggerRadius,
                out var resolution) ||
            resolution.SourceMapId is < byte.MinValue or > byte.MaxValue ||
            resolution.TargetMapId is < byte.MinValue or > byte.MaxValue)
        {
            return false;
        }

        var targetMapId = checked((byte)resolution.TargetMapId);
        var outcome = await TryBeginMapTransitionAsync(
            targetMapId,
            resolution.TargetArrival.X,
            resolution.TargetArrival.Z,
            resolution.Source,
            cancellationToken);
        return outcome != SceneTransitionOutcome.RejectedWithoutRelocation;
    }

    /// <summary>
    /// The reviewed five-argument entry point, used by every existing caller.
    /// </summary>
    /// <remarks>
    /// It stays a method of its own - rather than an optional parameter on the
    /// implementation - because the map-transition acceptance checks resolve this
    /// exact signature reflectively. Only the Cursed Land transports need the
    /// extra fight-state frame the reference sends before a landing, so that
    /// variant lives in <see cref="TryBeginMapTransitionCoreAsync"/> and every
    /// other caller keeps this spelling.
    /// </remarks>
    private Task<SceneTransitionOutcome> TryBeginMapTransitionAsync(
        byte targetMapId,
        float targetX,
        float targetZ,
        string source,
        CancellationToken cancellationToken) =>
        TryBeginMapTransitionCoreAsync(
            targetMapId,
            targetX,
            targetZ,
            source,
            cancellationToken,
            publishFightStateReset: false);

    private async Task<SceneTransitionOutcome> TryBeginMapTransitionCoreAsync(
        byte targetMapId,
        float targetX,
        float targetZ,
        string source,
        CancellationToken cancellationToken,
        bool publishFightStateReset)
    {
        if (_pendingMapTransition is not null ||
            _account is null ||
            _character is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(targetMapId) ||
            _character.CurrentMap == targetMapId ||
            !_gameplayCatalogs.MapTraversal.TryGetMap(
                _character.CurrentMap,
                out _) ||
            !_gameplayCatalogs.MapTraversal.TryGetMap(targetMapId, out _) ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(targetX, targetZ)))
        {
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.MapTransition,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }

        var sourceMapId = _character.CurrentMap;
        var sourceX = _character.PositionX;
        var sourceZ = _character.PositionZ;
        var accountId = _account.Id;
        var characterId = _character.Id;
        var sourceWorldInstanceCaptured =
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var sourceWorldInstanceId);

        try
        {
            if (!await PersistRelocationCheckpointAsync(
                    targetMapId,
                    targetX,
                    targetZ,
                    cancellationToken))
            {
                return SceneTransitionOutcome.RejectedWithoutRelocation;
            }
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }
        catch (Exception error)
            when (error is not OperationCanceledException ||
                  !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[map] transition persistence rejected " +
                $"character={_character.Name} " +
                $"map={sourceMapId}->{targetMapId}: {error.Message}");
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return SceneTransitionOutcome.CommittedRequiresReconnect;
        }

        bool transferred;
        try
        {
            transferred = _registry.TryTransferMap(
                _session,
                sourceMapId,
                targetMapId,
                targetX,
                targetZ);
        }
        catch (Exception error)
            when (error is not OperationCanceledException ||
                  !cancellationToken.IsCancellationRequested)
        {
            var restored = await RestoreSourcePositionAfterRejectedTransferAsync(
                accountId,
                characterId,
                sourceMapId,
                sourceX,
                sourceZ,
                $"registry transfer failed: {error.Message}",
                ownership,
                CancellationToken.None);
            return restored
                ? SceneTransitionOutcome.RejectedWithoutRelocation
                : SceneTransitionOutcome.CommittedRequiresReconnect;
        }

        if (!transferred)
        {
            var restored = await RestoreSourcePositionAfterRejectedTransferAsync(
                accountId,
                characterId,
                sourceMapId,
                sourceX,
                sourceZ,
                "registry rejected the authoritative source state",
                ownership,
                CancellationToken.None);
            return restored
                ? SceneTransitionOutcome.RejectedWithoutRelocation
                : SceneTransitionOutcome.CommittedRequiresReconnect;
        }
        if (!sourceWorldInstanceCaptured)
        {
            // A successful authoritative transfer requires a source
            // membership. Losing that identity between validation and
            // publication is an invariant violation; reconnecting is safer
            // than broadcasting the departure into an unrelated instance.
            _session.Disconnect();
            throw new InvalidOperationException(
                "The successful map transfer has no captured source " +
                "world-instance identity.");
        }
        if (!await PublishPlayerCoordinationEnteringAsync(
                targetMapId,
                cancellationToken))
        {
            _session.Disconnect();
            return SceneTransitionOutcome.CommittedRequiresReconnect;
        }

        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        _worldPresenceAnnounced = false;
        ClearLocalNpcCatalog();
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        _warehouseAccessContext = null;
        ResetPlayerMovementEcs();
        _nextBasicAttackAt = DateTimeOffset.MinValue;
        _nextSkillCastAt.Clear();

        var transition = new PendingMapTransition(
            sourceMapId,
            targetMapId,
            targetX,
            targetZ);
        _pendingMapTransition = transition;
        _mapTransitionTimeoutTask =
            MonitorMapTransitionTimeoutAsync(
                transition,
                _realtimeMovementStop.Token);

        try
        {
            await _registry.BroadcastToWorldInstanceAsync(
                sourceWorldInstanceId,
                PacketBuilder.RemoveWorldObjects(
                    CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "MapTransitionSourceRemove");
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return SceneTransitionOutcome.CommittedRequiresReconnect;
            }

            if (publishFightStateReset)
            {
                // Every captured relocation - the Cursed Land transports and the
                // free revive alike - clears the local player's fight flag
                // immediately before the landing frame. Sent once the transfer is
                // authoritative and just before the scene change.
                await _session.SendAsync(
                    PacketBuilder.ObjectFightState(
                        LocalPlayerObjectId,
                        engaged: false),
                    cancellationToken,
                    "MapTransitionFightStateReset");
            }

            await _session.SendAsync(
                PacketBuilder.SceneChange(
                    LocalPlayerObjectId,
                    targetX,
                    y: 0f,
                    targetZ,
                    targetMapId),
                cancellationToken,
                "SceneChange");
        }
        catch
        {
            // The destination is already the persisted authority. A reconnect
            // will enter it cleanly; continuing on an old client scene would
            // instead create two conflicting worlds.
            _session.Disconnect();
            throw;
        }

        Console.WriteLine(
            $"[map] scene change queued character={_character.Name} " +
            $"map={sourceMapId}->{targetMapId} " +
            $"arrival={targetX:F2},{targetZ:F2} " +
            $"source={source}");
        return SceneTransitionOutcome.CommittedAwaitingReadiness;
    }

    private async Task<bool> RestoreSourcePositionAfterRejectedTransferAsync(
        int accountId,
        int characterId,
        byte sourceMapId,
        float sourceX,
        float sourceZ,
        string reason,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return false;
        }

        try
        {
            if (!await PersistRelocationCheckpointAsync(
                    sourceMapId,
                    sourceX,
                    sourceZ,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The source-position checkpoint was not admitted.");
            }
            _positionDirty = false;
            _lastPositionPersistUtc = DateTime.UtcNow;
            Console.WriteLine(
                $"[map] restored source position after rejected transition " +
                $"character={_character?.Name ?? characterId.ToString()} " +
                $"map={sourceMapId} reason={reason}");
            return true;
        }
        catch (Exception compensationError)
        {
            Console.WriteLine(
                $"[map] source-position compensation failed " +
                $"character={_character?.Name ?? characterId.ToString()} " +
                $"map={sourceMapId} reason={reason}: " +
                compensationError.Message);
            _session.Disconnect();
            throw;
        }
    }

    private async Task HandleClientReadyAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is { } transition)
        {
            transition.ClientReadyReceived = true;
            Console.WriteLine(
                $"[map] transition ClientReady " +
                $"character={_character?.Name ?? "<none>"} " +
                $"map={transition.SourceMapId}->{transition.TargetMapId}");
            await TryCompleteMapTransitionAsync(cancellationToken);
            return;
        }

        _clientReadyReceived = true;
        Console.WriteLine(
            $"[game] ClientReady " +
            $"character={_character?.Name ?? "<none>"}");
        // The status frame this bootstrap sends is the session's first 10166, so
        // the panel's guild rows are read before it: measured with the login probe
        // (2026-10-02), EnterUiReady alone is too late and that first frame carried
        // the empty defaults.
        if (_character is { } ready)
        {
            await RefreshGuildPanelStatusAsync(ready.Id, cancellationToken);
        }

        await SendPostEnterBootstrapAsync(cancellationToken);
    }

    private async Task<bool>
        HandleMapTransitionPlayerDetailSentAsync(
            CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not { } transition)
        {
            return false;
        }

        transition.PlayerDetailSent = true;
        Console.WriteLine(
            $"[map] transition PlayerDetail " +
            $"character={_character?.Name ?? "<none>"} " +
            $"map={transition.SourceMapId}->{transition.TargetMapId}");
        await TryCompleteMapTransitionAsync(cancellationToken);
        return true;
    }

    private async Task TryCompleteMapTransitionAsync(
        CancellationToken cancellationToken)
    {
        var transition = _pendingMapTransition;
        if (transition is null ||
            !transition.ClientReadyReceived ||
            !transition.PlayerDetailSent)
        {
            return;
        }

        if (_character is null ||
            _character.CurrentMap != transition.TargetMapId)
        {
            _session.Disconnect();
            throw new InvalidOperationException(
                "Map transition authority changed before client readiness.");
        }

        if (!transition.TryStartCompletion())
        {
            return;
        }

        try
        {
            await SendMapWorldObjectsAsync(cancellationToken);
            await RestorePersistedPetPresenceAsync(cancellationToken);
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MapTransitionPlayerStatus");
            await SendExperienceBoostStatusAsync(
                "map-transition",
                cancellationToken);

            // A secure realtime client receives one destination keyframe only
            // after the reliable scene/AOI handoff is ready. This preserves
            // the triggering input acknowledgement while advancing the world
            // generation so old-map UDP input cannot mutate the new map.
            EnsureRealtimeWorld();
            PublishRealtimeSnapshotIfDue(decision: null);
            _pendingMapTransition = null;
            transition.MarkCompleted();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[map] transition completion failed " +
                $"character={_character.Name} " +
                $"map={transition.SourceMapId}->{transition.TargetMapId}: " +
                error.Message);
            _session.Disconnect();
            throw;
        }

        // A shop window cannot survive a map transfer, so a stale open shop
        // must never keep authorizing sales on the destination map.
        _openCapitalShopService = null;
        Console.WriteLine(
            $"[map] transition complete character={_character.Name} " +
            $"map={transition.SourceMapId}->{transition.TargetMapId} " +
            $"arrival={transition.TargetX:F2},{transition.TargetZ:F2}");
    }

    private async Task MonitorMapTransitionTimeoutAsync(
        PendingMapTransition transition,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    transition.TimeoutCancellation);
            await Task.Delay(
                _mapTransitionReadyTimeout,
                timeoutLifetime.Token);
            if (!transition.TryMarkTimedOut())
            {
                return;
            }

            Console.WriteLine(
                $"[map] transition readiness timed out " +
                $"character={_character?.Name ?? "<none>"} " +
                $"map={transition.SourceMapId}->{transition.TargetMapId}");
            _session.Disconnect();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested ||
                  transition.TimeoutCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            transition.DisposeTimeoutCancellation();
        }
    }

}
