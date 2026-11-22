# Building StripperClips

## Requirements

- Windows with Windows PowerShell 5.1
- Git with Git LFS (`git lfs install` before cloning): the asset bundles are stored in LFS. The
  build stops if it finds LFS pointer files instead of bundles.
- .NET 10 SDK
- .NET Framework 4.7.2 targeting pack (Visual Studio or the .NET Framework 4.7.2 Developer Pack)
- A local SPT 4.1.6 installation (Escape from Tarkov 0.16.9.40743) for build references
- Fika 2.4.6 installed in the reference SPT folder to build the optional adapter and installation ZIP

## Commands

Replace `<SPT folder>` with the path to your installation.

```powershell
# Build and test standalone StripperClips
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1 -SptRoot "<SPT folder>"

# Build the adapter and run the Fika tests as well
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1 -SptRoot "<SPT folder with Fika>" -IncludeFika

# Create the ZIP for standalone and multiplayer installs, plus its SHA-256 manifest
powershell -ExecutionPolicy Bypass -File scripts\Package.ps1 -SptRoot "<SPT folder with Fika>"
```

`SPT_ROOT` can supply the installation path.

The packaging script checks assembly versions and writes a SHA-256 manifest for the ZIP and its files.

## Projects

| Path | Output |
|---|---|
| `client` | BepInEx plugin and its reload framework (.NET Framework 4.7.2) |
| `server` | SPT server mod for clip items and their Prapor and Peacekeeper offers (.NET 10) |
| `fika` | Optional Fika adapter (.NET Standard 2.1) |
| `tests/StripperClips.Tests` | Reload rules and framework tests |
| `tests/StripperClips.Fika.Tests` | Fika packet tests |

## Assets

`clips/` contains the prebuilt reload and magazine-loading animation bundles and their sidecars.
`items/` contains the three prebuilt inventory model bundles, which reference EFT shaders and cubemaps. The build and
packaging scripts use these files directly. Building from this source requires neither the Unity
Editor nor Blender.
