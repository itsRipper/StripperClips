using System;
using EFT;
using EFT.InventoryLogic;

namespace StripperClips.Stanag;

// A private rendering handle: never inserted into inventory or sent in a native hands packet.
internal sealed class MagazineHands : Player.UsableItemController
{
    internal Action? ReleasePresentation;

    public override void Drop(float speed, Action callback, bool fastDrop, Item? nextControllerItem = null)
    {
        Destroyed = true;
        CurrentOperation.HideWeapon(callback, fastDrop);
    }

    public override void Destroy()
    {
        ReleasePresentation?.Invoke();
        ReleasePresentation = null;
        base.Destroy();
    }
}
