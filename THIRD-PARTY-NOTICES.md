# Third-party notices

## FxSsh

Source: https://github.com/Aimeast/FxSsh

Vendored snapshot: `cd352247e7d49831a8111d0983e000fca6551840` from branch `dev`, retrieved November 12, 2026. The upstream project declares version 1.4.0. This is a source snapshot, not the NuGet 1.4.0 release artifact.

License: MIT. The original copyright and license are retained in `vendor/FxSsh/LICENSE.md`.

Local changes: the project targets .NET 10, omits package-generation/AOT/signing build settings, and excludes the two X25519 implementations requiring .NET 11. Their algorithm registrations are omitted from `AlgorithmCatalog.cs`. ECDH NIST curves, SHA-2 Diffie-Hellman, AES-CTR/GCM, and SHA-2 MAC implementations remain available. Additional local changes serialize SFTP responses through a bounded sender, bound SSH and rekey queues, enforce channel and handle limits, return safe protocol errors with a local diagnostic event, handle partial socket writes, coordinate cancellation and shutdown, report verified authentication completion separately from public-key probes, and reject re-authentication after a successful login. Regression checks have internal access through `InternalsVisibleTo`. Application folder restrictions live in `SimpleSftp.Core`, separate from this dependency.

NuGet access was unavailable in the build environment. Vendoring this source allows an offline build using the installed SDK and framework reference packs.
