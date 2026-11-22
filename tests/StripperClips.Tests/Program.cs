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
    var clip = StanagRules.ClipTemplate;
    var colt = StanagRules.Colt30Template;
    Check(StanagRules.CanLoad(clip, colt, true, 10, 20, 30), "detached Colt 30-round magazine accepts a full STANAG clip");
    Check(StanagRules.CanLoad(clip, colt, true, 4, 26, 30), "STANAG uses the actual load of a partial clip");
    Check(!StanagRules.CanLoad(clip, colt, true, 5, 26, 30), "STANAG refuses a transfer that would split the clip");
    Check(!StanagRules.CanLoad(clip, colt, true, 1, 30, 30), "STANAG refuses a full magazine");
    Check(!StanagRules.CanLoad(clip, colt, true, 0, 0, 30), "STANAG refuses an empty clip");
    Check(!StanagRules.CanLoad(clip, colt, false, 10, 0, 30), "STANAG refuses a fitted magazine");
    Check(!StanagRules.CanLoad(clip, "59c1383d86f774290a37e0ca", true, 10, 0, 60), "5.56 D-60 ammo compatibility alone does not qualify a magazine");
    Check(!StanagRules.CanLoad("6738a1c0f1e2d3c4b5a69782", colt, true, 10, 0, 30), "STANAG refuses a rifle clip from another family");
    Check(StanagRules.SupportsMagazine("5c05413a0db834001c390617") &&
        StanagRules.SupportsMagazine("61840bedd92c473c77021635") && StanagRules.SupportsMagazine("61840d85568c120fdd2962a5"),
        "steel HK and FN magazine prefabs share the STANAG presentation family");
    Check(StanagRules.SupportsMagazine("5aaa5dfee5b5b000140293d3") && StanagRules.SupportsMagazine("544a378f4bdc2d30388b4567") &&
        StanagRules.SupportsMagazine("5c6d46132e221601da357d56"), "polymer 30- and 40-round STANAG prefabs share the presentation");
    Check(!StanagRules.SupportsMagazine("5aaa5e60e5b5b000140293d6") && !StanagRules.SupportsMagazine("5448c1d04bdc2dff2f8b4569") &&
        !StanagRules.SupportsMagazine("544a37c44bdc2d25388b4567"), "short PMAGs and quad-stack MAG5 stay outside the presentation");

    var inRig = StanagRules.ClassifyLocation(hasAddress: true, owned: true, underWeapon: false, underEquipment: true);
    Check(inRig == StanagItemLocation.DetachedEquipment && StanagRules.CanLoad(clip, colt,
        inRig == StanagItemLocation.DetachedEquipment, 10, 0, 30), "full clip loads an empty detached Colt magazine from a tactical rig");
    Check(StanagRules.ClassifyLocation(true, true, true, true) == StanagItemLocation.FittedToWeapon,
        "a magazine fitted to a carried weapon remains ineligible");
    Check(StanagRules.ClassifyLocation(true, false, false, true) == StanagItemLocation.ForeignOwner,
        "a magazine under another player's equipment remains ineligible");
    Check(StanagRules.ClassifyLocation(true, true, false, false) == StanagItemLocation.OutsideEquipment &&
        StanagRules.ClassifyLocation(false, false, false, false) == StanagItemLocation.Unaddressed,
        "stash and unaddressed items cannot be reserved");
}

{
    var observer = new StanagVisualOrder();
    bool Accept(long action, StanagPhase phase) => observer.Accept(new StanagVisual(action, phase));
    Check(!Accept(1, StanagPhase.Committed), "magazine observer requires a start before acknowledgement");
    Check(Accept(1, StanagPhase.Start) && observer.Active, "magazine observer opens a new action");
    Check(!Accept(1, StanagPhase.Start) && !Accept(1, StanagPhase.Finished), "duplicates and premature finishes do not advance playback");
    Check(Accept(1, StanagPhase.Committed) && observer.Active, "acknowledgement unlocks magazine playback");
    Check(Accept(1, StanagPhase.Finished) && !observer.Active, "completion retires the replay context");
    Check(!Accept(1, StanagPhase.Start) && Accept(2, StanagPhase.Cancelled) && !Accept(2, StanagPhase.Start),
        "late starts do not replay completed or cancelled actions");
    Check(Accept(3, StanagPhase.Start) && !Accept(2, StanagPhase.Cancelled) && observer.Active,
        "old disconnect cancellation does not affect a later load");
}

