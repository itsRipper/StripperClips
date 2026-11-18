using StripperClips.Framework;
using StripperClips.Pure;

var failures = 0;
void Check(bool condition, string what)
{
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

Check(!ReloadRules.CanStart(true, false, 5, 5, 5), "full magazine: no reload");
Check(ReloadRules.FitsWhole(5, 0, 5), "5 rounds, 5 free: clip used");
Check(!ReloadRules.FitsWhole(5, 2, 5), "5 rounds, 3 free: clip not used (never split)");
Check(ReloadRules.FitsWhole(3, 2, 5), "3 rounds, 3 free: clip used");
Check(ReloadRules.FitsWhole(10, 0, 10), "10 rounds, 10 free: clip used");
Check(!ReloadRules.FitsWhole(10, 4, 10), "10 rounds, 6 free: clip not used");
Check(!ReloadRules.FitsWhole(0, 0, 5), "empty clip: not used");
Check(!ReloadRules.CanStart(false, false, 5, 0, 10), "detachable magazine fitted: no clip reload");
Check(!ReloadRules.CanStart(true, true, 5, 0, 5), "malfunction: no clip reload");
Check(!ReloadRules.CanStart(true, false, 0, 0, 5), "empty clip: no reload");
Check(!ReloadRules.CanStart(true, false, 2, 4, 5), "a clip that would need splitting cannot start");
Check(ReloadRules.CanStart(true, false, 1, 4, 5), "a partially filled clip can transfer its last round");

Check(ReloadRules.ChamberOnClose(0, 3), "empty chamber is fed when the bolt closes");
Check(!ReloadRules.ChamberOnClose(1, 3), "loaded chamber stays");
Check(!ReloadRules.ChamberOnClose(0, 0), "nothing to chamber");

Check(ReloadRules.PickClip(new[] { 5, 3 }, 2, 5) == 1, "skip the larger clip that does not fit");
Check(ReloadRules.PickClip(new[] { 2, 5 }, 0, 5) == 1, "the clip that loads most");
Check(ReloadRules.PickClip(new[] { 3, 3 }, 2, 5) == 0, "equal clips retain inventory order");
Check(ReloadRules.PickClip(new[] { 5 }, 2, 5) == -1, "no clip fits entirely");
Check(ReloadRules.PickClip(new[] { 0, 0 }, 0, 5) == -1, "no loaded clip");
Check(ReloadRules.PickClip(new[] { 5 }, 5, 5) == -1, "full magazine");

{
    var tracker = new RaidPhaseTracker();
    Check(tracker.Observe(0, false, false) == RaidTransition.None, "raid: no world, no transition");
    Check(tracker.Observe(7, false, false) == RaidTransition.WorldCreated, "raid: world appears");
    Check(tracker.Observe(7, false, false) == RaidTransition.None, "raid: waiting for the player");
    Check(tracker.Observe(7, true, false) == RaidTransition.PlayerReady, "raid: player appears");
    Check(tracker.Observe(7, true, true) == RaidTransition.Started, "raid: game started");
    Check(tracker.Observe(7, true, true) == RaidTransition.None, "raid: steady state is quiet");
    Check(tracker.Observe(0, false, false) == RaidTransition.Ended, "raid: world gone");
    Check(tracker.Observe(0, false, false) == RaidTransition.None, "raid: ended only once");
}
{
    var tracker = new RaidPhaseTracker();
    tracker.Observe(1, false, false);
    tracker.Observe(1, true, false);
    Check(tracker.Observe(2, true, true) == RaidTransition.Ended, "replaced world: old world ends first");
    Check(tracker.Observe(2, true, true) == RaidTransition.WorldCreated, "replaced world: then the new one is created");
    Check(tracker.Observe(2, true, true) == RaidTransition.PlayerReady, "replaced world: player ready again");
    Check(tracker.Observe(2, true, true) == RaidTransition.Started, "replaced world: started again");
}
{
    var tracker = new RaidPhaseTracker();
    tracker.Observe(3, false, true);
    Check(tracker.Observe(3, false, true) == RaidTransition.None, "started without a player is not reported");
    Check(tracker.Observe(3, true, true) == RaidTransition.PlayerReady, "player ready before started");
    Check(tracker.Observe(3, true, true) == RaidTransition.Started, "then started");
    var aborted = new RaidPhaseTracker();
    aborted.Observe(4, false, false);
    Check(aborted.Observe(0, false, false) == RaidTransition.Ended, "a world that ends while loading reports its end");
}
{
    var previous = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
    try
    {
        var line = TraceLine.Format("FND_X", ("gap", 0.125f), ("name", "left hand"), ("ok", true), ("none", null), ("empty", ""));
        Check(line == "FND_X gap=0.125 name=left_hand ok=true none=null empty=\"\"", "trace line: " + line);
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
}
{
    var track = new ClipEventTrack(new[] { 1.25f, 0f, 2.5f, 0.4f, 0.4f });
    foreach (var fps in new[] { 30f, 60f, 144f, 7f })
    {
        var fired = new List<int>();
        var previous = ClipEventTrack.Start;
        var time = 0f;
        while (true)
        {
            track.Collect(previous, time, fired);
            if (time >= 2.5f) break;
            previous = time;
            time = Math.Min(time + 1f / fps, 2.5f);
        }
        Check(string.Join(",", fired) == "1,3,4,0,2", $"clip events at {fps} fps: order {string.Join(",", fired)}");
    }
    var single = new ClipEventTrack(new[] { 0.5f });
    var hits = new List<int>();
    single.Collect(0.4f, 0.5f, hits);
    Check(hits.Count == 1, "clip event: inclusive at the end of the interval");
    hits.Clear();
    single.Collect(0.5f, 0.6f, hits);
    Check(hits.Count == 0, "clip event: exclusive at the start, no second fire");
    single.Collect(0.6f, 0.6f, hits);
    Check(hits.Count == 0, "clip event: an empty interval fires nothing");
}

Console.WriteLine(failures == 0 ? "StripperClips rule and framework tests: all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;
