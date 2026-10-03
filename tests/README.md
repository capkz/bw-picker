Run the Windows regression checks with:

```powershell
dotnet run --project tests\BwPicker.Tests.csproj
```

Exit the running tray app before rebuilding its executable.

The checks use synthetic logins and a fake CLI runner; they never access your vault. They cover local-first loading, shared status requests, protected-memory credential reads without CLI processes, failed sync retaining the local vault, lock during sync, and cancellation during unlock.

Security checks cover encrypted memory and zeroing, unsigned CLI rejection, sanitized process environment, HTTPS server policy, malformed vault output and limits, failed unlock, unknown item IDs, preview isolation, cross-thread revocation, and typing interruption on focus/control/modifier changes or vault lock. Keyboard checks use a fake input adapter; they do not send real keystrokes. Clipboard tests cover ownership logic and do not alter your clipboard.

To verify the installed CLI signature and read-only status access with the hardened environment, explicitly run:

```powershell
dotnet tests\bin\Debug\net10.0-windows\BwPicker.Tests.dll --verify-cli
```

This optional check accesses your actual CLI profile but does not unlock or read vault items. See `SECURITY.md` for the review scope, remaining risks, and manual integration checks.

UI checks exercise DPI change messages at 100%, 125%, 150%, and 200%, including returning to 100%. They verify pixel-font sizes, field containment, result row sizes, and navigation with 1,000 items. Sample snapshots go to `bin/previews` when run from the repository root. This simulates DPI transitions; it does not replace checking physical mixed-DPI monitors.

The printed picker and cache timings use synthetic data. Real unlocking still includes Bitwarden CLI startup and password key derivation; network sync happens in the background after the local vault opens.
