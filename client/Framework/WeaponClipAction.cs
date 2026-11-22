using System;
using AnimationSystem;
using EFT;
using UnityEngine;

namespace StripperClips.Framework;

public sealed class WeaponClipAction
{
    private const int TraceFrames = 6;
    private readonly WeaponClipPlayer _player = new WeaponClipPlayer();
    private readonly Player _owner;
    private readonly Player.FirearmController _hands;
    private ClipProps? _props;
    private Transform? _weapon, _weaponRootAnim, _weaponRoot;
    private int _frames, _weaponIndex = -1;
    private static WeaponClipAction? _after;
    private static int _afterFrames;

    private WeaponClipAction(Player owner, Player.FirearmController hands)
    {
        _owner = owner;
        _hands = hands;
        _player.EventFired += e =>
        {
            _props?.Handle(e);
            EventFired?.Invoke(e);
        };
        _player.Finished += reason =>
        {
            Finishing?.Invoke(reason);
            _props?.Dispose();
            _props = null;
            if (ReferenceEquals(Active, this)) Active = null;
            _after = this;
            _afterFrames = TraceFrames;
            Finished?.Invoke(reason);
        };
    }

    public static Action<string>? Trace;

    public static WeaponClipAction? Active { get; private set; }

    public event Action<ClipEvent>? EventFired;
    // Return pooled game objects before destroying their parent props.
    public event Action<string>? Finishing;
    public event Action<string>? Finished;
    internal Func<bool>? ContinuePlaying { get; set; }

    public Player Owner => _owner;
    public Player.FirearmController Hands => _hands;
    public float Time => _player.Time;
    public string Props => _props != null ? _props.Describe() : "";

    public static void Install(PatchSet patches)
    {
        patches.Prefix(PatchSet.Method(typeof(Player), nameof(Player.LateUpdate)), (Action<Player>)LateUpdatePre, HarmonyLib.Priority.First);
        patches.Postfix(PatchSet.Method(typeof(UnityAnimatorWrapper), nameof(UnityAnimatorWrapper.Update), typeof(float)), (Action<UnityAnimatorWrapper, float>)AnimatorPost, HarmonyLib.Priority.Last);
        patches.Postfix(PatchSet.Method(typeof(Player), nameof(Player.LateUpdate)), (Action<Player>)LateUpdatePost, HarmonyLib.Priority.Last);
    }

    public static WeaponClipAction Start(Player player, ClipPackage package, float blend = 0.15f)
    {
        if (Active != null) throw new InvalidOperationException("another clip action is playing");
        var hands = player.HandsController as Player.FirearmController ?? throw new InvalidOperationException("no firearm in hands");
        if (!IsIdle(hands)) throw new InvalidOperationException("weapon not idle");
        var animator = (player.ArmsAnimatorCommon as UnityAnimatorWrapper)?.Animator ?? throw new InvalidOperationException("no arms animator");
        var rig = animator.transform;
        var missing = package.FirstMissingPath(rig);
        if (missing != null) throw new InvalidOperationException("rig has no " + missing);
        var action = new WeaponClipAction(player, hands);
        action._weaponRoot = rig.Find("Weapon_root");
        action._weaponRootAnim = rig.Find("Weapon_root/Weapon_root_anim");
        action._weapon = rig.Find("Weapon_root/Weapon_root_anim/weapon");
        _after = null;
        action.TraceSample("before");
        if (package.Props.Count > 0) action._props = ClipProps.Spawn(rig, package.Props, package.LoadAsset);
        var bound = new Transform[package.Paths.Length];
        for (var i = 0; i < bound.Length; i++)
        {
            bound[i] = rig.Find(package.Paths[i]);
            if (package.Paths[i] == "Weapon_root/Weapon_root_anim/weapon") action._weaponIndex = i;
        }
        action._player.Play(hands, animator, package.Clip, package.Events, blend, blend, bound);
        Active = action;
        return action;
    }

    // Use an exact type check because EFT utility operations inherit Idling.
    public static bool IsIdle(Player.FirearmController hands) =>
        hands.CurrentOperation != null && hands.CurrentOperation.GetType() == typeof(Player.FirearmController.Idling);