{
    var originalClip = new[] { (Id: 1, Count: 3), (Id: 2, Count: 2), (Id: 3, Count: 5) };
    var originalMagazine = new[] { (Id: 4, Count: 20) };
    var moveOrder = originalClip.Reverse().ToArray();
    var clip = originalClip.ToList();
    var magazine = originalMagazine.ToList();
    var cancelled = false;
    var connected = true;
    var submissions = 0;
    bool Matches(int moved) => clip.SequenceEqual(originalClip.Take(originalClip.Length - moved)) &&
        magazine.SequenceEqual(originalMagazine.Concat(moveOrder.Take(moved)));
    static Task<StackMoveOutcome> Move(List<(int, int)> from, List<(int, int)> to, (int, int) stack)
    {
        if (from.Count == 0 || from[^1] != stack) return Task.FromResult(StackMoveOutcome.Rejected);
        from.RemoveAt(from.Count - 1);
        to.Add(stack);
        return Task.FromResult(StackMoveOutcome.Moved);
    }
    async Task<TransferOutcome> Run(Action? afterMove = null, int reject = 0, int incompatible = 0) =>
        await StanagTransfer.Run(moveOrder, () => connected && !cancelled, () => connected, Matches,
            stack => stack.Id != incompatible, async stack =>
            {
                submissions++;
                if (stack.Id == reject) return StackMoveOutcome.Rejected;
                var result = await Move(clip, magazine, stack);
                afterMove?.Invoke();
                return result;
            }, stack => Move(magazine, clip, stack));

    cancelled = true;
    Check(await Run() == TransferOutcome.Refused && submissions == 0, "STANAG cancellation before commit changes nothing");
    cancelled = false;
    Check(await Run(incompatible: 1) == TransferOutcome.Refused && submissions == 0,
        "STANAG preflights every mixed stack before submitting native moves");
    Check(await Run(reject: 1) == TransferOutcome.Restored && Matches(0),
        "STANAG restores original stack identities, order, and counts after a rejection");
    Check(await Run(() => cancelled = true) == TransferOutcome.Restored && Matches(0),
        "STANAG cancellation during an acknowledgement restores completed moves");
    cancelled = false;
    Check(await Run() == TransferOutcome.Committed && Matches(3), "STANAG appends mixed stacks in EFT pop order");
    var submitted = submissions;
    Check(await Run() == TransferOutcome.Refused && submissions == submitted, "STANAG rejects a consecutive commit using stale inventory");

    clip = originalClip.ToList();
    magazine = originalMagazine.ToList();
    Check(await Run(() => magazine.Add((5, 1))) == TransferOutcome.RecoveryFailed && magazine[^1] == (5, 1),
        "STANAG does not roll back through an unrelated inventory change");
    clip = originalClip.ToList();
    magazine = originalMagazine.ToList();
    Check(await Run(() => connected = false) == TransferOutcome.RecoveryFailed && clip.Count == 2,
        "STANAG stops further moves when the native connection goes away");
}

