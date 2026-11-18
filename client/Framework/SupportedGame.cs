using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace StripperClips.Framework;

public static class SupportedGame
{
    public const string VerifiedBuild = "0.16.9.40743";

    // Unity's Mono truncates the FileVersion string. Use the numeric fields for the build check.
    public static string InstalledBuild()
    {
        var executable = Path.Combine(BepInEx.Paths.GameRootPath, "EscapeFromTarkov.exe");
        if (!File.Exists(executable)) return "";
        var info = FileVersionInfo.GetVersionInfo(executable);
        return info.FileMajorPart + "." + info.FileMinorPart + "." + info.FileBuildPart + "." + info.FilePrivatePart;
    }

    public static bool Check(ManualLogSource log, string supportedBuild = VerifiedBuild)
    {
        var installed = InstalledBuild();
        if (installed == supportedBuild) return true;
        var executable = Path.Combine(BepInEx.Paths.GameRootPath, "EscapeFromTarkov.exe");
        var text = File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "" : "missing";
        log.LogError(TraceLine.Format("FND_UNSUPPORTED_GAME", ("installed", installed), ("file_version", text), ("supported", supportedBuild)));
        return false;
    }
}
