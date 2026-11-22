using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Communications;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using StripperClips.Framework;
using StripperClips.Pure;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StripperClips.Stanag;

internal sealed class MagazineLoad
{
    private static MagazineLoad? _current;
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly Player _player;
    private readonly Player.AbstractHandsController _hands;
    private readonly MagazineTransfer _transfer;
    private readonly long _action;
    private MagazinePresentation? _view;
    private StanagVisual? _pendingStart;
    private bool _committed;
    private bool _handoff;
    private bool _started;
    private bool _ended;
    private float _deadline;

    private MagazineLoad(Player player, MagazineTransfer transfer)
    {
        _player = player;
        _hands = player.HandsController;
        _transfer = transfer;
        _action = FikaRuntime.NextAction();
        _deadline = Time.unscaledTime + 15f;
    }

    internal static void Install(PatchSet patches)
    {
        patches.Prefix(PatchSet.Only(typeof(ItemView), nameof(ItemView.ProcessItem)), (Drop)BeforeDrop);
        patches.Postfix(PatchSet.Only(typeof(GridView), nameof(GridView.CanAccept)), (CanDrop)AfterCanDrop);
        patches.Postfix(PatchSet.Only(typeof(SlotView), nameof(SlotView.CanAccept)), (CanDrop)AfterCanDrop);
        patches.Postfix(PatchSet.Only(typeof(Item), nameof(Item.CheckAction)), (CheckAction)AfterCheckAction);
        patches.Prefix(PatchSet.Only(typeof(Player.PlayerInventoryController), nameof(Player.PlayerInventoryController.LoadMagazine)), (LoadNative)BeforeNativeLoad);
        patches.Prefix(PatchSet.Only(typeof(Player.PlayerInventoryController), nameof(Player.PlayerInventoryController.UnloadMagazine)), (UnloadNative)BeforeNativeUnload);
        patches.Prefix(PatchSet.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetTriggerPressed), typeof(bool)), (Trigger)BeforeTrigger);
        patches.Prefix(PatchSet.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetAim), typeof(bool)), (Trigger)BeforeTrigger);
        patches.Prefix(PatchSet.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ToggleAim)), (Func<Player.FirearmController, bool>)BeforeToggleAim);
        patches.Prefix(PatchSet.Only(typeof(GamePlayerOwner), nameof(GamePlayerOwner.SwitchMagazine)), (SwitchMag)BeforeSwitchMagazine);
        patches.Postfix(PatchSet.Only(typeof(GamePlayerOwner), nameof(GamePlayerOwner.CanTranslateCommand)), (Input)AfterInput);
        patches.Prefix(PatchSet.Method(typeof(Player), nameof(Player.DropItemDead), typeof(Item), typeof(GameObject)),
            (Func<Player, bool>)BeforeCorpseItem);
        patches.Prefix(PatchSet.Method(typeof(Player), nameof(Player.VisualPass)), (Action<Player>)BeforeVisualPass, HarmonyLib.Priority.First);
        patches.Postfix(PatchSet.Method(typeof(Player), nameof(Player.VisualPass)), (Action<Player>)AfterVisualPass, HarmonyLib.Priority.Last);
    }

    private delegate bool Drop(DragItemContext __0, PointerEventData __1);
    private delegate void CanDrop(DragItemContext __0, ItemContext? __1, ref bool __result);
    private delegate void CheckAction(Item __instance, ItemAddress? __0, ref Option __result);
    private delegate bool LoadNative(Ammo __0, Magazine __1, ref Task<IResult> __result);
    private delegate bool UnloadNative(Magazine __0, ref Task<IResult> __result);
    private delegate bool Trigger(Player.FirearmController __instance, bool __0);
    private delegate bool SwitchMag(GamePlayerOwner __instance);
    private delegate void Input(GamePlayerOwner __instance, ECommand __0, ref bool __result);

    private static void AfterCanDrop(DragItemContext __0, ItemContext? __1, ref bool __result)
    {
        if (__0.Item is not Magazine clip || clip.TemplateId != StanagRules.ClipTemplate ||
            __1?.Item is not Magazine magazine || !StanagRules.SupportsMagazine(magazine.TemplateId) ||
            StripperReload.LocalPlayer is not Player player) return;
        __result = GetRejection(player, clip, magazine, __1) == null;
    }

    private static bool BeforeDrop(DragItemContext __0, PointerEventData __1)
    {
        if (__0.Item is not Magazine clip || clip.TemplateId != StanagRules.ClipTemplate ||
            StripperReload.LocalPlayer is not Player player) return true;
        var target = __1.pointerEnter?.GetComponentInParent<ItemView>()?.ItemContext;
        // A clip dropped on empty space or another clip still uses normal inventory movement.
        if (target?.Item is not Magazine magazine || magazine.TemplateId == StanagRules.ClipTemplate) return true;
        __0.DragCancelled();
        if (GetRejection(player, clip, magazine, target) is { } rejection)
        {
            StripperReload.Log.LogWarning(TraceLine.Format("STANAG_DROP_REJECTED", ("reason", rejection.Reason), ("detail", rejection.Detail),
                ("clip", clip.TemplateId), ("clip_rounds", $"{clip.Count}/{clip.MaxCount}"), ("clip_container", Address(clip)),
                ("magazine", magazine.TemplateId), ("magazine_rounds", $"{magazine.Count}/{magazine.MaxCount}"), ("magazine_container", Address(magazine))));
            NotificationManager.DisplayWarningNotification(rejection.Reason switch
            {
                "clip_empty" => "The stripper clip is empty.",
                "whole_clip_does_not_fit" => "The magazine has no room for every round in this clip.",
                "unsupported_magazine" => "This magazine does not support stripper-clip loading.",
                "clip_location" or "magazine_location" => "The clip and detached magazine must be in your carried inventory.",
                "incompatible_cartridge" => "The magazine does not accept all ammunition in this clip.",
                "fika_not_ready" => "Stripper-clip loading is unavailable until all Fika peers are ready.",
                _ => "Stripper-clip loading is unavailable right now. See the BepInEx log for the reason."
            });
            return false;
        }
        var transfer = MagazineTransfer.Reserve(player, clip, magazine);
        var action = _current = new MagazineLoad(player, transfer);
        action.Prepare(clip, magazine);
        return false;
    }

    private static (string Reason, string? Detail)? GetRejection(Player player, Magazine clip, Magazine magazine, ItemContext target)
    {
        if (!target.Searched) return ("target_unsearched", null);
        if (!target.ModificationAvailable) return ("target_modification_unavailable", null);
        if (_current != null) return ("stanag_action_active", null);
        if (!player.IsInventoryOpened) return ("inventory_closed", null);
        if (!player.HealthController.IsAlive) return ("player_dead", null);
        if (player.IsSprintEnabled) return ("player_sprinting", null);
        if (player.InventoryController is Player.PlayerInventoryController { _loadProcess: not null } inventory)
            return ("native_magazine_process_active", inventory._loadProcess.GetType().Name);
        if (player.HandsController is Player.FirearmController gun)
        {
            if (gun.IsInReloadOperation()) return ("firearm_reloading", null);
            if (gun.IsInSpawnOperation()) return ("firearm_equipping", null);
            if (gun.IsInRemoveOperation()) return ("firearm_unequipping", null);
        }
        else if (player.HandsController is not Player.EmptyHandsController)
            return ("unsupported_hands_controller", player.HandsController?.GetType().Name);
        if (!FikaRuntime.CanLoadStanag(player)) return ("fika_not_ready", null);
        return MagazineTransfer.GetRejection(player, clip, magazine);
    }

    private static string? Address(Item item)
    {
        var container = item.CurrentAddress?.Container;
        return container == null ? null : $"{container.GetType().Name}:{container.ID}/{container.ParentItem?.TemplateId}";
    }

    private async void Prepare(Magazine clip, Magazine magazine)
    {
        try
        {
            // Let EFT finish disposing the drag context before closing the screen.
            await Task.Yield();
            if (_ended) return;
            var rounds = MagazinePresentation.ReadRounds(clip);
            var view = await MagazinePresentation.Prepare(_player, magazine, clip, rounds, RestoreLocalHands, _cancellation.Token);
            if (_ended) { view?.Dispose(); return; }
            _view = view;
            var rejection = view == null ? "presentation_unavailable" :
                !_player.IsInventoryOpened ? "inventory_closed_during_preparation" :
                !ReferenceEquals(_hands, _player.HandsController) ? "hands_controller_changed" :
                !FikaRuntime.CanLoadStanag(_player) ? "fika_not_ready" : null;
            if (rejection != null) { Abort(rejection); return; }
            var owner = UnityEngine.Object.FindObjectOfType<EftGamePlayerOwner>();
            if (owner == null || !ReferenceEquals(owner.Player, _player)) { Abort("local_inventory_screen_owner_missing"); return; }
            if (_hands is Player.FirearmController firearm)
            {
                firearm.SetTriggerPressed(false);
                firearm.SetAim(false);
            }
            view!.Started += Started;
            view.CommitRequested += Commit;
            view.Failed += () => Abort("usable_controller_failed");
            view.Finished += () => End(completed: true);
            _pendingStart = new StanagVisual(_action, StanagPhase.Start, magazine.Id.ToString(), clip.Id.ToString(),
                rounds.Select(ammo => ammo.TemplateId.ToString()).ToArray());
            _deadline = Time.unscaledTime + 3f;
            owner.CloseInventoryIfOpen();
        }
        catch (Exception error)
        {
            StripperReload.Log.LogWarning("Detached magazine presentation could not start: " + error);
            End();
        }
    }

    private async void Commit()
    {
        try
        {
            var result = await _transfer.Commit(_cancellation.Token);
            StripperReload.Log.LogInfo(TraceLine.Format("STANAG_TRANSFER", ("action", _action), ("outcome", result)));
            if (_ended) return;
            if (result == TransferOutcome.Committed)
            {
                _committed = true;
                _view?.Confirm();
                FikaRuntime.NotifyStanag(_player, new StanagVisual(_action, StanagPhase.Committed));
            }
            else { NotificationManager.DisplayWarningNotification("Stripper clip loading was interrupted."); End(); }
        }
        catch (Exception error) { StripperReload.Log.LogWarning("Detached magazine transfer failed: " + error); End(); }
    }

    private static void RestoreLocalHands(Player actor, Item? previous)
    {
        // The weapon may have left equipment while the magazine was in hands.
        if (previous is Weapon weapon && ReferenceEquals(weapon.Owner, actor.InventoryController) &&
            weapon.IsChildOf(actor.InventoryController.Inventory.Equipment))
        {
            actor.Proceed(weapon, result =>
            {
                if (result.Failed && actor.HealthController.IsAlive && actor.HandsController == null)
                    actor.SetEmptyHands(_ => { });
            });
        }
        else actor.SetEmptyHands(_ => { });
    }

    private void Started()
    {
        if (_ended || _pendingStart is not StanagVisual start) return;
        _started = true;
        StripperReload.Log.LogInfo(TraceLine.Format("STANAG_START", ("action", start.Action), ("clip", start.ClipId),
            ("magazine", start.MagazineId), ("rounds", start.Ammunition?.Length)));
        FikaRuntime.NotifyStanag(_player, start);
    }

    private static void BeforeVisualPass(Player __instance)
    {
        var player = __instance;
        var action = _current;
        if (action == null || !ReferenceEquals(player, action._player)) return;
        var interruption = !player.HealthController.IsAlive ? "player_dead" :
            (action._handoff ? action._view?.Valid != true : !ReferenceEquals(player.HandsController, action._hands))
                ? "hands_controller_changed" :
            player.IsSprintEnabled ? "player_sprinting" :
            Time.unscaledTime > action._deadline ? (action._handoff ? "playback_timed_out" : "start_timed_out") :
            action._handoff && player.IsInventoryOpened ? "inventory_reopened" :
            !FikaRuntime.CanLoadStanag(player) ? "fika_not_ready" : null;
        if (interruption != null) { action.Abort(interruption); return; }
        try
        {
            if (!action._handoff)
            {
                if (player.IsInventoryOpened || action._pendingStart == null) return;
                action._handoff = true;
                action._deadline = Time.unscaledTime + 15f;
                action._view!.Play(0f, false);
            }
            action._view?.Tick(Time.deltaTime);
        }
        catch (Exception error) { StripperReload.Log.LogWarning("Detached magazine playback stopped: " + error); action.End(); }
    }

    private static void AfterVisualPass(Player __instance)
    {
        var action = _current;
        if (action != null && ReferenceEquals(__instance, action._player)) action._view?.TraceFrame();
    }

    private static bool BeforeCorpseItem(Player __instance)
    {
        if (__instance.HandsController is not MagazineHands) return true;
        __instance.DestroyController();
        return false;
    }

    private static void AfterCheckAction(Item __instance, ItemAddress? __0, ref Option __result)
    {
        if (__result.Succeeded && MagazineTransfer.Blocks(__instance, __0))
            __result = new StringError("Magazine strip loading is in progress.");
    }

    private static bool BeforeNativeLoad(Ammo __0, Magazine __1, ref Task<IResult> __result) => BeforeNativeUnload(__1, ref __result);

    private static bool BeforeNativeUnload(Magazine __0, ref Task<IResult> __result)
    {
        if (!MagazineTransfer.Blocks(__0, null)) return true;
        __result = Task.FromResult<IResult>(new FailedResult("Magazine strip loading is in progress."));
        return false;
    }

    private static bool BeforeTrigger(Player.FirearmController __instance, bool __0) =>
        !__0 || !ReferenceEquals(_current?._hands, __instance);

    private static bool BeforeToggleAim(Player.FirearmController __instance) => !ReferenceEquals(_current?._hands, __instance);

    private static bool BeforeSwitchMagazine(GamePlayerOwner __instance) => !ReferenceEquals(_current?._player, __instance.Player);

    private static void AfterInput(GamePlayerOwner __instance, ECommand __0, ref bool __result)
    {
        if (_current is not MagazineLoad action || !action._handoff || !ReferenceEquals(action._player, __instance.Player)) return;
        if (__0 == ECommand.ToggleInventory || __0 == ECommand.Escape) action.End();
        else if (__0 is ECommand.ReloadWeapon or ECommand.QuickReloadWeapon or ECommand.UnloadMagazine or
            ECommand.NextMagazine or ECommand.PreviousMagazine) __result = false;
    }

    internal static void EndRaid() => _current?.End();

    private void Abort(string reason)
    {
        StripperReload.Log.LogInfo(TraceLine.Format("STANAG_ABORTED", ("action", _action), ("reason", reason)));
        End();
    }

    private void End(bool completed = false)
    {
        if (_ended) return;
        _ended = true;
        _cancellation.Cancel();
        _view?.Dispose();
        _transfer.Dispose();
        _cancellation.Dispose();
        if (_started) FikaRuntime.NotifyStanag(_player, new StanagVisual(_action,
            completed && _committed ? StanagPhase.Finished : StanagPhase.Cancelled));
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
