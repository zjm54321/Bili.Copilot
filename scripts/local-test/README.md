# Packaged LocalTest

Windows App SDK: **1.8.251106002**; SDK BuildTools: **10.0.26100.6901**.
The recipe targets **x64** Windows PowerShell 5.1 only.

## Stable identity (never version-derived)

| Field | Value |
|---|---|
| Package name | `BiliCopilot.LocalTest` |
| Publisher | `CN=BiliCopilot Local Release` |
| Expected PFN | `BiliCopilot.LocalTest_p8tmrechhd89c` — final package/OS gate still required |
| Application ID / expected AUMID | `App` / `BiliCopilot.LocalTest_p8tmrechhd89c!App` |
| Instance key | `BiliCopilot.LocalTest` |
| Display label | `哔哩助理（测试）` |
| Toast/COM CLSID | `E0BD9275-CB75-5DAE-AD37-B9C8464F9DAF` |
| Native app and COM executable | Exactly flat `BiliCopilot.UI.exe` |
| COM arguments | `----AppNotificationActivated:` |
| New default version | `2.2611.2.0` |

`channel.json` documents the fixed channel identity; the module does not accept arbitrary identities.

Delivered formal identity is **BiliCopilot.LocalRelease / CN=BiliCopilot Local Release /
BiliCopilot.LocalRelease_p8tmrechhd89c / App**. Source **Richasy.BiliCopilot.Dev / CN=Richasy**
is not that delivered identity. This recipe admits only a new, resolved
2.2611.2.0-or-later layout with the corrected flat COM path and matching native PE version.

## Input and output contracts

- The parent supplies an external, curated native publish/layout directory with generated
  `AppxManifest.xml`, all assets/PRI and resolved framework declarations. No source tokens
  or `x-generate` remain. Source identity may be Dev or new LocalRelease only. Exactly
  one app/toast/COM server/class is required; no guessing or fixing a nested source path.
- Parent-reviewed `approved-layout.json` has shape:
  `{"schemaVersion":1,"files":[{"path":"AppxManifest.xml","sha256":"<actual lowercase SHA-256>"}, ...]}`.
  Paths use `/`, cover **every file**, and include the native EXE, PRI and every asset.
  The parent records its digest separately. Place it outside the payload. The scripts
  reject listed secret/source/log/evidence/private/profile material, symlinks and package
  metadata rather than silently filtering it. This deny-list is not a privacy audit:
  parent review of unknown embedded assets/strings remains required.
- Source, actual workspace and new output must be non-overlapping ordinary local paths.
  The parent must supply the real WorkspaceRoot, not a dummy exclusion path.
  `Prepare-Package.ps1` defaults to read-only preflight; `-Prepare` authorizes only the
  fresh-copy transform. Existing outputs are refused. `-Pack` also requires `-Prepare` and
  an explicitly admitted MakeAppx path/hash from the unchanged build-tools package.
  No automatic SDK lookup/installation or tool-version-to-NuGet-version inference.
- Original source handles are read-locked while copying. All unchanged payload bytes
  are rechecked. Only the *copy* has Identity name/publisher/version, package publisher
  display name, package/visual/tile/COM labels and matching toast/class GUID transformed.
  App/COM flat executable, App ID, arguments, capabilities, assets/PRI and dependency
  declarations are required/preserved, not rebuilt.
- `layout/` is packaging input, **not portable delivery**. `delivery/` contains only the
  versioned MSIX, `Install.ps1`, `LocalTest.psm1`, `channel.json`, and, after sealing,
  `test-artifacts.json`. Logs, evidence and approved inventories stay outside delivery.
  `-Pack` uses MakeAppx `pack /d ... /p ...`, never overwrite flags, and leaves an unsigned
  package for the parent's separate signing facility. Failures retain the new output;
  there is no cleanup/retry-overwrite behavior.
- A non-CLR x64 PE is necessary but does **not by itself prove NativeAOT** (an apphost
  can also lack a CLR header). Parent build/ILC provenance remains a separate gate.
  Both numeric and string PE FileVersion/ProductVersion must exactly equal the MSIX
  version. The sealing step verifies signed-package EXE SHA-256 equals the checked loose
  native EXE. The installer rechecks that pinned package/EXE parity without extraction.

## Signing/trust and bounded installer

