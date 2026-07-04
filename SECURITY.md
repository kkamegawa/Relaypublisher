# Security Policy

## Supported Versions

This project is under initial development. Security fixes are applied to the latest version on the default branch only.

## Reporting a Vulnerability

Please do not report security vulnerabilities through public GitHub issues.

Instead, report them privately using GitHub Security Advisories ("Report a vulnerability" on the repository's Security tab). If that is not available, contact the repository owner directly.

Please include:

- A description of the vulnerability and its impact
- Steps to reproduce
- Affected component (CLI, manifest validation, source providers, Intune Graph client, CI workflows)

You can expect an initial response within 7 days.

## Scope Notes

This tool handles credentials (workload identity federation tokens, GitHub PATs) and publishes application packages to Microsoft Intune tenants. The following are of particular interest:

- Credential leakage in logs or generated artifacts
- Path traversal in manifest-driven file staging
- Checksum verification bypass for downloaded binaries
- Supply chain issues around IntuneWinAppUtil acquisition
