# Security review — 2026-10-02

This review covers BwPicker's Windows tray app, Bitwarden CLI process boundary, vault parsing and credential cache, unlock and lock lifecycle, clipboard handling, and automatic typing. It includes source review, builds, adversarial regression checks, dependency inventory, and a live verification of the installed CLI. It is not an independent penetration test, antivirus certification, or a review of Bitwarden's own implementation.

No desktop password tool can be guaranteed 100% malware or hack proof. These changes reduce exposure and close identifiable failure paths. They cannot protect secrets from a compromised Windows account, administrator/kernel malware, a keylogger, a compromised destination application, or malicious changes to this app's binaries.

## Changes implemented

| Boundary | Protection |
| --- | --- |
| Retained passwords and CLI session | Same-process Windows `CryptProtectMemory` encryption in pinned buffers; disposable, zeroed plaintext leases; encrypted buffers wiped when replaced, locked, or disposed. |
| CLI vault output | Bounded, wipeable pooled byte buffers. Passwords are decoded directly into protected storage without creating immutable password strings or converting the complete vault to a string. Unused fields are skipped. |
| Master password | Masked field cleared as unlock begins; protected input disposed after use. Passwords never appear in CLI arguments. |
| Executable trust | Resolve an absolute CLI path, hold a read-only file handle that denies write/delete, hash its contents, verify Windows Authenticode trust and the Bitwarden Inc. signer. Recheck changed content; reject unsigned/untrusted executables. |
| Child-process environment | Clear inherited environment and allow only required profile/system paths. Remove Node injection, inherited session, and TLS-bypass settings; explicitly require TLS certificate verification. No shell invocation. |
| Native libraries | Restrict native DLL lookup to Windows System32. |
| Vault server | Reject HTTP, invalid URLs, and URLs containing embedded credentials before sending the master password. HTTPS certificate verification remains enabled in the CLI. |
| Unlock result | Require successful exit status and a structurally valid session key (88 base64 characters decoding to the CLI's 64-byte AES-256-CBC-HMAC key) before publishing it. |
| Authorization lifecycle | Immediately revoke the session, cache, and credential leases on manual lock, Windows lock/disconnect/logoff, suspend, idle timeout, and normal shutdown. Returning to Windows does not silently unlock the vault. |
| Async operations | Serialize CLI commands, cancel/terminate ongoing operations on revocation, and reject results from older authorization generations. Unlock waits for pending CLI-lock cleanup. |
| Credential selection | Only IDs present in the current unlocked cache can be read. No arbitrary-item CLI fallback. Preview mode cannot unlock/access the real vault. |
| Clipboard | No retained plaintext comparison copy; native writes carry Windows history/cloud/monitor-exclusion privacy flags. Clear app-owned data after 30 seconds and on revocation; use owner/sequence checks to preserve a later user copy. Retry busy clipboard cleanup. |
| Typing | Check original process identity, foreground window, title, that keyboard focus is inside the destination window, modifier state, and live authorization before every keystroke. Abort when any changes or Windows blocks input. Focus is checked at window scope rather than one exact control because embedded browsers (CEF, WebView2) move focus between their own child windows while handling input. Username-only and password-only modes type a single field for two-step logins. Validate control characters/size before typing; no automatic clipboard fallback on failure. |
| Errors/logging | Generic CLI errors; raw stdout/stderr and secrets are not echoed. Performance trace includes command name and elapsed time only. |
| Resource limits | CLI timeouts, bounded stdout/stderr, bounded JSON depth, login count, password and metadata lengths; malformed responses and duplicate login IDs are rejected. |

## Plaintext and remaining limits

* Passwords must temporarily exist as plaintext in the password textbox, CLI process environment/output, short-lived decoded buffers, Windows input events, the destination app, and—when explicitly copied—the system clipboard. Controlled buffers are wiped; Windows/.NET/Bitwarden-owned copies and immutable strings cannot be reliably erased by this app. Process creation still requires transient .NET strings for the master password/session environment values.
* Login names, usernames, URLs, search text, account email, and window titles remain readable metadata in memory and on screen. BwPicker does not create a plaintext password file or vault export. Bitwarden owns its existing on-disk profile/cache; this review does not certify its contents or another client's exports, backups, logs, or crash dumps.
* Clipboard privacy formats are requests to supporting clipboard consumers, not access controls. Other programs can read clipboard text while it exists. A crash, forced termination, or persistently busy clipboard can prevent cleanup. Previously copied data cannot be recalled from another process.
* Process-bound memory encryption is defense in depth, not a boundary against code executing inside this process or malware with access to the user's process/account. The CLI also necessarily decrypts data in its own process. See [Microsoft's memory-protection limits](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectmemory).
* Native window title/focus checks cannot authenticate a browser's URL, page origin, or DOM field. A malicious page can imitate a title or receive typed credentials; a focus race between the check and input is still possible. Ranking is a convenience, not authorization to trust a destination. Review the actual page/app before selecting a login.
* OS event handling is best effort. Abrupt process death, a lost system event, or OS compromise cannot be made safe by application code alone. Windows Forms idle checking can be delayed while its UI thread is occupied. Credential revocation on delivered Windows lock/suspend events is immediate and independent of that timer.
* This local build is not publisher-signed or protected by an installer ACL. A malicious process with write access to the checkout/build output could replace it. Signed distribution, protected install locations, Windows updates, endpoint protection, and Bitwarden account protections remain operational controls.
* Hardened CLI environment handling intentionally does not inherit custom proxy/CA or Node options. Private-CA installations may require a supported, reviewed trust configuration; bypassing TLS verification is rejected.

