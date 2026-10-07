using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The reviewed six-argument entry point, used by every existing caller.
    /// </summary>
    /// <remarks>
    /// Kept as its own method for the same reason as the cross-map spelling: the
    /// Cursed Land random transports are the only callers that need the
    /// fight-state frame the reference sends before a landing, so that variant
    /// lives in <see cref="TryBeginSameMapSceneTransitionCoreAsync"/>.
    /// </remarks>
    private Task<SceneTransitionOutcome> TryBeginSameMapSceneTransitionAsync(
        float targetX,
        float targetZ,
        string source,
        Func<bool>? continuationGuard,
        CancellationToken cancellationToken,
        bool publishRevivalVitals = false) =>
        TryBeginSameMapSceneTransitionCoreAsync(
            targetX,
            targetZ,
            source,
            continuationGuard,
            cancellationToken,
            publishRevivalVitals,
            publishFightStateReset: false);

    private async Task<SceneTransitionOutcome> TryBeginSameMapSceneTransitionCoreAsync(
        float targetX,
        float targetZ,
        string source,
        Func<bool>? continuationGuard,
        CancellationToken cancellationToken,
        bool publishRevivalVitals,
        bool publishFightStateReset)
    {
        if (_pendingMapTransition is not null ||
            _account is null ||
            _character is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            continuationGuard?.Invoke() == false ||
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
        var sourceMapId = _character.CurrentMap;
        if (!_registry.TryGetSessionWorldInstanceId(
                _session,
                out var sourceWorldInstanceId) ||
            !HasSameMapSceneAuthority(
                sourceMapId,
                sourceWorldInstanceId))
        {
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.MapTransition,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership) ||
            continuationGuard?.Invoke() == false ||
            !HasSameMapSceneAuthority(
                sourceMapId,
                sourceWorldInstanceId))
        {
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }

        try
        {
            if (!await PersistRelocationCheckpointAsync(
                    sourceMapId,
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
                $"[map] same-scene persistence rejected source={source}: " +
                error.Message);
            return SceneTransitionOutcome.RejectedWithoutRelocation;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership) ||
            continuationGuard?.Invoke() == false ||
            !HasSameMapSceneAuthority(
                sourceMapId,
                sourceWorldInstanceId))
        {
            // The target checkpoint is already durable. Continuing in the
            // old live position would split authority; reconnecting enters
            // the persisted destination cleanly.
            _session.Disconnect();
            return SceneTransitionOutcome.CommittedRequiresReconnect;
        }

        if (!_registry.TryHideForSameWorldSceneTransition(
                _session,
                ownership,
                sourceMapId,
                sourceWorldInstanceId,
                out _))
        {
            _session.Disconnect();
            return SceneTransitionOutcome.CommittedRequiresReconnect;
        }

        _character.PositionX = targetX;
        _character.PositionZ = targetZ;
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);

        _worldPresenceAnnounced = false;
        ClearLocalNpcCatalog();
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        _warehouseAccessContext = null;
        ResetPlayerMovementEcs();
        RebaseRealtimeWorld();
        _nextBasicAttackAt = DateTimeOffset.MinValue;
        _nextSkillCastAt.Clear();

        var transition = new PendingMapTransition(
            sourceMapId,
            sourceMapId,
            targetX,
            targetZ);
        _pendingMapTransition = transition;
        _mapTransitionTimeoutTask = MonitorMapTransitionTimeoutAsync(
            transition,
            _realtimeMovementStop.Token);

        try
        {
            await _registry.BroadcastToWorldInstanceAsync(
                sourceWorldInstanceId,
                PacketBuilder.RemoveWorldObjects(CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "SameMapSceneTransitionSourceRemove");
            if (publishRevivalVitals)
            {
                // Origin resets the retained local avatar to stand while
                // processing SceneChange. SetState forces its death animation
                // if native HP is still zero; a later status refresh cannot
                // undo that choice. Publish restored HP before the reset.
                await _session.SendAsync(
                    PacketBuilder.PlayerVitalsUpdate(
                        LocalPlayerObjectId,
                        _character.CurrentHp,
                        _character.CurrentMp),
                    cancellationToken,
                    "SameMapRevivalVitals");
            }

            if (publishFightStateReset)
            {
                // The reference clears the local player's fight flag immediately
                // before the landing frame on every captured relocation - the
                // Cursed Land transports and the free revive alike. Sent after
                // the guards and before the scene change so a rejected
                // transition never leaves a spurious reset behind.
                await _session.SendAsync(
                    PacketBuilder.ObjectFightState(
                        LocalPlayerObjectId,
                        engaged: false),
                    cancellationToken,
                    "SameMapFightStateReset");
            }

            await _session.SendAsync(
                PacketBuilder.SceneChange(
                    LocalPlayerObjectId,
                    targetX,
                    y: 0f,
                    targetZ,
                    sourceMapId),
                cancellationToken,
                "SameMapSceneChange");
            await PublishPartyPositionRefreshAsync(cancellationToken);
        }
        catch
        {
            // The destination is already durable and the source scene is
            // hidden. Reconnect rather than expose split scene authority.
            _session.Disconnect();
            throw;
        }

        Console.WriteLine(
            $"[map] same-scene change queued character={_character.Name} " +
            $"map={sourceMapId} arrival={targetX:F2},{targetZ:F2} " +
            $"source={source}");
        return SceneTransitionOutcome.CommittedAwaitingReadiness;
    }

    private bool HasSameMapSceneAuthority(
        byte expectedMapId,
        WorldInstanceId expectedWorldInstanceId) =>
        _character is not null &&
        _character.CurrentMap == expectedMapId &&
        _registry.IsSessionInWorldInstance(
            _session,
            expectedWorldInstanceId);
}
