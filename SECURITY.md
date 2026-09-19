# Security policy

S2ModKit is pre-alpha software. Please do not use it to modify an active game installation
without a backup and an explicit rollback path.

## Reporting a vulnerability

Please report suspected security issues privately through GitHub's Security Advisories/private
vulnerability reporting for this repository. Do not include proprietary Deadlock VPKs, compiled
models, personal paths, credentials, or other sensitive files in a report. If private reporting is
not available, contact the repository owner through their GitHub profile before opening a public
issue.

Useful reports include:

- reproducible path traversal, unintended overwrite, or unsafe extraction;
- verification, hash, receipt, or rollback bypasses;
- malicious VPK/model inputs that escape the fail-closed boundary;
- release-package tampering or exposed credentials.

Please include the affected version/commit, operating system, minimal reproduction steps, and a
sanitized log. There is no guaranteed response-time SLA, but reports will be triaged as soon as
practical.

## Scope

This policy covers S2ModKit source code, release packages, scripts, and public CI. Game behavior,
unsupported model layouts, third-party dependencies, and user-supplied native meshoptimizer DLLs
are not S2ModKit security promises; report dependency issues to their upstream maintainers too.
