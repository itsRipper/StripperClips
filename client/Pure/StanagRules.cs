using System.Collections.Generic;

namespace StripperClips.Pure;

internal enum StanagItemLocation { DetachedEquipment, Unaddressed, ForeignOwner, FittedToWeapon, OutsideEquipment }

internal static class StanagRules
{
    internal const string ClipTemplate = "6738a1c0f1e2d3c4b5a69783";
    internal const string Colt30Template = "55d4887d4bdc2d962f8b4570";
    internal const int ClipCapacity = 10;

    // Double-stack 30- and 40-round prefabs whose feed lips, cartridge socket and grip height match the authored guide and hand.
    private static readonly HashSet<string> Magazines = new HashSet<string>
    {
        Colt30Template,
        "5c05413a0db834001c390617", // HK Steel Maritime
        "5c6d42cb2e2216000e69d7d1", // HK 30 polymer
        "5c6d450c2e221600114c997d", // HK PM Gen.2
        "5aaa5dfee5b5b000140293d3", // Magpul PMAG 30 GEN M3
        "5d1340b3d7ad1a0b52682ed7", // Magpul PMAG 30 GEN M3, FDE
        "55802d5f4bdc2dac148b458e", // Magpul PMAG 30 GEN M3 W
        "5d1340cad7ad1a0b0b249869", // Magpul PMAG 30 GEN M3 W, FDE
        "544a378f4bdc2d30388b4567", // Magpul PMAG 40 GEN M3
        "5d1340bdd7ad1a0e8d245aab", // Magpul PMAG 40 GEN M3, FDE
        "5c6d46132e221601da357d56", // TROY BattleMag
        "61840bedd92c473c77021635", // FN SCAR-L
        "61840d85568c120fdd2962a5", // FN SCAR-L, FDE
    };

    internal static bool SupportsMagazine(string templateId) => Magazines.Contains(templateId);

    internal static StanagItemLocation ClassifyLocation(bool hasAddress, bool owned, bool underWeapon, bool underEquipment)
    {
        if (!hasAddress) return StanagItemLocation.Unaddressed;
        if (!owned) return StanagItemLocation.ForeignOwner;
        if (underWeapon) return StanagItemLocation.FittedToWeapon;
        return underEquipment ? StanagItemLocation.DetachedEquipment : StanagItemLocation.OutsideEquipment;
    }

    internal static bool CanLoad(string clipTemplate, string magazineTemplate, bool detached,
        int clipCount, int magazineCount, int magazineCapacity) =>
        clipTemplate == ClipTemplate && SupportsMagazine(magazineTemplate) && detached &&
        clipCount <= ClipCapacity && ReloadRules.FitsWhole(clipCount, magazineCount, magazineCapacity);
}
