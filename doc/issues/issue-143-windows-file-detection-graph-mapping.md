# Windows file-system detection Graph mapping (GitHub #143)

## Summary

Map validated Windows `Detection.Type: file` manifests to Microsoft Graph v1.0 `win32LobAppFileSystemRule` payloads without regressing the PowerShell script rule.

## Required work

- Make the `rules` payload polymorphic. `Win32LobAppPayload.Rules` is a `List<Win32LobAppDetectionRulePayload>` today, and System.Text.Json serializes by declared type, so a base-typed list silently drops derived properties and produces a Graph 400. Introduce an abstract `Win32LobAppRulePayload` carrying `ruleType`, annotate it with `[JsonPolymorphic(TypeDiscriminatorPropertyName = "@odata.type")]` and one `[JsonDerivedType]` per rule shape, and change the element type of `Rules`.
- Remove the hand-written `@odata.type` properties from the derived rule types. A property colliding with the polymorphic discriminator throws at the first Graph call, not at build time. Leave `@odata.type` alone on the non-polymorphic payload types.
- Rename `Win32LobAppDetectionRulePayload` to `Win32LobAppPowerShellScriptRulePayload` and migrate the two test files that reference it.
- Map `ruleType` with the fixed value `detection`, plus `path`, `fileOrFolderName`, `check32BitOn64System`, `operationType`, `operator`, and `comparisonValue`. Synthesize `operator: notConfigured` with a null `comparisonValue` for `operationType: exists`.
- Change `Win32LobAppPayloadMapper.Map`'s `detectionScriptContent` parameter to `string?` and dispatch on the validated `Detection.Type`, with a fail-closed default arm so a new detection type can never silently emit a PowerShell rule.
- Branch `WindowsAppPublisher.ReadDetectionScriptAsync` on the detection type. It currently dereferences `Detection!.ScriptFile!` unconditionally and feeds it to `PathSafety.ResolveWithin`, which fails for `file` detection on all three publish paths (`EnsureMappableAsync`, `CreateAppAsync`, `UpdateAppAsync`). `publish --dry-run` also calls `EnsureMappableAsync`, so a dry run exercises the full mapping.
- No change is needed in `WindowsStagingService` or `PlanService.EnumerateReferencedFiles`: both already guard with `Detection?.ScriptFile is { }`.
- Add payload serialization, mapper, publisher, and compatibility regression tests.

## Acceptance criteria

- File detection produces the expected Graph v1.0 JSON shape, with `@odata.type` as the rule object's first property.
- Script detection continues to produce the current PowerShell rule with an unchanged wire format.
- A `rules` list holding both rule shapes serializes each with its own discriminator and its own properties.
- File detection neither reads nor packages a detection script: `EnsureMappableAsync` succeeds for `Type: file` against a repository root containing no files.
- Publish preflight remains fail-closed for both variants.

## Dependencies

- Parent: GitHub #141
- Manifest schema and validation: GitHub #142
- Documentation, validation, and release: GitHub #144

Implemented by PR #145. This document remains the reviewable scope and acceptance record.
