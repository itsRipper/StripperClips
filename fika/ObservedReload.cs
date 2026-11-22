using System;
using EFT;
using Fika.Core.Main.Players;
using StripperClips.Pure;
using UnityEngine;

namespace StripperClips.Fika;

internal sealed class ObservedReload : IDisposable
{
    internal const float Lifetime = 12f;
    // Slow stack moves must still bypass EFT's hands step after the animation has expired.
    private const float TransferWindow = 30f;
    private readonly ReloadVisualOrder _order = new ReloadVisualOrder();
    private RemoteClipVisual? _clip;
    private Player.FirearmController? _hands;
    private ObservedPlayer? _player;
    private float _startedAt;
    private bool _attempted, _stateApplied, _closeApplied;

    internal ReloadPacketV1 StartPacket { get; private set; }
    internal ReloadPacketV1? UpdatePacket { get; private set; }
    internal bool Active { get; private set; }
    internal float Age => Time.unscaledTime - _startedAt;
    internal bool CanReplay => Active && _order.CanReplayStart && Age < Lifetime;
    internal bool HasTransferContext => StartPacket.Kind == ReloadPacketKind.Visual && _order.HasTransferContext && Age < TransferWindow;

    internal bool Accept(ReloadPacketV1 packet)
    {
        var visual = packet.Visual;
        if (visual.Phase != ReloadVisualPhase.Start && visual.Phase != ReloadVisualPhase.Cancelled &&
            (visual.Action != StartPacket.Visual.Action || visual.WeaponId != StartPacket.Visual.WeaponId)) return false;
        if (visual.Phase == ReloadVisualPhase.Loaded && !Active) return false;
        if (!_order.Accept(visual.Action, visual.Phase)) return false;
        if (visual.Phase == ReloadVisualPhase.Start)
        {
            Dispose();
            StartPacket = packet;
            UpdatePacket = null;
            _startedAt = Time.unscaledTime - packet.Age;
            _attempted = _stateApplied = _closeApplied = false;
            Active = true;
        }
        else if (visual.Phase == ReloadVisualPhase.Cancelled) Dispose();
        else if (Active && visual.Phase != ReloadVisualPhase.Settled)
        {
            UpdatePacket = packet;
            _stateApplied = false;
            _clip?.SetPhase(visual.Phase);
        }
        return true;
    }

    internal void Tick(ObservedPlayer player, float deltaTime)
    {
        if (!Active) return;
        if (Age >= Lifetime || !player.HealthController.IsAlive) { Dispose(); return; }
        if (!(player.HandsController is Player.FirearmController hands) ||
            hands.Item.Id.ToString() != StartPacket.Visual.WeaponId ||
            (_hands != null && !ReferenceEquals(hands, _hands)))
        {
            if (_hands != null) Dispose();
            return;
        }
        _player = player;
        _hands = hands;

        ApplyReplicatedVisualState(hands);
        if (!(hands.CurrentOperation is Player.FirearmController.Idling) ||
            hands.CurrentOperation is Player.FirearmController.UtilityOperation)
        {
            if (_attempted) StopAnimation();
            return;
        }
        if (!_attempted)
        {
            _attempted = true;
            _clip = RemoteClipVisual.Start(player, hands, StartPacket.Visual,
                UpdatePacket?.Visual.Phase ?? ReloadVisualPhase.Start, Age);
        }
        if (_clip == null) return;
        _clip.Tick(deltaTime);
        if (!_clip.IsPlaying) StopAnimation();
    }

    private void ApplyReplicatedVisualState(Player.FirearmController hands)
    {
        if (_stateApplied || !UpdatePacket.HasValue) return;
        var visual = UpdatePacket.Value.Visual;
        var weapon = hands.Item;
        hands.FirearmsAnimator.SetAmmoOnMag(weapon.GetCurrentMagazine()?.Count ?? 0);
        if (visual.Phase == ReloadVisualPhase.Closed)
        {
            if (!_closeApplied)
            {
                _closeApplied = true;
                if (weapon.IsBoltCatch) hands.FirearmsAnimator.SetBoltCatch(false);
            }
            // The native inventory packet may arrive after the animation message.
            if (visual.Chambered && weapon.ChamberAmmoCount == 0) return;
            if (visual.Chambered) StripperReload.RefreshChamberVisual(hands);
            hands.FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
        }
        _stateApplied = true;
    }

    internal void Sweep()
    {
        if (!Active) return;
        if (Age >= Lifetime || (_hands != null && (_player == null || !_player.HealthController.IsAlive ||
            !ReferenceEquals(_player.HandsController, _hands)))) Dispose();
    }

    internal void StopAnimation()
    {
        _clip?.Dispose();
        _clip = null;
    }

    public void Dispose()
    {
        Active = false;
        StopAnimation();
        _hands = null;
        _player = null;
    }
}
