# Security Policy

## Supported versions

Only the latest release is supported with security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for a security problem.

Report it privately through GitHub Security Advisories:

<https://github.com/yz7653886/DiskTreemap/security/advisories/new>

You can expect an initial reply within a few days. If the report is accepted, a
fix is prepared privately and published together with a new release, and you are
credited unless you prefer otherwise.

## Scope notes

DiskTreemap runs with the privileges of the user who launches it and never asks
for elevation. It reads the file system to compute sizes and can move files to
the Recycle Bin on request; it does not modify file contents.

Because the executable is not code-signed, some antivirus engines may flag a
fresh build. See the "Antivirus false positives" section of
[README.md](README.md) before reporting a detection — please include the engine
name and the exact verdict string if you do.
