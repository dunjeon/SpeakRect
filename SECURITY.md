# Security policy

## Supported versions

SpeakRect is a desktop Windows app. Security fixes target the latest released version on the public download channel when applicable.

| Version | Supported |
|---------|-----------|
| Latest release zip | Yes (best effort) |
| Older zips | Best effort only |

## Reporting a vulnerability

**Contact:** GitHub **Security Advisories** on the product repository when enabled; otherwise the owner ([dunjeon](https://github.com/dunjeon)).

Prefer private disclosure. Do not file high-severity security issues as public bugs.

Please include:

- SpeakRect version (`AppInfo` / release tag)
- OS build
- Steps to reproduce
- Impact (e.g. local file access, synthetic input abuse, model supply chain)

## Scope notes

- Recognition is **local** (Local-LLM on `127.0.0.1`). Screen captures are not uploaded by the app for OCR.
- Custom hotkeys can inject system input — user-configured.
- Bundled third-party host/model binaries: verify release checksums when published; report supply-chain concerns.

## Out of scope

- Bugs that require physical access or an already-compromised machine only
- Issues solely in upstream KoboldCpp / model projects (report upstream; link here if SpeakRect packaging is involved)
