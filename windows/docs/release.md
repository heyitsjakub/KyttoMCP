# Windows release

The current source release is **1.0.7**. Its user-facing notes are in
[`releases/1.0.7.md`](releases/1.0.7.md); earlier notes sit beside it.

Before creating an artifact, the exact release sources must pass:

```powershell
dotnet test
Push-Location web
npm test
Pop-Location
dotnet build
```

Do not use a previously published directory as evidence for a new version. The
assembly, ZIP and installer versions all come from the single `Version` property
in `src/Kytto.App/Kytto.App.csproj`; `Kytto.App.Tests/ReleaseMetadataTests.cs`
pins the intended source release and verifies that the packaging scripts do not
introduce a second release number. Bumping the version therefore means changing
both, and adding `docs/releases/<version>.md`, which the same test requires.

## Building the artifacts

Run releases from a clean Git checkout on Windows with the .NET 10 SDK and
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`ISCC.exe`, e.g.
`winget install JRSoftware.InnoSetup`):

```powershell
.\scripts\release.ps1
```

The script publishes the self-contained Release app and the gateway helper,
optionally signs Kytto's EXE/DLL files, verifies every signature, creates
`dist/Kytto-Windows-win-x64-<version>.zip`, and writes its SHA-256 beside it.
The installer is named `dist/Kytto-Setup-win-x64-<version>.exe`. For ARM64, pass
`-Runtime win-arm64`: both names then use `win-arm64`, and an ARM64-only
installer is compiled from the matching publish directory. `-SkipInstaller`
produces only the ZIP.

The default clean-tree check makes the commit being tagged the exact source of
the download. `-SkipGitCheck` exists only for local script validation in an
exported source tree.

## Signing

Without a certificate the script still runs and produces an **unsigned** build,
with a warning. For Authenticode signing, install your code-signing certificate
in the Windows certificate store and the Windows SDK `signtool.exe`, then set:

```powershell
$env:KYTTO_SIGNING_CERT_THUMBPRINT = '<your certificate thumbprint>'
$env:KYTTO_TIMESTAMP_URL = 'https://timestamp.digicert.com'   # optional
.\scripts\release.ps1
```

The thumbprint only selects a certificate already in your store. The private
key, its password and any `.pfx` never belong in the repository (`.gitignore`
excludes the common key and certificate formats). The timestamp URL is
optional; the script has a public default.

## Publishing a fork

A few values identify the upstream project rather than the code, and a fork that
publishes its own builds should change them:

- `src/Kytto.Core/Updates/UpdateChecker.cs` — the update manifest, download and
  release-notes URLs. Left as they are, a fork's builds are offered upstream's
  releases, and the update installer accepts them only if they pass the hash and
  Authenticode checks.
- `scripts/installer.iss` — `AppId` and `AppPublisher`. Reusing upstream's
  `AppId` makes the two installers treat each other as the same application.

## Release checklist

Before tagging a release, use a clean Windows user profile or disposable VM. Keep
all test configuration under that isolated profile, never under the developer's
real client paths:

1. Verify the checksum with `Get-FileHash -Algorithm SHA256`.
2. Extract the ZIP, start `Kytto.exe`, and complete first-run onboarding.
3. Confirm onboarding names five built-in writable clients plus read-only custom
   sources, and that no client configuration is created before an explicit
   change.
4. Create a harmless JSON fixture with a top-level `servers` map. Record its
   SHA-256 and bytes, attach it as `Demo workspace`, choose Workspace and label
   it `Demo project`.
5. Close and reopen Settings. Confirm the source, scope, label and completed
   onboarding state persist. Confirm its matrix/detail controls are read-only.
6. Edit the fixture externally and verify the watcher refreshes Kytto. Record
   the externally edited bytes, detach the source, and prove its final bytes and
   SHA-256 still match. The file must continue to exist.
7. In a fresh isolated profile, click a writable matrix cell and confirm no file
   changes until the **Change client configuration?** primary action is chosen.
   Confirm the change, use Undo, and verify the original state returns.
8. Confirm normal sidebar and matrix icons occupy 22px × 22px. Resize a window
   with a pending restart and verify the banner never overlaps the table or its
   last scrollable row.
9. Drag the sidebar edge, relaunch, and confirm its width survives. Double-click
   the handle to reset it; focus the handle and confirm Left/Right move it. With
   several Claude Code projects, confirm their compact labels remain distinct
   while their tooltips and detail rows retain the full paths.
10. Scan skills whose front matter uses literal and folded block descriptions,
    quoted scalars, indented continuations and an empty nested mapping. Confirm
    every card shows its scope exactly once and no skill body is read.
11. Open a detail containing long client paths and confirm paths wrap normally,
    client names do not stack vertically, and every action remains visible.
12. Check an OAuth-backed `mcp-remote` server. Confirm it becomes blue Waiting
    for authorization, is not counted as failed, and its validated HTTPS sign-in
    page opens only from the URL retained by the native health result. Complete
    consent and confirm Check now can replace the waiting state with Healthy.
13. Add, edit and delete another harmless built-in fixture server, then confirm
    Backups can restore it. Enable and disable the tray icon and confirm it
    changes at once.
14. Delete the extracted ZIP directory and confirm no machine-wide installation
    or service remains. Separately install the setup executable, exercise the
    same smoke path, uninstall it, and confirm its registered files are removed.
15. Tag the clean commit only after the VM run passes, and upload the signed
    installer/ZIP plus their `.sha256` files without rebuilding.

Certificate signing and the clean-profile/VM run cannot be completed on a
machine where those external resources are unavailable; an unsigned artifact
must be labelled as such.
