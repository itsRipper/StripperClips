using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using StripperClips.Framework;
using StripperClips.Pure;

namespace StripperClips;

internal sealed class StripperReload
{
    private static readonly Dictionary<string, ClipPackage> Packages = new Dictionary<string, ClipPackage>();
    internal static ManualLogSource Log = null!;
    internal static string ClipsFolder = "";

    private readonly Player _player;
    private readonly Player.FirearmController _hands;
    private readonly Weapon _weapon;
    private readonly Magazine _clip;
    private readonly AmmoPack _lock;
    private readonly int _capacity, _held;
    private int _loaded;
    private ClipRounds? _rounds;
    private bool _locked;

    private StripperReload(Player player, Player.FirearmController hands, Magazine clip)
    {
        _player = player;
        _hands = hands;
        _weapon = hands.Item;
        _clip = clip;
        _lock = new AmmoPack(Rounds(clip));
        _capacity = clip.MaxCount;
        _held = clip.Count;
    }

    public static StripperReload? Current { get; private set; }
    public static Player? LocalPlayer { get; set; }

    public static bool Busy(Player.FirearmController hands) => Current != null && ReferenceEquals(Current._hands, hands);

    public static List<Magazine> Usable(Player player, Weapon weapon)
    {
        var result = new List<Magazine>();
        var magazine = weapon.GetCurrentMagazine();
        if (magazine == null || !Catalog.PackageFor.ContainsKey(weapon.TemplateId)) return result;
        if (!ReloadRules.CanStart(weapon.SupportsInternalReload, weapon.MalfState.State != Weapon.EMalfunctionState.None, 1, magazine.Count, magazine.MaxCount)) return result;
        if (GuideBlocked(weapon)) return result;
        var controller = player.InventoryController;
        controller.GetReachableItemsOfTypeNonAlloc(result, clip => Catalog.MagazinesFor.TryGetValue(clip.TemplateId, out var targets) && targets.Contains(magazine.TemplateId)
            && ReloadRules.FitsWhole(clip.Count, magazine.Count, magazine.MaxCount) && controller.Examined(clip) && clip.CheckAction(null).Succeeded && Compatible(clip, magazine));
        return result;
    }

    private static bool GuideBlocked(Weapon weapon)
    {
        foreach (var mod in weapon.Mods)
        {
            if (Catalog.BlocksClipGuide.Contains(mod.TemplateId)) return true;
            if (Catalog.BlocksClipGuideWhenLoaded.Contains(mod.TemplateId))
                foreach (var slot in mod.AllSlots) if (slot.ContainedItem != null) return true;
        }
        return false;
    }

    public static bool TryStart(Player player, Magazine clip)
    {
        string reason;
        if (Current != null) reason = "busy";
        else if (!(player.HandsController is Player.FirearmController hands)) reason = "no_firearm";
        else if (!Usable(player, hands.Item).Contains(clip)) reason = "clip_not_usable";
        else if (!LoadPackage(OpenVariant(hands, PackageName(hands.Item)), out var package, out reason)) { }
        else
        {
            var reload = new StripperReload(player, hands, clip);
            try
            {
                var action = WeaponClipAction.Start(player, package!);
                reload.Bind(action);
                ClipLook.Apply(action.Prop("clip"), clip);
                reload._rounds = ClipRounds.Bind(action, clip);
                Log.LogInfo(TraceLine.Format("STRIPPER_START", ("weapon", hands.Item.ShortName), ("clip", clip.Count), ("magazine", hands.Item.GetCurrentMagazine()!.Count),
                    ("chamber", hands.Item.ChamberAmmoCount), ("package", package!.Name), ("props", action.Props)));
                return true;
            }
            catch (Exception error) { reason = error.Message; }
        }
        Log.LogInfo(TraceLine.Format("STRIPPER_REFUSED", ("reason", reason)));
        return false;
    }

    private static string PackageName(Weapon weapon) => Catalog.PackageFor[weapon.TemplateId];

    private static string OpenVariant(Player.FirearmController hands, string name)
    {
        if (!hands.Item.IsBoltCatch || !hands.FirearmsAnimator.GetBoltCatch()) return name;
        var open = name + "_open";
        return File.Exists(Path.Combine(ClipsFolder, open + ".clip.json")) ? open : name;
    }

    private void Bind(WeaponClipAction action)
    {
        Current = this;
        _lock.LockItems();
        _locked = true;
        // Top-align the cartridges. Part 0 of the clip prop is its frame.
        Parts(action, ("rounds", _capacity - _held, _capacity), ("clip", 0, 1));
        action.EventFired += e =>
        {
            if (e.Name == "prop.show" || e.Name == "strip.load") Log.LogDebug(TraceLine.Format("STRIPPER_PROPS", ("event", e.Name), ("time", action.Time), ("props", action.Props)));
            // Hide the lowest rounds as they enter the SKS double-stack magazine.
            if (e.Name == "strip.sink" && int.TryParse(e.Value, out var sunk)) Parts(action, ("rounds", _capacity - _held + sunk, _capacity));
            else if (e.Name == "strip.load") Load();
            else if (e.Name == "strip.chamber") Chamber();
        };
        action.Finished += reason =>
        {
            Current = null;
            Unlock();
            Log.LogInfo(TraceLine.Format("STRIPPER_END", ("reason", reason), ("loaded", _loaded), ("clip", _clip.Count),
                ("magazine", _weapon.GetCurrentMagazine()?.Count ?? -1), ("chamber", _weapon.ChamberAmmoCount)));
        };
    }

