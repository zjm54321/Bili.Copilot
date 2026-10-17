# Packaged LocalTest — PREPARATION ONLY

These new files are an unexecuted candidate, not an accepted installer/release. Do not run
them before the parent's final batch authorization. Windows App SDK stays **1.8.251106002**;
SDK BuildTools stays **10.0.26100.6901**. The bounded recipe targets **x64** only.

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

The GUID is UUIDv5 with URL namespace `6ba7b811-9dad-11d1-80b4-00c04fd430c8` and
name `https://github.com/zjm54321/Bili.Copilot/channels/LocalTest/toast-activator`.
The pre-UUID SHA-1 is `e0bd9275cb759daead37b9c8464f9dafb2c57020`.
It differs from source Dev `E3D99E22-57A2-41B9-BB2D-E1547E68DEBA` and delivered
LocalRelease `64DB9FDB-EF36-44D3-8DA6-04658D814915`. No compiled COM GUID changes.
`channel.json` documents the constants; the module independently fixes safety-critical
identity rather than accepting caller-configurable identities. Deferred tests compare both.

Delivered formal identity is **BiliCopilot.LocalRelease / CN=BiliCopilot Local Release /
BiliCopilot.LocalRelease_p8tmrechhd89c / App**. Source **Richasy.BiliCopilot.Dev / CN=Richasy**
is not that delivered identity. This recipe must not touch the sealed P6 2.2611.1.0 package,
including its known nested COM path defect. It admits only a *new*, parent-final, resolved
2.2611.2.0-or-later layout with the corrected flat COM path and matching native PE version.

## Parent-only source hook (NOT applied here)

1. Add the candidate new `src/Desktop/BiliCopilot.UI/Toolkits/AppChannelIdentity.cs`.
2. In `src/Desktop/BiliCopilot.UI/App.xaml.cs`, replace just the initializer at the existing
   `private const string Id = "Richasy.BiliCopilot";` declaration with:

   ```csharp
   private static readonly string Id = AppChannelIdentity.InstanceKey;
   ```

   The existing `using BiliCopilot.UI.Toolkits;` and `FindOrRegisterForKey(Id)` remain.
   This is the only required existing-file hook. The helper returns the existing
   `Richasy.BiliCopilot` key for formal/dev/unknown package names or expected package-API
   availability failures. The separate test key is conservative: no global scope/collision
   guarantee is asserted. Integrate before the one parent-final native build so both
   manifests may use identical EXE bytes.
3. No PackageLabel hook is required/proposed. Manifest labels distinguish the installed
   package/tile/COM display name. In-app titles/resource-based toast text remain existing
   behavior; do not imply a new in-app banner. No resource/style/layout edits belong here.

Keep `AppNotificationManager.Default.Register()`, package `Package.Current`, and 1.8
`ApplicationData.GetDefault()` behavior. Package identity provides the test PFN's own
LocalState/LocalSettings (including the existing `v1` settings container). No formal data
lookup, copy, migration, shared root, `GetForUnpackaged`, or new notification-registration
calls. An uninstalled loose EXE is **not a new supported portable application**; it is the
native executable delivered inside MSIX. Same-PFN upgrades are intended to retain test
data; actual device validation is outstanding.

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

## Deferred final commands — NOT EXECUTED

All variables below must be parent-supplied/approved, and all output/log capture must be
external to the source/workspace and delivery. A strict increase over an actually observed
prior **test** version is required for upgrades; no prior installed test version is assumed.
Use four components, each 0..65535, never reset identity or reuse an old sealed output.
There is no reason to rebuild solely for the different manifest GUID.

```powershell
# Run from the parent-approved NEW overlay/composed recipe, final batch only.
$version = '2.2611.2.0'
& "$recipe\Prepare-Package.ps1" -SourceLayout $nativeLayout -OutputRoot $newOutput `
  -WorkspaceRoot $realWorkspace -ApprovedInventory $approvedInventory `
  -ExpectedInventorySha256 $approvedInventorySha256 -Version $version

# After the batch owner admits packaging; same nonexistent output as above.
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

No instruction here authorizes those actions now. Before those commands the parent owns
syntax/AST checks, deferred fixtures, source composition, NativeAOT build provenance,
payload privacy/forbidden inventory and independent package preservation verification.
Final device gates include actual OS PFN/AUMID, two-channel coexistence, cold toast activation
from stopped app, running app behavior, launch/redirection keys, test data retention across
a strictly higher test version and formal data isolation. Do not inspect/migrate real account
data implicitly. Use explicit final-device authorization and synthetic channel-local sentinels.
The API review corroborates packaged manifest-driven activation but did not establish the
exact 1.8 servicing-build runtime behavior; no device acceptance exists yet.
