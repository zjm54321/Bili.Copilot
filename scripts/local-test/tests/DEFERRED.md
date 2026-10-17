# Deferred fixtures/specifications (NOT executed)

These are inert specifications, not a test runner and not evidence of passing tests.
No PowerShell parse/AST compilation, script/refusal invocation, build, PE/package probe,
package query, signing, trust or install was performed during preparation.

## Before any production final command

Parent executes the following only in its admitted final batch, capturing diagnostics in
external evidence (never delivery). The source overlay must first be composed/reviewed.

```powershell
$errorsFound = @()
foreach ($path in @('LocalTest.psm1','Prepare-Package.ps1','Seal-Package.ps1','Install.ps1')) {
  $tokens = $null; $errors = $null
  $null = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $recipe $path), [ref]$tokens, [ref]$errors)
  $errorsFound += @($errors)
}
if ($errorsFound.Count) { $errorsFound; throw 'LocalTest/DeferredSyntaxGate: Failed.' }
```

AST parsing is necessary, not sufficient. Parent supplies a fixture harness/mocks in a new
external tests directory; **do not dot-source either historical installer**. The NEW module
has definitions only, whereas entry-point scripts have top-level behavior. Intercept all
external cmdlets/tools for fixture tests so no test can touch package registrations, keys,
trust, profiles, actual deployment or the formal artifact. Positive real operations belong
only to separately admitted final device/package gates.

## Fixture table

`identity-cases.json` contains expected constant and synthetic upgrade values, not observed
installed state. Use `source-manifest.xml` as a synthetic resolved source fixture. Transform
only in a new temporary directory; supply dummy opaque asset/PRI bytes to the manifest-only
harness (they are not a valid app/package). Parent NativeAOT output, not invented PE bytes,
is required for final positive native/version tests.

1. **Deterministic identity:** independently calculate UUIDv5 using a standard implementation
   (e.g. Python `uuid.uuid5(uuid.NAMESPACE_URL, name)` in the final batch); assert uppercase
   `E0BD9275-CB75-5DAE-AD37-B9C8464F9DAF`, UUID version 5/RFC variant, unequal to both known
   GUIDs and source manifest uses. Compare config/module/helper fixed identities. Test
   `BiliCopilot.LocalTest` selection, formal/dev/unknown selection and package-API failure
   fallback. No claim that AppInstance's key space is global.
2. **Manifest-only transform:** fixture and real final generated source must each have one
   app/toast/server/class; change only identity name/publisher/version, all specified labels,
   publisher display and both CLSIDs. Canonical XML tree diff permits only those values;
   assets, PRI bytes, dependency metadata, phone IDs, capabilities, logos, app entry point,
   flat EXE/App/arguments unchanged. Keep version independent of identity. Preserve original
   file hashes/lengths/inventory before vs after independently of script reports.
3. **Malformed manifest refusals:** missing/duplicate app/toast/server/class/COM extension;
   toast/class mismatch; unknown source GUID/identity; nested or missing EXE; wrong App ID,
   arguments, arch/version; unresolved target tokens or `x-generate`; DTD/entity document;
   wrong package/visual/tile/COM label; formal input supplied to installer. All stop before
   any deployment. Source fixture is not accepted as an actual app layout without native PE.
4. **Native/version/parity:** missing MZ/PE, invalid offsets/optional header bounds, x86/ARM64,
   nonzero CLR RVA or size, FileVersion or ProductVersion mismatch (including suffixes),
   numeric version resource mismatch, changed loose/packaged EXE byte or SHA-256 all fail.
   Test duplicate/case-aliased/path-traversing ZIP entries, nested app EXE, missing signature
   or manifest, corrupted ZIP, signed package with wrong embedded manifest. Positive final
   ILC provenance is separate: no-CLR apphost alone is insufficient NativeAOT evidence.
5. **No parent-source mutation:** read-lock every inventoried source; reject inventory gaps,
   extras, duplicates, unsafe paths, symlinks/junctions, PFX/private key/user profile/source/
   PDB/log/evidence files; never copy private files or entire directories indiscriminately.
   Reject source/output/workspace overlap and existing output/sealed manifest. Forced copy
   failure must preserve original bytes and leave only the new incomplete output for parent
   diagnosis, without cleanup of any old artifact. `-Pack` without `-Prepare` must stop.
6. **Artifact trust boundary:** mandatory external manifest hash, changed installer/module/
   config/package/EXE hash, wrong signer DER/subject, unsigned/untrusted/expired signer all
   stop. Mock file replacement during checks/deploy: held FileShare.Read locks must prevent
   mutation. Entry-point authenticity must be established outside script self-checking.
7. **Installed package guard:** mock absent, one valid older test, same test version, higher
   test version, same name/different publisher/PFN, wrong architecture/signature kind/status,
   duplicate records, wrong installed manifest, malformed version and missing framework.
   Default preflight calls Add-AppxPackage **zero** times for every case. Same-version
   preflight reports no-write; explicit install refuses it. Positive mocked absent/strictly
   increasing upgrade calls ordinary Add-AppxPackage once with only Path/ErrorAction.
   Do not use a fixture version as evidence of a real prior installed test baseline.
8. **No forced deployment:** AST/cmdlet spies assert no force parameters, Remove-AppxPackage,
   registration, Stop-Process, Start-Process RunAs, trust writes/import/export/generation,
   automatic key search/PFX creation or dependency installation. Signing is solely a
   parent-owned explicit final adapter, not installer behavior.
9. **Runtime/manual limits:** separate device batch must validate OS-derived PFN/AUMID,
   package-manager framework compatibility, formal/test coexistence, each channel's
   notification and cold COM activation, launch/redirection behavior with both channels
   running, no global-key-scope assertions. Use synthetic LocalSettings/LocalState sentinels
   only in an explicitly authorized device test; verify retention on a strictly higher test
   version and unchanged formal identity/store. No migration, shared root or portable test.
   Blocked ordinary upgrade while app runs must not kill it. Record actual failures, not
   a blanket acceptance inferred from script branches.

Any failure blocks package/deployment acceptance. This file supplies no measured result.
