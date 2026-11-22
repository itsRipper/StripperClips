using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using StripperClips.Framework;
using StripperClips.Pure;

namespace StripperClips.Stanag;

// One main-thread reservation covers the presentation and its inventory callbacks; observers never take one.
internal sealed class MagazineTransfer : IDisposable
{
    private static MagazineTransfer? _active;
    private readonly Player _player;
    private readonly InventoryController _inventory;
    private readonly Magazine _clip;
    private readonly Magazine _magazine;
    private readonly ItemAddress _clipAddress;
    private readonly ItemAddress _magazineAddress;
    private readonly (Item Item, int Count)[] _clipStacks;
    private readonly (Item Item, int Count)[] _magazineStacks;
    private bool _started;
    private bool _settling;
    private bool _disposed;
    private bool _inventoryInDoubt;
    private Ammo? _moving;

    private MagazineTransfer(Player player, Magazine clip, Magazine magazine)
    {
        _player = player;
        _inventory = player.InventoryController;
        _clip = clip;
        _magazine = magazine;
        _clipAddress = clip.CurrentAddress;
        _magazineAddress = magazine.CurrentAddress;
        // StackSlot enumerates bottom to top and only permits removing Last.
        _clipStacks = clip.Cartridges.Items.Reverse().Select(Snapshot).ToArray();
        _magazineStacks = magazine.Cartridges.Items.Select(Snapshot).ToArray();
    }

    internal static (string Reason, string? Detail)? GetRejection(Player player, Magazine clip, Magazine magazine)
    {
        if (!ReferenceEquals(player, StripperReload.LocalPlayer)) return ("not_local_player", null);
        if (ReferenceEquals(_active?._player, player))
            return (_active!._inventoryInDoubt ? "inventory_recovery_failed" : "transfer_reserved", null);
        if (StripperReload.Current != null) return ("rifle_reload_active", null);
        if (clip.TemplateId != StanagRules.ClipTemplate) return ("unsupported_clip", null);
        if (!StanagRules.SupportsMagazine(magazine.TemplateId)) return ("unsupported_magazine", null);
        if (clip.MaxCount != StanagRules.ClipCapacity || clip.Count > clip.MaxCount) return ("invalid_clip_capacity", null);
        if (clip.Count == 0) return ("clip_empty", null);
        if (!StanagRules.CanLoad(clip.TemplateId, magazine.TemplateId, true, clip.Count, magazine.Count, magazine.MaxCount))
            return ("whole_clip_does_not_fit", null);

        var inventory = player.InventoryController;
        var clipLocation = Location(inventory, clip);
        if (clipLocation != StanagItemLocation.DetachedEquipment) return ("clip_location", clipLocation.ToString());
        var magazineLocation = Location(inventory, magazine);
        if (magazineLocation != StanagItemLocation.DetachedEquipment) return ("magazine_location", magazineLocation.ToString());
        var clipAction = clip.CheckAction(null);
        if (clipAction.Failed) return ("clip_inventory_action", clipAction.Error.ToString());
        var magazineAction = magazine.CheckAction(null);
        if (magazineAction.Failed) return ("magazine_inventory_action", magazineAction.Error.ToString());
        foreach (var item in clip.Cartridges.Items)
        {
            if (item is not Ammo ammo) return ("invalid_clip_cartridge", item.TemplateId.ToString());
            if (!magazine.CheckCompatibility(ammo)) return ("incompatible_cartridge", ammo.TemplateId.ToString());
        }
        if (magazine.Cartridges.Items.Any(item => item is not Ammo)) return ("invalid_magazine_cartridge", null);
        return null;
    }

    // The drop handler validates immediately before reserving, on Unity's main thread.
    internal static MagazineTransfer Reserve(Player player, Magazine clip, Magazine magazine) =>
        _active = new MagazineTransfer(player, clip, magazine);

    internal static bool Blocks(Item item, ItemAddress? destination)
    {
        var active = _active;
        if (active == null || active._disposed && !active._settling || !ReferenceEquals(item.Owner, active._inventory)) return false;
        if (ReferenceEquals(item, active._moving)) return false;
        bool Reserved(Item? other) => other != null && (ReferenceEquals(other, active._clip) || ReferenceEquals(other, active._magazine));
        return Reserved(item) || Reserved(destination?.Container.ParentItem) ||
            item.GetAllParentItems().Any(Reserved) ||
            active._clip.GetAllParentItems().Any(parent => ReferenceEquals(parent, item)) ||
            active._magazine.GetAllParentItems().Any(parent => ReferenceEquals(parent, item));
    }

