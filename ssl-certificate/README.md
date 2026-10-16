# Code signing (free, self-signed)

This folder holds everything needed to digitally sign the app and its installer **without paying for a certificate**.

| File | Public? | Purpose |
| :--- | :--- | :--- |
| `Create-CodeSigningCert.ps1` | yes | One-time setup: creates the certificate on the release PC. |
| `Sign-File.ps1` | yes | Called by `Build.cmd` to sign `Epic-Games_Account_Switcher.exe`, `-Globals.dll` and the installer. |
| `thumbprint.txt` | yes (commit it) | Certificate fingerprint. Builds embed it so the **in-app updater only accepts installers signed by this exact certificate**. |
| `EpicGamesAccountSwitcher-CodeSigning.cer` | yes (commit it) | Public certificate. Attached to each GitHub release so users can verify the signature or choose to trust it. |
| `private/codesign.pfx` | **NO, git-ignored** | Password-protected backup of the private key. Back it up offline. |

## One-time setup (release PC)

```powershell
powershell -ExecutionPolicy Bypass -File .\ssl-certificate\Create-CodeSigningCert.ps1
```

Then commit `thumbprint.txt` and the `.cer` file. From now on `Build.cmd` signs automatically. Signing is **mandatory** once `thumbprint.txt` exists, because every build pins it.

## What a self-signed signature does and does not do

- ✅ **Tamper detection.** Any modified byte breaks the signature. The in-app updater rejects such files, along with files signed by a different certificate.
- ✅ **Consistent identity.** Every release is provably from the same key.
- ❌ **It does NOT remove the Windows SmartScreen warning or the "Unknown publisher" UAC text for other people.** Only certificates from a paid or approved certificate authority do that.
  Users who want Windows to show the publisher can import the `.cer` into *Trusted People / Trusted Publishers*. This is optional.
- For a free *trusted* signature later, open-source projects can apply to programs such as SignPath Foundation. Verify that the program and its terms are still current before relying on it.

## Never

- Never commit `private/`, `*.pfx`, `*.key` or passwords. `.gitignore` here and at the repo root block them.
- Never replace the certificate casually. Older app versions pin the old thumbprint, so they cannot auto-update to builds signed with a new key. Users would need to reinstall manually once.

## Non-interactive signing (optional)

If the certificate is not in your Windows certificate store, for example on a fresh PC, set `EGAS_SIGN_PFX_PASSWORD` for the session before running `Build.cmd`. Never store it in a file.
