using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    private static bool _coopInventoryInDoubt;
    // Timed out but possibly still running: it no longer locks the weapon, yet still bypasses the hands step.
    private static StripperReload? _pending;

    private readonly Player _player;
    private readonly Player.FirearmController _hands;
    private readonly Weapon _weapon;
    private readonly Magazine _clip;
    private readonly AmmoPack _lock;
    private readonly int _capacity, _held;
    private readonly bool _coop;
    private readonly Magazine? _fittedMagazine;
    private readonly List<Ammo> _stacks;
    private readonly TaskCompletionSource<bool>? _boltClose;
    private long _visualAction;
    private int _loaded;
    private ClipRounds? _rounds;
    private bool _locked, _settling, _raidEnded;
    private CoopCommitGate _commit;

    private StripperReload(Player player, Player.FirearmController hands, Magazine clip)
    {
        _player = player;
        _hands = hands;
        _weapon = hands.Item;
        _clip = clip;
        _stacks = Rounds(clip);
        _lock = new AmmoPack(_stacks);
        _capacity = clip.MaxCount;
        _held = clip.Count;
        _coop = FikaRuntime.IsCoop(player);
        if (_coop) _boltClose = new TaskCompletionSource<bool>();
        _fittedMagazine = _weapon.GetCurrentMagazine();
    }

    public static StripperReload? Current { get; private set; }
    public static Player? LocalPlayer { get; set; }

    public static bool Busy(Player.FirearmController hands) => Current != null && ReferenceEquals(Current._hands, hands);

    internal static bool TryGetCoopTransfer(Player player, out string clipId, out string weaponId)
    {
        var reload = Current is { _settling: true } current ? current : _pending;
        if (reload is { _coop: true, _raidEnded: false } && ReferenceEquals(reload._player, player))
        {
            clipId = reload._clip.Id.ToString();
            weaponId = reload._weapon.Id.ToString();
            return true;
        }
        clipId = weaponId = "";
        return false;
    }

    public static List<Magazine> Usable(Player player, Weapon weapon)
    {
        var result = new List<Magazine>();
        if (FikaRuntime.IsCoop(player) && (_coopInventoryInDoubt || !ReferenceEquals(player, LocalPlayer) || !FikaRuntime.CanReload(player))) return result;
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
        if (FikaRuntime.IsCoop(player) && (_coopInventoryInDoubt || !ReferenceEquals(player, LocalPlayer) || !FikaRuntime.CanReload(player))) reason = "not_local_or_fika_unavailable";
        else if (Current != null) reason = "busy";
        else if (!(player.HandsController is Player.FirearmController hands)) reason = "no_firearm";
        else if (FikaRuntime.IsCoop(player) && !hands.CanStartReload()) reason = "native_reload_unavailable";
        else if (!Usable(player, hands.Item).Contains(clip)) reason = "clip_not_usable";
        else if (!LoadPackage(OpenVariant(hands, PackageName(hands.Item)), out var package, out reason)) { }
        else
        {
            var reload = new StripperReload(player, hands, clip);
            WeaponClipAction? action = null;
            try
            {
                action = WeaponClipAction.Start(player, package!);
                reload.Bind(action);
                ClipLook.Apply(action.Prop("clip"), clip);
                reload._rounds = ClipRounds.Bind(action, clip);
                if (reload._coop)
                {
                    reload._visualAction = FikaRuntime.NextAction();
                    FikaRuntime.Notify(player, new ReloadVisual(reload._visualAction, ReloadVisualPhase.Start,
                        hands.Item.Id.ToString(), clip.Id.ToString(), clip.Count, package!.Name.EndsWith("_open", StringComparison.Ordinal)));
                }
                Log.LogInfo(TraceLine.Format("STRIPPER_START", ("weapon", hands.Item.ShortName), ("clip", clip.Count), ("magazine", hands.Item.GetCurrentMagazine()!.Count),
                    ("chamber", hands.Item.ChamberAmmoCount), ("package", package!.Name), ("props", action.Props)));
                return true;
            }
            catch (Exception error) { action?.Stop("error"); reason = error.Message; }
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
        if (_coop)
        {
            action.ContinuePlaying = CanContinueCoop;
            action.HoldAt("strip.load");
        }
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
            else if (e.Name == "strip.load")
            {
                if (_coop) LoadCoop(action);
                else Load();
            }
            else if (e.Name == "strip.chamber")
            {
                if (_coop) _boltClose!.TrySetResult(true);
                else Chamber();
            }
        };
        action.Finished += reason =>
        {
            _commit.Finish(reason != "done");
            if (_coop && _commit.Interrupted) SendPhase(ReloadVisualPhase.Cancelled);
            _boltClose?.TrySetResult(false);
            ReleaseCurrent();
            Unlock();
            Log.LogInfo(TraceLine.Format("STRIPPER_END", ("reason", reason), ("loaded", _loaded), ("clip", _clip.Count),
                ("magazine", _weapon.GetCurrentMagazine()?.Count ?? -1), ("chamber", _weapon.ChamberAmmoCount)));
        };
    }

    private void ReleaseCurrent()
    {
        if (!_commit.Finished || _settling || !ReferenceEquals(Current, this)) return;
        Current = null;
        if (_coop && _commit.Interrupted && !ReferenceEquals(_pending, this)) SendPhase(ReloadVisualPhase.Settled);
    }

    // Release the weapon without sending another transfer. The transaction can still be applied.
    private void Abandon(WeaponClipAction action, Task outstanding)
    {
        _pending = this;
        if (!_raidEnded) _coopInventoryInDoubt = true;
        Log.LogError($"Fika did not acknowledge a stripper-clip transaction within {CoopReload.TransactionTimeout.TotalSeconds} s. " +
            "The weapon is released without further transfers. Clip reloads are disabled until the raid ends.");
        action.Stop("inventory_timeout");
        SettleWhenDone(outstanding);
    }

    private async void SettleWhenDone(Task outstanding)
    {
        try { await outstanding; }
        catch (Exception error) { Log.LogWarning("Late Fika stripper reload transaction failed: " + error.Message); }
        if (!ReferenceEquals(_pending, this)) return;
        _pending = null;
        Log.LogWarning("The unacknowledged stripper-clip transaction has completed.");
        SendPhase(ReloadVisualPhase.Settled);
    }

    private void SendPhase(ReloadVisualPhase phase, bool chambered = false)
    {
        if (_visualAction != 0 && !_raidEnded)
            FikaRuntime.Notify(_player, new ReloadVisual(_visualAction, phase, _weapon.Id.ToString(), chambered: chambered));
    }

    private async void LoadCoop(WeaponClipAction action)
    {
        if (_fittedMagazine == null || !_commit.TryBegin()) return;
        _settling = true;
        Task? outstanding = null;
        try
        {
            Unlock();
            var outcome = await CoopReload.Load(_player, _weapon, _clip, _fittedMagazine, _stacks, _held,
                () => !_raidEnded && FikaRuntime.Connected?.Invoke(_player) == true, task => outstanding = task);
            if (outcome == TransferOutcome.Pending)
            {
                Abandon(action, outstanding!);
                return;
            }
            if (outcome != TransferOutcome.Committed)
            {
                if (outcome == TransferOutcome.RecoveryFailed)
                {
                    if (!_raidEnded) _coopInventoryInDoubt = true;
                    Log.LogError("Fika refused or interrupted stack recovery. Clip reloads are disabled until the raid ends.");
                }
                else Log.LogWarning("Fika stripper reload transfer: " + outcome);
                action.Stop("inventory_failed");
                return;
            }
            _loaded = _held;
            if (!CanContinueCoop()) return;
            _hands.FirearmsAnimator.SetAmmoOnMag(_fittedMagazine.Count);
            _weapon.RaiseRefreshEvent();
            SendPhase(ReloadVisualPhase.Loaded);
            action.HoldAt("strip.chamber");
            if (!await _boltClose!.Task || !CanContinueCoop()) return;

            var chamber = await CoopReload.Chamber(_player, _weapon, task => outstanding = task);
            if (chamber == ChamberOutcome.Pending)
            {
                Abandon(action, outstanding!);
                return;
            }
            if (chamber == ChamberOutcome.Failed)
            {
                action.Stop("chamber_failed");
                return;
            }
            var chambered = chamber == ChamberOutcome.Chambered;
            // Fika's host can invoke the callback before queuing the native inventory packet.
            if (chambered) await Task.Yield();
            if (!CanContinueCoop()) return;
            if (_weapon.IsBoltCatch && _hands.FirearmsAnimator.GetBoltCatch()) _hands.FirearmsAnimator.SetBoltCatch(false);
            if (chambered) RefreshChamberVisual(_hands);
            _hands.FirearmsAnimator.SetAmmoOnMag(_fittedMagazine.Count);
            _hands.FirearmsAnimator.SetAmmoInChamber(_weapon.ChamberAmmoCount);
            _weapon.RaiseRefreshEvent();
            SendPhase(ReloadVisualPhase.Closed, chambered);
            action.Resume();
        }
        catch (Exception error)
        {
            Log.LogError("Fika stripper reload failed: " + error);
            action.Stop("inventory_error");
        }
        finally
        {
            // Keep the observer context until Fika can queue any compensation packet.
            if (_commit.Interrupted) await Task.Yield();
            _settling = false;
            ReleaseCurrent();
        }
    }

    private bool CanContinueCoop() => !_raidEnded && !_commit.Interrupted && _player != null &&
        _player.HealthController.IsAlive && ReferenceEquals(_player.HandsController, _hands) &&
        ReferenceEquals(_weapon.GetCurrentMagazine(), _fittedMagazine) && FikaRuntime.Connected?.Invoke(_player) == true;

    internal static void RefreshChamberVisual(Player.FirearmController hands)
    {
        hands.Firearms.DestroyPatronInWeapon(0);
        if (hands.Item.Chambers[0].ContainedItem is Ammo round) hands.Firearms.SetRoundIntoWeapon(round, 0);
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
        _coopInventoryInDoubt = false;
        if (_pending != null) _pending._raidEnded = true;
        _pending = null;
        if (Current != null) Current._raidEnded = true;
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
        RefreshChamberVisual(_hands);
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

    internal static bool LoadPackage(string name, out ClipPackage? package, out string reason)
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