    internal async Task<TransferOutcome> Commit(CancellationToken cancellation)
    {
        if (_disposed || _started) return TransferOutcome.Refused;
        _started = _settling = true;
        try
        {
            // Presentation cancellation still permits compensation while the inventory is live.
            var outcome = await StanagTransfer.Run(_clipStacks, CanContinue, InventoryLive, IsExpected,
                stack => _magazine.CheckCompatibility((Ammo)stack.Item),
                stack => Move((Ammo)stack.Item, _clip.Cartridges, _magazine.Cartridges),
                stack => Move((Ammo)stack.Item, _magazine.Cartridges, _clip.Cartridges));
            _inventoryInDoubt = outcome == TransferOutcome.RecoveryFailed;
            if (_inventoryInDoubt) StripperReload.Log.LogError("STANAG inventory recovery failed. Magazine strip loading is disabled for this player until the raid ends.");
            if (outcome != TransferOutcome.Refused && InventoryLive())
            {
                _clip.RaiseRefreshEvent(true);
                _magazine.RaiseRefreshEvent(true);
            }
            return outcome;
        }
        finally
        {
            _settling = false;
            if (_disposed && !_inventoryInDoubt) Release();
        }

        bool CanContinue() => !_disposed && !cancellation.IsCancellationRequested && InventoryLive() &&
            _player.HealthController.IsAlive && FikaRuntime.CanReload(_player) &&
            Equals(_clip.CurrentAddress, _clipAddress) && Equals(_magazine.CurrentAddress, _magazineAddress) &&
            OwnedAndDetached(_inventory, _clip) && OwnedAndDetached(_inventory, _magazine);
    }

    private async Task<StackMoveOutcome> Move(Ammo ammo, StackSlot from, StackSlot to)
    {
        if (!ReferenceEquals(from.Last, ammo)) return RejectMove("source_stack_changed");
        if (!ReferenceEquals(ammo.Owner, _inventory)) return RejectMove("source_owner_changed");

        var acknowledged = false;
        _moving = ammo;
        try
        {
            var operation = ItemManipulator.Move(ammo, to.CreateItemAddress(), _inventory, simulate: true);
            if (operation.Failed) return RejectMove("native_move_refused", operation.Error.ToString());
            if (operation.Value.ItemsToDestroy?.Any() == true) return RejectMove("native_move_would_destroy_items");
            if (!operation.Value.CanExecute(_inventory)) return RejectMove("native_move_cannot_execute");
            var result = await _inventory.TryRunNetworkTransaction(operation);
            acknowledged = result.Succeed;
            if (!acknowledged) StripperReload.Log.LogWarning($"STANAG inventory move {ammo.Id} failed: {result.Error}");
        }
        catch (Exception error) { StripperReload.Log.LogWarning($"STANAG inventory move {ammo.Id} failed: {error}"); }
        finally { _moving = null; }

        if (!ReferenceEquals(ammo.CurrentAddress?.Container, to))
            return acknowledged ? RejectMove("acknowledged_stack_missing_from_destination") : StackMoveOutcome.Rejected;
        return acknowledged ? StackMoveOutcome.Moved : StackMoveOutcome.MovedWithoutConfirmation;

        StackMoveOutcome RejectMove(string reason, string? detail = null)
        {
            StripperReload.Log.LogWarning(TraceLine.Format("STANAG_MOVE_REJECTED", ("reason", reason), ("detail", detail),
                ("ammo", ammo.TemplateId), ("from", from.ParentItem?.Id), ("to", to.ParentItem?.Id)));
            return StackMoveOutcome.Rejected;
        }
    }

    private bool IsExpected(int moved) =>
        OwnedAndDetached(_inventory, _clip) && OwnedAndDetached(_inventory, _magazine) &&
        _clip.Cartridges.Items.Select(Snapshot).SequenceEqual(_clipStacks.Skip(moved).Reverse()) &&
        _magazine.Cartridges.Items.Select(Snapshot).SequenceEqual(_magazineStacks.Concat(_clipStacks.Take(moved)));

    private bool InventoryLive() => ReferenceEquals(_player, StripperReload.LocalPlayer) &&
        ReferenceEquals(_player.InventoryController, _inventory) &&
        (!FikaRuntime.IsCoop(_player) || FikaRuntime.Connected?.Invoke(_player) == true);

    private static bool OwnedAndDetached(InventoryController inventory, Magazine magazine) =>
        Location(inventory, magazine) == StanagItemLocation.DetachedEquipment;

    private static StanagItemLocation Location(InventoryController inventory, Magazine magazine)
    {
        var hasAddress = magazine.CurrentAddress != null;
        var owned = ReferenceEquals(magazine.Owner, inventory);
        var underWeapon = false;
        var underEquipment = false;
        // IsAtReachablePlace validates items that can be equipped in hands. Storage magazines are never eligible.
        if (owned)
        {
            foreach (var parent in magazine.GetAllParentItems())
            {
                underWeapon |= parent is Weapon;
                underEquipment |= ReferenceEquals(parent, inventory.Inventory.Equipment);
            }
        }
        return StanagRules.ClassifyLocation(hasAddress, owned, underWeapon, underEquipment);
    }

    private static (Item Item, int Count) Snapshot(Item item) => (item, item.StackObjectsCount);

    public void Dispose()
    {
        _disposed = true;
        if (!_settling && !_inventoryInDoubt) Release();
    }

    internal static void EndRaid()
    {
        _active?.Dispose();
        _active = null;
    }

    private void Release()
    {
        if (ReferenceEquals(_active, this)) _active = null;
    }
}
