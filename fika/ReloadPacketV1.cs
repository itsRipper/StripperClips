using Fika.Core.Networking.LiteNetLib.Utils;
using StripperClips.Pure;

namespace StripperClips.Fika;

internal enum ReloadPacketKind : byte { Hello, Ready, Visual, Blocked, StanagHello, StanagReady, StanagBlocked, StanagVisual }

internal struct ReloadPacketV1 : INetSerializable
{
    // Raise when the payload changes. The host enrols only peers on the same protocol.
    internal const byte CurrentProtocol = 1;

    public ReloadPacketKind Kind;
    public byte Protocol;
    public int NetId;
    public ReloadVisual Visual;
    public StanagVisual Stanag;
    public float Age;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Kind);
        if (Kind == ReloadPacketKind.Hello) writer.Put(Protocol);
        if (Kind == ReloadPacketKind.StanagVisual)
        {
            writer.Put(NetId);
            writer.Put(Stanag.Action);
            writer.Put((byte)Stanag.Phase);
            if (Stanag.Phase != StanagPhase.Start) return;
            writer.Put(Stanag.MagazineId);
            writer.Put(Stanag.ClipId);
            var rounds = Stanag.Ammunition!;
            writer.Put((byte)rounds.Length);
            foreach (var round in rounds) writer.Put(round);
            writer.Put(Age);
            return;
        }
        if (Kind != ReloadPacketKind.Visual) return;
        writer.Put(NetId);
        writer.Put(Visual.Action);
        writer.Put((byte)Visual.Phase);
        writer.Put(Visual.WeaponId);
        if (Visual.Phase == ReloadVisualPhase.Start)
        {
            writer.Put(Visual.ClipId);
            writer.Put((byte)Visual.Count);
            writer.Put(Visual.Open);
            writer.Put(Age);
        }
        if (Visual.Phase == ReloadVisualPhase.Closed) writer.Put(Visual.Chambered);
    }

    public void Deserialize(NetDataReader reader)
    {
        // Fika reuses the same packet instance for this subscription.
        this = default;
        Kind = (ReloadPacketKind)reader.GetByte();
        if (Kind == ReloadPacketKind.Hello) Protocol = reader.GetByte();
        if (Kind == ReloadPacketKind.StanagVisual)
        {
            NetId = reader.GetInt();
            var id = reader.GetLong();
            var stage = (StanagPhase)reader.GetByte();
            if (stage != StanagPhase.Start) { Stanag = new StanagVisual(id, stage); return; }
            var magazineId = reader.GetString(32);
            var clipId = reader.GetString(32);
            var count = reader.GetByte();
            if (count < 1 || count > StanagRules.ClipCapacity) return;
            var rounds = new string[count];
            for (var i = 0; i < count; i++) rounds[i] = reader.GetString(32);
            Stanag = new StanagVisual(id, stage, magazineId, clipId, rounds);
            Age = reader.GetFloat();
            return;
        }
        if (Kind != ReloadPacketKind.Visual) return;
        NetId = reader.GetInt();
        var action = reader.GetLong();
        var phase = (ReloadVisualPhase)reader.GetByte();
        var weapon = reader.GetString(32);
        if (phase == ReloadVisualPhase.Start)
        {
            Visual = new ReloadVisual(action, phase, weapon, reader.GetString(32), reader.GetByte(), reader.GetBool());
            Age = reader.GetFloat();
        }
        else Visual = new ReloadVisual(action, phase, weapon, chambered: phase == ReloadVisualPhase.Closed && reader.GetBool());
    }
}
