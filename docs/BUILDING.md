# Building StripperClips

## Requirements

- Windows with Windows PowerShell 5.1
- Git with Git LFS (`git lfs install` before cloning): the asset bundles are stored in LFS. The
  build stops if it finds LFS pointer files instead of bundles.
- .NET 10 SDK
- .NET Framework 4.7.2 targeting pack (Visual Studio or the .NET Framework 4.7.2 Developer Pack)
- A local SPT 4.1.6 installation (Escape from Tarkov 0.16.9.40743) for build references

## Commands

Replace `<SPT folder>` with the path to your installation.

```powershell
# Build and test
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1 -SptRoot "<SPT folder>"

# Create the installation ZIP and SHA-256 manifest
powershell -ExecutionPolicy Bypass -File scripts\Package.ps1 -SptRoot "<SPT folder>"
```

`SPT_ROOT` can supply the installation path.

The client and server DLLs are deterministic: the same commit, checked out with the line endings
in `.gitattributes`, produces byte-identical DLLs in any folder.

## Projects

| Path | Output |
|---|---|
| `client` | BepInEx plugin and its reload framework (.NET Framework 4.7.2) |
| `server` | SPT server mod for clip items and Prapor offers (.NET 10) |
| `tests/StripperClips.Tests` | Reload rules and framework tests |

## Assets

`clips/` contains the prebuilt reload animation bundles and their sidecars. `items/` contains the
two prebuilt inventory model bundles, which reference EFT shaders and cubemaps. The build and
packaging scripts use these files directly. Building from this source requires neither the Unity
Editor nor Blender.
