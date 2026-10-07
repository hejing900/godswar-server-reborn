using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleBreakItemAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: no active character");
            return;
        }

        if (!IsHostileStatusItemUseAllowed(DateTimeOffset.UtcNow))
        {
            return;
        }

        if (!TryReadBreakItemEquip(
                packet.Payload,
                out var sourceSlot))
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: payload does not " +
                "contain a valid bag page/index");
            return;
        }

        var itemId = KitBagSlots.GetItemId(
            _character.KitBag,
            sourceSlot);

        if (await TryOpenQuestScrollAsync(sourceSlot, cancellationToken)) return;

        if (packet.ClientOperationId is { } operationId)
        {
            if (itemId == PetItemCatalog.PackedSealJade)
            {
                await HandleDurablePetManagerUtilityAsync(
                    PetCommandOperationIdentity.SecureClient(operationId),
                    PetManagerUtilityOperation.Unseal,
                    sourceSlot,
                    cancellationToken);
                return;
            }
            await HandleDurableBagItemActivationAsync(
                PetCommandOperationIdentity.SecureClient(operationId),
                sourceSlot,
                cancellationToken);
            return;
        }

        var isPetEgg =
            RequirePetContent().TryGetSpeciesByEggItemId(itemId, out _);
        var isPetShedExpansion = itemId == PetItemCatalog.SpecialPetShed;
        var isPetSkillCellItem = itemId is
            PetItemCatalog.PetEnhanceSpring or
            PetItemCatalog.GoldenAppleJuice;
        var isPetExperienceItem =
            PetExperienceItemPolicy.IsMorningDew(itemId);
        var isPetExperienceBoostPotion =
            IsReviewedPetExperienceBoostPotion(itemId);
        var isExperienceBoostPotion =
            ExperienceBoostPotionPolicy.IsReviewedItem(itemId);
        var isPetCareItem =
            PetCareItemPolicy.IsReviewedItem(itemId);
        var isReviewedPetSkillBook =
            PetSkillBookActivationPolicy.IsReviewedItem(itemId);
        var isReviewedPlayerSkillBook =
            _gameplayCatalogs.Content.SkillBooks.Any(
                book => book.ItemId == itemId);
        // Boss sacks are ordinary consumed bag items; the durable activation
        // path owns their reward draw, so they are classified here rather than
        // falling through to the equipment-only rejection.
        var isWonderlandSack =
            WonderlandSackRewardPolicy.IsSack(itemId);
        var isEquipment =
            EquipmentSlots.TryGetAuthoritativeSlot(
                RequireItemContent().Templates,
                itemId,
                out var authoritativeEquipmentSlot);

        if (_session.IsSecure)
        {
            // Opcode 10051 is shared by right-click equipment activation and
            // pet-egg hatching. The secure shim identifies the operation as
            // a generic bag-item activation; the server then classifies the
            // locked authoritative item. A tokenless secure request is an
            // identity downgrade and must never reach compatibility logic.
            // Validated local raw traffic may still use the equipment-only
            // compatibility path below while the legacy transport remains.
            Console.WriteLine(
                "[equip-re] BreakItem ignored: operation identity is " +
                "ambiguous between equipment activation and pet hatch");
            if (isEquipment)
            {
                await SendEquipRejectionRefreshAsync(
                    requestedEquipmentSlot: -1,
                    resolvedEquipmentSlot:
                        authoritativeEquipmentSlot,
                    bagSlot: sourceSlot,
                    cancellationToken);
            }
            else
            {
                await SendKitBagRefreshAsync(cancellationToken);
            }

            return;
        }

        var isPackedSealJade = itemId == PetItemCatalog.PackedSealJade;
        if (isPetEgg || isPetShedExpansion || isPetSkillCellItem ||
            isPetExperienceItem || isPetExperienceBoostPotion ||
            isExperienceBoostPotion || isPetCareItem ||
            isReviewedPetSkillBook ||
            isReviewedPlayerSkillBook ||
            isWonderlandSack ||
            isPackedSealJade)
        {
                if (!AllowLegacyPlayerMutationFallback(
                    isPackedSealJade
                        ? "pet_unseal"
                        : isPetEgg
                        ? "pet_egg_hatch"
                        : isPetShedExpansion
                            ? "pet_shed_expand"
                            : isPetSkillCellItem
                                ? "pet_skill_cell_advance"
                                : isPetExperienceItem
                                    ? "pet_experience_item"
                                    : isPetExperienceBoostPotion
                                        ? "pet_experience_boost_potion"
                                        : isPetCareItem
                                            ? "pet_care_item"
                                            : isExperienceBoostPotion
                                            ? "experience_boost_potion"
                                            : isWonderlandSack
                                            ? "wonderland_sack_open"
                                            : isReviewedPlayerSkillBook
                                        ? "player_skill_book_learn"
                                    : "pet_skill_book_learn"))
            {
                return;
            }

            var identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
            if (isPackedSealJade)
            {
                await HandleDurablePetManagerUtilityAsync(
                    identity,
                    PetManagerUtilityOperation.Unseal,
                    sourceSlot,
                    cancellationToken);
            }
            else
            {
                await HandleDurableBagItemActivationAsync(
                    identity,
                    sourceSlot,
                    cancellationToken,
                    executionConstraint: isReviewedPlayerSkillBook
                        ? BagItemActivationExecutionConstraint
                            .PlayerSkillBookOnly
                        : BagItemActivationExecutionConstraint.None);
            }
            return;
        }

        if (!isEquipment)
        {
            Console.WriteLine(
                $"[equip-re] BreakItem ignored: sourceSlot={sourceSlot} " +
                $"item={itemId} is not genuine equipment");
            return;
        }

        await HandleEquipItemAsync(
            sourceSlot,
            requestedEquipmentSlot: -1,
            itemIdHint: 0,
            cancellationToken);
    }

    /// <summary>
    /// Whether the equipped bag slot holds one of the five reviewed
    /// pet-experience potions. The narrow reviewed item set is read without the
    /// pinned template, exactly as <c>IsMorningDew</c> does, so the
    /// compatibility classifier cannot throw on an unknown ID.
    /// </summary>
    private static bool IsReviewedPetExperienceBoostPotion(uint itemId) =>
        itemId is 4529 or 4530 or 4531 or 4532 or 4533 or 4540;
}
