using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using HarmonyLib;
using StripperClips.Pure;
using UnityEngine;
using InventoryOperation = EFT.InventoryLogic.Operations.AbstractOperation;

namespace StripperClips.Fika;

// Loaded by the base plugin only after the supported Fika assembly is present.
public sealed class FikaIntegration : MonoBehaviour
{
    private static FikaIntegration? _instance;
    private readonly Harmony _patches = new Harmony("itsripper.stripperclips.fika");
    private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
    private readonly List<NetPeer> _connected = new List<NetPeer>();
    private readonly Dictionary<int, int> _peerActors = new Dictionary<int, int>();
    private readonly Dictionary<int, ObservedReload> _remote = new Dictionary<int, ObservedReload>();
    private readonly Dictionary<int, ObservedStanag> _stanagRemote = new Dictionary<int, ObservedStanag>();
    private readonly Dictionary<int, NetPeer> _stanagPeers = new Dictionary<int, NetPeer>();
    private readonly HashSet<string> _rigWarnings = new HashSet<string>();
    private ConditionalWeakTable<InventoryOperation, StrongBox<ReloadVisual>> _queuedTransfers =
        new ConditionalWeakTable<InventoryOperation, StrongBox<ReloadVisual>>();
    private IFikaNetworkManager? _manager;
    private ManualLogSource _log = null!;
    private bool _ready, _hasHost, _helloSent, _registered;
    private bool _stanagReady;
    private ReloadPacketV1 _stanagLocal;
    private StanagPhase _stanagLocalPhase;
    private float _stanagLocalAt;
    private ReloadPacketV1 _localStart;
    private ReloadPacketV1? _localUpdate;
    private float _localStartedAt, _nextSweep;

