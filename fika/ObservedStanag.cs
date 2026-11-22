using System;
using System.Linq;
using System.Threading;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using HarmonyLib;
using StripperClips.Pure;
using StripperClips.Stanag;
using UnityEngine;

namespace StripperClips.Fika;

internal sealed class ObservedStanag : IDisposable
{
    internal const float Lifetime = 15f;
    internal static readonly Action<ObservedPlayer> RestoreEmpty = AccessTools.MethodDelegate<Action<ObservedPlayer>>(
        AccessTools.DeclaredMethod(typeof(ObservedPlayer), "CreateEmptyHandsController", Type.EmptyTypes)
        ?? throw new MissingMethodException("ObservedPlayer.CreateEmptyHandsController"));
    private readonly StanagVisualOrder _order = new StanagVisualOrder();
    private CancellationTokenSource? _prepare;
    private MagazinePresentation? _view;
    private float _started;
    private bool _played;

    internal ReloadPacketV1 Start { get; private set; }
    internal StanagPhase Phase => _order.Phase;
    internal float Age => Time.unscaledTime - _started;
    internal bool Active => _order.Active && Age < Lifetime;

    internal bool Accept(ReloadPacketV1 packet)
    {
        if (!_order.Accept(packet.Stanag)) return false;
        switch (packet.Stanag.Phase)
        {
            case StanagPhase.Start:
                Stop();
                Start = packet;
                _started = Time.unscaledTime - packet.Age;
                _played = false;
                break;
            case StanagPhase.Committed:
                _view?.Confirm();
                break;
            case StanagPhase.Finished:
                // Playback is already released; a slow-loading observer still finishes the authored exit.
                if (_view == null) Stop();
                break;
            case StanagPhase.Cancelled:
                Stop();
                break;
        }
        return true;
    }

    internal void Tick(Player actor, float deltaTime)
    {
        if (Age >= Lifetime || !actor.HealthController.IsAlive) { Stop(); return; }
        if (_view != null)
        {
            if (!_view.Valid) Stop();
            else _view.Tick(deltaTime);
        }
        else if (Active && !_played && _prepare == null && !actor.UsedSimplifiedSkeleton &&
            actor.HandsController is Player.FirearmController or Player.EmptyHandsController &&
            !actor.HandsController.Destroyed)
        {
            var visual = Start.Stanag;
            if (!actor.InventoryController.TryFindItem(visual.MagazineId, out var target) || target is not Magazine magazine ||
                !StanagRules.SupportsMagazine(magazine.TemplateId) ||
                !actor.InventoryController.TryFindItem(visual.ClipId, out var source) || source is not Magazine clip ||
                clip.TemplateId != StanagRules.ClipTemplate) return;
            Prepare(actor, magazine, clip, visual.Ammunition!);
        }
    }

    private async void Prepare(Player actor, Magazine magazine, Magazine clip, string[] ammunition)
    {
        var request = _prepare = new CancellationTokenSource();
        var cancellation = request.Token;
        _played = true;
        try
        {
            var factory = Singleton<ItemFactory>.Instance;
            var rounds = ammunition.Select(id => factory.CreateItem(MongoID.Generate(false).ToString(), id, null)).OfType<Ammo>().ToArray();
            if (rounds.Length != ammunition.Length || rounds.Any(round => !clip.CheckCompatibility(round) || !magazine.CheckCompatibility(round))) return;
            var view = await MagazinePresentation.Prepare(actor, magazine, clip, rounds, RestoreObserverHands, cancellation);
            if (!ReferenceEquals(request, _prepare) || !Active || view == null) { view?.Dispose(); return; }
            _view = view;
            view.Finished += Stop;
            view.Failed += Stop;
            view.Play(Age, Phase == StanagPhase.Committed);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(request, _prepare))
            {
                StripperReload.Log.LogWarning("Observed magazine presentation could not start: " + error.Message);
                Stop();
            }
        }
        finally
        {
            if (ReferenceEquals(_prepare, request)) _prepare = null;
            request.Dispose();
        }
    }

    internal void Sweep()
    {
        if (Age >= Lifetime) Stop();
    }

    internal void TraceFrame() => _view?.TraceFrame();

    private static void RestoreObserverHands(Player actor, Item? previous)
    {
        if (actor is not ObservedPlayer observer) return;
        if (previous is Weapon weapon && ReferenceEquals(weapon.Owner, actor.InventoryController) &&
            weapon.IsChildOf(actor.InventoryController.Inventory.Equipment))
            _ = observer.SpawnController(ObservedFirearmController.Create(observer, weapon));
        else RestoreEmpty(observer);
    }

    internal void Stop()
    {
        _prepare?.Cancel();
        _prepare = null;
        _view?.Dispose();
        _view = null;
    }

    public void Dispose() => Stop();
}