## Validation performed

* Debug and Release builds succeeded with zero warnings/errors.
* Synthetic regressions passed for secret encryption/zeroing, unsigned executable rejection, sanitized environment, HTTPS policy, malformed vault JSON, output limits, failed unlock, lock during unlock/sync/typing, modifier/foreground/focus-leaving-window changes, focus moving between controls inside the destination, username-only and password-only typing, blocked input, preview isolation, unknown item IDs, and cross-thread revocation.
* UI regression checks passed simulated 96/120/144/192/96 DPI transitions, field containment, font/row sizing, and 1,000-item navigation. Synthetic cached credential reads do not launch a CLI process.
* Live CLI verification passed: official Windows Bitwarden CLI 2026.9.1, valid Bitwarden Inc. signature, SHA-256 `E83615B8DC3A31A2EFECE3772756145FC9954532E95A2D11517C26F54E463015`, and successful status access using the sanitized environment. No live master password or vault item was read in this check.
* NuGet vulnerability inventory reported no vulnerable packages. The app has no third-party NuGet package references. This does not audit the CLI's bundled dependencies or certify the OS/runtime against all vulnerabilities.

The default tests use fake CLI data and simulated keyboard input. Native clipboard privacy/cleanup, physical mixed-DPI monitors, genuine Windows lock/suspend events, browser behavior, and real credential typing still require controlled manual integration checks. No malware scan, exploit fuzzing campaign, or independent assessment was performed.

## Repeatable checks

```powershell
dotnet build -c Release
dotnet run --project tests\BwPicker.Tests.csproj
dotnet tests\bin\Debug\net10.0-windows\BwPicker.Tests.dll --verify-cli
dotnet list BwPicker.csproj package --vulnerable --include-transitive
```

Exit the running app before rebuilding its executable. `--verify-cli` performs read-only status access to the user's actual CLI profile; the default regression suite never accesses the real vault or types/copies secrets into another app.

Windows clipboard format behavior is documented in [Microsoft's clipboard format reference](https://learn.microsoft.com/en-gb/windows/desktop/dataxchg/clipboard-formats). The CLI password/session boundary is documented in [Bitwarden's CLI reference](https://bitwarden.com/en-gb/help/cli/).