Same publisher means existing-signing compatibility **only**, not available key or trust.
No helper generates/exports/imports certificates, creates a PFX, modifies trust or searches
key stores. Parent signing must explicitly provide an existing compatible facility/key.
A non-exportable certificate is usable only if the chosen existing facility supports it
and the key is accessible. Missing key: `LocalTest/SigningKeyUnavailable` at the parent
adapter; missing/invalid/untrusted signature: `LocalTest/SignatureOrTrustUnavailable`.
Sealing/installation demand platform `Valid`, exact subject, signer DER SHA-256 and current
certificate validity. They never bootstrap trust; a trust failure is a stop, not a prompt.

The installer requires an externally communicated SHA-256 of `test-artifacts.json`.
That digest, not a self-computed value from untrusted delivery, roots its pins for scripts,
module, config, MSIX, native EXE and signer certificate. Parent release controls must also
authenticate `Install.ps1` before running it; self-hashing cannot secure an already modified
entry point. Pins are not digital signatures for the scripts themselves.

Default installer behavior is read-only preflight (including current-user package metadata
and signature verification, only when later authorized). All declared framework dependencies
must **already** be installed with matching publisher/version/architecture and signed
package kind. Missing dependencies are typed failures; no package discovery, download,
bundled-dependency installation or provisioning is added. No dependency payload is pruned
from the source layout, and its declarations remain unchanged.

Only explicit `-Install` can invoke ordinary `Add-AppxPackage -Path ... -ErrorAction Stop`.
It refuses formal/dev input, wrong publisher/PFN/manifest/digests/signature, downgrade and
same-version writes. Matching existing test identity is checked immediately before deploy.
No force-update/force-shutdown/remove/register/app-kill/elevation/trust operation. Same-version
preflight returns `SameVersionNoWrite`; `-Install` then fails `LocalTest/VersionUnchanged`.
If a running app or OS policy prevents an ordinary upgrade, it fails rather than forcing it.
Concurrent external deployment is not serialized by this script; final batches must avoid it.
Post-deployment identity checking is not runtime/toast/data-retention acceptance. A post-deploy
failure never automatically rolls back or removes the app.

## Packaging and installation

All variables below must be supplied by the package maintainer, and all output/log capture must be
external to the source/workspace and delivery. A strict increase over an actually observed
prior **test** version is required for upgrades; no prior installed test version is assumed.
Use four components, each 0..65535, never reset identity or reuse an old sealed output.
There is no reason to rebuild solely for the different manifest GUID.

```powershell
# Set $recipe to this directory and supply the paths and hashes below.
$version = '2.2611.2.0'
& "$recipe\Prepare-Package.ps1" -SourceLayout $nativeLayout -OutputRoot $newOutput `
  -WorkspaceRoot $realWorkspace -ApprovedInventory $approvedInventory `
  -ExpectedInventorySha256 $approvedInventorySha256 -Version $version

# Create the new layout and unsigned package after the read-only preflight.
& "$recipe\Prepare-Package.ps1" -SourceLayout $nativeLayout -OutputRoot $newOutput `
  -WorkspaceRoot $realWorkspace -ApprovedInventory $approvedInventory `
  -ExpectedInventorySha256 $approvedInventorySha256 -Version $version -Prepare -Pack `
  -MakeAppxPath $admittedMakeAppx -ExpectedMakeAppxSha256 $admittedMakeAppxSha256

$delivery = Join-Path $newOutput 'delivery'
$msix = Join-Path $delivery "BiliCopilot.LocalTest_${version}_x64.msix"
# Example EXISTING CurrentUser/My non-exportable-key-compatible facility only if explicitly selected.
# Otherwise use the parent's supported existing adapter, or STOP; never produce a PFX.
if (-not $admittedSignTool -or $explicitExistingThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
  throw 'LocalTest/SigningKeyUnavailable: No explicitly admitted compatible key/facility.'
}
& $admittedSignTool sign /fd SHA256 /sha1 $explicitExistingThumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw 'LocalTest/SigningKeyUnavailable: Signing failed; do not create/export/import keys.' }

& "$recipe\Seal-Package.ps1" -DeliveryRoot $delivery `
  -LooseExePath (Join-Path $newOutput 'layout\BiliCopilot.UI.exe') `
  -ExpectedLooseExeSha256 $parentPinnedNativeExeSha256 `
  -ExpectedSignerCertificateSha256 $explicitSignerDerSha256 -Version $version
# Communicate/approve the emitted manifest digest OUTSIDE delivery before using it below.
& "$delivery\Install.ps1" -ExpectedManifestSha256 $externallyApprovedManifestSha256
# Separate final deployment gate only:
& "$delivery\Install.ps1" -ExpectedManifestSha256 $externallyApprovedManifestSha256 -Install
```

Package/identity checks do not replace device checks for launch, notifications, channel
isolation and data retention across upgrades.
