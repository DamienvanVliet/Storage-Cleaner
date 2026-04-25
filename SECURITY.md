# Security Policy

## Supported versions

The latest release is the supported version.

## Reporting a security issue

If you find a security issue, please open a private report instead of posting full details publicly.

Recommended steps:

1. Create a GitHub issue with minimal details and mark it as security-sensitive.
2. Include:
   - What happened
   - Steps to reproduce
   - Expected safe behavior
   - Screenshots/log snippets (if available)
3. Do not include personal data, passwords, or private tokens in the report.

## Release integrity

Each release includes SHA256 checksum files so users can verify downloads before running:

- `StorageCleaner.exe.sha256.txt`
- `StorageCleaner-win-x64-portable.sha256.txt`
- `StorageCleaner-win-x64-installer.sha256.txt`

The repository also includes:

- `scripts\verify-release.ps1` to validate hashes locally
- `scripts\sign-release.ps1` to sign artifacts with a real code-signing certificate