    private void Awake()
    {
        _log = BepInEx.Logging.Logger.CreateLogSource("StripperClips Fika");
        try
        {
            var pass = AccessTools.Method(typeof(ObservedPlayer), "ObservedVisualPass", new[] { typeof(float), typeof(int) })
                ?? throw new MissingMethodException("ObservedPlayer.ObservedVisualPass");
            var execute = AccessTools.DeclaredMethod(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.Execute),
                new[] { typeof(IInventoryOperation), typeof(Callback) }) ?? throw new MissingMethodException("Idling.Execute");
            _instance = this;
            _patches.Patch(pass, prefix: new HarmonyMethod(typeof(FikaIntegration), nameof(BeforeObservedPass)),
                postfix: new HarmonyMethod(typeof(FikaIntegration), nameof(AfterObservedPass)));
            _patches.Patch(execute, prefix: new HarmonyMethod(typeof(FikaIntegration), nameof(BeforeHandsInventory)));
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnManagerDestroyed);
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            FikaEventDispatcher.SubscribeEvent<PeerConnectedEvent>(OnConnected);
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(OnDisconnected);
            FikaRuntime.Ready = CanReload;
            FikaRuntime.Connected = Connected;
            FikaRuntime.StanagReady = player => _stanagReady && CanReload(player);
            FikaRuntime.StanagChanged += SendLocalStanag;
            FikaRuntime.VisualChanged += SendLocal;
            FikaRuntime.RaidEnded += ClearVisuals;
            _log.LogInfo("Fika 2.4.6 integration loaded.");
        }
        catch (Exception error)
        {
            _log.LogError("StripperClips Fika integration could not start: " + error);
            OnDestroy();
            enabled = false;
        }
    }

    private void OnDestroy()
    {
        if (!ReferenceEquals(_instance, this)) return;
        _instance = null;
        FikaRuntime.Ready = null;
        FikaRuntime.Connected = null;
        FikaRuntime.StanagReady = null;
        FikaRuntime.StanagChanged -= SendLocalStanag;
        FikaRuntime.VisualChanged -= SendLocal;
        FikaRuntime.RaidEnded -= ClearVisuals;
        FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
        FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnManagerDestroyed);
        FikaEventDispatcher.UnsubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
        FikaEventDispatcher.UnsubscribeEvent<PeerConnectedEvent>(OnConnected);
        FikaEventDispatcher.UnsubscribeEvent<PeerDisconnectedEvent>(OnDisconnected);
        if (_registered) _manager?.UnregisterPacket<ReloadPacketV1>();
        ResetSession();
        _patches.UnpatchSelf();
    }

    private void OnManagerCreated(FikaNetworkManagerCreatedEvent evt)
    {
        if (_registered) _manager?.UnregisterPacket<ReloadPacketV1>();
        ResetSession();
        var manager = evt.Manager;
        try
        {
            manager.RegisterPacket<ReloadPacketV1, NetPeer>((packet, peer) =>
            {
                if (!ReferenceEquals(manager, _manager)) return;
                try { Receive(packet, peer); }
                catch (Exception error) { _log.LogWarning("Fika reload packet rejected: " + error.Message); }
            });
            _manager = manager;
            _registered = true;
            // Peers that connected before this event have not been enrolled.
            RefreshReadiness();
        }
        catch (Exception error) { _log.LogError("Could not register the StripperClips Fika packet: " + error); }
    }

    private void OnManagerDestroyed(FikaNetworkManagerDestroyedEvent evt)
    {
        if (ReferenceEquals(evt.Manager, _manager)) ResetSession();
    }

    private void ResetSession()
    {
        _manager = null;
        _ready = _hasHost = _helloSent = _registered = false;
        _stanagReady = false;
        _peers.Clear();
        _stanagPeers.Clear();
        _peerActors.Clear();
        ClearVisuals();
    }

    private void ClearVisuals()
    {
        foreach (var entry in _remote.Values) entry.Dispose();
        _remote.Clear();
        _rigWarnings.Clear();
        _queuedTransfers = new ConditionalWeakTable<InventoryOperation, StrongBox<ReloadVisual>>();
        _localStart = default;
        _localUpdate = null;
        foreach (var presentation in _stanagRemote.Values) presentation.Dispose();
        _stanagRemote.Clear();
        _stanagLocal = default;
        _stanagLocalPhase = default;
    }

    private bool CanReload(Player player) => _ready && Connected(player);

    private bool Connected(Player player) =>
        (_manager is FikaServer || _hasHost) && CoopHandler.TryGetCoopHandler(out var coop) &&
        ReferenceEquals(coop.MyPlayer, player);

    private void OnConnected(PeerConnectedEvent evt)
    {
        if (!ReferenceEquals(evt.NetworkManager, _manager)) return;
        if (_manager is FikaClient) SendHello();
        else RefreshReadiness();
    }

    private void OnRaidStarted(FikaRaidStartedEvent evt) => SendHello();

    private void SendHello()
    {
        // Send only one capability probe to the host. Compatible peers alone receive reload packets.
        if (_helloSent || !(_manager is FikaClient client) || client.ServerConnection == null) return;
        _helloSent = true;
        var packet = new ReloadPacketV1 { Kind = ReloadPacketKind.Hello, Protocol = ReloadPacketV1.CurrentProtocol };
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        packet.Kind = ReloadPacketKind.StanagHello;
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void OnDisconnected(PeerDisconnectedEvent evt)
    {
        if (!ReferenceEquals(evt.NetworkManager, _manager)) return;
        if (_manager is FikaClient)
        {
            _ready = _stanagReady = _hasHost = _helloSent = false;
            ClearVisuals();
            return;
        }
        _peers.Remove(evt.Peer.Id);
        _stanagPeers.Remove(evt.Peer.Id);
        RefreshReadiness();
        if (!_peerActors.TryGetValue(evt.Peer.Id, out var netId)) return;
        _peerActors.Remove(evt.Peer.Id);
        if (_stanagRemote.TryGetValue(netId, out var stanag))
        {
            if (stanag.Active)
            {
                var stopStanag = stanag.Start;
                stopStanag.Stanag = new StanagVisual(stopStanag.Stanag.Action, StanagPhase.Cancelled);
                stanag.Accept(stopStanag);
                BroadcastStanag(stopStanag);
            }
            else stanag.Stop();
        }
        if (!_remote.TryGetValue(netId, out var reload)) return;
        if (reload.Active)
        {
            var stop = reload.StartPacket;
            stop.Visual = new ReloadVisual(stop.Visual.Action, ReloadVisualPhase.Cancelled, stop.Visual.WeaponId);
            Broadcast(stop);
        }
        reload.Dispose();
        _remote.Remove(netId);
    }

    private void SendLocal(Player player, ReloadVisual visual)
    {
        if (!Connected(player) || !(player is FikaPlayer owner)) return;
        var packet = new ReloadPacketV1 { Kind = ReloadPacketKind.Visual, NetId = owner.NetId, Visual = visual };
        if (visual.Phase == ReloadVisualPhase.Start)
        {
            _localStart = packet;
            _localStartedAt = Time.unscaledTime;
            _localUpdate = null;
        }
        else if (visual.Action == _localStart.Visual.Action)
        {
            if (ReloadVisualOrder.CanReplay(visual.Phase)) _localUpdate = packet;
            else
            {
                _localStart = default;
                _localUpdate = null;
            }
        }
        if (_manager is FikaServer) Broadcast(packet);
        else _manager?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void Receive(ReloadPacketV1 packet, NetPeer peer)
    {
        if (packet.Kind >= ReloadPacketKind.StanagHello && packet.Kind <= ReloadPacketKind.StanagVisual)
        {
            ReceiveStanag(packet, peer);
            return;
        }
        if (_manager is FikaServer)
        {
            if (packet.Kind == ReloadPacketKind.Hello)
            {
                if (packet.Protocol != ReloadPacketV1.CurrentProtocol)
                {
                    // An unenrolled peer keeps co-op clip reloads blocked for everyone.
                    _log.LogWarning($"FIKA_RELOAD role=host step=reject peer={peer.Id} protocol={packet.Protocol} " +
                        $"expected={ReloadPacketV1.CurrentProtocol} reason=stripperclips_version_mismatch");
                    return;
                }
                _peers[peer.Id] = peer;
                RefreshReadiness();
                SendActiveTo(peer);
                return;
            }
            if (packet.Kind != ReloadPacketKind.Visual) return;
            if (!_peers.TryGetValue(peer.Id, out var enrolled) || !ReferenceEquals(peer, enrolled) ||
                !OwnsActor(peer, packet.NetId))
            {
                if (packet.Visual.Phase == ReloadVisualPhase.Start)
                    _log.LogWarning($"FIKA_RELOAD role=host step=reject actor={packet.NetId} peer={peer.Id} " +
                        $"boundActor={peer.Player?.NetId} reason=peer_actor_not_verified");
                return;
            }
            packet.Age = 0;
            if (Apply(packet)) Broadcast(packet, peer);
        }
        else if (_manager is FikaClient client && ReferenceEquals(client.ServerConnection, peer))
        {
            if (packet.Kind == ReloadPacketKind.Ready || packet.Kind == ReloadPacketKind.Blocked)
            {
                _hasHost = true;
                _ready = packet.Kind == ReloadPacketKind.Ready;
            }
            else if (_hasHost && packet.Kind == ReloadPacketKind.Visual) Apply(packet);
        }
    }

    private void RefreshReadiness()
    {
        if (!(_manager is FikaServer host)) return;
        _connected.Clear();
        host.NetServer.GetConnectedPeers(_connected);
        _ready = CoopReloadPolicy.AllPeersEnrolled(_connected, _peers, peer => peer.Id);
        Broadcast(new ReloadPacketV1 { Kind = _ready ? ReloadPacketKind.Ready : ReloadPacketKind.Blocked });
        _stanagReady = _ready && CoopReloadPolicy.AllPeersEnrolled(_connected, _stanagPeers, peer => peer.Id);
        BroadcastStanag(new ReloadPacketV1 { Kind = _stanagReady ? ReloadPacketKind.StanagReady : ReloadPacketKind.StanagBlocked });
    }

    private bool OwnsActor(NetPeer peer, int netId)
    {
        if (!CoopHandler.TryGetCoopHandler(out var coop) ||
            !coop.Players.TryGetValue(netId, out var actor) || !(actor is ObservedPlayer) || actor.IsAI ||
            !ReferenceEquals(peer.Player, actor)) return false;
        if (_peerActors.TryGetValue(peer.Id, out var bound)) return bound == netId;
        if (_peerActors.ContainsValue(netId)) return false;
        _peerActors.Add(peer.Id, netId);
        return true;
    }

    private bool Apply(ReloadPacketV1 packet)
    {
        var visual = packet.Visual;
        if (visual.Action <= 0 || visual.WeaponId.Length != 24 || packet.NetId < 0 ||
            float.IsNaN(packet.Age) || packet.Age < 0 || packet.Age >= ObservedReload.Lifetime ||
            (visual.Phase == ReloadVisualPhase.Start && (visual.ClipId.Length != 24 || visual.Count < 1 || visual.Count > 10)))
        {
            _log.LogWarning($"Invalid reload animation packet for actor {packet.NetId}, action {visual.Action}.");
            return false;
        }
        if (CoopHandler.TryGetCoopHandler(out var coop) && coop.MyPlayer?.NetId == packet.NetId) return false;
        if (!_remote.TryGetValue(packet.NetId, out var state)) _remote.Add(packet.NetId, state = new ObservedReload());
        var hadTransfer = state.HasTransferContext;
        if (!state.Accept(packet)) return false;
        if (hadTransfer && (visual.Phase == ReloadVisualPhase.Closed || visual.Phase == ReloadVisualPhase.Settled) &&
            _manager is FikaClient client &&
            coop != null && coop.Players.TryGetValue(packet.NetId, out var actor) &&
            actor.HandsController is Player.FirearmController hands)
        {
            // Fika can receive an ordered inventory packet before it can execute the queued operation.
            var start = state.StartPacket.Visual;
            foreach (var operation in client.InventoryOperations)
                if (ReferenceEquals(operation.Controller, actor.InventoryController) && operation is IOneItemOperation move &&
                    MatchesTransfer(move, hands.Item, start.ClipId, start.WeaponId))
                    _queuedTransfers.GetValue(operation, _ => new StrongBox<ReloadVisual>(start));
        }
        return true;
    }

    private void SendActiveTo(NetPeer peer)
    {
        if (_localStart.Kind == ReloadPacketKind.Visual)
        {
            var packet = _localStart;
            packet.Age = Time.unscaledTime - _localStartedAt;
            if (packet.Age < ObservedReload.Lifetime)
            {
                SendTo(peer, packet);
                if (_localUpdate.HasValue) SendTo(peer, _localUpdate.Value);
            }
        }
        foreach (var state in _remote.Values)
        {
            if (!state.CanReplay) continue;
            var packet = state.StartPacket;
            packet.Age = state.Age;
            SendTo(peer, packet);
            if (state.UpdatePacket.HasValue) SendTo(peer, state.UpdatePacket.Value);
        }
    }

    private void Broadcast(ReloadPacketV1 packet, NetPeer? except = null)
    {
        foreach (var peer in _peers.Values)
            if (!ReferenceEquals(peer, except)) SendTo(peer, packet);
    }

    private void SendTo(NetPeer peer, ReloadPacketV1 packet) =>
        _manager?.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);

    private static bool BeforeHandsInventory(Player.FirearmController.Idling __instance, IInventoryOperation __0, Callback __1)
    {
        var instance = _instance;
        if (instance == null || instance._manager == null || !(__0 is ISubOperation sub) ||
            !(sub.BaseInventoryOperation is IOneItemOperation move) ||
            !(move is MoveOperation || move is SplitOperation) || !(move.Item1 is Ammo) ||
            !(__instance.Controller._player is FikaPlayer owner)) return true;

        string clipId, weaponId;
        if (owner is ObservedPlayer)
        {
            if (move is InventoryOperation operation && instance._queuedTransfers.TryGetValue(operation, out var queued))
            {
                clipId = queued.Value.ClipId;
                weaponId = queued.Value.WeaponId;
            }
            else
            {
                if (!instance._remote.TryGetValue(owner.NetId, out var state) || !state.HasTransferContext) return true;
                clipId = state.StartPacket.Visual.ClipId;
                weaponId = state.StartPacket.Visual.WeaponId;
            }
        }
        else if (!StripperReload.TryGetCoopTransfer(owner, out clipId, out weaponId)) return true;
        if (!MatchesTransfer(move, __instance.Controller.Item, clipId, weaponId)) return true;

        // The native transaction owns the mutation. EFT's hands suboperation would start a second reload or reject it.
        __1?.Succeed();
        return false;
    }

    private static bool MatchesTransfer(IOneItemOperation move, Weapon weapon, string clipId, string weaponId)
    {
        if (!(move is MoveOperation || move is SplitOperation) || !(move.Item1 is Ammo)) return false;
        var magazine = weapon.GetCurrentMagazine();
        if (weapon.Id.ToString() != weaponId || magazine == null || weapon.Chambers.Length != 1) return false;
        var from = move.From1?.Container;
        var to = move.To1?.Container;
        bool IsClip(object? container) => container is StackSlot stack && stack.ParentItem is Magazine clip &&
            clip.Id.ToString() == clipId && Catalog.IsClip(clip.TemplateId);
        return (ReferenceEquals(to, magazine.Cartridges) && IsClip(from)) ||
            (ReferenceEquals(from, magazine.Cartridges) && (IsClip(to) || ReferenceEquals(to, weapon.Chambers[0])));
    }

    private static void BeforeObservedPass(ObservedPlayer __instance, float __0)
    {
        var instance = _instance;
        // A headless host renders nothing, but it still relays and tracks reloads for late joiners.
        if (instance == null || FikaBackendUtils.IsHeadless) return;
        if (instance._stanagRemote.TryGetValue(__instance.NetId, out var magazine))
        {
            try { magazine.Tick(__instance, __0); }
            catch (Exception error) { instance._log.LogWarning("Remote magazine animation stopped: " + error.Message); magazine.Stop(); }
        }
        if (!instance._remote.TryGetValue(__instance.NetId, out var reload) || !reload.Active) return;
        try { reload.Tick(__instance, __0); }
        catch (Exception error)
        {
            if (instance._rigWarnings.Add(error.Message))
                instance._log.LogWarning("Remote clip animation unavailable: " + error.Message);
            reload.StopAnimation();
        }
    }

    private static void AfterObservedPass(ObservedPlayer __instance, float __0)
    {
        var instance = _instance;
        if (instance == null || !instance._stanagRemote.TryGetValue(__instance.NetId, out var magazine)) return;
        try { magazine.TraceFrame(); }
        catch (Exception error) { instance._log.LogWarning("Remote magazine animation stopped: " + error.Message); magazine.Stop(); }
    }

    private void Update()
    {
        if ((_remote.Count == 0 && _stanagRemote.Count == 0) || Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + .25f;
        foreach (var reload in _remote.Values) reload.Sweep();
        foreach (var magazine in _stanagRemote.Values) magazine.Sweep();
    }

    private void SendLocalStanag(Player player, StanagVisual visual)
    {
        if (!Connected(player) || player is not FikaPlayer owner) return;
        var packet = new ReloadPacketV1 { Kind = ReloadPacketKind.StanagVisual, NetId = owner.NetId, Stanag = visual };
        if (visual.Phase == StanagPhase.Start)
        {
            _stanagLocal = packet;
            _stanagLocalAt = Time.unscaledTime;
        }
        _stanagLocalPhase = visual.Phase;
        if (_manager is FikaServer) BroadcastStanag(packet);
        else _manager?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void ReceiveStanag(ReloadPacketV1 packet, NetPeer peer)
    {
        if (_manager is FikaServer)
        {
            if (packet.Kind == ReloadPacketKind.StanagHello && _peers.TryGetValue(peer.Id, out var enrolled) && ReferenceEquals(peer, enrolled))
            {
                _stanagPeers[peer.Id] = peer;
                RefreshReadiness();
                ReplayStanag(peer);
            }
            if (packet.Kind != ReloadPacketKind.StanagVisual || !_stanagPeers.TryGetValue(peer.Id, out var capable) ||
                !ReferenceEquals(peer, capable) || !OwnsActor(peer, packet.NetId) ||
                packet.Stanag.Phase == StanagPhase.Start && !_stanagReady) return;
            packet.Age = 0;
            if (ApplyStanag(packet)) BroadcastStanag(packet, peer);
        }
        else if (_manager is FikaClient client && ReferenceEquals(client.ServerConnection, peer))
        {
            if (packet.Kind == ReloadPacketKind.StanagReady || packet.Kind == ReloadPacketKind.StanagBlocked)
                _stanagReady = packet.Kind == ReloadPacketKind.StanagReady;
            else if (_hasHost && packet.Kind == ReloadPacketKind.StanagVisual) ApplyStanag(packet);
        }
    }

    private bool ApplyStanag(ReloadPacketV1 packet)
    {
        var visual = packet.Stanag;
        if (packet.NetId < 0 || visual.Action <= 0 || float.IsNaN(packet.Age) || packet.Age < 0 || packet.Age >= ObservedStanag.Lifetime ||
            CoopHandler.TryGetCoopHandler(out var coop) && coop.MyPlayer?.NetId == packet.NetId) return false;
        if (visual.Phase == StanagPhase.Start && (!IsItemId(visual.MagazineId) || !IsItemId(visual.ClipId) ||
            visual.Ammunition is not { Length: >= 1 and <= StanagRules.ClipCapacity } rounds || Array.Exists(rounds, id => !IsItemId(id)))) return false;
        if (!_stanagRemote.TryGetValue(packet.NetId, out var observed))
            _stanagRemote.Add(packet.NetId, observed = new ObservedStanag());
        return observed.Accept(packet);
    }

    private static bool IsItemId(string id)
    {
        if (id.Length != 24) return false;
        foreach (var digit in id) if (!Uri.IsHexDigit(digit)) return false;
        return true;
    }

    private void BroadcastStanag(ReloadPacketV1 packet, NetPeer? except = null)
    {
        foreach (var peer in _stanagPeers.Values)
            if (!ReferenceEquals(peer, except)) SendTo(peer, packet);
    }

    private void ReplayStanag(NetPeer peer)
    {
        Send(_stanagLocal, _stanagLocalPhase, Time.unscaledTime - _stanagLocalAt);
        foreach (var observer in _stanagRemote.Values)
            if (observer.Active) Send(observer.Start, observer.Phase, observer.Age);

        void Send(ReloadPacketV1 packet, StanagPhase phase, float age)
        {
            if (phase != StanagPhase.Start && phase != StanagPhase.Committed || age >= ObservedStanag.Lifetime) return;
            packet.Age = age;
            SendTo(peer, packet);
            if (phase != StanagPhase.Committed) return;
            packet.Stanag = new StanagVisual(packet.Stanag.Action, phase);
            SendTo(peer, packet);
        }
    }
}