{
    var commit = new CoopCommitGate();
    Check(commit.TryBegin() && !commit.TryBegin(), "one native commit per load event");
    commit.Finish(interrupted: true);
    Check(commit.Interrupted && !commit.TryBegin(), "interruption stops chambering and a replayed load event");
    var cancelled = new CoopCommitGate();
    cancelled.Finish(interrupted: true);
    Check(!cancelled.TryBegin(), "cancel before strip.load submits no transactions");
    var completed = new CoopCommitGate();
    Check(completed.TryBegin(), "normal commit starts");
    completed.Finish(interrupted: false);
    Check(completed.Finished && !completed.Interrupted, "normal animation end allows an acknowledged batch to settle");
}
{
    var hostView = new ReloadVisualOrder();
    var otherClientView = new ReloadVisualOrder();
    Check(hostView.Accept(10, ReloadVisualPhase.Start), "replica accepts a reload start");
    Check(!hostView.Accept(10, ReloadVisualPhase.Start), "replica ignores duplicate starts");
    Check(otherClientView.Accept(20, ReloadVisualPhase.Start), "simultaneous reloads track separate actors");
    Check(hostView.Accept(10, ReloadVisualPhase.Cancelled) && !hostView.CanReplayStart && hostView.HasTransferContext,
        "cancelled pose cannot be replayed while native recovery can still settle");
    Check(!hostView.Accept(10, ReloadVisualPhase.Closed), "stale close after cancellation is ignored");
    Check(hostView.Accept(10, ReloadVisualPhase.Settled) && !hostView.HasTransferContext,
        "settled cancellation clears the observer's inventory context");
    Check(hostView.Accept(11, ReloadVisualPhase.Start), "a newer reload resumes normally");
    Check(!hostView.Accept(10, ReloadVisualPhase.Cancelled), "a previous cancel cannot stop the new reload");
    Check(!hostView.Accept(10, ReloadVisualPhase.Settled) && hostView.HasTransferContext,
        "an old settlement cannot clear the current reload");
    Check(!hostView.Accept(11, ReloadVisualPhase.Settled), "settlement cannot end a running reload");
    Check(!otherClientView.Accept(21, ReloadVisualPhase.Closed), "a late peer cannot apply an orphaned close");
    Check(otherClientView.Accept(21, ReloadVisualPhase.Start) &&
        otherClientView.Accept(21, ReloadVisualPhase.Loaded) && otherClientView.CanReplayStart,
        "late peer can catch up while a reload is still loading or chambering");
    Check(otherClientView.Accept(21, ReloadVisualPhase.Closed) && !otherClientView.CanReplayStart &&
        !otherClientView.HasTransferContext, "close ends replay and inventory context while playback can finish");
    Check(!otherClientView.Accept(21, ReloadVisualPhase.Closed), "visual chamber state is applied once");
    Check(otherClientView.Accept(21, ReloadVisualPhase.Cancelled) && !otherClientView.HasTransferContext,
        "an interruption after close cannot reopen the transfer context");
    Check(hostView.Accept(12, ReloadVisualPhase.Cancelled) && !hostView.HasTransferContext,
        "an orphaned cancellation cannot retain the previous reload's context");
    Check(!ReloadVisualOrder.CanReplay(ReloadVisualPhase.Closed), "host cache stops replay at close");
}
{
    var peer = new object();
    var other = new object();
    var peers = new Dictionary<int, object> { [1] = peer };
    int Id(object value) => ReferenceEquals(value, other) ? 2 : 1;
    Check(CoopReloadPolicy.AllPeersEnrolled(new[] { peer }, peers, Id), "registered peer permits reload");
    Check(!CoopReloadPolicy.AllPeersEnrolled(new[] { peer, other }, peers, Id), "missing peer blocks co-op reloads");
    Check(!CoopReloadPolicy.AllPeersEnrolled(new[] { new object() }, peers, Id), "reused transport ID needs a new handshake");
}
{
    var stacks = new[] { "first", "second", "third" };
    var moves = new List<string>();
    Task<StackMoveOutcome> Move(string stack)
    {
        moves.Add(stack);
        return Task.FromResult(StackMoveOutcome.Moved);
    }
    Task<StackMoveOutcome> Restore(string stack)
    {
        moves.Add("-" + stack);
        return Task.FromResult(StackMoveOutcome.Moved);
    }
    Check(await WholeClipTransfer.Run(stacks, stack => stack != "second", Move, Restore) == TransferOutcome.Refused &&
        moves.Count == 0, "whole batch preflight refuses before moving any stack");
    Check(await WholeClipTransfer.Run(stacks, _ => true, Move, Restore) == TransferOutcome.Committed &&
        moves.SequenceEqual(stacks), "accepted mixed stacks submit exactly once in clip order");
    moves.Clear();
    Check(await WholeClipTransfer.Run(stacks, _ => true, stack => stack == "third"
        ? Task.FromResult(StackMoveOutcome.Rejected) : Move(stack), Restore) == TransferOutcome.Restored &&
        moves.SequenceEqual(new[] { "first", "second", "-second", "-first" }),
        "mid-batch rejection restores confirmed stacks in reverse order");
    moves.Clear();
    Check(await WholeClipTransfer.Run(stacks, _ => true, stack => stack == "second"
        ? Task.FromResult(StackMoveOutcome.MovedWithoutConfirmation) : Move(stack), Restore) == TransferOutcome.Restored &&
        moves.SequenceEqual(new[] { "first", "-second", "-first" }),
        "a locally applied move without server confirmation is included in recovery");
    Check(await WholeClipTransfer.Run(stacks, _ => true, stack => stack == "second"
        ? Task.FromResult(StackMoveOutcome.Rejected) : Move(stack),
        _ => Task.FromResult(StackMoveOutcome.Rejected)) == TransferOutcome.RecoveryFailed,
        "connection loss during recovery is reported as unresolved");
}
{
    var clip = new Stack<string>(new[] { "bottom", "middle", "top" });
    var magazine = new Stack<string>();
    var ordered = clip.ToArray();
    bool CanSimulate(string stack) => clip.TryPeek(out var top) && top == stack;
    var confirmed = new TaskCompletionSource<StackMoveOutcome>();
    Task<StackMoveOutcome> Submit(string stack)
    {
        Check(CanSimulate(stack), "native move can remove only the current top stack");
        magazine.Push(clip.Pop());
        return stack == "top" ? confirmed.Task : Task.FromResult(StackMoveOutcome.Moved);
    }
    var old = await WholeClipTransfer.Run(ordered, CanSimulate, Submit, _ => throw new InvalidOperationException());
    Check(old == TransferOutcome.Refused && magazine.Count == 0, "old native preflight reproduces the mixed-clip refusal");

    var transfer = WholeClipTransfer.Run(ordered, clip.Contains, Submit, _ => throw new InvalidOperationException());
    Check(!transfer.IsCompleted && magazine.Count == 1 && clip.Peek() == "middle",
        "wait for Fika acknowledgement before simulating the next top stack");
    confirmed.SetResult(StackMoveOutcome.Moved);
    Check(await transfer == TransferOutcome.Committed && clip.Count == 0 && magazine.Reverse().SequenceEqual(ordered),
        "mixed clip commits in cartridge order after native acknowledgements");
}
{
    var barrier = new StripperClips.Framework.ClipPlaybackBarrier();
    barrier.Hold(1f);
    Check(barrier.Advance(0.9f, 0.3f, 2f) == 1f && barrier.Advance(1f, 0.5f, 2f) == 1f,
        "asynchronous reload holds at the push across frames");
    barrier.Hold(1.75f);
    Check(barrier.Advance(1f, 1f, 2f) == 1.75f && barrier.Holding,
        "acknowledged transfer can reach chambering but cannot finish yet");
    barrier.Resume();
    Check(barrier.Advance(1.75f, 1f, 2f) == 2f && !barrier.Holding,
        "acknowledged chambering releases the final animation");
}
{
    var stacks = new[] { "first", "second", "third" };
    var sent = new List<string>();
    Task<StackMoveOutcome> Move(string stack)
    {
        sent.Add(stack);
        return Task.FromResult(stack == "second" ? StackMoveOutcome.Pending : StackMoveOutcome.Moved);
    }
    Task<StackMoveOutcome> Restore(string stack)
    {
        sent.Add("-" + stack);
        return Task.FromResult(StackMoveOutcome.Moved);
    }
    Check(await WholeClipTransfer.Run(stacks, _ => true, Move, Restore) == TransferOutcome.Pending &&
        sent.SequenceEqual(new[] { "first", "second" }),
        "unacknowledged move sends neither the next stack nor a restore that could duplicate it");
    sent.Clear();
    Check(await WholeClipTransfer.Run(stacks, _ => true,
        stack => { sent.Add(stack); return Task.FromResult(stack == "third" ? StackMoveOutcome.Rejected : StackMoveOutcome.Moved); },
        stack => { sent.Add("-" + stack); return Task.FromResult(stack == "second" ? StackMoveOutcome.Pending : StackMoveOutcome.Moved); })
        == TransferOutcome.Pending && sent.SequenceEqual(new[] { "first", "second", "third", "-second" }),
        "unacknowledged restore stops recovery before the next restore");
    Check(await WholeClipTransfer.Run(new[] { "only" }, _ => true, _ => Task.FromResult(StackMoveOutcome.Pending),
        _ => throw new InvalidOperationException()) == TransferOutcome.Pending,
        "first move without acknowledgement is pending, not refused");
}
{
    var silent = new TaskCompletionSource<int>();
    var timedOut = await Deadline.Await(silent.Task, TimeSpan.FromMilliseconds(20));
    Check(!timedOut.Completed && !silent.Task.IsCompleted, "deadline expires without completing the transaction");
    silent.SetResult(7);
    Check(await silent.Task == 7, "the abandoned transaction can still complete later");
    var answered = new TaskCompletionSource<int>();
    var waiting = Deadline.Await(answered.Task, TimeSpan.FromSeconds(30));
    answered.SetResult(3);
    var result = await waiting;
    Check(result.Completed && result.Result == 3, "an acknowledgement before the deadline returns its result");
    var immediate = await Deadline.Await(Task.FromResult(5), TimeSpan.Zero);
    Check(immediate.Completed && immediate.Result == 5, "an already completed transaction never times out");
}

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
