using System;
using System.Collections.Generic;
using AnimationSystem;
using EFT;
using EFT.InventoryLogic;
using StripperClips.Framework;
using StripperClips.Pure;
using UnityEngine;

namespace StripperClips;

internal sealed class RemoteClipVisual : IDisposable
{
    private readonly WeaponClipPlayer _player = new WeaponClipPlayer();
    private readonly ClipProps _props;
    private readonly ClipRounds? _rounds;
    private readonly List<Transform> _slots;
    private bool _disposed;

    private RemoteClipVisual(ClipProps props, ClipRounds? rounds, List<Transform> slots, int capacity, int held)
    {
        _props = props;
        _rounds = rounds;
        _slots = slots;
        _props.ShowParts("rounds", capacity - held, capacity);
        _props.ShowParts("clip", 0, 1);
        _player.EventFired += evt =>
        {
            _props.Handle(evt);
            if (evt.Name == "strip.sink" && int.TryParse(evt.Value, out var sunk))
                _props.ShowParts("rounds", capacity - held + sunk, capacity);
        };
    }

    internal bool IsPlaying => _player.IsPlaying;

    internal static RemoteClipVisual? Start(Player player, Player.FirearmController hands, ReloadVisual visual,
        ReloadVisualPhase phase, float age)
    {
        if (player.UsedSimplifiedSkeleton)
            throw new InvalidOperationException("The observed player uses a simplified body skeleton.");
        if (!Catalog.PackageFor.TryGetValue(hands.Item.TemplateId, out var family))
            throw new InvalidOperationException("The equipped weapon has no reload package.");
        var capacity = family == "sks_stripper_reload" ? 10 : 5;
        if (visual.Count < 1 || visual.Count > capacity) return null;
        if (!StripperReload.LoadPackage(visual.Open ? family + "_open" : family, out var package, out var reason))
            throw new InvalidOperationException(reason);
        if (phase == ReloadVisualPhase.Closed && age >= package!.Clip.length) return null;

        // Bind the equipped weapon prefab. The active arms wrapper can point at the spirit rig.
        var rig = hands.GunBaseTransform?.parent?.parent;
        if (rig == null || rig.Find("Weapon_root/Weapon_root_anim/weapon") == null)
            rig = (hands.FirearmsAnimator.Animator as UnityAnimatorWrapper)?.Animator?.transform;
        if (rig == null) throw new InvalidOperationException("The observed weapon has no presentation rig.");
        var slots = new List<Transform>(3);
        ClipProps? props = null;
        ClipRounds? rounds = null;
        RemoteClipVisual? result = null;
        try
        {
            AddPresentationSlots(rig, slots);
            var missing = package!.FirstMissingPath(rig);
            if (missing != null) throw new InvalidOperationException($"Observed rig {rig.name} lacks {missing}");
            var bound = Array.ConvertAll(package.Paths, rig.Find);
            props = ClipProps.Spawn(rig, package.Props, package.LoadAsset);
            if (player.InventoryController.TryFindItem(visual.ClipId, out var item) && item is Magazine clip &&
                Catalog.IsClip(clip.TemplateId) && clip.Count == visual.Count && clip.MaxCount == capacity)
            {
                ClipLook.Apply(props.Find("clip"), clip);
                rounds = ClipRounds.Bind(props.Find, clip);
            }
            result = new RemoteClipVisual(props, rounds, slots, capacity, visual.Count);
            result._player.PlaySampled(rig.gameObject, package.Clip, package.Events, bound, age, HoldEvent(phase));
            return result;
        }
        catch
        {
            if (result != null) result.Dispose();
            else { rounds?.Release(); props?.Dispose(); DestroySlots(slots); }
            throw;
        }
    }

    internal void Tick(float deltaTime) => _player.AfterArmsAnimator(deltaTime);

    internal void SetPhase(ReloadVisualPhase phase)
    {
        if (HoldEvent(phase) is string hold) _player.HoldAt(hold);
        else _player.Resume();
    }

    private static string? HoldEvent(ReloadVisualPhase phase) => phase switch
    {
        ReloadVisualPhase.Start => "strip.load",
        ReloadVisualPhase.Loaded => "strip.chamber",
        _ => null,
    };

    private static void AddPresentationSlots(Transform rig, List<Transform> created)
    {
        // The inspected model_simple weapon prefabs omit only the cosmetic prop tracks.
        var weapon = rig.Find("Weapon_root/Weapon_root_anim/weapon");
        if (weapon == null) return;
        var prop = Ensure(weapon, "prop");
        Ensure(prop, "prop_bone_001");
        Ensure(prop, "prop_bone_002");

        Transform Ensure(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing;
            var createdSlot = new GameObject(name) { layer = parent.gameObject.layer, hideFlags = HideFlags.DontSave }.transform;
            createdSlot.SetParent(parent, false);
            created.Add(createdSlot);
            return createdSlot;
        }
    }

    private static void DestroySlots(List<Transform> slots)
    {
        for (var index = slots.Count - 1; index >= 0; index--)
        {
            var slot = slots[index];
            if (slot == null) continue;
            slot.SetParent(null, false);
            UnityEngine.Object.Destroy(slot.gameObject);
        }
        slots.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Stop("remote_end");
        _rounds?.Release();
        _props.Dispose();
        DestroySlots(_slots);
    }
}
