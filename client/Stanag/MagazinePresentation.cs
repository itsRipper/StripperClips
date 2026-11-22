using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimationSystem;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.InventoryLogic;
using Audio.SpatialSystem;
using EFT.UI;
using StripperClips.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace StripperClips.Stanag;

internal sealed class MagazinePresentation : IDisposable
{
    internal const string PackageName = "stanag_magazine_load";
    internal const string CommitEvent = "stanag.commit";
    private const string SeatedEvent = "stanag.seated";
    private const string AudioEvent = "stanag.sfx";
    internal static Func<bool> Trace = () => false;
    private const string UtilityTemplate = "61605e13ffa6e502ac5e7eef";
    private const string WeaponPath = "Weapon_root/Weapon_root_anim/weapon";
    private const string AssemblyPath = WeaponPath + "/STANAG";
    private const string AssemblyAsset = "assets/generated/stanag_mounts.prefab";
    private const float SeatSpacing = .01f;
    private const float HolsterTimeout = 4f;

    private readonly Player _owner;
    private readonly Player.AbstractHandsController _previous;
    private readonly Item? _previousItem;
    private readonly Item _visualItem;
    private readonly ClipPackage _package;
    private readonly Action<Player, Item?> _restore;
    private readonly WeaponPrefab _prefab;
    private readonly Animator _animator;
    private readonly Transform _assembly;
    private readonly Transform _magazine;
    private readonly Transform _clip;
    private readonly float _strokeStart;
    private readonly float _strokeEnd;
    private readonly Transform _stack;
    private readonly Transform _rightIk;
    private readonly GameObject _magazineMesh;
    private readonly GameObject _clipMesh;
    private readonly Transform[] _rounds;
    private readonly GameObject _donorModel;
    private readonly bool _donorWasActive;
    private readonly ClipProps _props;
    private readonly WeaponClipPlayer _timeline = new WeaponClipPlayer();
    private readonly string _magazineSound;
    private readonly string _clipSound;

    private MagazineHands? _hands;
    private State _state;
    private bool _committed;
    private bool _returnRequested;
    private bool _released;
    private float _age;
    private float _handoffAt;
    private float _audioStart;
    private int _traceStage;
    private int _roundSound = -1;
    private bool _trace;

    private enum State { Ready, Holstering, Drawing, Playing, Returning, Closed }

    private MagazinePresentation(Player owner, Item visualItem, WeaponPrefab prefab, ClipPackage package,
        Magazine magazine, Magazine clip, IReadOnlyList<Ammo> rounds, Action<Player, Item?> restore)
    {
        _owner = owner;
        _previous = owner.HandsController;
        _previousItem = _previous is Player.EmptyHandsController ? null : _previous.Item;
        _visualItem = visualItem;
        _prefab = prefab;
        _package = package;
        _restore = restore;
        _animator = prefab.Hierarchy.GetComponent<Animator>();
        if (_animator == null || _animator.avatar == null || !_animator.avatar.isValid || _animator.avatar.isHuman ||
            _animator.runtimeAnimatorController == null)
            throw new InvalidOperationException("The native usable rig requires a valid generic avatar and Animator Controller.");
        var weapon = Require(_animator.transform, WeaponPath);
        _donorModel = Require(weapon, "item").gameObject;
        _donorWasActive = _donorModel.activeSelf;
        _rightIk = Require(weapon, "weapon_R_IK_marker");
        var mounts = package.LoadAsset(AssemblyAsset) ?? throw new InvalidOperationException("STANAG mount prefab is missing.");
        _assembly = UnityEngine.Object.Instantiate(mounts, weapon, false).transform;
        _assembly.name = "STANAG";
        ClipProps? props = null;
        try
        {
            _magazine = Require(_assembly, "Magazine");
            _magazineSound = magazine.ItemSound;
            _clipSound = clip.ItemSound;
            var guide = Require(_magazine, "Guide");
            _clip = Require(_magazine, "Clip");
            // The push runs from the acknowledgement to the seated event, eased like the baked thumb.
            _strokeStart = package.Events.Single(evt => evt.Name == CommitEvent).Time;
            _strokeEnd = package.Events.Single(evt => evt.Name == SeatedEvent).Time;
            var missing = package.FirstMissingPath(_animator.transform);
            if (missing != null) throw new InvalidOperationException("The STANAG usable rig is missing " + missing);

            var layer = LayerMask.NameToLayer("Player");
            _magazineMesh = CopyItem(magazine, _magazine, layer, out var mouth);
            _clipMesh = CopyItem(clip, _clip, layer, out _);
            var offset = new Vector3(0, mouth.y, mouth.z + .059f);
            Require(guide, "Guide mount").localPosition = offset;
            var spec = package.Props.Single(prop => prop.Id == "guide");
            props = ClipProps.Spawn(_animator.transform, new[] { new ClipProps.Spec
            {
                Id = spec.Id, Source = spec.Source, Slot = AssemblyPath + "/Magazine/Guide/Guide mount",
                Position = spec.Position, Rotation = spec.Rotation, Scale = spec.Scale, Visible = false,
            } }, package.LoadAsset);
            _props = props;
            _stack = Require(_clip, "Cartridges");
            _stack.localPosition = offset;
            _rounds = new Transform[rounds.Count];
            for (var i = 0; i < rounds.Count; i++)
            {
                var visual = CopyItem(rounds[i], _stack, layer, out _).transform;
                visual.localPosition = Seat(i);
                visual.localRotation = Quaternion.Euler(180f, 0f, 0f);
                _rounds[i] = visual;
            }
            _clipMesh.transform.localPosition = offset;
            SetClipVisible(false);
            _magazineMesh.SetActive(false);
            _donorModel.SetActive(false);
            _timeline.EventFired += OnEvent;
            _timeline.Finished += OnTimelineFinished;
        }
        catch
        {
            props?.Dispose();
            DetachAssembly();
            throw;
        }
    }

