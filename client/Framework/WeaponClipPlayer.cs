using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace StripperClips.Framework;

// Evaluate after the arms animator so EFT's procedural animation and IK use the clip pose.
public sealed class WeaponClipPlayer
{
    private readonly List<int> _fired = new List<int>();
    private PlayableGraph _graph;
    private AnimationClipPlayable _playable;
    private AnimationPlayableOutput _output;
    private Player.FirearmController? _hands;
    private IReadOnlyList<ClipEvent> _events = Array.Empty<ClipEvent>();
    private ClipEventTrack _track = new ClipEventTrack(Array.Empty<float>());
    private float _previous, _blendIn, _blendOut;
    private ClipPlaybackBarrier _barrier;
    private GameObject? _sampleTarget;
    private bool _fullWeight;
    private Transform[] _bound = Array.Empty<Transform>();
    private Vector3[] _nativeP = Array.Empty<Vector3>(), _nativeS = Array.Empty<Vector3>(), _clipP = Array.Empty<Vector3>();
    private Quaternion[] _nativeR = Array.Empty<Quaternion>();

    public event Action<ClipEvent>? EventFired;
    public event Action<string>? Finished;

    public bool IsPlaying => _sampleTarget != null || _graph.IsValid();
    public AnimationClip? Clip { get; private set; }
    public float Time { get; private set; }
    public float Weight { get; private set; }

    // Unity's output weight does not blend against the native pose written this frame.
    public void Play(Player.FirearmController hands, Animator animator, AnimationClip clip, IReadOnlyList<ClipEvent> events, float blendIn = 0.15f, float blendOut = 0.15f,
        Transform[]? bound = null) =>
        Begin(hands, animator, null, clip, events, blendIn, blendOut, bound, 0f);

    internal void PlaySampled(GameObject rig, AnimationClip clip, IReadOnlyList<ClipEvent> events, Transform[] bound,
        float age, string? holdAt)
    {
        Begin(null, null, rig, clip, events, 0.15f, 0.15f, bound, InitialTime(events, age, holdAt));
        if (holdAt != null) HoldAt(holdAt);
    }

    internal void PlayOnAnimator(Animator animator, AnimationClip clip, IReadOnlyList<ClipEvent> events, float age, string? holdAt)
    {
        Begin(null, animator, null, clip, events, 0.15f, 0.15f, null, InitialTime(events, age, holdAt));
        _graph.Play();
        if (holdAt != null) HoldAt(holdAt);
    }

    private static float InitialTime(IReadOnlyList<ClipEvent> events, float age, string? holdAt)
    {
        if (holdAt != null)
            foreach (var evt in events)
                if (evt.Name == holdAt) return Math.Min(age, evt.Time);
        return age;
    }

    private void Begin(Player.FirearmController? hands, Animator? animator, GameObject? rig, AnimationClip clip,
        IReadOnlyList<ClipEvent> events, float blendIn, float blendOut, Transform[]? bound, float startAt)
    {
        Stop("restart");
        _bound = bound ?? Array.Empty<Transform>();
        _nativeP = new Vector3[_bound.Length];
        _nativeS = new Vector3[_bound.Length];
        _nativeR = new Quaternion[_bound.Length];
        _clipP = new Vector3[_bound.Length];
        _hands = hands;
        Clip = clip;
        _events = events;
        var times = new float[events.Count];
        for (var i = 0; i < times.Length; i++) times[i] = events[i].Time;
        _track = new ClipEventTrack(times);
        _blendIn = blendIn;
        _blendOut = blendOut;
        Time = startAt;
        _barrier = default;
        _sampleTarget = rig;
        _fullWeight = animator != null && hands == null;
        _previous = ClipEventTrack.Start;
        if (animator == null) return;
        _graph = PlayableGraph.Create("foundation-weapon-clip");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        _output = AnimationPlayableOutput.Create(_graph, "foundation-weapon-clip", animator);
        _playable = AnimationClipPlayable.Create(_graph, clip);
        _output.SetSourcePlayable(_playable);
        _output.SetWeight(0f);
    }

    internal void HoldAt(string name)
    {
        foreach (var evt in _events)
            if (evt.Name == name)
            {
                _barrier.Hold(Math.Max(Time, evt.Time));
                return;
            }
        throw new InvalidOperationException("Missing clip event " + name);
    }

    internal void Resume() => _barrier.Resume();

    // Called from Player.LateUpdate in Auto mode and UnityAnimatorWrapper.Update in Manual mode.
    public void AfterArmsAnimator(float deltaTime)
    {
        if (!IsPlaying) return;
        var length = Clip!.length;
        Time = _barrier.Advance(Time, deltaTime, length);
        Weight = Math.Min(1f, Math.Min(_blendIn > 0f ? Time / _blendIn : 1f, _blendOut > 0f ? (length - Time) / _blendOut : 1f));
        var manual = _bound.Length > 0;
        var blend = manual && Weight < 1f;
        if (blend)
            for (var i = 0; i < _bound.Length; i++)
            {
                var t = _bound[i];
                _nativeP[i] = t.localPosition;
                _nativeR[i] = t.localRotation;
                _nativeS[i] = t.localScale;
            }
        if (_sampleTarget != null) Clip.SampleAnimation(_sampleTarget, Time);
        else
        {
            _playable.SetTime(Time);
            _output.SetWeight(manual || _fullWeight ? 1f : Weight);
            _graph.Evaluate(0f);
        }
        if (blend)
            for (var i = 0; i < _bound.Length; i++)
            {
                var t = _bound[i];
                _clipP[i] = t.localPosition;
                t.localPosition = Vector3.LerpUnclamped(_nativeP[i], _clipP[i], Weight);
                t.localRotation = Quaternion.Slerp(_nativeR[i], t.localRotation, Weight);
                t.localScale = Vector3.LerpUnclamped(_nativeS[i], t.localScale, Weight);
            }

        _fired.Clear();
        _track.Collect(_previous, Time, _fired);
        _previous = Time;
        for (var i = 0; i < _fired.Count; i++)
        {
            var clipEvent = _events[_fired[i]];
            if (clipEvent.Native && _hands != null) NativeAnimationEvents.Send(_hands, clipEvent);
            EventFired?.Invoke(clipEvent);
            if (!IsPlaying) return;
        }
        if (Time >= length && !_barrier.Holding) Stop("done");
    }

    public bool BlendSample(int i, out Vector3 native, out Vector3 clip)
    {
        var ok = i >= 0 && i < _bound.Length && Weight < 1f;
        native = ok ? _nativeP[i] : Vector3.zero;
        clip = ok ? _clipP[i] : Vector3.zero;
        return ok;
    }

    public void Stop(string reason)
    {
        if (!IsPlaying) return;
        if (_graph.IsValid()) _graph.Destroy();
        _sampleTarget = null;
        _hands = null;
        Finished?.Invoke(reason);
    }
}