    public void Stop(string reason) => _player.Stop(reason);
    internal void HoldAt(string name) => _player.HoldAt(name);
    internal void Resume() => _player.Resume();

    public bool ShowParts(string propId, int from, int to) => _props != null && _props.ShowParts(propId, from, to);

    public UnityEngine.GameObject? Prop(string propId) => _props?.Find(propId);

    private void Tick(float deltaTime)
    {
        // Contain handler failures before returning to EFT's LateUpdate.
        try
        {
            if (!ReferenceEquals(_owner.HandsController, _hands)) { Stop("hands_changed"); return; }
            if (!_owner.HealthController.IsAlive) { Stop("dead"); return; }
            if (!IsIdle(_hands)) { Stop("native_operation"); return; }
            if (ContinuePlaying?.Invoke() == false) { Stop("context_changed"); return; }
            _player.AfterArmsAnimator(deltaTime);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            if (ReferenceEquals(Active, this)) Active = null;
            _props?.Dispose();
            _props = null;
            Stop("error");
        }
    }

    private static void LateUpdatePre(Player __instance)
    {
        var active = Active;
        if (active != null && ReferenceEquals(__instance, active._owner) && __instance.ArmsUpdateMode == Player.EUpdateMode.Auto) active.Tick(UnityEngine.Time.deltaTime);
    }

    private static void LateUpdatePost(Player __instance)
    {
        if (Trace == null) return;
        var active = Active;
        if (active != null && ReferenceEquals(__instance, active._owner))
        {
            active._frames++;
            var left = active._player.Clip != null ? active._player.Clip.length - active._player.Time : 0f;
            if (active._frames <= TraceFrames) active.TraceSample("start");
            else if (left < TraceFrames / 30f) active.TraceSample("end");
        }
        else if (_after != null && ReferenceEquals(__instance, _after._owner) && _afterFrames > 0)
        {
            _afterFrames--;
            _after.TraceSample("after");
            if (_afterFrames == 0) _after = null;
        }
    }

    private void TraceSample(string phase)
    {
        var trace = Trace;
        var camera = Camera.main;
        if (trace == null || camera == null || _weapon == null) return;
        var c = camera.transform;
        var p = c.InverseTransformPoint(_weapon.position) * 1000f;
        var r = (Quaternion.Inverse(c.rotation) * _weapon.rotation).eulerAngles;
        var a = _weaponRootAnim != null ? _weaponRootAnim.localPosition * 1000f : Vector3.zero;
        var ar = _weaponRootAnim != null ? _weaponRootAnim.localEulerAngles : Vector3.zero;
        var root = _weaponRoot != null ? _weaponRoot.localPosition * 1000f : Vector3.zero;
        _player.BlendSample(_weaponIndex, out var native, out var clip);
        trace(TraceLine.Format("FND_CLIP_TRANSITION", ("phase", phase), ("frame", UnityEngine.Time.frameCount), ("t", _player.Time), ("w", _player.Weight),
            ("op", _hands.CurrentOperation != null ? _hands.CurrentOperation.GetType().Name : "none"),
            ("weapon_cam_mm", V(p)), ("weapon_cam_deg", V(r)), ("weapon_local_mm", V(_weapon.localPosition * 1000f)),
            ("blend_native_mm", V(native * 1000f)), ("blend_clip_mm", V(clip * 1000f)),
            ("root_anim_mm", V(a)), ("root_anim_deg", V(ar)), ("root_mm", V(root)), ("camera_mm", V(c.position * 1000f))));
    }

    private static string V(Vector3 v) => FormattableString.Invariant($"{v.x:0.0},{v.y:0.0},{v.z:0.0}");

    private static void AnimatorPost(UnityAnimatorWrapper __instance, float dt)
    {
        var active = Active;
        if (active != null && active._owner.ArmsUpdateMode != Player.EUpdateMode.Auto && ReferenceEquals(__instance, active._owner.ArmsAnimatorCommon)) active.Tick(dt);
    }
}