    internal event Action? Started;
    internal event Action? CommitRequested;
    internal event Action? Finished;
    internal event Action? Failed;

    internal bool Valid => _owner != null && _owner.HealthController.IsAlive && (_state switch
    {
        State.Holstering => ReferenceEquals(_owner.HandsController, _previous),
        State.Drawing or State.Playing => ReferenceEquals(_owner.HandsController, _hands) && !_hands!.Destroyed,
        State.Returning => ReferenceEquals(_owner.HandsController, _hands),
        _ => false,
    });

    internal static Ammo[] ReadRounds(Magazine clip) => Enumerable.Range(0, clip.Count)
        .Select(index => clip.GetBulletAtPosition(clip.Count - 1 - index)).ToArray();

    internal static async Task<MagazinePresentation?> Prepare(Player owner, Magazine magazine, Magazine clip,
        IReadOnlyList<Ammo> rounds, Action<Player, Item?> restore, CancellationToken cancellation)
    {
        if (rounds.Count == 0 || rounds.Any(round => round == null) || !Singleton<ObjectsFactory>.Instantiated ||
            !StripperReload.LoadPackage(PackageName, out var package, out _)) return null;
        var utility = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(false).ToString(), UtilityTemplate, null);
        var factory = Singleton<ObjectsFactory>.Instance;
        var resources = rounds.Cast<Item>().Concat(new[] { magazine, clip, utility }).Select(item => item.Prefab)
            .GroupBy(key => key.path).Select(group => group.First()).ToArray();
        await factory.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
            resources, JobYieldPriority.General, ct: cancellation);
        if (cancellation.IsCancellationRequested || owner == null || !owner.HealthController.IsAlive ||
            owner.HandsController is not (Player.FirearmController or Player.EmptyHandsController)) return null;

        // Validate the native utility rig before holstering; the bundle carries only the animation and guide.
        var model = factory.CreateItem(utility, Player.GetVisibleToCamera(owner), null, true);
        if (model == null) return null;
        try { return new MagazinePresentation(owner, utility, model.GetComponent<WeaponPrefab>() ??
            throw new InvalidOperationException("The native utility asset has no WeaponPrefab."), package!, magazine, clip, rounds, restore); }
        catch { AssetPoolObject.ReturnToPool(model); throw; }
    }

    internal void Play(float age, bool committed)
    {
        if (_state != State.Ready || !ReferenceEquals(_owner.HandsController, _previous))
            throw new InvalidOperationException("Hands changed during STANAG preparation.");
        _age = age;
        _handoffAt = Time.unscaledTime;
        _committed = committed;
        _trace = Trace();
        _state = State.Holstering;
        // Fika observers follow this swap through the STANAG phase packets, not the native hands packets.
        _previous.Drop(1f, OnHolstered, fastDrop: false, nextControllerItem: _visualItem);
        if (_state == State.Holstering) _owner.StartCoroutine(HolsterWatchdog());
    }

    // A holster that never reports back would otherwise leave the action waiting until raid end.
    private IEnumerator HolsterWatchdog()
    {
        yield return new WaitForSecondsRealtime(HolsterTimeout);
        if (_state != State.Holstering) yield break;
        var hands = _owner != null ? _owner.HandsController : null;
        StripperReload.Log.LogWarning(TraceLine.Format("STANAG_HOLSTER_STALLED", ("seconds", HolsterTimeout),
            ("hands", hands?.GetType().Name), ("same_hands", ReferenceEquals(hands, _previous)),
            ("operation", (_previous as Player.ItemHandsController)?.CurrentHandsOperationName)));
        Fail("holster_timed_out");
    }

    private void Fail(string reason)
    {
        StripperReload.Log.LogInfo(TraceLine.Format("STANAG_PRESENTATION_FAILED", ("reason", reason), ("state", _state)));
        if (_state is State.Ready or State.Holstering) ReleasePrepared();
        Failed?.Invoke();
    }

    private async void OnHolstered()
    {
        // Native operations and Fika may finish another hands transition in this callback; let it unwind first.
        await Task.Yield();
        if (_state != State.Holstering) return;
        if (_owner == null || !_owner.HealthController.IsAlive || !ReferenceEquals(_owner.HandsController, _previous))
        {
            Fail(_owner == null || !_owner.HealthController.IsAlive ? "player_dead_while_holstering" : "hands_changed_while_holstering");
            return;
        }

        try
        {
            _owner.DestroyController();
            if (_returnRequested)
            {
                ReleasePrepared();
                _restore(_owner, _previousItem);
                return;
            }
            _hands = Player.ItemHandsController.smethod_1<MagazineHands>(_owner, _visualItem, (_, actor) =>
            {
                _prefab.Init(actor, true);
                return _prefab.gameObject;
            });
            _hands.ReleasePresentation = ReleasePresentation;
            Player.UsableItemController.Setup(_hands, _owner);
            _state = State.Drawing;
            _ = _owner.SpawnController(_hands, OnDrawn);
        }
        catch (Exception error)
        {
            StripperReload.Log.LogWarning("STANAG native hands handoff failed: " + error);
            if (_hands != null && _owner != null && ReferenceEquals(_owner.HandsController, _hands)) CloseAndRestore();
            else
            {
                ReleasePrepared();
                if (_owner != null && _owner.HandsController == null && _owner.HealthController.IsAlive) _restore(_owner, _previousItem);
            }
            Failed?.Invoke();
        }
    }

    private void OnDrawn()
    {
        if (_state != State.Drawing || _returnRequested) return;
        try
        {
            if (!Valid || !ReferenceEquals((_owner.ArmsAnimatorCommon as UnityAnimatorWrapper)?.Animator, _animator))
                throw new InvalidOperationException("EFT did not activate the STANAG usable Animator.");
            _state = State.Playing;
            var commitTime = _package.Events.Single(evt => evt.Name == CommitEvent).Time;
            var age = _owner.IsYourPlayer ? 0f : Math.Min(commitTime, _age + Time.unscaledTime - _handoffAt);
            _audioStart = age;
            _timeline.PlayOnAnimator(_animator, _package.Clip, _package.Events, age, _committed ? null : CommitEvent);
            _timeline.AfterArmsAnimator(0f);
            if (_magazine.localPosition.sqrMagnitude < .01f)
                throw new InvalidOperationException("The usable Animator did not sample the magazine mount.");
            Started?.Invoke();
        }
        catch (Exception error)
        {
            StripperReload.Log.LogWarning("STANAG usable animation could not bind: " + error);
            Failed?.Invoke();
            Dispose();
        }
    }

    internal void Confirm()
    {
        _committed = true;
        _timeline.Resume();
    }

    // Runs before EFT's procedural transforms and body IK in both viewpoints.
    internal void Tick(float deltaTime)
    {
        if (_state != State.Playing) return;
        _timeline.AfterArmsAnimator(Math.Min(deltaTime, .1f));
        if (_state != State.Playing || !_timeline.IsPlaying) return;
        var progress = StrokeProgress;
        var travel = _committed ? progress * (_rounds.Length * SeatSpacing + .013f) : 0f;
        for (var i = 0; i < _rounds.Length; i++)
        {
            var round = _rounds[i];
            if (round.parent != _stack) continue;
            round.localPosition = Seat(i) - Vector3.up * travel;
            // Rounds stay visible through the feed lips; the last one pushed rests in them.
            round.gameObject.SetActive(round.localPosition.y + .011f > -.012f);
        }
        // The top round now belongs to the magazine: it stays when the empty clip leaves.
        if (_committed && progress >= 1f && _rounds.Length > 0 && _rounds[_rounds.Length - 1].parent == _stack)
            _rounds[_rounds.Length - 1].SetParent(_magazineMesh.transform, true);
        // The thumb is keyed for ten rounds; shorter stacks move the IK target down to their top round.
        var time = _timeline.Time;
        var contact = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(59f / 30f, 62f / 30f, time)) *
            (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(80f / 30f, 83f / 30f, time)));
        _rightIk.position += _magazine.up * (contact * (1f - progress) * (_rounds.Length - 10) * SeatSpacing);
    }

    internal void TraceFrame()
    {
        if (!_trace || _state != State.Playing) return;
        var time = _timeline.Time;
        var stage = time >= 80f / 30f ? 4 : time >= 58f / 30f ? 3 : time >= 37f / 30f ? 2 : time >= 17f / 30f ? 1 : 0;
        if (stage <= _traceStage) return;
        _traceStage = stage;
        StripperReload.Log.LogInfo(TraceLine.Format("STANAG_PHASE", ("phase", stage), ("time", time),
            ("committed", _committed), ("view", _owner.PointOfView), ("stroke", StrokeProgress),
            ("rounds_visible", _rounds.Count(round => round.gameObject.activeInHierarchy))));
    }

    private void OnEvent(ClipEvent evt)
    {
        if (evt.Name == CommitEvent) { if (!_committed) CommitRequested?.Invoke(); return; }
        if (evt.Name == AudioEvent)
        {
            // A late observer still rebuilds prop visibility from prior events, but hears only new cues.
            if (evt.Time >= _audioStart && evt.Value != null) PlayCue(evt.Value);
            return;
        }
        if (_props.Handle(evt)) return;
        if (evt.Value == "magazine") _magazineMesh.SetActive(evt.Name == "prop.show");
        if (evt.Value == "clip") SetClipVisible(evt.Name == "prop.show");
    }

    // Native sounds, played like an item spawned in the hands: 2D in first person, occluded for observers.
    private void PlayCue(string cue)
    {
        if (!Singleton<GUISounds>.Instantiated || !MonoBehaviourSingleton<BetterAudio>.Instantiated) return;
        var sounds = Singleton<GUISounds>.Instance;
        var (clip, volume) = cue switch
        {
            "draw" => (sounds.GetItemClip(_magazineSound, EInventorySoundType.pickup), .7f),
            "guide_draw" => (sounds.GetItemClip("item_smallmetal", EInventorySoundType.pickup), .8f),
            "guide_seat" => (sounds.GetItemClip("mod", EInventorySoundType.use), .45f),
            "clip_draw" => (sounds.GetItemClip("ammo_generic", EInventorySoundType.pickup), .9f),
            "clip_seat" => (sounds.GetItemClip(_clipSound, EInventorySoundType.use), .6f),
            "round" => (NextRoundSound(sounds), .4f),
            "clip_out" => (sounds.GetItemClip("smallmetal", EInventorySoundType.pickup), .45f),
            "guide_out" => (sounds.GetItemClip("mod", EInventorySoundType.pickup), .45f),
            "stow" => (sounds.GetItemClip(_magazineSound, EInventorySoundType.drop), .6f),
            _ => (null, 0f),
        };
        if (clip == null) return;
        var firstPerson = _owner.FirstPersonPointOfView;
        var source = MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(_magazine.position, clip,
            BetterAudio.AudioSourceGroupType.Collisions, 15, volume, EOcclusionTest.None, null, !firstPerson);
        if (source != null && !firstPerson && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
            MonoBehaviourSingleton<SpatialAudioSystem>.Instance.ProcessSourceOcclusion(_owner, source);
    }

    private static readonly System.Reflection.FieldInfo? UiSounds = HarmonyLib.AccessTools.Field(typeof(GUISounds), "_UISounds");

    // EFT's magazine-loading clicks; successive cartridges never repeat a sample.
    private AudioClip? NextRoundSound(GUISounds sounds)
    {
        var clips = UiSounds != null ? (UiSounds.GetValue(sounds) as UISoundsWrapper)?.LoadSounds?._audioClips : null;
        if (clips == null || clips.Length == 0) return null;
        _roundSound = _roundSound < 0 ? UnityEngine.Random.Range(0, clips.Length) : (_roundSound + 1) % clips.Length;
        return clips[_roundSound];
    }

    private void SetClipVisible(bool visible)
    {
        _clipMesh.SetActive(visible);
        _stack.gameObject.SetActive(visible);
    }

    private void OnTimelineFinished(string reason)
    {
        if (reason == "done") RequestReturn();
    }

    private void RequestReturn()
    {
        if (_state is State.Returning or State.Closed) return;
        _returnRequested = true;
        if (_state == State.Holstering)
        {
            if (_owner == null || !_owner.HealthController.IsAlive || !ReferenceEquals(_owner.HandsController, _previous))
                ReleasePrepared();
            else _owner.StartCoroutine(FinishStalledTransition(State.Holstering, _previous));
            return;
        }
        if (_state == State.Ready) { ReleasePrepared(); return; }
        _state = State.Returning;
        _timeline.Stop("return");
        if (_owner == null || !_owner.HealthController.IsAlive || !ReferenceEquals(_owner.HandsController, _hands))
        {
            ReleasePresentation();
            return;
        }
        _hands!.Drop(1f, CloseAndRestore, fastDrop: false);
        _owner.StartCoroutine(FinishStalledTransition(State.Returning, _hands));
    }

    private IEnumerator FinishStalledTransition(State expected, Player.AbstractHandsController controller)
    {
        yield return new WaitForSecondsRealtime(2f);
        if (_state == expected && _owner != null && _owner.HealthController.IsAlive &&
            ReferenceEquals(_owner.HandsController, controller))
            controller.FastForwardCurrentState();
    }

    private async void CloseAndRestore()
    {
        await Task.Yield();
        if (_state == State.Closed) return;
        _state = State.Closed;
        if (_owner == null || !ReferenceEquals(_owner.HandsController, _hands)) return;
        try
        {
            _owner.DestroyController();
            if (_owner.HealthController.IsAlive) _restore(_owner, _previousItem);
            Finished?.Invoke();
        }
        catch (Exception error)
        {
            StripperReload.Log.LogWarning("STANAG hands restoration failed: " + error);
            Failed?.Invoke();
        }
    }

    private void ReleasePrepared()
    {
        _state = State.Closed;
        ReleasePresentation();
        AssetPoolObject.ReturnToPool(_prefab.gameObject);
    }

    private void ReleasePresentation()
    {
        if (_released) return;
        _released = true;
        _timeline.Stop("release");
        _props.Dispose();
        _donorModel.SetActive(_donorWasActive);
        DetachAssembly();
    }

    private void DetachAssembly()
    {
        _assembly.gameObject.SetActive(false);
        _assembly.SetParent(null, false);
        UnityEngine.Object.Destroy(_assembly.gameObject);
    }

    public void Dispose() => RequestReturn();

    private float StrokeProgress => _committed ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_strokeStart, _strokeEnd, _timeline.Time)) : 0f;

    private static Vector3 Seat(int index) => new Vector3(0f, .005f + index * SeatSpacing, -.0005f);

    private static Transform Require(Transform parent, string path) => parent.Find(path) ??
        throw new InvalidOperationException("Native usable rig is missing " + path);

    private static Transform Node(Transform parent, string name)
    {
        var node = new GameObject(name).transform;
        node.SetParent(parent, false);
        return node;
    }

    private static GameObject CopyItem(Item item, Transform parent, int layer, out Vector3 mount)
    {
        var source = Singleton<ObjectsFactory>.Instance.CreateItem(item, false);
        if (source == null) throw new InvalidOperationException("Could not render " + item.TemplateId);
        var root = Node(parent, "Item mesh");
        try
        {
            var socket = source.GetComponentsInChildren<Transform>(true).FirstOrDefault(node => node.name == "patron_001");
            mount = socket != null ? source.transform.InverseTransformPoint(socket.position) : new Vector3(0f, 0f, -.059f);
            foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || !renderer.enabled ||
                    renderer.name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !renderer.name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsActivePart(renderer.transform, source.transform)) continue;
                // Pooled cartridges in patron_ sockets are inactive here; skip them or the stack is baked into the clip.
                if (item is Magazine && (renderer.GetComponentInParent<AmmoPoolObject>(true) != null ||
                    InCartridgeSocket(renderer.transform, source.transform))) continue;
                var child = Node(root, renderer.name);
                child.gameObject.layer = layer;
                var transform = source.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                child.localPosition = transform.GetColumn(3);
                child.localRotation = transform.rotation;
                child.localScale = transform.lossyScale;
                child.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = child.gameObject.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = ShadowCastingMode.Off;
            }
            if (root.childCount == 0) throw new InvalidOperationException("No LOD0 mesh for " + item.TemplateId);
            return root.gameObject;
        }
        finally { AssetPoolObject.ReturnToPool(source); }
    }

    private static bool InCartridgeSocket(Transform part, Transform root)
    {
        for (var node = part.parent; node != null && node != root; node = node.parent)
            if (node.name.StartsWith("patron_", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsActivePart(Transform part, Transform root)
    {
        // Respect each child's own visibility, including EFT's full-round/shell switch.
        for (var node = part; node != root; node = node.parent)
            if (!node.gameObject.activeSelf) return false;
        return true;
    }
}
