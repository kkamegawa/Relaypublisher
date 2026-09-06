# Recover from Azure Storage SAS authentication 403 during content upload (GitHub #150)

## Summary

A `publish` run over a `manifest-list.json` with several manifests aborted on the second package with an
unhandled `Azure.RequestFailedException` (403 `AuthenticationFailed` /
`AuthenticationErrorDetail: SAS identifier cannot be found for specified signed identifier`) from the
Azure Storage blob upload. `validate` and `publish --dry-run` both succeeded, because neither performs a
blob upload. The root cause is not fully confirmed - the log evidence is consistent with the stored access
policy behind Intune's `azureStorageUri` not having propagated yet (Azure Storage documents up to ~30
seconds for that), but does not rule out Intune revoking or rotating the policy - and both explanations
call for the same remediation. See `doc/adr.md` (2026-09-06 entry) for the full reasoning.

Three confirmed defects compounded the symptom: the exception escaped every catch in the publish CLI (not
a `PublisherException`), so `--result-file` was never written; a retried block would have resent an
already-consumed `MemoryStream`; and `IsRecoverableUncommittedUploadState` rejected the exact upload states
(`azureStorageUriRequestSuccess` / `azureStorageUriRenewalSuccess`) a run interrupted mid-upload is left
in, so a rerun could not reach the new recovery logic at all.

Separately, and by explicit user request rather than as a fix for the above, the Graph session used by
`publish` (`HttpClient`, its authentication/retry handlers, and their cached token) is now opened and
disposed per manifest entry instead of once for the whole run.

## Required work

- Recover from a SAS authentication 403 in `AzureStorageBlockBlobUploader`: branch on remaining SAS
  lifetime; retry the same SAS across a window sized past the documented propagation delay when not near
  expiry; otherwise (or once that window is exhausted) call `renewUpload`, bounded to a small number of
  calls separate from the existing pre-emptive expiry-driven renewals, and wait again after each renewal.
  Bound the whole recovery for one stage/commit call by a single deadline, enforced with a token linked to
  the caller's own, so a recovery timeout can be told apart from the caller's cancellation.
- Rebuild the block payload stream from the same bytes on every retry attempt instead of reusing a
  `MemoryStream` whose read position has already advanced.
- Translate a non-recoverable `RequestFailedException` into a new `ContentUploadRejectedException`, built
  from status/error-code/detail/request-id only - never the SDK's own `Message`, and never the original
  exception as an inner exception - so "never log a signed URL" (AGENTS.md) does not depend on Azure SDK
  internals.
- Accept `azureStorageUriRequestSuccess` and `azureStorageUriRenewalSuccess` as recoverable uncommitted
  upload states in `MobileAppContentUploadOrchestrator`, keeping every existing file-count/metadata safety
  check, and document that this recovery depends on publish being serialized (doc/00-overview.md 6.9).
- Unify `PublishCommand.PublishEntriesAsync`'s result-file output to a single write point (a `finally`,
  using `CancellationToken.None`), recording a failure entry for any exception type the loop does not
  otherwise recognize before aborting, while still letting a genuine `OperationCanceledException` from the
  caller's own token propagate instead of being recorded as a failure.
- Open and dispose the Graph session (`IPublishSession` / `PublishComposition`) per manifest entry rather
  than once for the whole run, while keeping the `TokenCredential` itself shared across the run (a
  per-entry credential could let `DefaultAzureCredential`'s chain resolution drift to a different identity
  between entries when `AZURE_TOKEN_CREDENTIALS` is unset, doc/00-overview.md 6.19).
- Update `doc/00-overview.md` (6.10, 6.12, 6.16), `doc/02-dotnet-architecture.md`,
  `doc/06-troubleshooting.md` / `_ja`, and `doc/05-operation.md` / `_ja` to describe the above, and record
  the decision in `doc/adr.md` and the session outcome in `doc/task.md`.

## Acceptance criteria

- A SAS authentication 403 recovers without a renewal when the SAS is not near expiry and the failure
  clears within the same-SAS retry window; recovery cannot livelock (bounded renewal count and deadline).
- A retried block sends a byte-identical body to the first attempt.
- No SAS URI, `sig=`, or `si=` value appears in the resulting exception's message, its `ToString()`, or any
  logger output.
- A batch of N manifest entries where one entry's upload fails after exhausting recovery still records all
  N entries in `--result-file` and exits non-zero, with the other entries published.
- An upload interrupted while its file was in `azureStorageUriRequestSuccess` or
  `azureStorageUriRenewalSuccess` is recovered by a rerun (renewed and resent) without deleting or
  recreating any app, content version, or file.
- `publish` still builds one Graph session per manifest entry and disposes it before the next entry starts;
  `--expected-tenant` verification and the token-identity log line now happen once per entry instead of
  once per run.

## Verification

- `dotnet build` and `dotnet test` pass on `IntuneLobPublisher.slnx`.
- Before any end-to-end rerun against the interrupted production app, read its current remote state (content
  version count, file count, `uploadState`, `name`/`size`/`sizeEncrypted`) and confirm it satisfies the
  recovery conditions, and confirm the target pipeline's publish stage is actually protected by an
  exclusive lock (doc/00-overview.md 6.9) - the recovery above depends on that, not just on it being
  documented.
- End-to-end: a recovery run for the previously-interrupted app, then a run where both entries genuinely
  upload, then an idempotent rerun reporting both unchanged. `--result-file` must be present and complete
  in all three.

## Out of scope

No change to CLI options, console output format, exit codes, or the `--result-file` JSON shape. The root
cause is not asserted as settled by this issue; the diagnostic logging it adds is meant to settle it on the
next production occurrence.

## Dependencies

None; builds on the existing Win32 content upload flow (`doc/issues/issue-003-intune-graph-win32.md`).