    // Unlock before moving rounds because AmmoPack.UnlockItems reads their current parent.
    private void Unlock()
    {
        if (!_locked) return;
        _locked = false;
        _lock.UnlockItems();
    }

    public static void Reset()
    {
        Current?.Unlock();
        Current?._rounds?.Release();
        Current = null;
    }

    private void Load()
    {
        Unlock();
        var magazine = _weapon.GetCurrentMagazine();
        var controller = _player.InventoryController;
        if (magazine == null || !ReferenceEquals(_clip.Owner, controller)) { Log.LogInfo("STRIPPER_LOAD skipped=clip_or_magazine_gone"); return; }
        var count = _clip.Count;
        if (!ReloadRules.FitsWhole(count, magazine.Count, magazine.MaxCount))
        {
            Log.LogWarning("STRIPPER_LOAD skipped=clip_no_longer_fits");
            return;
        }
        var pack = new AmmoPack(Rounds(_clip));
        for (var i = 0; i < count; i++)
        {
            var result = pack.LoadAmmo(controller, controller, magazine.Cartridges.CreateItemAddress());
            if (result.Failed) { Log.LogError(TraceLine.Format("STRIPPER_LOAD_FAILED", ("round", i), ("error", result.Error.ToString()))); break; }
            result.Value.RaiseEvents(controller, CommandStatus.Begin);
            result.Value.RaiseEvents(controller, CommandStatus.Succeed);
            _loaded++;
        }
        _hands.FirearmsAnimator.SetAmmoOnMag(magazine.Count);
        _weapon.RaiseRefreshEvent();
        Log.LogInfo(TraceLine.Format("STRIPPER_LOAD", ("loaded", _loaded), ("clip", _clip.Count), ("magazine", magazine.Count)));
    }

    private void Chamber()
    {
        // Release the catch as EFT does when chambering. Its animator layer holds the bolt open and blocks firing.
        if (_weapon.IsBoltCatch && _hands.FirearmsAnimator.GetBoltCatch()) _hands.FirearmsAnimator.SetBoltCatch(false);
        var magazine = _weapon.GetCurrentMagazine();
        if (magazine == null || !ReloadRules.ChamberOnClose(_weapon.ChamberAmmoCount, magazine.Count) || !magazine.IsAmmoCompatible(_weapon.Chambers)) return;
        var result = magazine.Cartridges.PopTo(_player.InventoryController, _weapon.Chambers[0].CreateItemAddress());
        if (result.Failed) { Log.LogError(TraceLine.Format("STRIPPER_CHAMBER_FAILED", ("error", result.Error.ToString()))); return; }
        // EFT replaces the visual round before updating the animator counts.
        _hands.Firearms.DestroyPatronInWeapon(0);
        if (_weapon.Chambers[0].ContainedItem is Ammo round) _hands.Firearms.SetRoundIntoWeapon(round, 0);
        _hands.FirearmsAnimator.SetAmmoOnMag(magazine.Count);
        _hands.FirearmsAnimator.SetAmmoInChamber(_weapon.ChamberAmmoCount);
        _weapon.RaiseRefreshEvent();
        Log.LogInfo(TraceLine.Format("STRIPPER_CHAMBER", ("magazine", magazine.Count), ("chamber", _weapon.ChamberAmmoCount)));
    }

    private static void Parts(WeaponClipAction action, params (string prop, int from, int to)[] parts)
    {
        foreach (var (prop, from, to) in parts)
            if (!action.ShowParts(prop, from, to)) Log.LogWarning(TraceLine.Format("STRIPPER_PARTS_MISSING", ("prop", prop)));
    }

    private static List<Ammo> Rounds(Magazine clip)
    {
        var rounds = new List<Ammo>();
        foreach (var item in clip.Cartridges.Items) if (item is Ammo ammo) rounds.Add(ammo);
        rounds.Reverse();
        return rounds;
    }

    private static bool Compatible(Magazine clip, Magazine magazine)
    {
        foreach (var item in clip.Cartridges.Items)
            if (!(item is Ammo ammo) || !magazine.CheckCompatibility(ammo)) return false;
        return true;
    }

    private static bool LoadPackage(string name, out ClipPackage? package, out string reason)
    {
        reason = "";
        if (Packages.TryGetValue(name, out package)) return true;
        var sidecar = Path.Combine(ClipsFolder, name + ".clip.json");
        if (!File.Exists(sidecar)) { reason = "no_package " + sidecar; return false; }
        try { package = ClipPackage.Load(sidecar); }
        catch (Exception error) { reason = "package_load " + error.Message; return false; }
        Packages[name] = package;
        return true;
    }

    public static void Unload()
    {
        Reset();
        foreach (var package in Packages.Values) package.Dispose();
        Packages.Clear();
    }
}
