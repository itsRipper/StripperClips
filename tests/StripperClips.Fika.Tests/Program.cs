using Fika.Core.Networking.LiteNetLib.Utils;
using StripperClips.Fika;
using StripperClips.Pure;

const string weaponId = "1234567890abcdef12345678";
const string clipId = "abcdef1234567890abcdef12";
const long action = 638880114960000000;

ReloadPacketV1 RoundTrip(ReloadPacketV1 packet)
{
    var writer = new NetDataWriter();
    packet.Serialize(writer);
    var reader = new NetDataReader(writer.CopyData());
    var decoded = new ReloadPacketV1();
    decoded.Deserialize(reader);
    if (reader.AvailableBytes != 0 || packet.Kind != decoded.Kind)
        throw new InvalidOperationException("Mismatched packet framing: " + packet.Kind);
    return decoded;
}

foreach (var kind in new[] { ReloadPacketKind.Ready, ReloadPacketKind.Blocked,
             ReloadPacketKind.StanagHello, ReloadPacketKind.StanagReady, ReloadPacketKind.StanagBlocked })
    RoundTrip(new ReloadPacketV1 { Kind = kind });

if (RoundTrip(new ReloadPacketV1 { Kind = ReloadPacketKind.Hello, Protocol = ReloadPacketV1.CurrentProtocol }).Protocol !=
    ReloadPacketV1.CurrentProtocol)
    throw new InvalidOperationException("Hello did not carry the protocol version.");

var start = RoundTrip(new ReloadPacketV1
{
    Kind = ReloadPacketKind.Visual,
    NetId = 9,
    Visual = new ReloadVisual(action, ReloadVisualPhase.Start, weaponId, clipId, 3, open: true),
    Age = 1.25f,
});
if (start.NetId != 9 || start.Visual.Action != action || start.Visual.Phase != ReloadVisualPhase.Start ||
    start.Visual.WeaponId != weaponId || start.Visual.ClipId != clipId || start.Visual.Count != 3 ||
    !start.Visual.Open || start.Age != 1.25f)
    throw new InvalidOperationException("Start and late-join snapshot did not round-trip.");

foreach (var phase in new[] { ReloadVisualPhase.Loaded, ReloadVisualPhase.Closed, ReloadVisualPhase.Cancelled, ReloadVisualPhase.Settled })
{
    var sent = new ReloadVisual(action, phase, weaponId, chambered: phase == ReloadVisualPhase.Closed);
    var decoded = RoundTrip(new ReloadPacketV1 { Kind = ReloadPacketKind.Visual, NetId = 9, Visual = sent });
    if (decoded.NetId != 9 || decoded.Visual.Action != action || decoded.Visual.Phase != phase ||
        decoded.Visual.WeaponId != weaponId || decoded.Visual.Chambered != sent.Chambered)
        throw new InvalidOperationException("Reload phase did not round-trip: " + phase);
}

// Fika's registration path reuses a deserialization target for all phases and peer callbacks.
var startPacket = start;
var closedPacket = new ReloadPacketV1
{
    Kind = ReloadPacketKind.Visual,
    NetId = start.NetId,
    Visual = new ReloadVisual(action, ReloadVisualPhase.Closed, weaponId, chambered: true),
};
var wire = new NetDataWriter();
var sender = new NetPacketProcessor();
sender.WriteNetSerializable(wire, ref startPacket);
sender.WriteNetSerializable(wire, ref closedPacket);
foreach (var direction in new[] { "host-to-client", "client-to-host" })
{
    var receiver = new NetPacketProcessor();
    var received = new List<ReloadPacketV1>();
    receiver.SubscribeNetSerializable<ReloadPacketV1, string>((packet, peer) =>
    {
        if (peer != direction) throw new InvalidOperationException("Incorrect peer on dispatch.");
        received.Add(packet);
    });
    receiver.ReadAllPackets(new NetDataReader(wire.CopyData()), direction);
    if (received.Count != 2 || received[0].Age != start.Age || received[1].Age != 0 ||
        received[1].NetId != start.NetId || received[1].Visual.Phase != ReloadVisualPhase.Closed ||
        !received[1].Visual.Chambered)
        throw new InvalidOperationException("Dispatcher reused stale fields: " + direction);
}

var rounds = new[] { "54527a984bdc2d4e668b4567", "59e6920f86f77411d82aa167", "54527a984bdc2d4e668b4567" };
foreach (var phase in Enum.GetValues<StanagPhase>())
{
    var packet = RoundTrip(new ReloadPacketV1
    {
        Kind = ReloadPacketKind.StanagVisual, NetId = 4,
        Stanag = new StanagVisual(action, phase, weaponId, clipId, rounds), Age = 1.5f,
    });
    if (packet.NetId != 4 || packet.Stanag.Action != action || packet.Stanag.Phase != phase ||
        (phase == StanagPhase.Start
            ? packet.Stanag.MagazineId != weaponId || packet.Stanag.ClipId != clipId ||
              packet.Stanag.Ammunition == null || !packet.Stanag.Ammunition.SequenceEqual(rounds) || packet.Age != 1.5f
            : packet.Stanag.Ammunition != null || packet.Age != 0))
        throw new InvalidOperationException("Magazine packet did not round-trip: " + phase);
}

Console.WriteLine("StripperClips packet round trips against Fika.Core 2.4.6: all passed");
