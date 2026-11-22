using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using StripperClips.Pure;

namespace StripperClips;

internal enum ChamberOutcome { Empty, Chambered, Failed, Pending }

internal static class CoopReload
{
    // Fika has no transaction timeout. A host that never answers would otherwise hold the reload forever.
    internal static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<TransferOutcome> Load(Player player, Weapon weapon, Magazine clip, Magazine magazine,
        IReadOnlyList<Ammo> stacks, int held, Func<bool> inRaid, Action<Task> pending)
    {
        var inventory = player.InventoryController;
        bool Owned() => inRaid() && ReferenceEquals(player.InventoryController, inventory) &&
            ReferenceEquals(clip.Owner, inventory) && ReferenceEquals(magazine.Owner, inventory) &&
            ReferenceEquals(weapon.Owner, inventory);
        if (!Owned() || !ReferenceEquals(weapon.GetCurrentMagazine(), magazine) ||
            clip.Count != held || !ReloadRules.FitsWhole(held, magazine.Count, magazine.MaxCount) ||
            !clip.Cartridges.Items.Reverse().SequenceEqual(stacks))
        {
            Report(inventory, "clip-check", clip.Cartridges, magazine.Cartridges, null,
                "raid, ownership, fitted magazine, clip contents or capacity changed");
            return TransferOutcome.Refused;
        }

        // EFT can only simulate removal of the current top stack.
        bool CanMove(Ammo stack)
        {
            if (ReferenceEquals(stack.Parent?.Container, clip.Cartridges) && magazine.CheckCompatibility(stack)) return true;
            Report(inventory, "stack-check", clip.Cartridges, magazine.Cartridges, stack, "source or compatibility changed");
            return false;
        }

        async Task<StackMoveOutcome> Move(Ammo stack, StackSlot from, StackSlot to)
        {
            var confirmed = false;
            try
            {
                if (!Owned() || !ReferenceEquals(stack.Owner, inventory) || !ReferenceEquals(from.Last, stack) ||
                    (ReferenceEquals(from, clip.Cartridges) && !ReferenceEquals(weapon.GetCurrentMagazine(), magazine)))
                {
                    Report(inventory, "Move.source", from, to, stack, "owner, top stack or fitted magazine changed");
                    return StackMoveOutcome.Rejected;
                }
                var operation = ItemManipulator.Move(stack, to.CreateItemAddress(), inventory, simulate: true);
                var refusal = operation.Failed ? operation.Error.ToString() :
                    operation.Value.ItemsToDestroy?.Any() == true ? "would destroy items" :
                    !operation.Value.CanExecute(inventory) ? "MoveResult.CanExecute declined" : null;
                if (refusal != null)
                {
                    Report(inventory, "Move.simulate", from, to, stack, refusal);
                    return StackMoveOutcome.Rejected;
                }
                var transaction = inventory.TryRunNetworkTransaction(operation);
                var (completed, result) = await Deadline.Await(transaction, TransactionTimeout);
                if (!completed)
                {
                    Report(inventory, "Move.transaction", from, to, stack, "no acknowledgement within " + TransactionTimeout.TotalSeconds + " s");
                    pending(transaction);
                    return StackMoveOutcome.Pending;
                }
                confirmed = result.Succeed;
                if (result.Failed || !ReferenceEquals(stack.Parent?.Container, to))
                    Report(inventory, "Move.transaction", from, to, stack,
                        result.Failed ? result.Error.ToString() : "success callback but destination did not change");
            }
            catch (Exception error) { Report(inventory, "Move.transaction", from, to, stack, error.Message); }

            if (!ReferenceEquals(stack.Parent?.Container, to)) return StackMoveOutcome.Rejected;
            return confirmed ? StackMoveOutcome.Moved : StackMoveOutcome.MovedWithoutConfirmation;
        }

        return await WholeClipTransfer.Run(stacks, CanMove, stack => Move(stack, clip.Cartridges, magazine.Cartridges),
            stack => Move(stack, magazine.Cartridges, clip.Cartridges));
    }

    internal static async Task<ChamberOutcome> Chamber(Player player, Weapon weapon, Action<Task> pending)
    {
        var magazine = weapon.GetCurrentMagazine();
        if (magazine == null || !ReloadRules.ChamberOnClose(weapon.ChamberAmmoCount, magazine.Count))
            return ChamberOutcome.Empty;

        var inventory = player.InventoryController;
        if (weapon.Chambers.Length != 1 || !magazine.IsAmmoCompatible(weapon.Chambers) ||
            !(magazine.Cartridges.Last is Ammo ammo)) return ChamberOutcome.Empty;
        var chamber = weapon.Chambers[0];
        var feed = Ammo.ApplyToAddress(ammo, chamber.CreateItemAddress(), 1, inventory, simulate: true);
        if (feed.Failed || !feed.Value.CanExecute(inventory))
        {
            Report(inventory, "ApplyToAddress.simulate", magazine.Cartridges, chamber, ammo,
                feed.Failed ? feed.Error.ToString() : "feed result CanExecute declined");
            return ChamberOutcome.Failed;
        }
        var transaction = inventory.TryRunNetworkTransaction(feed);
        var (completed, result) = await Deadline.Await(transaction, TransactionTimeout);
        if (!completed)
        {
            Report(inventory, "ApplyToAddress.transaction", magazine.Cartridges, chamber, ammo,
                "no acknowledgement within " + TransactionTimeout.TotalSeconds + " s");
            pending(transaction);
            return ChamberOutcome.Pending;
        }
        if (result.Failed || weapon.ChamberAmmoCount == 0)
        {
            Report(inventory, "ApplyToAddress.transaction", magazine.Cartridges, chamber, ammo,
                result.Failed ? result.Error.ToString() : "success callback but chamber is empty");
            return ChamberOutcome.Failed;
        }
        return ChamberOutcome.Chambered;
    }

    private static void Report(ItemController controller, string operation, IContainer from, IContainer to,
        Ammo? ammo, string reason) =>
        StripperReload.Log.LogWarning($"FIKA_INVENTORY operation={operation} controller={controller.GetType().Name}/{controller.ID} " +
            $"source={Address(from)} destination={Address(to)} ammo={ammo?.Id} reason={reason}");

    private static string Address(IContainer container) =>
        $"{container.ParentItem?.Id}/{container.ID}@{container.ParentItem?.Owner?.ID}";
}
