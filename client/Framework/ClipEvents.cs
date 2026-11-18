using System.Collections.Generic;
using AnimationEventSystem;
using EFT;
using UnityEngine;

namespace StripperClips.Framework;

public sealed class ClipEvent
{
    public float Time;
    public string Name = "";
    public string? Value;
    public bool Native;
    // Custom playback does not evaluate native event conditions.
    public int Conditions;

    internal AnimationEventParameter? Parameter;
}

// Forward presentation events only. Gameplay events would bypass the hands controller.
public static class NativeAnimationEvents
{
    public static readonly HashSet<string> Allowed = new HashSet<string> { "Sound", "SoundAtPoint" };

    private static Dictionary<string, int>? _hashes;

    public static bool Send(Player.FirearmController hands, ClipEvent clipEvent)
    {
        if (!Allowed.Contains(clipEvent.Name) || !TryHash(clipEvent.Name, out var hash)) return false;
        var emitter = hands._weaponPrefab != null ? hands._weaponPrefab.AnimationEventsEmitter : null;
        if (emitter == null) return false;
        clipEvent.Parameter ??= new AnimationEventParameter
        {
            ParamType = clipEvent.Value != null ? EAnimationEventParamType.String : EAnimationEventParamType.None,
            StringParam = clipEvent.Value,
        };
        emitter.SendEvent(clipEvent.Name, hash, clipEvent.Parameter, 0);
        return true;
    }

    public static bool TryHash(string name, out int hash)
    {
        if (_hashes == null)
        {
            _hashes = new Dictionary<string, int>();
            foreach (var pair in AnimationControllerEventsTable._hashToFunctionName) _hashes[pair.Value] = pair.Key;
        }
        return _hashes.TryGetValue(name, out hash);
    }

    public static List<ClipEvent> ForState(Animator animator, int fullPathHash, float clipLength)
    {
        var events = new List<ClipEvent>();
        foreach (var behaviour in animator.GetBehaviours<AnimationEventsStateBehaviour>())
        {
            if (behaviour.FullNameHash != fullPathHash || behaviour.AnimationEvents == null) continue;
            foreach (var native in behaviour.AnimationEvents)
                events.Add(new ClipEvent
                {
                    Time = native.Time * clipLength,
                    Name = native.FunctionName,
                    Value = native.Parameter != null && native.Parameter.ParamType == EAnimationEventParamType.String ? native.Parameter.StringParam : null,
                    Native = Allowed.Contains(native.FunctionName),
                    Conditions = native.EventConditions != null ? native.EventConditions.Count : 0,
                });
            break;
        }
        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        return events;
    }
}
