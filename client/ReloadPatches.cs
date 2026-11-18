using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using StripperClips.Framework;

namespace StripperClips;

internal static class ReloadPatches
{
    internal static Func<bool> PreferClips = () => true;

    public static void Install(PatchSet patches)
    {
        patches.Postfix(PatchSet.Only(typeof(AmmoSelector), nameof(AmmoSelector.FindAmmoForWeapon)), (FindAmmo)FindAmmoPost);
        patches.Prefix(PatchSet.Only(typeof(GamePlayerOwner), nameof(GamePlayerOwner.SwitchMagazine)), (SwitchMagazine)SwitchMagazinePre);
        patches.Prefix(PatchSet.Only(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.ReloadWithAmmo)), (ReloadWithAmmo)ReloadWithAmmoPre);

        var hands = typeof(Player.FirearmController);
        patches.Prefix(PatchSet.Method(hands, nameof(Player.FirearmController.SetTriggerPressed), typeof(bool)), (SetFlag)SetTriggerPressedPre);
        patches.Prefix(PatchSet.Method(hands, nameof(Player.FirearmController.SetAim), typeof(bool)), (SetFlag)SetAimPre);
        patches.Prefix(PatchSet.Method(hands, nameof(Player.FirearmController.ToggleAim)), (Func<Player.FirearmController, bool>)NotBusy);
        foreach (var name in new[] { nameof(Player.FirearmController.CanStartReload), nameof(Player.FirearmController.CheckChamber), nameof(Player.FirearmController.CheckAmmo),
                     nameof(Player.FirearmController.ExamineWeapon), nameof(Player.FirearmController.CheckFireMode) })
            patches.Prefix(PatchSet.Method(hands, name), (BoolQuery)RefuseWhileBusy);
        patches.Prefix(PatchSet.Method(hands, nameof(Player.FirearmController.ChangeFireMode), typeof(Weapon.EFireMode)), (BoolQuery)RefuseWhileBusy);
    }

    private static bool NotBusy(Player.FirearmController __instance) => !StripperReload.Busy(__instance);

    private delegate void FindAmmo(InventoryController controller, Weapon weapon, ref IEnumerable<Item> __result);
    private delegate bool SwitchMagazine(GamePlayerOwner __instance, Weapon weapon, Item item);
    private delegate bool ReloadWithAmmo(FirearmHandsInputTranslator __instance, Weapon weapon, ref bool __result);
    private delegate bool SetFlag(Player.FirearmController __instance, bool __0);
    private delegate bool BoolQuery(Player.FirearmController __instance, ref bool __result);

    private static void FindAmmoPost(InventoryController controller, Weapon weapon, ref IEnumerable<Item> __result)
    {
        var player = Owner(controller);
        if (player == null) return;
        var clips = StripperReload.Usable(player, weapon);
        if (clips.Count == 0) return;
        __result = __result != null ? clips.Cast<Item>().Concat(__result).ToList() : clips.Cast<Item>().ToList();
    }

    private static bool SwitchMagazinePre(GamePlayerOwner __instance, Weapon weapon, Item item)
    {
        if (!(item is Magazine clip) || !Catalog.IsClip(clip.TemplateId)) return true;
        StripperReload.TryStart(__instance.Player, clip);
        return false;
    }

    // EFT reaches this path after its magazine-swap decision.
    private static bool ReloadWithAmmoPre(FirearmHandsInputTranslator __instance, Weapon weapon, ref bool __result)
    {
        if (!PreferClips()) return true;
        var player = __instance._player;
        var clips = StripperReload.Usable(player, weapon);
        var magazine = weapon.GetCurrentMagazine();
        if (clips.Count == 0 || magazine == null) return true;
        var pick = Pure.ReloadRules.PickClip(clips.ConvertAll(c => c.Count), magazine.Count, magazine.MaxCount);
        if (pick < 0) return true;
        if (!StripperReload.TryStart(player, clips[pick])) return true;
        __result = true;
        return false;
    }

    private static bool SetTriggerPressedPre(Player.FirearmController __instance, bool __0) => !(__0 && StripperReload.Busy(__instance));

    private static bool SetAimPre(Player.FirearmController __instance, bool __0) => !(__0 && StripperReload.Busy(__instance));

    private static bool RefuseWhileBusy(Player.FirearmController __instance, ref bool __result)
    {
        if (!StripperReload.Busy(__instance)) return true;
        __result = false;
        return false;
    }

    private static Player? Owner(InventoryController controller)
    {
        var current = StripperReload.LocalPlayer;
        return current != null && ReferenceEquals(current.InventoryController, controller) ? current : null;
    }
}
