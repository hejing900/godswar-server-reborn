using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task AdvanceElementalPeriodicDamageOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var playerCommits = CommitDuePlayerElementalBurns(
            now,
            cancellationToken);
        foreach (var commit in playerCommits)
        {
            await commit.DeathInterruption;
            UpdateCharacter(
                commit.Target.Session,
                commit.Target.Character,
                advanceWorldRevision: false);
            if (commit.SourceRecoveryApplied && commit.Source is { } source)
            {
                UpdateCharacter(
                    source.Session,
                    source.Character,
                    advanceWorldRevision: false);
            }

            await PublishPlayerElementalBurnAsync(
                commit,
                cancellationToken);
            await PersistPlayerElementalBurnAsync(
                commit,
                cancellationToken);
            if (commit.Killed)
            {
                if (commit.Source?.RecordQuestPlayerKill is { } record)
                    await record(commit.Target, cancellationToken);
                await TryClearPvpDeathStatusAsync(
                    commit.Target,
                    commit.DeathLifeRevision,
                    now,
                    cancellationToken);
            }
        }

        var monsterCommits = CommitDueMonsterElementalBurns(now);
        foreach (var commit in monsterCommits)
        {
            await PublishMonsterElementalBurnAsync(
                commit,
                cancellationToken);
        }

        await ReconcileTrainingDummyElementalStatusesOnceAsync(
            now,
            cancellationToken);
    }

    private IReadOnlyList<PlayerElementalBurnCommit>
        CommitDuePlayerElementalBurns(
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var commits = new List<PlayerElementalBurnCommit>();
        foreach (var pair in _elementalCombatSessions.ToArray())
        {
            var session = pair.Key;
            var state = pair.Value;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var target) ||
                    !target.WorldReady ||
                    target.Character.CurrentHp <= 0 ||
                    !_playerLifeRevisions.ContainsKey(session) ||
                    state.Identity.CharacterId != target.CharacterId ||
                    state.Identity.MapId != target.MapId ||
                    state.Identity.WorldInstanceId != target.WorldInstanceId ||
                    state.Identity.Ownership != target.Ownership ||
                    !IsCurrentAccountSession(
                        target.AccountId,
                        session,
                        target.Ownership))
                {
                    continue;
                }

                IReadOnlyList<ElementalPeriodicDamageIntent> due;
                GameSessionContext? source;
                PvpEligibilityResult eligibility;
                uint appliedDamage;
                ElementalPeriodicDamageIntent fatalIntent;
                Task interruption = Task.CompletedTask;
                var killed = false;
                var deathLifeRevision = 0L;
                lock (target.Character.VitalsSync)
                lock (state.Gate)
                {
                    due = state.Statuses.CollectDuePeriodicDamage(
                        now.ToUnixTimeMilliseconds());
                    if (due.Count == 0)
                    {
                        continue;
                    }

                    source = FindCurrentElementalSourceLocked(
                        target,
                        due[0].SourceCharacterId);
                    if (source is not null &&
                        !_playerLifeRevisions.ContainsKey(source.Session))
                    {
                        source = null;
                    }
                    eligibility = source is null
                        ? default
                        : _gameplayCatalogs.PvpWorldAuthority
                            .EvaluateOpposingFaction(
                                source.Character,
                                target.Character,
                                now);
                    var remainingHealth = target.Character.CurrentHp;
                    long totalApplied = 0;
                    fatalIntent = default;
                    foreach (var intent in due)
                    {
                        if (intent.Effect != ElementalEffectKind.Burn ||
                            intent.Provenance !=
                                CombatEventProvenance.ElementalStatus ||
                            intent.SourceCharacterId <= 0 ||
                            intent.TargetCharacterId != target.CharacterId ||
                            intent.SourceEventId == 0 ||
                            intent.TickOrdinal <= 0 ||
                            intent.Damage <= 0 ||
                            remainingHealth <= 0)
                        {
                            continue;
                        }

                        var applied = Math.Min(
                            (long)remainingHealth,
                            intent.Damage);
                        remainingHealth = checked(
                            remainingHealth - (int)applied);
                        totalApplied = checked(totalApplied + applied);
                        if (remainingHealth == 0)
                        {
                            fatalIntent = intent;
                            break;
                        }
                    }

                    if (totalApplied <= 0)
                    {
                        continue;
                    }

                    killed = remainingHealth == 0;
                    if (killed)
                    {
                        interruption = RequestSkillCastInterruptionAsync(
                            target.Session,
                            SkillCastInterruptionReason.Death,
                            cancellationToken);
                    }

                    target.Character.CurrentHp = remainingHealth;
                    target.Character.MarkVitalsChanged();
                    appliedDamage = checked((uint)totalApplied);
                    if (killed)
                    {
                        deathLifeRevision = AdvancePlayerLifeRevision(
                            target.Session,
                            now);
                    }
                }

                var recoveryApplied = killed && source is not null &&
                    eligibility.Allowed &&
                    ApplyPlayerBurnKillRecoveryLocked(
                        source,
                        target,
                        fatalIntent,
                        eligibility,
                        now);
                commits.Add(new(
                    target,
                    source,
                    checked((int)due[0].SourceCharacterId),
                    due[0].SourceEventId,
                    killed ? fatalIntent.TickOrdinal : due[^1].TickOrdinal,
                    appliedDamage,
                    killed,
                    deathLifeRevision,
                    recoveryApplied,
                    interruption));
            }
        }

        return commits.AsReadOnly();
    }

    private GameSessionContext? FindCurrentElementalSourceLocked(
        GameSessionContext target,
        long sourceCharacterId)
    {
        if (sourceCharacterId is <= 0 or > int.MaxValue)
        {
            return null;
        }

        return _sessions.Values.FirstOrDefault(candidate =>
            candidate.WorldReady &&
            candidate.CharacterId == sourceCharacterId &&
            candidate.WorldInstanceId == target.WorldInstanceId &&
            candidate.MapId == target.MapId &&
            IsCurrentAccountSession(
                candidate.AccountId,
                candidate.Session,
                candidate.Ownership));
    }

    private bool ApplyPlayerBurnKillRecoveryLocked(
        GameSessionContext source,
        GameSessionContext target,
        ElementalPeriodicDamageIntent fatalIntent,
        PvpEligibilityResult eligibility,
        DateTimeOffset now)
    {
        if (source.CharacterId == target.CharacterId ||
            source.Character.CurrentHp <= 0 ||
            fatalIntent.SourceCharacterId != source.CharacterId ||
            fatalIntent.TargetCharacterId != target.CharacterId)
        {
            return false;
        }

        lock (source.Character.VitalsSync)
        {
            if (source.Character.CurrentHp <= 0)
            {
                return false;
            }

            var killEvent = AuthoredElementalCombatV1.CreditedKillEvent(
                fatalIntent.SourceEventId,
                source.CharacterId,
                target.CharacterId,
                source.MapId,
                fatalIntent.TickOrdinal,
                now,
                eligibility);
            var fence = new ElementalCombatSessionFence(
                source.CharacterId,
                source.MapId,
                source.Ownership);
            if (!TryProcessElementalCreditedKill(
                    source.Session,
                    fence,
                    killEvent,
                    source.Character.ElementalEquipment,
                    source.Character.CurrentHp,
                    source.Character.CurrentMp,
                    source.Character.MaxHp,
                    source.Character.MaxMp,
                    out var recovery))
            {
                return false;
            }

            var health = AdjustPvpElementalHealingReceivedLocked(
                source,
                now,
                recovery.AppliedHealth);
            var nextHealth = checked((int)Math.Min(
                source.Character.MaxHp,
                source.Character.CurrentHp + health));
            var nextMana = checked((int)Math.Min(
                source.Character.MaxMp,
                source.Character.CurrentMp + recovery.AppliedMana));
            if (nextHealth == source.Character.CurrentHp &&
                nextMana == source.Character.CurrentMp)
            {
                return false;
            }

            source.Character.CurrentHp = nextHealth;
            source.Character.CurrentMp = nextMana;
            source.Character.MarkVitalsChanged();
            return true;
        }
    }

    private async Task PublishPlayerElementalBurnAsync(
        PlayerElementalBurnCommit commit,
        CancellationToken cancellationToken)
    {
        var target = commit.Target;
        if (!WorldInstances.TryFind(target.WorldInstanceId, out var runtime))
        {
            return;
        }

        var targetWorldId = target.ObjectId;
        var currentSource = commit.Source;
        var sourceIsCurrent = currentSource is not null &&
            IsCurrentWorldSessionSnapshot(
                currentSource.Session,
                currentSource);
        foreach (var recipient in GetWorldInstanceSessions(
                     target.WorldInstanceId))
        {
            var targetId = ReferenceEquals(
                    recipient.Session,
                    target.Session)
                ? LocalPlayerObjectId
                : targetWorldId;
            try
            {
                if (sourceIsCurrent && currentSource is not null)
                {
                    var sourceId = ReferenceEquals(
                            recipient.Session,
                            currentSource.Session)
                        ? LocalPlayerObjectId
                        : currentSource.ObjectId;
                    var selector = currentSource.Character.Profession is 2 or 3
                        ? (byte)5
                        : (byte)3;
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PhysicalDamage(
                            sourceId,
                            currentSource.Character.PositionX,
                            0f,
                            currentSource.Character.PositionZ,
                            targetId,
                            commit.AppliedDamage,
                            selector,
                            (byte)CombatHitOutcome.Normal),
                        cancellationToken,
                        "PlayerElementalBurnDamage");
                }

                await TrySendWorldInstancePacketAsync(
                    runtime,
                    recipient,
                    PacketBuilder.PlayerVitalsUpdate(
                        targetId,
                        target.Character.CurrentHp,
                        target.Character.CurrentMp),
                    cancellationToken,
                    "PlayerElementalBurnVitals");
                if (sourceIsCurrent &&
                    currentSource is not null &&
                    commit.SourceRecoveryApplied)
                {
                    var recoveryId = ReferenceEquals(
                            recipient.Session,
                            currentSource.Session)
                        ? LocalPlayerObjectId
                        : currentSource.ObjectId;
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PlayerVitalsUpdate(
                            recoveryId,
                            currentSource.Character.CurrentHp,
                            currentSource.Character.CurrentMp),
                        cancellationToken,
                        "PlayerElementalBurnRecovery");
                }

                if (commit.Killed)
                {
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        PacketBuilder.PlayerDeath(
                            targetId,
                            target.Character.PositionX,
                            0f,
                            target.Character.PositionZ,
                            target.MapId),
                        cancellationToken,
                        "PlayerElementalBurnDeath");
                }
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(recipient.Session);
            }
        }
    }

    private async Task PersistPlayerElementalBurnAsync(
        PlayerElementalBurnCommit commit,
        CancellationToken cancellationToken)
    {
        try
        {
            await PersistRoutineVitalsAsync(
                commit.Target,
                cancellationToken);
            if (commit.SourceRecoveryApplied && commit.Source is { } source)
            {
                await PersistRoutineVitalsAsync(source, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                "[elemental] player Burn vitals persistence deferred " +
                $"target={commit.Target.DisplayName}: {ex.Message}");
        }
    }
}
