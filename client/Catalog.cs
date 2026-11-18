using System.Collections.Generic;

namespace StripperClips;

internal static class Catalog
{
    public const string Clip762x54R = "6738a1c0f1e2d3c4b5a69781";
    public const string Clip762x39 = "6738a1c0f1e2d3c4b5a69782";

    // These magazines have a straight feed path. EFT also checks single-round loading support.
    public static readonly Dictionary<string, HashSet<string>> MagazinesFor = new Dictionary<string, HashSet<string>>
    {
        [Clip762x54R] = new HashSet<string>
        {
            "5ae0973a5acfc4001562206c",   // Mosin 5-round internal magazine
            "5bae13ded4351e44f824bf38",   // ProMag Archangel OPFOR 10-round
            "6422e1ea3c0f06190302161a",   // SVT-40 10-round
            "641074a07fd350b98c0b3f96",   // AVT-40 15-round
        },
        [Clip762x39] = new HashSet<string>
        {
            "587df3a12459772c28142567",   // SKS 10-round internal box magazine
            "587df583245977373c4f1129",   // TAPCO 6610 20-round
            "5c5970672e221602b21d7855",   // ProMag AALVX 35-round
        },
    };

    public static readonly Dictionary<string, string> PackageFor = new Dictionary<string, string>
    {
        ["5bfd297f0db834001a669119"] = "mosin_stripper_reload",          // Mosin Infantry
        ["5ae08f0a5acfc408fb1398a1"] = "mosin_sniper_stripper_reload",   // Mosin Sniper: own native bolt moves (bent handle)
        ["574d967124597745970e7c94"] = "sks_stripper_reload",            // TOZ SKS
        ["587e02ff24597743df3deaeb"] = "sks_stripper_reload",            // OP-SKS: same carrier, bolt and controller
        ["643ea5b23db6f9f57107d9fd"] = "svt_stripper_reload",            // SVT-40
        ["6410733d5dd49d77bd07847e"] = "svt_stripper_reload",            // AVT-40: same rig and controller
    };

    public static readonly HashSet<string> BlocksClipGuide = new HashSet<string>
    {
        "6415c694da439c6a97048b56",       // SKS Leapers UTG PRO MTU017 receiver cover mount
        "5b3f7bf05acfc433000ecf6b",       // Mosin Kochetov mount
    };

    // These side mounts block the clip guide only when occupied.
    public static readonly HashSet<string> BlocksClipGuideWhenLoaded = new HashSet<string>
    {
        "587e08ee245977446b4410cf",       // OP-SKS dovetail mount
        "6415d33eda439c6a97048b5b",       // SKS CHOATE scope mount
    };

    public static bool IsClip(string templateId) => MagazinesFor.ContainsKey(templateId);
}
