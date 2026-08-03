# Epic Games Account Switcher — Full Technical Audit
**Date:** 2026-08-02
**Commit:** `aea07af` (chore(release): publish v2026.04.01.1310)
**Scope:** All 4 projects (Client / Globals / Tests / SingleFilePacker), `Build.cmd`, `project_structure.txt`
**Codebase size:** 8,255 lines across `.cs` / `.xaml` / `.csproj` / `.cmd`

Severity key: **S1** = data loss / security / silent corruption · **S2** = correctness or reliability defect · **S3** = architectural debt blocking evolution · **S4** = performance / hygiene / product gap

---

## SECTION A — CORRECTNESS & DATA-LOSS DEFECTS

---

### 1. `CopyFilesRecursive` reports success after copying zero files — the atomicity guarantee is fictional

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\Globals.cs`
**Lines:** 313–374 (defect concentrated at **343–365**, return at **367**)

**Evidence**

```csharp
// Globals.cs:343-365
foreach (var f in files)
{
    ...
    if (ovr || !File.Exists(dest))
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest)) try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
            try { File.Copy(f, dest, ovr); }
            catch
            {
                var fLong  = f.StartsWith(@"\\") ? f : @"\\?\" + f;
                var destLong = dest.StartsWith(@"\\") ? dest : @"\\?\" + dest;
                if (File.Exists(destLong)) try { File.SetAttributes(destLong, FileAttributes.Normal); } catch { }
                File.Copy(fLong, destLong, ovr);
            }
        }
        catch { }          // <-- line 363: every per-file failure is discarded
    }
}
...
return true;               // <-- line 367: unconditional success
```

Directory enumeration is equally lossy — `Directory.GetDirectories` (line 338) and `Directory.GetFiles` (line 342) both fall back to an empty array on exception, so an `UnauthorizedAccessException` on a subtree makes that subtree simply *not exist* from the copier's perspective.

**Why this is critical**

`CopyFilesRecursive` is the terminal primitive under every "strict" copy in the system:

```
AccountService.CopyDirectoryStrictAsync (AccountService.cs:1030-1038)
  └─ Globals.RetryAsync
       └─ Globals.CopyFilesRecursiveAsync (Globals.cs:309-312)
            └─ Globals.CopyFilesRecursive        <-- always returns true
```

`CopyDirectoryStrictAsync` throws `IOException("Recursive copy failed...")` only when the delegate returns `false` (AccountService.cs:1037). Because `false` is only reachable via the *outer* catch at line 369 (i.e. a failure in `Path.GetFullPath` or the root `Directory.CreateDirectory`), the strict path is effectively unreachable. A session copy where 100% of file copies failed due to a lingering `EpicWebHelper.exe` handle is reported as a complete success.

The only downstream gate is `ValidateSession` (AccountService.cs:197–295), which inspects `PersistentAuthStorage.json` / `OC_*.dat` **only**. `WebCaches`, `SocialCache`, `Manifests`, `Portal` and `Overlay` can all be empty and the session still validates. The user then gets a launcher that is signed in but has lost its library manifests, download state, and web cache — the classic "my games disappeared" failure mode. This directly violates stated Architectural Principle #1 (*Atomicity & Rollback*).

**Correction**

Convert the primitive to a fail-closed, accounting copier:

```csharp
public sealed class CopyReport
{
    public long FilesAttempted, FilesCopied, DirectoriesFailed;
    public readonly List<(string Path, Exception Error)> Failures = new List<(string, Exception)>();
    public bool IsComplete => Failures.Count == 0 && DirectoriesFailed == 0;
}

public static CopyReport CopyFilesRecursive(string s, string d, bool ovr = true, IList<string> ex = null)
```

Rules:
1. Every `catch` in the loop records `(path, exception)` into `report.Failures` instead of discarding.
2. Enumeration failures increment `DirectoriesFailed` and are recorded — never silently treated as "empty directory".
3. `CopyDirectoryStrictAsync` throws when `!report.IsComplete`, including the first 10 failure paths in the message so the log is actionable.
4. Add a post-copy structural assertion for session payloads: compare source vs destination file counts and total byte length per top-level `SessionItemsToCapture` entry; mismatch → throw.
5. Keep a `CopyFilesBestEffort` variant with the current semantics for genuinely optional payloads (skin icons, `Legacy_Import_*`), so intent is explicit at the call site rather than implicit in a swallowed exception.

---

### 2. `CleanOrphanedTempFolders` deletes the crash-recovery rollback instead of restoring it

**Severity:** S1 (permanent loss of the pre-switch live session)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1199–1233 (predicate **1209–1213**, deletion **1216–1226**)
**Called from:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs` line **75** (`OnStartup`, before the main window is shown)

**Evidence**

```csharp
// AccountService.cs:1209-1226
var isTemp = name.StartsWith("_tmp_res", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("_switch_rb", StringComparison.OrdinalIgnoreCase) ||   // <-- rollback root
             name.IndexOf("_rollback_", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("_n", StringComparison.Ordinal) > 3 ||
             name.IndexOf("_r", StringComparison.Ordinal) > 3;
if (isTemp)
{
    var info = new DirectoryInfo(dir);
    if (info.LastWriteTimeUtc < now.AddHours(-1) || info.CreationTimeUtc < now.AddHours(-1))
    {
        Globals.ForceDeleteDirectory(dir);   // <-- destroys the only copy of the live session
```

**Why this is critical**

Trace the switch state machine in `SwitchToAccount` (AccountService.cs:1540–1719):

| Step | Line | Effect |
|---|---|---|
| Create `_switch_rb` | 1598–1599 | rollback root created |
| **Move live `Saved\` into `_switch_rb\Saved`** | 1640 | **the user's live Epic session now exists only inside `_switch_rb`** |
| Move live EOS / Roaming / ProgramData in | 1641–1647 | same |
| Move prepared target into place | 1650–1656 | new session becomes live |
| Delete `_switch_rb` | 1675 | rollback discarded on success |

Between lines 1640 and 1675 there is a window of seconds-to-minutes during which the live Epic state exists **only** under `%LOCALAPPDATA%\EpicAccountSwitcher\EG\_switch_rb`. If the process is killed, the machine loses power, or `Environment.Exit` fires from the crash handler (App.xaml.cs:81) inside that window, `_switch_rb` survives on disk as the sole copy.

On the next launch, `App.OnStartup` line 75 calls `CleanOrphanedTempFolders`, which matches `_switch_rb` at line 1210 and — if more than an hour has elapsed — **permanently deletes it** at line 1221. The recovery artifact is treated as garbage. The user is left with whatever partial state the interrupted switch had written, and no path back.

The predicate at lines 1212–1213 is separately unsound: `name.IndexOf("_n", StringComparison.Ordinal) > 3` matches *any* directory whose name contains `_n` after index 3. Epic account IDs are 32-char hex so this does not fire today, but the heuristic is one schema change away from deleting real account data. Naming conventions are not a safe substitute for explicit metadata.

**Correction**

Replace purge-on-startup with a **journaled recovery state machine**:

1. Write a transaction manifest before the first destructive move (before line 1640):
   ```json
   { "op":"switch", "opId":"<GetShortId>", "startedUtc":"...",
     "fromAccountId":"...", "toAccountId":"...", "phase":"live-moved-to-rollback",
     "entries":[{"rollbackName":"Saved","liveTarget":"C:\\...\\EpicGamesLauncher\\Saved"}, ...] }
   ```
   at `EG\_switch_rb\transaction.json`, flushed with `FileStream.Flush(true)` so it survives power loss.
2. Advance `phase` at each milestone (`target-restored`, `registry-restored`, `verified`, `committed`).
3. On startup, before `CleanOrphanedTempFolders`, run `RecoverInterruptedSwitchAsync()`: if `transaction.json` exists with `phase != "committed"`, replay `RollbackSwitchAsync` from the manifest, then prompt the user ("A previous account switch was interrupted and your session has been restored").
4. Only after successful recovery — or an explicit user decision — is `_switch_rb` deleted.
5. Delete the `_n` / `_r` substring heuristics entirely. Mark every temp directory with a `.egas-temp` marker file containing its creation timestamp and owning PID, and purge based on that marker plus a liveness check on the PID.

---

### 3. Lost-update race: a switch silently overwrites concurrent user edits to `accounts.json`

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 54–55 (two independent locks), 146–176 (`GetAccounts`/`SaveAccounts`), 1550 (read), 1673 (write)
**Also:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines 563, 573, 605, 616

**Evidence**

```csharp
// AccountService.cs:54-55  -- two unrelated mutual-exclusion domains
private static readonly object AccountsFileLock = new object();
public static readonly SemaphoreSlim SessionOperationLock = new SemaphoreSlim(1, 1);
```

```csharp
// AccountService.cs:1550  (inside SwitchToAccount, holding SessionOperationLock)
var accs = GetAccounts();
...                                   // 60+ seconds of process kills and file I/O
// AccountService.cs:1673
SaveAccounts(accs);                   // writes the 60-second-old snapshot
```

Meanwhile the UI thread is free to mutate the same file:

```csharp
// MainViewModel.cs:563   (EditAccount)
AccountService.SaveAccounts(Accounts.ToList());
// MainViewModel.cs:605   (CommitAccountOrderChange)
AccountService.SaveAccounts(currentOrder);
// MainViewModel.cs:616   (UndoAccountOrder)
AccountService.SaveAccounts(Accounts.ToList());
```

`EditAccount` (MainViewModel.cs:552–566) and `DeleteAccount` (568–577) are gated only on `IsBusy`, and `DeleteAccount` additionally calls `AccountService.DeleteAccount` (AccountService.cs:2708–2717) which does its own read-modify-write plus a `ForceDeleteDirectory` on the account's session folder.

**Why this is critical**

`AccountsFileLock` makes each individual read and each individual write atomic, but it does not make the *read-modify-write cycle* atomic. Two interleavings both lose data:

- **Lost edit:** user renames account B at T+5s; switch to account A completes at T+60s and writes its stale list → the rename vanishes with no error.
- **Resurrected account:** user deletes account B at T+5s (JSON updated, `EG\<B>` directory force-deleted); switch writes its stale list at T+60s → account B reappears in the UI as a card pointing at a directory that no longer exists. Clicking it fails at AccountService.cs:1562 with "Saved account session folder is missing."

**Correction**

1. Serialize *all* account-store mutations under the same primitive. `SessionOperationLock` becomes the single writer gate; `AccountsFileLock` is demoted to protecting only the physical file write.
2. Introduce optimistic concurrency. Add a monotonically increasing `Revision` (or the file's `LastWriteTimeUtc` + length) to the store:
   ```csharp
   public static bool TrySaveAccounts(List<Account> accounts, long expectedRevision, out long newRevision);
   ```
   `SwitchToAccount` captures the revision at line 1550 and passes it at line 1673; a mismatch triggers a re-read-and-merge of only the fields the switch owns (`IsActive`, `UsageCount`, `LastPlayed`, `RegistryData`, `Credentials`, `RememberMeDataByPath`, `LauncherWindowSettingsByPath`) rather than a wholesale overwrite.
3. Better still: stop writing the whole list. The switch mutates exactly one account plus the `IsActive` flag; expose `UpdateAccount(string id, Action<Account> mutate)` that re-reads under the lock, applies the delta, and writes. Whole-list serialization is what creates the lost-update surface.
4. Disable destructive UI commands for the duration of a session operation by binding their `CanExecute` to `SessionOperationLock.CurrentCount > 0` semantics exposed as a view-model property (see finding 27 for why the view must not poll the semaphore directly).

---

### 4. Rollback failure is swallowed — there is no "session lost" escalation path

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1755–1772 (`RollbackSwitchAsync`), 1774–1782 (`RestoreRollbackPath`), 1694–1704 (failure handler), 992–1002 (`SafeDeleteDirectoryAsync`)

**Evidence**

```csharp
// AccountService.cs:1755-1772
private static async Task RollbackSwitchAsync(string rollbackRoot)
{
    try
    {
        await RestoreRollbackPath(rollbackRoot, "Saved", EpicSavedPath);
        ...
    }
    catch (Exception ex)
    {
        Globals.WriteToLog("[ROLLBACK] Switch rollback failed.", ex);   // <-- log and continue
    }
}
```

```csharp
// AccountService.cs:1694-1703
catch (Exception ex)
{
    SetLastSwitchFailure("Switch failed during restore: " + ex.Message);
    await RollbackSwitchAsync(rbB);            // return value: void, outcome unknowable
    RestoreRegistrySnapshot(registryRollback); // void, per-key failures swallowed at 1823-1827
    ...
    return false;                              // caller cannot distinguish "safely rolled back"
}                                              // from "live session destroyed"
```

`SafeDeleteDirectoryAsync` (lines 992–1002) also swallows and reports success-by-probe (`return !Directory.Exists(path)`), so `RestoreRollbackPath` line 1778 can leave a partially-deleted target directory that then causes `SafeMovePathAsync` to throw `"Move target already exists"` (line 1104) — inside the rollback, where the exception is caught and discarded.

**Why this is critical**

The three outcomes below are indistinguishable to the caller, all returning `false`:

| Outcome | User impact |
|---|---|
| Failed before any destructive move | None — safe |
| Failed after move, rollback succeeded | None — safe |
| Failed after move, **rollback also failed** | Live Epic session destroyed; user must re-login manually and may have lost launcher state |

`MainViewModel.SwitchToAccount` (MainViewModel.cs:459–463) renders all three as a generic "Switch Failed" dialog. In the third case the correct behaviour is a blocking, unmistakable error with recovery instructions and the rollback path — not a toast-adjacent message box.

**Correction**

1. Change the signature to `Task<RollbackOutcome> RollbackSwitchAsync(...)` where `RollbackOutcome` is `{ Completed, PartiallyCompleted, Failed }` plus a per-entry result list.
2. Make `SwitchToAccount` return a result object (see finding 22):
   ```csharp
   public sealed class SwitchResult
   {
       public bool Succeeded;
       public SwitchFailureStage Stage;      // Validation | ProcessCleanup | Prepare | Commit | Verify
       public RollbackOutcome Rollback;
       public bool LiveSessionIntact;        // <-- the field the UI must branch on
       public string OperationId, Message;
   }
   ```
3. When `LiveSessionIntact == false`, persist a `RECOVERY_REQUIRED` marker next to `_switch_rb`, surface a modal that names the rollback directory, and offer a one-click "Attempt recovery again" action on next launch.
4. Make `RestoreRegistrySnapshot` (lines 1817–1829) return the count of keys it failed to set after its 3 retries, and fold that into `RollbackOutcome` — a rolled-back filesystem with a half-restored registry identity is still a broken state.

---

### 5. "OAuth expiration verification" for modern auth blobs is actually a file-mtime heuristic

**Severity:** S2 (contradicts a documented core guarantee)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 297–336 (`SelectModernAuthBlob`), 219–228 and 256–264 (consumption in `ValidateSession`)
**Contradicts:** `project_structure.txt` §4 principle 2 — *"validate modern binary auth blobs against OAuth expiration timestamps before restoration"*

**Evidence**

```csharp
// AccountService.cs:312-321
var lastWrite = File.GetLastWriteTimeUtc(path);
if (lastWrite <= DateTime.MinValue.AddYears(1)) lastWrite = File.GetCreationTimeUtc(path);

return new ModernAuthBlobInfo
{
    AccountId    = id.Trim(),
    Path         = path,
    LastWriteUtc = lastWrite,
    FreshUntilUtc = lastWrite.AddDays(maxAgeDays)   // <-- "expiry" == mtime + AppSettings value
};
```

`maxAgeDays` comes from `AppSettings.ModernAuthBlobMaxAgeDays` (default 30 — `AppSettings.cs` line 42). The `OC_<id>.dat` file is never parsed; only its name and timestamp are used.

**Why this matters**

Two failure directions:

- **False positive** (worse): a blob whose refresh token Epic revoked server-side — password change, "sign out everywhere", 2FA reset, suspicious-login lockout — still has a recent mtime. `ValidateSession` returns `IsValid = true` at line 260, the switch commits, the launcher opens and prompts for a password. That is precisely the Zero-UI Mandate failure the principle exists to prevent, and it happens *after* the live session has been destructively replaced.
- **False negative:** a long-lived blob for an account the user rarely opens is rejected at line 223 as "stale" even though the token is fine, blocking a legitimate switch.

Copying a blob during capture also rewrites its mtime (via `File.Copy` in `SafeReplaceFileAsync`, lines 1016–1028), so the "freshness" clock silently resets on every capture — the metric measures *switcher activity*, not *token validity*.

**Correction**

1. Parse the blob. `OC_<accountId>.dat` is a DPAPI-protected container; call `ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)` and extract the token envelope, then read the real `expires_at` / `refresh_expires_at`. Guard the whole path in try/catch and degrade to the current heuristic only when parsing fails, logging the degradation explicitly.
2. Rename the fields so the code cannot lie about itself: `FreshUntilUtc` → `HeuristicFreshUntilUtc`, and add `AuthoritativeExpiryUtc` (nullable). `ValidateSession` prefers the authoritative value and marks the result `ConfidenceLevel = Heuristic | Authoritative`.
3. Surface confidence in the UI: an account card whose validity is heuristic-only should show "expiry unknown" rather than a green state (see finding 40).
4. Preserve timestamps across capture: use `File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src))` in `SafeReplaceFileAsync` so the heuristic is at least measuring the right thing while it remains in place.
5. Correct `project_structure.txt` §4 to describe what the code does until item 1 ships.

---

### 6. `AtomicReplaceFile` will throw on non-NTFS and cross-volume targets

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1865–1876
**Same pattern:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AppSettings.cs` lines 88–99

**Evidence**

```csharp
// AccountService.cs:1865-1876
private static void AtomicReplaceFile(string tmp, string target)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(target));
    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    if (File.Exists(target))
    {
        var backup = target + ".bak_" + Guid.NewGuid().ToString("N");
        File.Replace(tmp, target, backup, true);   // <-- NTFS-only, same-volume-only
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
    }
    else { File.Move(tmp, target); }
}
```

**Why this matters**

`File.Replace` maps to `ReplaceFile` (Win32) and throws when source and destination are on different volumes, and on filesystems without transactional rename support (exFAT/FAT32 USB drives, some network redirectors, certain FUSE-backed mounts). This is reachable in practice:

- `BackupAccounts` line 2062 calls `AtomicReplaceFile(outputTmp, zip)` where `zip` is a user-chosen `SaveFileDialog` path (`MainViewModel.cs` lines 636–642) — a USB stick or network share is a normal choice for a backup destination.
- `outputTmp` is `zip + ".tmp_" + Guid` (line 2015), so it *is* on the same volume; but if the target is exFAT the call still fails.

The resulting exception propagates out of `BackupAccounts` after the encrypted temp file has been written, and the `finally` at lines 2065–2071 deletes it — so the user sees "Backup Failed" and gets nothing, despite the expensive work having completed successfully.

**Correction**

```csharp
private static void AtomicReplaceFile(string tmp, string target)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(target));
    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

    if (!File.Exists(target)) { MoveOrCopyDelete(tmp, target); return; }

    var backup = target + ".bak_" + Guid.NewGuid().ToString("N");
    try
    {
        File.Replace(tmp, target, backup, true);
        try { File.Delete(backup); } catch { }
    }
    catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
    {
        Globals.WriteToLog("[IO] File.Replace unsupported on target volume; using degraded swap. Target=" + target, ex);
        // degraded, non-atomic but recoverable: rename-aside, move-in, delete-aside
        File.Move(target, backup);
        try { MoveOrCopyDelete(tmp, target); }
        catch { File.Move(backup, target); throw; }   // restore original on failure
        try { File.Delete(backup); } catch { }
    }
}
```

Add a unit test that runs the degraded path by stubbing an `IFileSystem` whose `Replace` throws (see finding 32). Apply the identical fix to `AppSettings.SaveSettings` lines 88–99.

---

### 7. `MakeRelativePath` uses `System.Uri` and corrupts paths containing `#`, `?` or `%`

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1878–1883
**Consumers:** lines 415 (`CaptureIniValuesByPath`), 432 and 450 (surgical setters), 575 (`VerifyRememberMeApplied`)

**Evidence**

```csharp
// AccountService.cs:1878-1883
private static string MakeRelativePath(string root, string fullPath)
{
    var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(...) + Path.DirectorySeparatorChar);
    var pathUri = new Uri(Path.GetFullPath(fullPath));
    return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
}
```

**Why this matters**

`new Uri(@"C:\a\b#c\GameUserSettings.ini")` treats `#c\GameUserSettings.ini` as a URI *fragment*, so `MakeRelativeUri` returns `b` and the filename is lost. `%20` in a directory name round-trips through `UnescapeDataString` into a literal space, producing a path that does not exist.

The keys produced here are persisted into `Account.RememberMeDataByPath` and `Account.LauncherWindowSettingsByPath` (`AccountModel.cs` lines 60, 62) and later re-joined via `Path.Combine(EpicConfigPath, kv.Key)` at lines 437, 454 and 580. A corrupted key silently writes the `RememberMe` blob to the wrong file — or to a file outside `EpicConfigPath` if the relative path escapes. `VerifyRememberMeApplied` then fails at line 583 ("RememberMe target missing"), which `ValidateRestoredState` (line 1849) escalates into a full switch failure *and rollback* — a user with a `#` in a config directory name can never switch accounts, with a misleading error.

There is also no containment check: nothing verifies that `Path.Combine(EpicConfigPath, kv.Key)` remains under `EpicConfigPath`. Since these keys survive into backup files (`accounts.json` inside the `.egbackup`), a crafted backup can direct `ApplyIniValuesAtomic` (lines 487–494) to write attacker-chosen INI content to an arbitrary path — see finding 15.

**Correction**

1. Replace with string arithmetic, no `Uri`:
   ```csharp
   private static string MakeRelativePath(string root, string fullPath)
   {
       var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
       var f = Path.GetFullPath(fullPath);
       if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
           throw new ArgumentException("Path is not under root: " + fullPath);
       return f.Substring(r.Length);
   }
   ```
   (On .NET 5+, `Path.GetRelativePath` is the correct primitive — another reason to migrate, finding 30.)
2. Add a `ResolveUnderRoot(root, relative)` helper used by lines 437, 454 and 580 that re-validates containment after `Path.Combine` and throws on escape. Reuse the same containment logic as `GetSafeExtractionPath` (lines 2694–2706) rather than duplicating it.
3. Validate keys at deserialization time in `NormalizeAccount` (lines 178–190): drop any `RememberMeDataByPath` / `LauncherWindowSettingsByPath` key that is rooted, contains `..`, or fails containment.

---

### 8. Return values of the surgical INI setters are discarded; one always returns `true`

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 425–441 (`SetRememberMeDataSurgically`), 443–458 (`SetLauncherWindowSettingsSurgically`), call sites 1664–1666

**Evidence**

```csharp
// AccountService.cs:443-458
private static bool SetLauncherWindowSettingsSurgically(Account account)
{
    ...
    foreach (var kv in values)
    {
        var ini = Path.Combine(EpicConfigPath, kv.Key);
        ApplyIniValuesAtomic(ini, "WindowSettings", ...);   // void, throws are unhandled
    }
    return true;                                            // <-- unconditional
}
```

```csharp
// AccountService.cs:1664-1666  -- all three results discarded
SetEmailSurgically(t.Email);
SetRememberMeDataSurgically(t);           // returns bool (verification result) — ignored
SetLauncherWindowSettingsSurgically(t);   // returns bool (always true) — ignored
```

**Why this matters**

`SetRememberMeDataSurgically` performs a real verification via `VerifyRememberMeApplied` (line 440) and returns the outcome — which is thrown away at line 1665. The failure is only caught later by `ValidateRestoredState` (line 1849) calling `VerifyRememberMeApplied` a *second* time. That double work is wasteful, but worse, it means the log records the failure at the wrong point in the sequence, and any developer reading lines 1664–1666 reasonably concludes these operations cannot fail.

`SetLauncherWindowSettingsSurgically`'s `bool` is pure decoration: it has no failure detection at all, and `ApplyIniValuesAtomic` can throw (its `Directory.CreateDirectory` and `File.WriteAllLines` are unguarded), which escapes into the outer `catch` at line 1694 and triggers a full rollback over a cosmetic window-position setting.

`SetEmailSurgically` (lines 460–474) swallows everything into a log line, so a failure to write `LastCheckedEmail` is invisible — yet that value is what `GetDetectedMetadata` (line 1946) reads back to identify the account.

**Correction**

1. Make the contract explicit: `IniPatchResult ApplyAccountConfig(Account target)` returning `{ RequiredPatchesApplied: bool, CosmeticPatchesApplied: bool, Failures: List<(path, exception)> }`.
2. Classify each patch. `RememberMe` and `LastCheckedEmail` are **required** — a failure must abort and roll back. `WindowSettings` is **cosmetic** — a failure must be logged and ignored, never rolled back.
3. Branch at line 1665 on `RequiredPatchesApplied`, and delete the redundant `VerifyRememberMeApplied` call inside `ValidateRestoredState` (line 1849) now that the result is consumed at the point of action.
4. Wrap `ApplyIniValuesAtomic` in try/catch that attributes the exception to a specific INI path, so a single unwritable config file names itself in the log.

---

### 9. Backups embed DPAPI `CurrentUser` blobs, so "restore on any machine" silently produces broken profiles

**Severity:** S1 (silent data loss across machines; the UI makes a promise the format cannot keep)
**Files / lines:**
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs` lines **2556–2591** (`CloneAccounts` copies `EncryptedPassword` at 2583 and raw credential `Blob` at 2576), line **2039** (serialized into the backup's `accounts.json`)
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountModel.cs` lines **145–194** (`Password` property, `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser` at 155 and 179)
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines **644–648** — the UI text that makes the promise

**Evidence**

```csharp
// MainViewModel.cs:644-648
var passwordWindow = new PasswordPromptWindow(
    "Encrypt Backup",
    "Choose a password for this backup. It will be required to restore the account profiles on any machine.",
    true, 8) { Owner = Application.Current.MainWindow };
```

```csharp
// AccountModel.cs:155
decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
// AccountModel.cs:158-161
catch { return string.Empty; }   // <-- cross-machine failure returns "" with no signal
```

```csharp
// AccountService.cs:2576, 2583  (CloneAccounts, feeding the backup)
Blob = c.Blob == null ? null : c.Blob.ToArray(),   // Windows Credential Manager blob, as-is
...
EncryptedPassword = account.EncryptedPassword,     // DPAPI CurrentUser ciphertext, as-is
```

**Why this is critical**

DPAPI `CurrentUser` ciphertext is bound to the user's master key, which is derived from the account's credentials on that machine (or roamed via a domain profile). On a different machine or a different local user, `Unprotect` throws `CryptographicException`. `AccountModel.Password` catches it at line 158 and returns `string.Empty` — the password is gone with no error, no log, and no UI signal.

The Credential Manager blobs are worse. `CredentialService.RestoreCredentials` (`CredentialService.cs`) writes the captured bytes back verbatim via `CredWrite`. Epic's own blob content is itself DPAPI-protected in most builds, so a cross-machine restore writes a credential entry that exists, has the right target name, and contains undecryptable bytes — the launcher fails at a layer far from the switcher, and the user has no way to attribute it.

Net effect: the backup file is portable, but roughly half its payload is machine-bound. Restore on a new machine appears to succeed (`RestoreAccounts` completes, cards appear) and then switching fails in ways that look like Epic's fault.

**Correction**

1. **Rewrap at backup time.** In `CloneAccounts` (lines 2556–2591), decrypt `EncryptedPassword` with `DataProtectionScope.CurrentUser` and re-encrypt under the backup's derived key material (the same `keyMaterial` produced by `DeriveBackupKeyMaterial`, lines 2471–2477), writing it to a new `PortablePassword` field. Do the same for `SavedCredential.Blob`. Bump the archive magic from `EGASBKP2` to `EGASBKP3` (line 56) so the format change is explicit.
2. **Rewrap at restore time**, converting `PortablePassword` back to a local DPAPI `CurrentUser` blob so at-rest protection on the new machine is unchanged.
3. **Fail loudly, not silently.** `AccountModel.Password`'s getter (lines 158–161) must log the `CryptographicException` and set a `CredentialsUnavailable` flag on the account. The card should render a "credentials need re-capture" badge rather than pretending nothing happened.
4. **Fix the promise.** If item 1 is not shipped immediately, change the string at MainViewModel.cs:646 to state that stored passwords and vault credentials are machine-bound and will require re-capture after restoring elsewhere.
5. Add a round-trip test that simulates the cross-machine case by generating a backup, corrupting the DPAPI entropy, and asserting the restore surfaces a typed warning rather than an empty string.

---

### 10. Unauthenticated legacy ZIP restore is a permanent code-execution-adjacent ingress

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 2215–2232 (`PrepareBackupForRestore`), 2199–2213 (`IsEncryptedBackup`), 2098–2164 (extraction and application)
**UI reachability:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines 674–677 — the file filter accepts `*.zip` and `*.*`

**Evidence**

```csharp
// AccountService.cs:2215-2218
private static string PrepareBackupForRestore(string path, string password, out string decryptedZip, CancellationToken ct)
{
    decryptedZip = null;
    if (!IsEncryptedBackup(path)) return path;   // <-- no magic header => trusted verbatim
```

```csharp
// MainViewModel.cs:674-677
var ofd = new Microsoft.Win32.OpenFileDialog
{
    Filter = "Epic Switcher Backup (*.egbackup;*.zip)|*.egbackup;*.zip|All Files (*.*)|*.*"
};
```

**Why this is critical**

`IsEncryptedBackup` only checks for the 8-byte `EGASBKP2` magic (line 56). Any file lacking it is treated as a legacy plain ZIP and processed with **no authentication, no integrity check, and no provenance check**. The restore then:

- extracts every entry (lines 2101–2117),
- deserializes `accounts.json` into `List<Account>` (line 2127) — attacker-controlled `RegistryData`, `Credentials`, `RememberMeDataByPath`, `LauncherWindowSettingsByPath`, `ProfileImagePath`,
- **deletes the user's entire existing account store** (lines 2138–2151, `SafeMovePathBlocking(BaseDataPath, rb)`),
- copies the attacker's tree into `%LOCALAPPDATA%\EpicAccountSwitcher\EG` (line 2157),
- **overwrites `appsettings.json` and hot-reloads it** (lines 2160–2161).

That last step is the sharpest edge: `AppSettings.RegistryKeysToSwap` (`AppSettings.cs` lines 29–37) is a caller-supplied list of registry paths that `ClearRegistryIdentity` (AccountService.cs:1831–1835) will subsequently **delete** on the next switch. A malicious backup can point that list at arbitrary `HKCU`/`HKLM` values. Combined with attacker-controlled `Account.RegistryData` — which `RestoreRegistryIdentity` (lines 1784–1815) writes verbatim through `Globals.SetRegistryKey` — a restored backup can write arbitrary registry values, including `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. `SetRegistryKey` (`Registry.cs` lines 128–177) performs no allow-listing of the target path.

Path traversal in the ZIP itself *is* correctly blocked (`GetSafeExtractionPath`, lines 2694–2706, covered by `RestoreRejectsZipTraversalEntries` in the test suite). The unvalidated *content* is the gap.

**Correction**

1. Make authenticated restore the default and legacy restore explicit: `RestoreAccounts(path, password, RestoreOptions { AllowUnauthenticatedLegacyArchive = false })`. The UI must present a distinct, clearly-worded confirmation before setting that flag.
2. Restrict the file dialog filter at MainViewModel.cs:676 to `*.egbackup` for the primary path; expose legacy import behind a separate "Import legacy backup (unverified)" menu item.
3. **Validate the deserialized model** before applying it, regardless of archive type:
   - `Account.Id` must match `^[a-fA-F0-9]{32}$`.
   - Every `RegistryData` key must match an allow-list rooted at `HKCU\Software\Epic Games\`.
   - Every `RememberMeDataByPath` / `LauncherWindowSettingsByPath` key must pass containment under `EpicConfigPath` (finding 7).
   - `ProfileImagePath` must resolve inside a known skin root or the extraction root.
4. **Never import `appsettings.json` from a backup.** Delete lines 2160–2161. Application settings are machine-local; the only field worth migrating is user preferences, which should be a separate, schema-validated section.
5. Add a hard allow-list inside `Globals.SetRegistryKey` (`Registry.cs` line 128) so the primitive itself refuses to write outside `HKCU\Software\Epic Games\`. Defence in depth: the validation in item 3 can be bypassed by a future call site; the primitive cannot.

---

### 11. `AppSettings.SettingsPath` is captured at type-initialization, defeating `--user-data`

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AppSettings.cs`
**Lines:** 13 (static field), 15–27 (`Instance`), 56–75 (`LoadSettings`), 77–103 (`SaveSettings`)
**Interacting call site:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs` line **425**

**Evidence**

```csharp
// AppSettings.cs:13
private static readonly string SettingsPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");
```

```csharp
// App.xaml.cs:425  -- override applied AFTER the installer flow has already touched AppSettings
if (!string.IsNullOrWhiteSpace(sourceUserDataPath)) Globals.UserDataFolderOverride = sourceUserDataPath;
```

**Why this matters**

`static readonly` fields are initialized once, at first access to the type, and `Globals.UserDataFolder` (Globals.cs lines 36–46) resolves `UserDataFolderOverride` *at that moment*. Any code path that reads `AppSettings.Instance` before the override is assigned permanently binds the settings file to the default `%LOCALAPPDATA%\EpicAccountSwitcher` location for the remainder of the process.

In `OnStartup` the ordering happens to be correct (override at line 41, first `AppSettings` read at line 77). In `PerformSmartInstall` it is not: the elevated relaunch path constructs `CustomMessageBox` instances (line 408) and touches `Globals.UserDataFolder` (line 397) before line 425 sets the override. The test suite (`AccountServiceSafetyTests.ClassInitialize`) sets the override first and therefore never exercises the broken ordering — the bug is structurally invisible to the current tests.

Consequence: `--user-data` is a load-bearing installer argument (`BuildInstallArguments`, App.xaml.cs lines 133–141) whose effect on settings is order-dependent and untested.

**Correction**

1. Convert to a computed property so the override is always honoured:
   ```csharp
   private static string SettingsPath => Path.Combine(Globals.UserDataFolder, "appsettings.json");
   ```
2. Make the override write-once and fail-fast: `Globals.UserDataFolderOverride`'s setter should throw `InvalidOperationException` if the folder has already been resolved by any consumer, so ordering violations surface at development time rather than as a mysterious empty settings file in production.
3. Longer term, eliminate the ambient singleton: inject `ISettingsStore` (finding 20) so the path is a constructor argument and the ordering hazard cannot exist.
4. Add a regression test that reads `AppSettings.Instance` *before* setting the override and asserts the override still takes effect after `LoadSettings()`.

---

## SECTION B — SECURITY DEFECTS

---

### 12. Local privilege escalation: elevated installer copies an unverified `%TEMP%` payload into Program Files

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs`
**Lines:** 510–518 (the elevated copy), 419–421 (elevation relaunch), 43 (`--extract-dir` intake), 171–182 (`IsSafeTempExtractDir`)
**Origin of the directory:** `C:\Full_Control\Epic-Games_Account_Switcher\SingleFilePacker\Program.cs` lines 40–53

**Evidence**

```csharp
// SingleFilePacker/Program.cs:40-53
temp = Path.Combine(Path.GetTempPath(), "EpicSwitcherInstaller_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);                       // inherits parent ACL, no explicit DACL
...
ExtractZipSafely(Path.Combine(temp, "payload.zip"), temp);
var exe = Path.Combine(temp, "Epic-Games_Account_Switcher.exe");
...
Process.Start(new ProcessStartInfo { FileName = exe, Arguments = cmdArgs, WorkingDirectory = temp, UseShellExecute = true });
```

```csharp
// App.xaml.cs:419-421  -- relaunch elevated, passing the temp dir through
string args = BuildInstallArguments(preserve, originalUserData, originalBackupPath, extractDir);
Process.Start(new ProcessStartInfo { FileName = CurrentProcessPath, Arguments = args, UseShellExecute = true, Verb = "runas" });
```

```csharp
// App.xaml.cs:510-518  -- now running as Administrator
Directory.CreateDirectory(targetDir);                                  // C:\Program Files\Epic-Games_Account_Switcher
File.Copy(CurrentProcessPath, targetExe, true);
Globals.CopyFilesRecursive(AppDomain.CurrentDomain.BaseDirectory, targetDir, true);   // <-- copies %TEMP% tree, elevated
```

**Why this is critical**

The exploitable window is between the unelevated extraction (Program.cs line 47) and the elevated copy (App.xaml.cs line 517). During that window — which includes a UAC prompt the user must read and click, typically 2–10 seconds — the extraction directory is writable by the invoking user and by any process running as that user.

A malicious or compromised low-privilege process on the machine can:
1. Watch `%TEMP%` for `EpicSwitcherInstaller_*` (the prefix is fixed and predictable, and `PurgeOldExtractedPayloads` at App.xaml.cs:207 confirms it is enumerable).
2. Drop or replace a DLL that the WPF app will load, or add arbitrary files to the tree.
3. Wait for the user to accept UAC.
4. `CopyFilesRecursive` at line 517 writes the planted content into `C:\Program Files\Epic-Games_Account_Switcher` with administrator rights.

The planted file now sits in a directory that standard users cannot write but that the application executes from — a persistent escalation. The binary is unsigned, so there is no signature check anywhere in the chain. `IsSafeTempExtractDir` (lines 171–182) validates only that the *path string* is under `%TEMP%` and starts with the expected prefix; it makes no statement about the *contents*.

The same primitive is reused in the uninstall path at line 619 (`CopyFilesRecursive` into `Epic_Uninstaller_Workspace`, then executed at line 623).

**Correction**

1. **Harden the extraction directory ACL at creation** (Program.cs line 41). Create the directory with an explicit DACL granting only the creating user and `BUILTIN\Administrators`, and deny inherited permissions:
   ```csharp
   var sec = new DirectorySecurity();
   sec.SetAccessRuleProtection(true, false);
   sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
       InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
   Directory.CreateDirectory(temp, sec);
   ```
2. **Verify before elevated copy.** Emit a manifest (`payload.sha256`) at pack time (Program.cs `Pack`, lines 19–29) listing every file with its SHA-256. Before line 517, re-hash the tree and abort the install on any mismatch or unexpected file. This closes the TOCTOU window even if the ACL is bypassed.
3. **Copy an allow-list, not a directory.** Line 517 copies *whatever is present*. Replace with an explicit list of expected files derived from the manifest; anything unexpected is a hard error, not a silent inclusion.
4. **Authenticode-sign** the distribution executable and verify the signature of `CurrentProcessPath` before self-copying at line 514.
5. Apply items 1–3 to the uninstall workspace at lines 614–623.

---

### 13. `taskkill` invoked without a fully-qualified path while elevated (PATH hijack)

**Severity:** S1
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs`
**Lines:** **259** and **286**
**Correct pattern already present in:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs` lines 773–774

**Evidence**

```csharp
// App.xaml.cs:259  (inside KillAllProcesses, reached from the elevated installer at line 455)
Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /T /PID " + p.Id,
                                     CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
// App.xaml.cs:286  -- identical
```

Contrast with the correct form already used elsewhere in the codebase:

```csharp
// AccountService.cs:773-774
var sysTaskkill   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
var sysPowershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
```

Also relevant: `schtasks` at App.xaml.cs lines **456** and **662**, and `cmd.exe` at lines **192** and **721** — all unqualified.

**Why this is critical**

With `UseShellExecute = false`, `Process.Start` resolves the image name using the standard search order, which includes the **current working directory** and every `%PATH%` entry. Two aggravating factors here:

- `App.OnStartup` line 74 executes `Directory.SetCurrentDirectory(Globals.UserDataFolder)` — a user-writable directory — though the installer path returns before reaching it. In the installer path the working directory is the `%TEMP%` extraction folder (set by Program.cs line 53), which is user-writable and is exactly the directory an attacker is already targeting per finding 12.
- `KillAllProcesses` is called at line 455 and line 661 from code running **elevated**. A planted `taskkill.exe` therefore executes with administrator rights.

If any `%PATH%` entry is user-writable (a very common misconfiguration from developer tooling installs), the hijack does not even require the TOCTOU window.

**Correction**

1. Introduce a single helper and route every process launch through it:
   ```csharp
   private static string System32(string exe) =>
       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
   ```
   Apply to `taskkill.exe` (lines 259, 286), `schtasks.exe` (456, 662), `cmd.exe` (192, 721).
2. Prefer managed APIs over shelling out where possible. `Process.Kill(entireProcessTree: true)` on .NET 5+ removes the need for `taskkill /T` entirely (finding 30). `TaskService` / COM removes `schtasks`.
3. Never call `Directory.SetCurrentDirectory` to a user-writable path (App.xaml.cs line 74). It affects DLL search order and relative path resolution process-wide. Resolve `Globals.UserDataFolder` explicitly at each use site instead.
4. Add a build-time analyzer rule or a unit test that greps the compiled sources for `FileName = "` followed by a bare executable name.

---

### 14. Self-deleting batch files written to `%TEMP%` and executed via `cmd.exe`

**Severity:** S2 (attack surface + AV false-positive risk)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs`
**Lines:** 184–201 (`ScheduleDirectoryDeleteAfterExit`), 704–722 (uninstall self-delete batch)

**Evidence**

```csharp
// App.xaml.cs:189-197
string batchPath = Path.Combine(Path.GetTempPath(), "del_payload_" + Guid.NewGuid().ToString("N") + ".bat");
string batchContent = string.Format("@echo off\r\ntimeout /t 2 /nobreak > nul\r\nrd /s /q \"{0}\"\r\ndel \"%~f0\"\r\n", path);
File.WriteAllText(batchPath, batchContent);
Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c \"" + batchPath + "\"", ... });
```

```csharp
// App.xaml.cs:708-721  -- the uninstall variant, running elevated
string batchContent = string.Format(
    "@echo off\r\nset count=0\r\n:retry\r\ntimeout /t 1 /nobreak > nul\r\n" +
    "del /f /q \"{0}\" 2>nul\r\nif not exist \"{0}\" goto done\r\n..." +
    (isTempWorkspace ? "rd /s /q \"" + tempDir + "\" 2>nul\r\n" : "") +
    "del \"%~f0\"", CurrentProcessPath);
File.WriteAllText(batch, batchContent);
Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c \"" + batch + "\"", ... });
```

Note line 704: the uninstall batch path is **not randomized** — `Path.Combine(Path.GetTempPath(), "epic_sw_del.bat")`. Any process running as the user can pre-create that file, or replace it in the window between `File.WriteAllText` (line 720) and `Process.Start` (line 721). The uninstall path runs elevated (guaranteed by the `runas` relaunch at line 629), so the replaced batch executes with administrator rights — a second, independent escalation vector.

There is no argument quoting or escaping applied to `{0}`; `CurrentProcessPath` and `tempDir` are interpolated directly into a batch script where `&`, `^`, `%` and `"` are all metacharacters.

**Why this matters**

Beyond the escalation vector, the composite behaviour — an unsigned executable that writes a script to `%TEMP%`, spawns `cmd.exe` to delete itself, terminates processes by image name, and writes to `HKLM\...\Uninstall` — is the exact heuristic profile of a dropper. Expect SmartScreen blocks, Defender ASR rule "Block process creations originating from PSExec and WMI commands" interference, and third-party AV quarantine. That is a distribution problem, not just a security one.

**Correction**

1. **Delete this mechanism.** Move installation and uninstallation to a real packaging technology (finding 26). MSI (WiX) and MSIX both handle self-removal, reboot-pending file deletion, and rollback natively, and neither requires a batch trampoline.
2. If a stopgap is needed before that migration:
   - Randomize the uninstall batch filename at line 704 exactly as line 189 does.
   - Create the batch file with `FileMode.CreateNew` and an explicit admin-only DACL so pre-creation and replacement both fail.
   - Use `MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)` instead of a polling batch script for the self-delete — it is the supported Win32 mechanism and requires no child process.
   - Escape all interpolated values, or better, pass them via environment variables rather than inline script text.
3. Sign the executable (finding 12, item 4) — an Authenticode signature with reputation materially reduces the heuristic detection surface.

---

### 15. Windows Vault credential blobs and account metadata are stored unencrypted at rest

**Severity:** S1
**Files / lines:**
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs` lines **166–176** (`SaveAccounts` → plaintext JSON), line **1321** and **1352** (`CredentialService.CaptureCredentials` results assigned onto the model)
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountModel.cs` line **63** (`List<SavedCredential> Credentials` — a plain serialized property)
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\CredentialService.cs` — `SavedCredential.Blob` is a `byte[]`, serialized by Newtonsoft as base64

**Evidence**

```csharp
// AccountService.cs:166-176
public static void SaveAccounts(List<Account> accounts)
{
    lock (AccountsFileLock)
    {
        Directory.CreateDirectory(BaseDataPath);
        foreach (var a in accounts) NormalizeAccount(a);
        var tmp = AccountsJsonPath + ".tmp_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, JsonConvert.SerializeObject(accounts, Formatting.Indented));  // <-- plaintext
        AtomicReplaceFile(tmp, AccountsJsonPath);
    }
}
```

The `Password` field is correctly DPAPI-protected (`AccountModel.cs` lines 145–194) and marked `[JsonIgnore]`, with only `EncryptedPassword` persisted. **`Credentials` is not.** `SavedCredential.Blob` — the raw bytes lifted out of the Windows Credential Manager via `CredEnumerate` — is written to `%LOCALAPPDATA%\EpicAccountSwitcher\EG\accounts.json` as base64 with no additional protection layer.

`RegistryData` (`AccountModel.cs` line 64) is likewise plaintext and contains `MachineId`, `DeviceId`, `AppId` and `AccountId` — the full device identity fingerprint for each account.

**Why this is critical**

The Windows Credential Manager exists precisely so that these blobs are protected by the user's DPAPI master key and are not sitting in a readable file. This code enumerates them out of that protected store and writes them to a flat file, which:

- makes them readable by anything running as the user — every browser extension host, every game mod loader, every piece of adware,
- makes them harvestable by generic infostealer malware, which already targets `%LOCALAPPDATA%` JSON files as a matter of routine,
- persists them indefinitely with no expiry, so a credential revoked months ago is still on disk.

The session directories under `EG\<Id>\Saved\` contain `PersistentAuthStorage.json` / `.bin` in whatever form Epic wrote them, which is at least consistent with Epic's own protection — but `accounts.json` is a net *reduction* in the security posture of data that Windows was already protecting.

**Correction**

1. **Wrap the sensitive fields in DPAPI at rest.** Add a `[JsonConverter(typeof(DpapiProtectedConverter))]` on `SavedCredential.Blob` and `Account.RegistryData` so serialization emits `ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)` and deserialization reverses it. Use a per-install random entropy value stored in the settings file so the ciphertext is not directly interchangeable between installs.
2. **Better: stop persisting blobs entirely.** The credential swap only needs to know *which* targets to delete and restore. Investigate keeping the blob inside the Credential Manager under a renamed target (`com.epicgames.launcher.egas-profile-<id>`) rather than exporting the bytes. This keeps the secret inside the OS store for its whole lifecycle and eliminates the file-based exposure.
3. **ACL the data directory.** `Globals.UserDataFolder` (Globals.cs lines 36–46) is created with inherited permissions. Create it with an explicit DACL granting only the current user, and set `FileAttributes.NotContentIndexed` so its contents are excluded from Windows Search indexing.
4. **Add expiry.** `SavedCredential.CapturedAtUtc` already exists but is never read. Purge or re-capture credentials older than a configurable threshold, and never restore an expired one.
5. Document the at-rest threat model in `project_structure.txt` §5 so the trade-off is a decision rather than an accident.

---

### 16. Backup KDF parameters are read from the untrusted archive header and floor at 10,000 iterations

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 61 (`BackupPasswordIterations = 150000`), 2334–2348 (header parse and key derivation), 2471–2477 (`DeriveBackupKeyMaterial`)

**Evidence**

```csharp
// AccountService.cs:2334-2348
var mode        = reader.ReadInt32();
var iterations  = reader.ReadInt32();          // <-- attacker-controlled
var salt        = reader.ReadBytes(reader.ReadInt32());
...
if (mode == EncryptedBackupPasswordMode)
{
    if (string.IsNullOrWhiteSpace(password)) throw new BackupPasswordRequiredException();
    keyMaterial = DeriveBackupKeyMaterial(password, salt, iterations);   // <-- honoured verbatim
}
```

```csharp
// AccountService.cs:2471-2477
using (var derive = new Rfc2898DeriveBytes(password ?? "", salt ?? new byte[0],
                                           Math.Max(10000, iterations), HashAlgorithmName.SHA256))
```

**Why this matters**

Three distinct issues:

- **Attacker-controlled work factor.** An attacker who obtains a backup file can rewrite the `iterations` field to `10000` (the floor) and the `salt` to a value from a precomputed table, then mount a dramatically cheaper offline dictionary attack against the *user's* password. The header is authenticated by the HMAC (lines 2359–2374), but the HMAC key is itself derived using those same parameters — the verification is circular and cannot detect tampering before the expensive derivation has already been performed with the attacker's parameters.
- **Unbounded upside.** There is a floor but no ceiling. `iterations = int.MaxValue` turns a restore into an effectively infinite CPU burn — a trivial denial of service against anyone who opens an untrusted backup.
- **Weak algorithm choice for 2026.** PBKDF2-HMAC-SHA256 at 150,000 iterations is GPU-friendly. Contemporary guidance for password-derived file encryption is a memory-hard KDF (Argon2id, or scrypt) precisely because PBKDF2's cost is dominated by cheap parallel hashing.

**Correction**

1. **Bound the parsed value on both sides** and treat out-of-range as corruption:
   ```csharp
   const int MinIterations = 100_000, MaxIterations = 5_000_000;
   if (iterations < MinIterations || iterations > MaxIterations)
       throw new InvalidDataException("Backup header declares an unsupported KDF work factor.");
   ```
2. **Version the format rather than trusting per-file parameters.** Encode a `kdfProfileId` byte; the reader maps it to a hard-coded parameter set the *reader* owns. New profiles are added with new IDs. This removes attacker influence over the work factor entirely.
3. **Migrate to Argon2id** (`Konscious.Security.Cryptography.Argon2` or `libsodium` bindings) with a profile of roughly `m=64MiB, t=3, p=4`, keeping PBKDF2 as a read-only legacy profile so existing `EGASBKP2` archives still restore.
4. **Consider AES-GCM** in place of AES-CBC + separate HMAC (lines 2283–2306). GCM is a single authenticated primitive and removes the encrypt-then-MAC composition risk. .NET's `AesGcm` requires .NET Core 3.0+ — another item gated on the framework migration (finding 30).

---

### 17. Key material is copied into unpinned, uncleared arrays, defeating the `Array.Clear` hygiene

**Severity:** S3
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 2260, 2285, 2359, 2381 (the copies); 2308–2311 and 2405–2408 (the `finally` blocks that only clear the original)

**Evidence**

```csharp
// AccountService.cs:2260
using (var hmac = new HMACSHA256(keyMaterial.Skip(BackupKeyBytes).Take(BackupHmacBytes).ToArray()))
// AccountService.cs:2285
aes.Key = keyMaterial.Take(BackupKeyBytes).ToArray();
...
// AccountService.cs:2308-2311  -- only the source array is scrubbed
finally
{
    if (keyMaterial != null) Array.Clear(keyMaterial, 0, keyMaterial.Length);
}
```

**Why this matters**

Each `.Skip().Take().ToArray()` allocates a fresh managed array containing the key bytes. The `finally` block clears `keyMaterial` but not these copies, which remain on the managed heap until collected — and, because the GC may compact and relocate them, potentially in several places at once. `aes.Key = ...` makes yet another internal copy inside the `Aes` implementation.

This is not the highest-impact finding, but it matters here because the same process handles a user-supplied password, writes crash dumps via the unhandled-exception handler (App.xaml.cs lines 47–52), and can be captured by Windows Error Reporting. A heap dump taken at the wrong moment contains the backup key in cleartext. The code clearly *intends* to prevent this — the intent is defeated by the LINQ convenience.

**Correction**

1. Slice without allocating copies that outlive their use, and clear what you must allocate:
   ```csharp
   var encKey  = new byte[BackupKeyBytes];
   var macKey  = new byte[BackupHmacBytes];
   try
   {
       Buffer.BlockCopy(keyMaterial, 0, encKey, 0, BackupKeyBytes);
       Buffer.BlockCopy(keyMaterial, BackupKeyBytes, macKey, 0, BackupHmacBytes);
       using (var hmac = new HMACSHA256(macKey))
       using (var aes = Aes.Create())
       {
           aes.Key = encKey;
           ...
       }
   }
   finally
   {
       Array.Clear(encKey, 0, encKey.Length);
       Array.Clear(macKey, 0, macKey.Length);
       Array.Clear(keyMaterial, 0, keyMaterial.Length);
   }
   ```
2. On .NET 5+, use `CryptographicOperations.ZeroMemory(span)` — it is not elided by the JIT, whereas `Array.Clear` on a soon-to-be-dead array theoretically can be. Another framework-migration dependency (finding 30).
3. Replace the hand-rolled `FixedTimeEquals` (lines 2548–2554) with `CryptographicOperations.FixedTimeEquals` once on .NET 5+; the hand-rolled version is correct but the framework version is the maintained one.
4. Disable Windows Error Reporting heap dumps for this process, or scrub via `SetErrorMode`, given that it handles user passwords.

---

### 18. Crash handler displays a full stack trace to the user and does not terminate

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs`
**Lines:** 47–52

**Evidence**

```csharp
// App.xaml.cs:47-52
AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
{
    string err = ev.ExceptionObject.ToString();
    Globals.WriteToLog("[FATAL_CRASH] " + err);
    MessageBox.Show("CRITICAL ERROR:\n\n" + err, "Epic Games Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
};
```

**Why this matters**

- **Information disclosure.** `ev.ExceptionObject.ToString()` includes full file paths, and for exceptions raised inside `AccountService` the message frequently embeds account IDs and session paths (e.g. line 1037's `"Recursive copy failed from " + source + " to " + dest`, line 1104's `"Move target already exists: " + target`). The log path applies `RedactSensitiveLogText` (Globals.cs lines 172–184); the **message box does not**. The most exposed surface is the least redacted one.
- **No `DispatcherUnhandledException` handler.** `AppDomain.UnhandledException` does not catch exceptions thrown on the WPF dispatcher thread — those are routed through `Application.DispatcherUnhandledException`, which is not subscribed anywhere. The most likely crash source (UI thread) is unhandled by this handler.
- **Execution continues.** `AppDomain.UnhandledException` is notification-only; after the handler returns, the CLR terminates the process anyway — but critically, **`AccountService.SessionOperationLock` is never released** and `_switch_rb` is left on disk with no recovery marker (finding 2). The handler is the single best place to write that marker, and it does not.

**Correction**

```csharp
AppDomain.CurrentDomain.UnhandledException += (s, ev) => HandleFatal(ev.ExceptionObject as Exception, "AppDomain");
DispatcherUnhandledException                += (s, ev) => { HandleFatal(ev.Exception, "Dispatcher"); ev.Handled = false; };
TaskScheduler.UnobservedTaskException        += (s, ev) => { Globals.WriteToLog("[UNOBSERVED_TASK]", ev.Exception); ev.SetObserved(); };

private static void HandleFatal(Exception ex, string source)
{
    try { Globals.WriteToLog("[FATAL_CRASH:" + source + "]", ex); } catch { }
    try { AccountService.MarkRecoveryRequired(ex); } catch { }      // <-- finding 2, item 3
    try
    {
        MessageBox.Show(
            "Epic Games Account Switcher encountered a fatal error and must close.\n\n" +
            "Reference: " + Globals.LogSessionId + "\n" +
            "Details have been written to the log file.",
            "Epic Games Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
    } catch { }
}
```

Show the user a correlation ID (`Globals.LogSessionId`, Globals.cs line 26 — already exists and is already stamped on every log line) and keep the stack trace in the redacted log. Add a "Copy diagnostic report" button that packages the log rather than dumping raw text into a dialog.

---

### 19. Application log is written to a world-readable `%PROGRAMDATA%` location

**Severity:** S3
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\Globals.cs`
**Lines:** 56–63 (`GetLogDirectory`), 172–184 (`RedactSensitiveLogText`)

**Evidence**

```csharp
// Globals.cs:56-63
public static string GetLogDirectory()
{
    var ld = string.IsNullOrWhiteSpace(LogFolderOverride)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic-Games_Account_Switcher")
        : Path.GetFullPath(LogFolderOverride);
    if (!Directory.Exists(ld)) Directory.CreateDirectory(ld);   // <-- inherits ProgramData ACL
    return ld;
}
```

**Why this matters**

`%PROGRAMDATA%` grants `CREATOR OWNER` full control and `Users` read + write-new-file by default. Every local user account can read this log. The redaction at lines 172–184 is a best-effort regex chain and demonstrably incomplete:

- `\b[a-fA-F0-9]{16,}\b` (line 179) catches 32-char Epic account IDs, but also mangles every GUID-like token, SHA hash, and hex offset in stack traces, degrading diagnostic value.
- The path collapse at line 180 (`([A-Za-z]:\\)(?:[^\\\r\n]+\\)+([^\\\r\n]+)` → `$1...\$2`) removes the *middle* of paths, which is where the username lives — good — but leaves the leaf filename, which for `EG\<accountid>\Saved` payloads can still be identifying.
- Nothing redacts display names (`DisplayName`, `-epicusername=` values parsed at AccountService.cs lines 1966–1976) or the `RememberMe` blob content.
- `WriteToLog` at Globals.cs line 72 also writes to `Console` when `VerboseMode` is set, applying redaction to the message but the console may itself be redirected.

Multi-user machines (family PCs, shared gaming rigs — a plausible primary use case for an account switcher) are exactly where this leaks.

**Correction**

1. Move the log to `%LOCALAPPDATA%\EpicAccountSwitcher\logs` — per-user, ACL'd by default, and consistent with where the account data already lives. If a machine-wide install log is genuinely needed for the elevated installer, keep only *that* in `%PROGRAMDATA%` and create the directory with an explicit admin-only DACL.
2. Replace regex-based redaction with **structured logging**: `Log.Switch(opId, accountId: SafeLogId(id), phase: "prepare", ...)` where the formatter decides per-field whether to hash, truncate, or emit. `SafeLogId` (lines 186–192) already exists and is the right idea — apply it at the call site for every identifier rather than post-processing free text.
3. Add a `LogLevel` and gate the verbose switch trace (finding 33) behind `Debug`, so the default log contains far less to leak.
4. Reduce retention. `MaxLogAgeDays = 30` (line 33) with `MaxTotalLogBytes = 40 MB` (line 31) is generous for a desktop utility; 7 days / 10 MB is ample for support purposes.

---

## SECTION C — ARCHITECTURAL DEBT

---

### 20. `AccountService` is a 2,772-line static god class with at least nine distinct responsibilities

**Severity:** S3 (root cause of findings 21, 22, 24, 32, and much of 33)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 19–2771 (the entire type)

**Evidence — responsibilities co-located in one static class**

| Responsibility | Lines | Representative members |
|---|---|---|
| Path resolution | 62–76 | `BaseDataPath`, `EpicSavedPath`, `ProgramDataManifestsPath` |
| Profile-image resolution | 80–144 | `NormalizeProfileImagePath`, `FindSkinByFileName` |
| Account persistence | 146–190 | `GetAccounts`, `SaveAccounts`, `NormalizeAccount` |
| Auth validation | 192–392 | `ValidateSession`, `SelectModernAuthBlob`, `CollectAuthFacts` |
| INI configuration | 394–596 | `CaptureIniValuesByPath`, `SetIniValue`, `ApplyIniValuesAtomic` |
| Process management | 756–990 | `EnsureEpicIsClosed`, `GetEpicProcesses`, `ProbeDirectoryWritable` |
| Filesystem transactions | 992–1279 | `SafeMovePathAsync`, `ReplacePathWithRollbackAsync` |
| Switch orchestration | 1281–1855 | `CaptureCurrentAccount`, `SwitchToAccount`, `RollbackSwitchAsync` |
| Backup cryptography | 2000–2554 | `EncryptBackupFile`, `DecryptBackupFile`, `BoundedStream` |
| Registry identity | 1784–1842, 2719–2762 | `RestoreRegistryIdentity`, `CaptureRegistryRecursive` |
| Presentation concerns | 2764–2770 | `GetDeterministicColor` — a UI palette function in the session engine |

Every member is `static`. Every dependency — filesystem, registry, process table, clock, Credential Manager — is reached through a concrete BCL type at the point of use.

**Why this matters**

1. **Untestable by construction.** The test suite is forced into reflection to reach anything:
   ```csharp
   // Epic-Games_Account_Switcher-Tests\AccountServiceSafetyTests.cs:135-140
   var method = typeof(AccountService).GetMethod("ValidateSession", BindingFlags.NonPublic | BindingFlags.Static);
   Assert.IsNotNull(method);
   return method.Invoke(null, new object[] { folder, expectedAccountId });
   ```
   Reflection-based tests silently rot on rename and cannot express the interesting scenarios — "what happens when the move succeeds but the registry write fails" is unreachable without seams.
2. **No seams means no fault injection.** Findings 1, 4, and 6 are all "what happens when an I/O primitive fails" questions. None can be tested today because there is no way to make `File.Copy` fail on demand.
3. **Change amplification.** Adding a launcher-state category (a new `SessionItemsToCapture` entry) requires coordinated edits at lines 74, 1050–1057, 1605–1613, and 1641–1647 — four places, no compiler assistance if one is missed.
4. **Cognitive load.** `SwitchToAccount` alone spans 180 lines (1540–1719) with four nested scopes and three distinct catch blocks. Reasoning about the rollback invariants requires holding the entire method in working memory.

**Correction — target decomposition**

```
Epic-Games_Account_Switcher-Globals/
├── Abstractions/
│   ├── IFileSystem.cs         // Exists, Copy, Move, Delete, Enumerate, Replace  (fault-injectable)
│   ├── IRegistryStore.cs      // Read, Write, Delete, EnumerateValues            (allow-list enforced)
│   ├── IProcessHost.cs        // Enumerate, Kill, Start, WaitForExit
│   ├── ICredentialVault.cs    // Capture, Restore, Delete
│   ├── IClock.cs              // UtcNow                                          (deterministic tests)
│   └── ILogSink.cs            // structured events, not strings
├── Paths/EpicPathProvider.cs           // was lines 62-76
├── Sessions/
│   ├── SessionRepository.cs            // was 146-190, 1040-1076
│   ├── AuthValidator.cs                // was 192-392
│   └── SessionTransaction.cs           // was 992-1279 + the journal from finding 2
├── Launcher/
│   ├── EpicProcessManager.cs           // was 756-990
│   └── LauncherLauncher.cs             // was 1387-1538
├── Config/IniConfigStore.cs            // was 394-596
├── Identity/RegistryIdentityService.cs // was 1784-1842, 2719-2762
├── Backup/
│   ├── BackupService.cs                // orchestration, was 2000-2197
│   └── BackupCodec.cs                  // crypto only, was 2234-2554
└── Orchestration/AccountSwitchService.cs  // was 1281-1719 — thin, ~150 lines
```

Migration sequence that keeps the app shippable throughout:
1. Introduce the interfaces with pass-through implementations wrapping the BCL. No behaviour change.
2. Convert `AccountService`'s statics to instance members on a class taking those interfaces, keeping a thin static facade so existing call sites compile untouched.
3. Extract one collaborator per pull request, writing its tests as it lands.
4. Delete the static facade once the client is constructing the service through a composition root in `App.OnStartup`.
5. Move `GetDeterministicColor` (lines 2764–2770) to the client project immediately — it is a one-line move with zero risk and removes a presentation concern from the engine.

---

### 21. 127 empty `catch { }` blocks — errors are structurally invisible

**Severity:** S3 (systemic; the enabling condition for findings 1, 4, and 8)
**Files:** all `.cs` under the four projects
**Measured:** `grep -rn "catch\s*{\s*}" --include=*.cs . | grep -v obj/ | wc -l` → **127**

**Highest-impact instances**

| File | Line | Consequence of swallowing |
|---|---|---|
| `Epic-Games_Account_Switcher-Globals\Globals.cs` | 363 | Per-file copy failure lost → finding 1 |
| `Epic-Games_Account_Switcher-Globals\Globals.cs` | 335, 338, 342 | Directory enumeration failure becomes "empty directory" |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 118 | Profile-image resolution failure → silent default avatar |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 617, 661 | Account-ID detection failure → "no account logged in" |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 808, 811, 818, 828 | Every process-kill failure lost inside `EnsureEpicIsClosed` |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 1000 | `SafeDeleteDirectoryAsync` failure lost |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 1088, 1091, 1094 | Pre-move cleanup failures lost, then surface as a confusing "target already exists" |
| `Epic-Games_Account_Switcher-Globals\AccountService.cs` | 1225, 1746, 2600, 2645, 2682 | Cleanup, registry capture, skin restore failures lost |
| `Epic-Games_Account_Switcher-Client\App.xaml.cs` | 113, 214, 217, 236, 261, 290, 293, 490, 620 | Installer step failures lost while running elevated |
| `Epic-Games_Account_Switcher-Globals\AppSettings.cs` | — | (uses logged catches; the good pattern) |

**Why this matters**

An empty catch is a decision that the caller will proceed on a false premise. In a program whose entire value proposition is *destructively replacing the user's authentication state and putting it back correctly*, that is the wrong default. The project's own documentation claims "robust rollback, retry loops... without silent crashes" — 127 empty catches is the precise definition of a silent failure.

Note the codebase already demonstrates the correct pattern in places (`Globals.WriteToLog("[CRED] Failed to capture Epic credential.", ex)` in `CredentialService.cs`). The problem is inconsistency, not ignorance.

**Correction**

1. **Ban the construct.** Add to `.editorconfig`:
   ```ini
   dotnet_diagnostic.CA1031.severity = warning   # do not catch general exception types
   dotnet_diagnostic.RCS1075.severity = warning  # avoid empty catch clause that catches System.Exception
   ```
   and set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in the `.csproj` files after the backlog is cleared.
2. **Triage each site into one of three categories**, and make the category explicit in code:
   - *Expected and ignorable* → `catch (FileNotFoundException) { /* intentional: probe */ }` — narrow type, comment stating why.
   - *Expected but interesting* → `catch (IOException ex) { Globals.WriteToLog("[IO] <specific context>", ex); }`.
   - *Unexpected* → do not catch. Let it propagate to the transaction boundary, which now has a real rollback contract (finding 4).
3. **Introduce a `SwallowedFailure` counter** on the operation context. `SwitchToAccount` logs it at the end; a switch that "succeeded" while swallowing 40 failures is a bug report waiting to happen, and now it is visible in the log.
4. Prioritize the ~15 sites in the table above; the remainder (mostly `try { p.Dispose(); } catch { }`) are genuinely benign and can be narrowed to `catch (InvalidOperationException)` mechanically.

---

### 22. Failure detail travels through a static side channel instead of the return type

**Severity:** S3
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 53 (`_lastSwitchFailureMessage`), 78 (`LastSwitchFailureMessage`), 666–669 (`SetLastSwitchFailure`), 1540 (`SwitchToAccount` returns `bool`)
**Consumer:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines 459–463

**Evidence**

```csharp
// AccountService.cs:53
private static string _lastSwitchFailureMessage;
// AccountService.cs:78
public static string LastSwitchFailureMessage { get { return _lastSwitchFailureMessage ?? ""; } }
// AccountService.cs:1540
public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null, CancellationToken ct = default)
```

```csharp
// MainViewModel.cs:459-463
else
{
    var detail = AccountService.LastSwitchFailureMessage;   // <-- read from a static, after the fact
    new CustomMessageBox("Switch Failed", "Failed to switch to " + account.Name + "." + ...).ShowDialog();
}
```

Related shapes in the same file:
- `CaptureCurrentAccount` returns `Tuple<bool, string>` (line 1281), consumed as `result.Item1` / `result.Item2` (MainViewModel.cs lines 532, 537, 539).
- `GetDetectedMetadata` returns `Tuple<string, string, string>` (line 1925), consumed as `det.Item1` / `Item2` / `Item3` (lines 1290, 1303, 1307; MainViewModel.cs lines 337, 356–361, 494, 504).
- `_lastDetected` is a static `Tuple<string,string,string>` cache (line 1886) guarded by `_metadataLock` (line 1885).

**Why this matters**

- **Not thread-safe by contract.** `_lastSwitchFailureMessage` is a plain static with no memory barrier. `SessionOperationLock` serializes switches, but nothing prevents the UI reading it while a *different* operation (backup, capture) writes it — or reading a stale value from a switch that completed minutes ago.
- **Temporal coupling.** The read at MainViewModel.cs:461 is only valid immediately after the `false` return. Any refactor that inserts an `await` between them silently produces a wrong error message.
- **`Item1`/`Item2`/`Item3` destroys readability.** `det.Item1` is the account ID, `Item2` the display name, `Item3` the email — knowable only by reading `GetDetectedMetadata`'s body. Line 1290's `if (string.IsNullOrWhiteSpace(det.Item1))` is unreviewable at a glance.
- **No room for structure.** A `bool` cannot express "failed but your session is intact" versus "failed and your session is gone" — exactly the distinction finding 4 requires.

**Correction**

1. Introduce result types and delete the statics:
   ```csharp
   public sealed class DetectedAccount
   {
       public string AccountId { get; }
       public string DisplayName { get; }
       public string Email { get; }
       public bool IsDetected => !string.IsNullOrWhiteSpace(AccountId);
   }

   public sealed class SwitchResult
   {
       public bool Succeeded { get; }
       public SwitchFailureStage Stage { get; }
       public bool LiveSessionIntact { get; }
       public RollbackOutcome Rollback { get; }
       public string OperationId { get; }
       public string UserMessage { get; }
       public string DiagnosticDetail { get; }
   }
   ```
2. Change signatures: `Task<SwitchResult> SwitchToAccountAsync(...)`, `Task<CaptureResult> CaptureCurrentAccountAsync(...)`, `Task<DetectedAccount> GetDetectedAccountAsync()`.
3. Delete `_lastSwitchFailureMessage`, `LastSwitchFailureMessage`, `SetLastSwitchFailure` (lines 53, 78, 666–669) and the `_lastDetected` static cache (lines 1885–1886) — move caching into an injected `IMemoryCache`-like collaborator with an explicit TTL so it is testable and can be invalidated.
4. `CaptureCurrentAccount`'s 11 parameters (line 1281) should become a `CaptureRequest` options object; the current signature has three consecutive nullable strings and two booleans, which is an argument-transposition bug waiting to happen.

---

### 23. Eight dead private members, including six unused cryptographic helpers

**Severity:** S3 (contradicts the documented "dead code purged" status)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`

**Evidence** — each verified as having exactly one occurrence in the codebase (its own declaration):

| Member | Lines | Occurrences |
|---|---|---|
| `IsSessionValid` | 192–195 | 1 (declaration only) |
| `SafeReplaceDirectoryAsync` | 1004–1014 | 1 |
| `EncryptBackupBytes` | 2411–2427 | 1 |
| `DecryptBackupBytes` | 2429–2445 | 1 |
| `BuildEncryptedBackupBody` | 2479–2494 | 1 |
| `ComputeBackupHmac` | 2503–2509 | 1 |
| `EncryptAesCbc` | 2511–2524 | 1 |
| `DecryptAesCbc` | 2526–2539 | 1 |

Also redundant: `CredentialService.CredentialPrefixes` (`CredentialService.cs`) — every prefix it lists (`com.epicgames.`, `epicgameslauncher`) is already matched as a substring by `CredentialNeedles`, so the second `Any(...)` clause in `IsEpicCredential` can never change the result.

**Why this matters**

- **`EncryptBackupBytes` / `DecryptBackupBytes` (lines 2411–2445) are a liability, not just clutter.** They write plaintext to `Path.GetTempFileName()` before encrypting and after decrypting:
  ```csharp
  // AccountService.cs:2414-2420
  var tmpPlain = Path.GetTempFileName();
  ...
  File.WriteAllBytes(tmpPlain, plainBytes);
  ```
  `GetTempFileName` creates a file with default `%TEMP%` permissions; the plaintext account store lands on disk unprotected before the `finally` deletes it (a delete that is not a secure erase). If anyone ever wires these up, they reintroduce the exposure that the streaming path was specifically built to avoid.
- **They encode a superseded format.** `BuildEncryptedBackupBody` writes the ciphertext length-prefixed (line 2490), while the live `EncryptBackupFile` writes a bare `cipherLen` int followed by a stream (line 2275). Two incompatible layouts for the same magic number sit in one file. A maintainer reading `BuildEncryptedBackupBody` to understand the format will reach the wrong conclusion.
- **`project_structure.txt` §2 asserts "Dead code (including legacy CV-based AutoLogin) has been purged."** The document is now actively misleading, which is worse than having no document.

**Correction**

1. Delete all eight members and the redundant `CredentialPrefixes` clause. Git history is the archive; commented-out or unreferenced code in the working tree is a maintenance tax.
2. If the byte-array crypto helpers are wanted for testing, move them into the test project as fixtures — but prefer testing `EncryptBackupFile` / `DecryptBackupFile` directly against a `MemoryStream`-backed `IFileSystem` (finding 20), which tests the real path.
3. Turn on dead-code detection so this cannot silently accumulate:
   ```xml
   <PropertyGroup>
     <EnableNETAnalyzers>true</EnableNETAnalyzers>
     <AnalysisMode>Recommended</AnalysisMode>
   </PropertyGroup>
   ```
   plus `dotnet_diagnostic.IDE0051.severity = warning` (unused private member) in `.editorconfig`.
4. Update `project_structure.txt` §2 in the same commit, and add a checklist item to the release process: *"the status section is a claim about the current tree; verify it before publishing."*

---

### 24. `SafeMovePathAsync` and `SafeMovePathBlocking` are ~60 lines duplicated verbatim

**Severity:** S3
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1078–1133 (async) and 1135–1197 (blocking)

**Evidence** — the bodies are character-identical from the target-cleanup block through the cross-volume move:

```csharp
// AccountService.cs:1088-1096   ||   AccountService.cs:1146-1154
if (File.Exists(target)) try { File.Delete(target); } catch { }
if (Directory.Exists(target))
{
    try { Globals.ForceDeleteDirectory(target); } catch { }
    if (Directory.Exists(target))
    {
        try { Directory.Move(target, target + "_old_" + Guid.NewGuid().ToString("N").Substring(0, 6)); } catch { }
    }
}
```

```csharp
// AccountService.cs:1098-1100   ||   AccountService.cs:1156-1158
var srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
var dstRoot = Path.GetPathRoot(Path.GetFullPath(target));
var isCrossVolume = !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
```

The only differences: the async version wraps the body in `Globals.RetryActionAsync(..., 10, 500, true, ct)` (line 1132) and honours cancellation (line 1082); the blocking version hand-rolls a `for (var i = 0; i < 10; i++)` with `Thread.Sleep(500 * (i + 1))` (lines 1137, 1194).

**Why this matters**

This is the single most safety-critical primitive in the application — it is what makes a switch atomic — and it exists in two copies that can drift. `SafeMovePathBlocking` has exactly one caller (`RestoreAccounts`, lines 2142 and 2175), reached from a `Task.Run` in `MainViewModel.PerformRestore` (MainViewModel.cs line 686) — i.e. it is *already* on a background thread and had no need to be a separate synchronous implementation. Any fix to the move semantics (such as finding 6's degraded-replace path) must now be applied twice, and a reviewer must diff 60 lines to confirm it was.

**Correction**

1. Delete `SafeMovePathBlocking` (lines 1135–1197). Change `RestoreAccounts` to `async Task RestoreAccountsAsync` and `await SafeMovePathAsync(...)` at the two call sites (2142, 2175). `MainViewModel.PerformRestore` already `await`s a `Task.Run` — it becomes a direct `await`, which is simpler and preserves cancellation.
2. If a synchronous entry point must be retained for a non-async caller, express it as a one-line shim rather than a copy — and route it through the same `RetryActionAsync` so the retry semantics are identical by construction.
3. Extract the shared body into a private `MovePathCore(IFileSystem fs, string source, string target)` once `IFileSystem` exists (finding 20). The retry/cancellation policy then decorates the core rather than being interleaved with it.
4. Add the missing test: `SafeMovePathAllowsReadOnlySourceFiles` (`AccountServiceSafetyTests.cs` lines 108–121) covers the async path only. Whatever survives must be covered for cross-volume, target-exists, and target-locked cases.

---

### 25. `IsPathUnder` and `ExplodeRegistryKey` are each implemented twice across assemblies

**Severity:** S3
**Files / lines:**
- `IsPathUnder`: `Epic-Games_Account_Switcher-Globals\AccountService.cs` lines **956–966** and `Epic-Games_Account_Switcher-Client\App.xaml.cs` lines **153–163** — byte-identical
- `ExplodeRegistryKey`: `Epic-Games_Account_Switcher-Globals\Registry.cs` lines **22–39** (`ExplodeRegistryKey`, supports HKCR/HKCU/HKLM/HKCC/HKPD) and `Epic-Games_Account_Switcher-Globals\AccountService.cs` lines **2749–2762** (`ExplodeRegistryKeyForCapture`, supports HKCU/HKLM only)

**Evidence**

```csharp
// AccountService.cs:2749-2762  -- a narrower reimplementation of Registry.cs:22-39
private static Tuple<RegistryKey, string, string> ExplodeRegistryKeyForCapture(string path)
{
    if (string.IsNullOrEmpty(path) || path.Length < 5) return new Tuple<RegistryKey, string, string>(null, null, null);
    var rKey = path.StartsWith("HKCU") ? Microsoft.Win32.Registry.CurrentUser
             : (path.StartsWith("HKLM") ? Microsoft.Win32.Registry.LocalMachine : null);
    ...
}
```

**Why this matters**

- `ExplodeRegistryKeyForCapture` exists only because `ExplodeRegistryKey` is `private` (Registry.cs line 22) despite both being members of the same `partial class Globals`. A visibility change would have made the duplicate unnecessary; instead a second, subtly different parser now exists. Both split on `':'` with `parts.Length < 2`, so a registry value name containing a colon breaks both — and would need fixing twice.
- `IsPathUnder` is a **security-relevant containment check**. `App.xaml.cs`'s copy gates `IsOwnedProcessPath` (lines 165–169), which decides which processes the elevated installer terminates. `AccountService`'s copy gates which processes count as "Epic" (line 948). Divergence between two copies of a containment predicate is how privilege boundaries get holes.

**Correction**

1. Promote a single `PathUtil.IsPathUnder(path, root)` into the Globals assembly as `public static`, delete both copies, and reference it from the client. Pair it with the containment check from `GetSafeExtractionPath` (AccountService.cs lines 2694–2706) so there is exactly one implementation of "is A inside B" in the product.
2. Change `Globals.ExplodeRegistryKey` from `private` to `internal` (Registry.cs line 22) and delete `ExplodeRegistryKeyForCapture` (AccountService.cs lines 2749–2762).
3. Replace the `':'`-split encoding scheme entirely. `HKCU\Path\To\Key:ValueName` is ambiguous and stringly-typed; introduce `readonly struct RegistryValueRef { RegistryHive Hive; string SubKey; string ValueName; }` with an explicit parser and `ToString()`, so the encoding exists in one place with round-trip tests.
4. Add an architecture test (NetArchTest or a simple reflection assertion) that fails the build when two methods with identical signatures and bodies exist in different namespaces.

---

### 26. The installer and uninstaller live inside the WPF `Application` class

**Severity:** S3 (blocks signing, servicing, and clean upgrades; root cause of findings 12, 13, 14)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\App.xaml.cs`
**Lines:** 385–601 (`PerformSmartInstall`), 603–734 (`PerformThoroughUninstall`) — **350 of the file's 736 lines**

**Evidence — responsibilities inside `App`**

| Concern | Lines |
|---|---|
| UAC elevation relaunch | 395–423, 627–632 |
| Installer mutex + MSI-busy detection | 100–115, 427–433, 639–649 |
| Process termination by name and path | 220–298 |
| Registry uninstall-entry authoring | 542–554 |
| Registry key-tree wiping across 4 hive/view combinations | 479–491, 666–679 |
| Legacy data migration | 332–377, 460–466 |
| Backup/restore around upgrade | 467–477, 520–540 |
| Shortcut creation and removal | 557–575, 682–685 |
| Payload staging and self-copy | 510–518, 614–623 |
| Self-deletion via batch trampoline | 184–201, 704–722 |
| Progress UI marshalling | 443–447, 589–596, 652–656 |

**Why this matters**

1. **No transactional rollback.** A failure at line 517 (payload copy) leaves Program Files half-populated, the uninstall registry entry absent (written later at line 543), and shortcuts unwritten (line 561). The `catch` at line 586 shows a dialog and exits — it does not undo. MSI gives this for free.
2. **Upgrade is destructive by design.** The flow *deletes* the install (lines 495–507), backs the data up to `~\Downloads\Epic_Setup_Backup_*.egbackup` (line 471), then restores (line 525). If the restore fails, the user is told to find a file in Downloads (line 536). A package manager performs a versioned in-place upgrade with automatic rollback; this reimplements it, worse.
3. **Servicing is impossible.** No upgrade codes, no product codes, no per-user vs per-machine distinction, no repair, no MSI logging, no Group Policy deployment, no winget manifest. Enterprise or shared-machine deployment is off the table.
4. **The heuristic profile blocks distribution.** See finding 14.
5. **The client project cannot be reasoned about.** `App.OnStartup` (lines 35–82) branches into three completely different programs based on argv. The WPF app, the installer, and the uninstaller share a process, a mutex namespace, a logging configuration, and an unhandled-exception handler, for no benefit.

**Correction**

1. **Adopt WiX v4** (or MSIX if the Store/AppInstaller channel is acceptable). Move to the installer: directory creation, file placement, shortcuts, uninstall registry entries, upgrade logic (`MajorUpgrade` element), and removal. This deletes lines 184–201, 385–601 and 603–734 outright, and makes `SingleFilePacker` redundant alongside finding 30.
2. **Keep exactly one custom action**: data migration/preservation, implemented as a small separate console executable invoked by the installer with a documented exit-code contract — not as a mode of the GUI app.
3. **Authenticode-sign** the MSI and the application binary. This is the single highest-leverage change for reducing support burden from AV false positives.
4. **Delete the `--installing` / `--uninstall` / `--preserve` / `--upgrade-backup` / `--extract-dir` argument surface** from `App.OnStartup` (lines 37–70). The application should have one job.
5. Interim, if the migration cannot land immediately: extract lines 385–734 into a separate `Installer` class library with `IInstallerHost` for UI callbacks, so at minimum the logic becomes testable and `App.xaml.cs` returns to ~200 lines.

---

### 27. MVVM boundary violations: the view-model drives the view directly

**Severity:** S3
**Files / lines:**
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines **166–172** (VM reaches into the concrete `MainWindow`), **452–457** (VM minimizes and closes the window), and **387, 447, 462, 472, 497, 521, 539, 571, 663, 707** (VM constructs `CustomMessageBox` directly)
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\MainWindow.xaml.cs` line **70** (view polls a `SemaphoreSlim` from the engine)

**Evidence**

```csharp
// MainViewModel.cs:166-172
Application.Current?.Dispatcher?.Invoke(() =>
{
    if (Application.Current.MainWindow is MainWindow win)   // <-- VM depends on a concrete Window type
    {
        win.UpdateTrayIconVisibility();
    }
});
```

```csharp
// MainViewModel.cs:452-457
Application.Current.Dispatcher.Invoke(new Action(() => {
    var mainWindow = Application.Current.MainWindow;
    if (mainWindow == null) return;
    mainWindow.WindowState = WindowState.Minimized;
    if (closeOnceLaunched) mainWindow.Close();               // <-- VM owns window lifecycle
}));
```

```csharp
// MainWindow.xaml.cs:70
if (_isDragInProgress || _isUnsavedLoginScanRunning || ViewModel == null || ViewModel.IsBusy
    || AccountService.SessionOperationLock.CurrentCount == 0) return;   // <-- view reads the engine's semaphore
```

**Why this matters**

- **The view-model is untestable.** Every branch through `SwitchToAccount` (MainViewModel.cs lines 423–478) either shows a modal or manipulates `Application.Current.MainWindow`. A headless test cannot execute it, so the most user-visible logic in the app — the switch flow's error handling — has zero coverage.
- **`CustomMessageBox.ShowDialog()` blocks the calling thread.** At line 539 the VM already recognises this and wraps the call in `Dispatcher.Invoke`; at lines 387, 447, 462, 472 it does not. The inconsistency means some error paths deadlock or throw depending on which thread the continuation resumed on — a latent, timing-dependent bug.
- **Line 70 leaks a synchronization primitive across three layers.** `SessionOperationLock.CurrentCount` is a snapshot with no memory guarantees and is inherently racy — by the time the timer proceeds, the count may have changed. The view should never know that a semaphore exists.

**Correction**

1. Introduce two services, injected into the view-model:
   ```csharp
   public interface IDialogService
   {
       Task<bool?> ShowMessageAsync(string title, string body, string confirmText = null, string cancelText = null,
                                    MessageBoxVisualStyle style = MessageBoxVisualStyle.Info);
       Task<CaptureAccountResult> ShowCaptureAsync(AccountMetadata seed, bool isNew);
       Task<string> PromptForPasswordAsync(string title, string body, bool confirm, int minLength);
       Task<string> PickSaveFileAsync(string filter, string suggestedName);
       Task<string> PickOpenFileAsync(string filter);
   }

   public interface IShellService
   {
       void MinimizeMainWindow();
       void CloseMainWindow();
       void SetTrayIconVisible(bool visible);
   }
   ```
   The WPF implementations live in the client and are the only code that touches `Application.Current` or constructs windows.
2. Replace line 70's semaphore poll with a view-model property: `public bool IsSessionOperationInProgress { get; }` maintained by the engine's operation events. The timer binds to that.
3. Move the drag-reorder logic out of `MainWindow.xaml.cs` (613 lines) into an attached behaviour or a small `ReorderController`, leaving code-behind for genuinely visual concerns (tray icon, monitor bounds, `Ctrl+Wheel` zoom).
4. With these in place, write view-model tests for: switch success, switch failure with intact session, switch failure with lost session, verification timeout, and cancellation. Those five paths are the app's entire risk surface from the user's perspective.

---

### 28. Version string is hard-coded in source and duplicated in the project file

**Severity:** S4
**Files / lines:**
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\Globals.cs` line **25** — `public static readonly string Version = "2026.04.01.1310";`
- `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj` line 9 — `<Version>2026.04.01.1310</Version>`

**Evidence of drift risk**

`Globals.Version` is stamped into every log line (Globals.cs line 69) and written to the uninstall registry entry as `DisplayVersion` (App.xaml.cs line 549). The `.csproj` value drives the assembly's `FileVersion`/`InformationalVersion`. Nothing enforces that they agree, and nothing enforces that either matches the git tag. `project_structure.txt` §2 additionally carries a hand-maintained status date (`2026-04-27`) that is now three months stale.

**Correction**

1. Single source of truth in `Directory.Build.props` at the repository root:
   ```xml
   <Project>
     <PropertyGroup>
       <VersionPrefix>2026.8.0</VersionPrefix>
       <Company>Epic-Games_Account_Switcher Team</Company>
       <Product>Epic-Games_Account_Switcher</Product>
     </PropertyGroup>
   </Project>
   ```
2. Read it at runtime instead of duplicating it:
   ```csharp
   public static string Version =>
       typeof(Globals).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
       ?? typeof(Globals).Assembly.GetName().Version.ToString();
   ```
   Delete Globals.cs line 25.
3. Derive the version from the git tag in CI (Nerdbank.GitVersioning or MinVer) so a release cannot ship with a stale number.
4. Replace the hand-maintained status block in `project_structure.txt` §2 with generated content, or remove the date and keep only claims that are verified by tests.

---

## SECTION D — BUILD, TEST & TOOLING

---

### 29. `Build.cmd` never runs the tests, and the test project is not in the build graph

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Build.cmd`
**Lines:** 1–72 (whole file); the required-directory check at **17–24** omits the test project

**Evidence**

```bat
:: Build.cmd:17
set "requiredDirs=Epic-Games_Account_Switcher-Client Epic-Games_Account_Switcher-Globals SingleFilePacker"
```

The four build steps are: clean (lines 26–33), `dotnet publish` the client (line 37), `dotnet run --project SingleFilePacker -- --pack` (line 47), `dotnet build SingleFilePacker` (line 52). **There is no `dotnet test` anywhere in the file**, and `Epic-Games_Account_Switcher-Tests` is absent from `requiredDirs`.

There is also no `.github` directory — no CI of any kind:
```
$ ls -a .github  →  no .github
```

**Why this matters**

The six safety tests in `AccountServiceSafetyTests.cs` cover the highest-risk behaviours in the product — backup round-trip, wrong-password rejection, ZIP traversal rejection, legacy restore, auth-blob selection, read-only file moves. **None of them run during a release build.** The release artifact at `compiled\Epic-Games_Account_Switcher.exe` can be produced from a tree where every one of them fails. Combined with the absence of CI, there is no automated gate between a broken commit and a published installer.

**Correction**

1. Insert a mandatory test stage before packaging:
   ```bat
   set "currentStage=Running Tests"
   echo.
   echo [2/5] !currentStage!...
   dotnet test "Epic-Games_Account_Switcher-Tests\Epic-Games_Account_Switcher-Tests.csproj" -c !BUILD_CONFIG! --logger "trx;LogFileName=test-results.trx" --results-directory "compiled\test-results"
   if %ERRORLEVEL% NEQ 0 goto :error
   ```
   and add the test directory to `requiredDirs` at line 17.
2. Add `.github/workflows/ci.yml` running on `windows-latest`: restore → build → test → upload the `.trx` and the packed artifact. Make it a required status check on the default branch.
3. Add a release workflow that additionally signs the binary and generates SHA-256 checksums (the release commit message mentions "sync documentation and checksums" — automate it rather than doing it by hand).
4. Fail the build on compiler warnings once the backlog from findings 21 and 23 is cleared.

---

### 30. `net48` + `LangVersion 7.3` is the root cause of much of the code's shape

**Severity:** S3
**Files:** all four `.csproj` files
- `Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj` lines 4, 13
- `Epic-Games_Account_Switcher-Globals\Epic-Games_Account_Switcher-Globals.csproj` lines 4, 14
- `Epic-Games_Account_Switcher-Tests\Epic-Games_Account_Switcher-Tests.csproj` lines 4, 6
- `SingleFilePacker\SingleFilePacker.csproj` lines 4, 6

**Evidence of the constraint's cost**

| Constraint | Consequence in this codebase |
|---|---|
| No `record` / tuples-with-names (C# 7.3 has value tuples but the code uses `System.Tuple`) | `Tuple<bool,string>` and `.Item1/.Item2/.Item3` throughout (finding 22) |
| No nullable reference types | ~40 defensive `if (x == null)` guards and `?? new List<>()` fallbacks that the compiler could enforce |
| No `System.Text.Json` | Newtonsoft dependency in every project |
| No `File.Move(src, dst, overwrite)` | Hand-rolled delete-then-move with a race window (AccountService.cs lines 1088, 1118) |
| No `Process.Kill(entireProcessTree: true)` | Shelling out to `taskkill /T` (finding 13) |
| No `AesGcm` | AES-CBC + separate HMAC composition (finding 16) |
| No `CryptographicOperations.ZeroMemory` / `FixedTimeEquals` | Hand-rolled equivalents (finding 17) |
| No `Path.GetRelativePath` | `Uri`-based relative path computation (finding 7) |
| No single-file publish | **The entire `SingleFilePacker` project exists solely to work around this** |
| No `ArrayPool`/`Span` | 81,920-byte arrays allocated per crypto call (lines 2293, 2363, 2389) |

`Build.cmd` line 12 already instructs the user to install the **.NET 8 SDK**, and line 37 passes `-r win-x64 --self-contained false` — a runtime identifier that is meaningless for a `net48` target. The build script has already half-migrated; the projects have not.

**Correction**

1. Retarget to `net9.0-windows` with `<UseWPF>true</UseWPF>`. WPF is fully supported on modern .NET and the app has no `net48`-only dependencies — `System.Management` and `System.ServiceProcess` (Globals `.csproj` lines 20–21) are referenced but unused, and `System.Drawing` is available via `System.Drawing.Common`.
2. Enable in `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
3. Publish with `<PublishSingleFile>true</PublishSingleFile>` and **delete the entire `SingleFilePacker` project** (97 lines of `Program.cs` plus the extraction, purge, and staging logic it forces into `App.xaml.cs` — findings 12 and 14 largely evaporate with it).
4. Sequence the migration: Globals first (no UI dependencies), then Tests, then Client, then delete SingleFilePacker. Each step is independently shippable.
5. Adopt the modern primitives in the same pass where they eliminate a finding: `Path.GetRelativePath` (finding 7), `Process.Kill(true)` (finding 13), `CryptographicOperations` (finding 17), `AesGcm` (finding 16), `File.Move(overwrite: true)` (finding 6).

---

### 31. Test suite: 6 tests, 182 lines, reflection-based, zero coverage of the switch pipeline

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Tests\AccountServiceSafetyTests.cs`
**Lines:** 1–182 (the entire suite)

**Evidence**

The six tests: `PasswordEncryptedBackupRoundTripsAndRejectsWrongPassword` (lines 42–66), `LegacyPlainZipRestoreStillWorks` (68–78), `RestoreRejectsZipTraversalEntries` (80–95), `ModernAuthBlobValidatesByExpectedAccountId` (97–106), `SafeMovePathAllowsReadOnlySourceFiles` (108–121). Two of them reach private statics by reflection:

```csharp
// AccountServiceSafetyTests.cs:135-140
var method = typeof(AccountService).GetMethod("ValidateSession", BindingFlags.NonPublic | BindingFlags.Static);
Assert.IsNotNull(method);
return method.Invoke(null, new object[] { folder, expectedAccountId });
// AccountServiceSafetyTests.cs:142-148  -- same shape for SafeMovePathAsync
```

**What is untested**

| Behaviour | Lines | Risk |
|---|---|---|
| `SwitchToAccount` end-to-end | AccountService.cs 1540–1719 | The core feature |
| Rollback on prepare failure | 1694–1704 | Data loss (finding 4) |
| Rollback on cancellation | 1683–1693 | Data loss |
| `RestoreRegistryIdentity` + its rollback | 1784–1815 | Identity corruption |
| `EnsureEpicIsClosed` timeout path | 767–851 | Switch blocked |
| `CaptureCurrentAccount` overwrite confirmation | 1281–1338 | Silent overwrite |
| INI patch/verify round-trip | 425–596 | Zero-UI failure |
| `CleanOrphanedTempFolders` predicate | 1199–1233 | Deletes real data (finding 2) |
| `CopyFilesRecursive` failure accounting | Globals.cs 313–374 | Silent corruption (finding 1) |
| Concurrent `SaveAccounts` during a switch | — | Lost updates (finding 3) |
| Shell folder probing | Globals.cs 411–520 | Missing shortcuts |
| Any view-model logic | MainViewModel.cs (854 lines) | All error paths |

**Correction**

1. **Create the seams first** (finding 20). Without `IFileSystem`/`IRegistryStore`/`IProcessHost`, most of the above is physically untestable — that is the actual blocker, not test-writing effort.
2. **Delete the reflection helpers** (lines 135–148) once `ValidateSession` lives on an injectable `AuthValidator` with `public` surface. Reflection-based tests do not survive refactoring, which is exactly when you need them most.
3. **Prioritise fault-injection tests** over happy paths. The highest-value first suite:
   - `Switch_WhenPreparedCopyFails_RollsBackAndLeavesLiveSessionIntact`
   - `Switch_WhenRegistryWriteFails_RestoresRegistrySnapshot`
   - `Switch_WhenCancelledAfterLiveMove_RestoresRollback`
   - `Switch_WhenProcessCleanupTimesOut_MakesNoDestructiveChange`
   - `Recovery_WhenTransactionJournalIncomplete_RestoresOnStartup` (finding 2)
   - `SaveAccounts_ConcurrentWithSwitch_DoesNotLoseEdits` (finding 3)
   - `CopyFilesRecursive_WhenSomeFilesFail_ReportsIncomplete` (finding 1)
4. **Split the file.** One test class per collaborator, mirroring the target structure from finding 20.
5. **Add a coverage gate** in CI (Coverlet + ReportGenerator), starting at whatever the baseline is and ratcheting up — never down.

---

## SECTION E — PERFORMANCE & OPERABILITY

---

### 32. `PathStateSummary` performs up to 5,000 `FileInfo` stats per call, 14 times per switch, unconditionally

**Severity:** S2 (multiple seconds added to the critical path, on the slowest possible medium)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 685–719 (implementation); call sites at **1561, 1597 (×2), 1623, 1627 (×2), 1631 (×2), 1635 (×2), 1648, 1651, 1653, 1655, 1657, 1697 (×2), 1702** — 14 distinct statements, ~20 invocations per switch

**Evidence**

```csharp
// AccountService.cs:697-713
long bytes = 0;
var count = 0;
var capped = false;
DateTime newest = Directory.GetLastWriteTimeUtc(path);
foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
{
    var fi = new FileInfo(file);     // <-- a full stat syscall per file
    bytes += fi.Length;
    count++;
    if (fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
    if (count >= 5000) { capped = true; break; }
}
```

```csharp
// AccountService.cs:1597  -- two full directory walks, purely to build a log string
LogSwitch(operationId, "Preparing target session. RollbackState=" + PathStateSummary(rbB) + "; TempState=" + PathStateSummary(tmpB));
```

**Why this matters**

The paths being walked are Epic launcher state directories. `WebCaches` and `SocialCache` routinely contain thousands of small files; `Manifests` scales with the size of the user's library. A user with a large library can easily hit the 5,000 cap on several of these calls.

Cost model: ~20 invocations × up to 5,000 `FileInfo` constructions = **up to 100,000 stat syscalls per switch**, none of which affect the outcome. On an HDD, or on a directory not in the OS file cache (which is likely — these files were just moved), that is measured in seconds. It runs *inside* the destructive window between lines 1640 and 1675, extending exactly the interval during which a crash causes data loss (finding 2).

`WriteToLog` compounds it: each call opens a named mutex, acquires it with a 2-second timeout, appends to the file, and then runs `CleanupLogFiles` — which enumerates the log directory and constructs a `FileInfo` per log file (Globals.cs lines 128–165). That is a directory enumeration *per log line*, ~25 log lines per switch.

**Correction**

1. **Gate diagnostics behind a level.** Introduce `LogLevel` and wrap every `PathStateSummary` call:
   ```csharp
   if (Globals.IsEnabled(LogLevel.Debug))
       LogSwitch(opId, "Preparing target session. RollbackState=" + PathStateSummary(rbB));
   ```
   The default build should not compute them at all — `PathStateSummary(...)` is evaluated *before* `LogSwitch` is entered, so the guard must wrap the call site, not live inside the logger.
2. **Use a cheap summary by default.** File count and newest-mtime can come from `Directory.EnumerateFiles(...).Count()` with a much lower cap (100), or be dropped entirely in favour of "exists / not exists".
3. **Avoid `FileInfo` per entry.** On .NET Core+, `new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)` returns `FileInfo` objects populated from the directory enumeration itself, with no additional stat call per file. Another framework-migration win (finding 30).
4. **Amortise the log cleanup.** Call `CleanupLogFiles` (Globals.cs line 108) at most once per process start and once per rotation, not on every append.
5. **Move logging off the critical path.** Buffer log lines in a `BlockingCollection` drained by a single background writer; the switch thread then pays only an enqueue.

---

### 33. Process termination spawns `taskkill` and `powershell` on every loop iteration for up to 60 seconds

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 767–851 (`EnsureEpicIsClosed`), specifically **815–828** (per-iteration external processes) and **920–954** (`GetEpicProcesses`)

**Evidence**

```csharp
// AccountService.cs:815-828  -- inside the while loop at line 777
var targetImages = new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe", "EpicOnlineServices.exe",
                           "EpicOnlineServicesHost.exe", "EpicOnlineServicesUserHelper.exe", "EOSUserHelper.exe",
                           "EOSOverlay.exe", "CrashReportClient.exe", "UnrealCEFSubProcess.exe" };
foreach (var img in targetImages)
{
    try { Process.Start(new ProcessStartInfo(sysTaskkill, "/F /IM " + img) { ... })?.WaitForExit(500); } catch { }
}

try
{
    if (File.Exists(sysPowershell))
    {
        Process.Start(new ProcessStartInfo(sysPowershell, "-NoProfile -ExecutionPolicy Bypass -Command \"Stop-Process -Name ... -Force -ErrorAction SilentlyContinue\"") { ... })?.WaitForExit(1000);
    }
}
catch { }
```

**Why this matters**

Per loop iteration: **9 `taskkill.exe` process creations** (each `WaitForExit(500)`) **+ 1 `powershell.exe` process creation** (`WaitForExit(1000)`). PowerShell cold-start alone is 300–800 ms. Worst case per iteration is ~5.5 seconds of process-creation overhead; the loop runs until 60 seconds elapse.

Separately, `GetEpicProcesses` (lines 920–954) calls `Process.GetProcesses()` — enumerating **every process on the machine** — and then accesses `p.MainModule.FileName` for each (line 939). `MainModule` on a process owned by another user or a protected process throws `Win32Exception`, caught and discarded at line 939; on a busy machine this is several hundred failed P/Invokes per call, and this method is called at lines 780, 834, and 841 — i.e. twice per outer iteration plus once in the inner poll loop.

Worse: `Process.Start` returns a `Process` object that is **never disposed** at lines 809, 818, and 825, leaking a Win32 handle per invocation. Over a 60-second timeout that is hundreds of leaked handles.

**Correction**

1. **Escalate, don't repeat.** Restructure as a ladder:
   - Iteration 1: graceful — `CloseMainWindow()` on the launcher, wait 3 s.
   - Iteration 2: managed — `Process.Kill(entireProcessTree: true)` (.NET 5+; finding 30) on the identified PIDs only.
   - Iteration 3+: `taskkill /F /T /PID <pid>` for stragglers, by PID, never by image name.
   - Remove the PowerShell invocation entirely — it does nothing `Process.Kill` cannot.
2. **Dispose everything.** Wrap every `Process.Start` result in `using`, and dispose the `Process` objects from `GetProcesses()` that are not retained (line 949 does this for the non-matching case; lines 942–946 do not for the matching case until much later).
3. **Cache the process snapshot.** Enumerate once per iteration and pass the list to both the writability probe and the kill logic, instead of re-enumerating at 780, 834, and 841.
4. **Prefer `Process.GetProcessesByName`** over `GetProcesses()` for the name-matched set, and only fall back to a full enumeration + `MainModule` walk when path-based identification is genuinely required.
5. **Reduce the default timeout** from 60 s to ~15 s with an explicit "Epic is still running — force close?" prompt. A minute of silent grinding is a worse user experience than an honest question.

---

### 34. Image-name-based termination kills unrelated Unreal Engine applications

**Severity:** S2
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 815 (`targetImages`), 825 (PowerShell `Stop-Process -Name`), 922–930 (`launcherProcessNames`), 942–946 (unconditional match)
**Contradicts:** `project_structure.txt` §4 principle 3 — *"Epic processes must be identified by installation path"*

**Evidence**

```csharp
// AccountService.cs:815
var targetImages = new[] { ..., "CrashReportClient.exe", "UnrealCEFSubProcess.exe" };
```

```csharp
// AccountService.cs:942-946  -- name match short-circuits the path check entirely
if (launcherProcessNames.Contains(n))
{
    active.Add(p);
    continue;                    // <-- never reaches the path check at line 948
}
```

**Why this matters**

`CrashReportClient.exe` and `UnrealCEFSubProcess.exe` are **generic Unreal Engine binaries**. They ship inside every UE game and inside the Unreal Editor itself. `taskkill /F /IM UnrealCEFSubProcess.exe` at line 818 terminates them across the entire machine, irrespective of which application owns them.

Concrete failure: a developer with the Unreal Editor open, or a player with any UE-based game running, initiates an account switch. Their editor's CEF subprocess and crash reporter are force-killed. `/F` means no save prompt. This is unannounced data loss in a *third-party* application.

The PowerShell command at line 825 has the same defect via `Stop-Process -Name`, and the path-based filter that lines 853–918 carefully construct (`GetEpicInstallPaths`) is bypassed for the entire `launcherProcessNames` set by the `continue` at line 945.

**Correction**

1. **Remove `CrashReportClient.exe` and `UnrealCEFSubProcess.exe` from `targetImages`** (line 815) and from the PowerShell name list (line 825). If they must be terminated, resolve them by PID after confirming their `MainModule.FileName` is under a known Epic install root.
2. **Remove the `continue` at line 945.** Name matching should *narrow the candidate set*, not authorize termination. Every candidate must then pass the path check at line 948:
   ```csharp
   if (!launcherProcessNames.Contains(n) && !IsUnderEpicRoot(path)) { p.Dispose(); continue; }
   if (!IsUnderEpicRoot(path)) { p.Dispose(); continue; }   // path is authoritative
   active.Add(p);
   ```
3. **Handle the unresolvable case explicitly.** When `MainModule` is inaccessible (line 939 swallows the exception), the process must be treated as *unknown*, not as *ours*. Log it and surface it in the "could not close Epic" message so the user can act.
4. **Kill by PID only.** Delete every `/IM` invocation (line 818). Image-name termination is never correct when the images are shared across vendors.
5. Add a test asserting that a process whose path is outside every Epic install root is not returned by `GetEpicProcesses`.

---

### 35. Log-file parsing reads entire multi-megabyte files into memory to inspect the last 1,000 lines

**Severity:** S4
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 1888–1918 (`ReadAllLinesSafe`), 1956–1984 (consumption in `GetDetectedMetadata`)

**Evidence**

```csharp
// AccountService.cs:1893-1901  -- materialises the whole file
var lines = new List<string>();
using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
using (var reader = new StreamReader(fs))
{
    string line;
    while ((line = reader.ReadLine()) != null) lines.Add(line);
}
return lines;
```

```csharp
// AccountService.cs:1961-1964  -- then throws away all but the last 1,000
var lines = ReadAllLinesSafe(f);
var q = new Queue<string>(1000);
foreach (var l in lines) { if (q.Count >= 1000) q.Dequeue(); q.Enqueue(l ?? ""); }
```

And the fallback path copies the entire file to `%TEMP%` before reading it:

```csharp
// AccountService.cs:1907-1910
var tmpCopy = Path.Combine(Path.GetTempPath(), "read_copy_" + Guid.NewGuid().ToString("N") + ".tmp");
File.Copy(path, tmpCopy, true);
try { return File.ReadAllLines(tmpCopy); }
```

**Why this matters**

`EpicGamesLauncher.log` regularly reaches tens of megabytes. Two files are processed (line 1956, `.Take(2)`), so a metadata detection can allocate 100+ MB of `string` objects and a `List<string>` with hundreds of thousands of entries — all to examine the tail. Every one of those strings is a Gen-0/LOH allocation.

`GetDetectedMetadata` is called from `PromptForUnsavedLoginIfNeeded`, which runs on a **20-second `DispatcherTimer`** (`MainWindow.xaml.cs` lines 66–77). The cache at lines 1928–1931 only short-circuits when an ID is already known *and* the cached name is not `"N/A"` — so on a machine where the display name cannot be parsed, this full-file read repeats every 20 seconds for the life of the process.

The `File.Copy` fallback additionally writes a full copy of the launcher log into `%TEMP%` — a log that contains the user's email address and display name (which is precisely why the parser is looking at it) — as a world-readable temp file (see finding 19).

**Correction**

1. **Read the tail, not the file.** Seek to `length - 256 KB`, read forward, discard the first partial line:
   ```csharp
   private static IReadOnlyList<string> ReadTailLines(string path, int maxLines, int maxBytes = 256 * 1024)
   {
       using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
       var start = Math.Max(0, fs.Length - maxBytes);
       fs.Seek(start, SeekOrigin.Begin);
       using var reader = new StreamReader(fs);
       if (start > 0) reader.ReadLine();          // discard partial first line
       var ring = new Queue<string>(maxLines);
       string line;
       while ((line = reader.ReadLine()) != null) { if (ring.Count == maxLines) ring.Dequeue(); ring.Enqueue(line); }
       return ring.ToArray();
   }
   ```
2. **Delete the `File.Copy` shadow fallback** (lines 1905–1912). `FileShare.ReadWrite | FileShare.Delete` on the primary read already handles Epic's write lock; the copy path is both redundant and a privacy leak. If a shadow read is genuinely needed, use a VSS snapshot or accept the failure.
3. **Fix the cache.** Cache negative results too, with a TTL — `_lastDetected` (line 1886) should be invalidated on an explicit signal (launcher exit, user action) rather than re-derived every 20 s.
4. **Back off the timer.** The 20-second `DispatcherTimer` should extend its interval when nothing changes (20 s → 60 s → 300 s) and reset on a detected launcher state change.

---

### 36. Profile-image normalization performs recursive directory scans on every account load

**Severity:** S4
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\Epic-Games_Account_Switcher-Globals\AccountService.cs`
**Lines:** 87–120 (`NormalizeProfileImagePath`), 122–144 (`FindSkinByFileName`), 178–190 (`NormalizeAccount`), 154 and 171 (invocation per account)

**Evidence**

```csharp
// AccountService.cs:132-141  -- recursive scan of three roots, per miss
foreach (var root in roots)
{
    if (!Directory.Exists(root)) continue;
    var match = Directory.GetFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(match)) return Path.GetFullPath(match);
}
```

```csharp
// AccountService.cs:153-154  -- runs for every account on every load
var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(AccountsJsonPath)) ?? new List<Account>();
foreach (var a in accs) NormalizeAccount(a);       // -> NormalizeProfileImagePath -> FindSkinByFileName
```

```csharp
// AccountService.cs:171  -- and again on every save
foreach (var a in accounts) NormalizeAccount(a);
```

**Why this matters**

`NormalizeProfileImagePath` probes up to six candidate paths with `File.Exists` (lines 95–113) and, on a miss, falls through to `FindSkinByFileName`, which runs `Directory.GetFiles(..., SearchOption.AllDirectories)` against up to three roots. With N accounts and a missing skin file, that is 3N recursive directory walks.

`GetAccounts` is called from `LoadAccountsAsync` (`MainViewModel.cs` line 300) on window load, after every switch (line 443), after every capture (line 535), and after every restore (line 698). `SaveAccounts` normalizes *again* on every write, including the write at the end of every switch (line 1673). The work is entirely redundant — the same missing file is re-searched every time.

**Correction**

1. **Cache resolution results.** A `ConcurrentDictionary<string, string>` keyed by the raw `ProfileImagePath`, invalidated by a `FileSystemWatcher` on the skin roots or simply on an explicit refresh. A miss is as worth caching as a hit.
2. **Enumerate the skin roots once** into a `Dictionary<fileName, fullPath>` at startup rather than calling `Directory.GetFiles` per lookup.
3. **Separate normalization from resolution.** `NormalizeAccount` (lines 178–190) should only fill in null/default fields — a pure, cheap, I/O-free operation. Path *resolution* belongs in a `ProfileImageResolver` invoked by the view layer when a card is rendered, where it can be async and cached.
4. **Do not normalize on save** (line 171). Writing should persist what the caller supplied; rewriting `ProfileImagePath` to a fully-resolved absolute path on every save also makes `accounts.json` machine-specific, which works against the portable-backup goal (finding 9).

---

## SECTION F — PRODUCT & DOCUMENTATION GAPS

---

### 37. No recovery experience for an interrupted switch

**Severity:** S2 (the user-facing half of finding 2)
**Files:** `Epic-Games_Account_Switcher-Client\App.xaml.cs` line 75; `Epic-Games_Account_Switcher-Client\MainViewModel.cs` lines 445–449, 459–463

**Evidence**

Startup does exactly one thing about leftover state — it deletes it:
```csharp
// App.xaml.cs:75
AccountService.CleanOrphanedTempFolders();
```

Failure is communicated as an undifferentiated modal:
```csharp
// MainViewModel.cs:459-463
var detail = AccountService.LastSwitchFailureMessage;
new CustomMessageBox("Switch Failed", "Failed to switch to " + account.Name + "." + ... + "\n\nOpen Show Logs for the full trace.").ShowDialog();
```

**Correction**

1. Implement the transaction journal (finding 2) and run `RecoverInterruptedSwitchAsync` before the main window is shown.
2. Add a first-class recovery UI: a banner stating what happened, which account was affected, and a single "Restore my previous session" action.
3. Differentiate the failure dialog on `SwitchResult.LiveSessionIntact` (finding 22). "Nothing changed, try again" and "your session was restored from backup" and "manual recovery required" are three different messages requiring three different user actions.
4. Add a "Diagnostics" pane that surfaces the last operation ID, the rollback directory, and a one-click log export — replacing "Open Show Logs for the full trace" with something actionable.

---

### 38. Saved sessions silently rot; there is no expiry visibility and no proactive refresh

**Severity:** S3 (the highest-value product gap)
**Files / lines:**
- Snapshot refresh happens **only** during a switch: `AccountService.cs` lines 1340–1373 (`RefreshSnapshotOfCurrentSavedAccount`), invoked once at line 1586
- Expiry is computed but never surfaced: `AccountService.cs` lines 268, 286 (`ExpiresUtc` on `SessionValidationResult`) — the type is `private sealed` (line 37) and never escapes the class
- The card model has no expiry field: `AccountModel.cs` lines 96–143 (`HasUsageHistory`, `IsRecentlyUsed`, `RecencyLabel`, `UsageLabel`) — all activity metrics, none about validity

**Why this matters**

The user's mental model is "these are my saved accounts, one click each." The reality is that each saved session carries an authentication token with a finite life. An account not switched to for several weeks may have a dead token, and the user discovers this only at the moment they try to use it — *after* their current session has been destructively replaced (`SwitchToAccount` validates the source at line 1568 before the destructive phase, which prevents the worst case, but the user still gets a failure instead of a switch).

`RecencyLabel` (`AccountModel.cs` lines 119–129) returns `"RESTING"` for an account not used in 3 days. That label describes *user behaviour*, not *session health* — the two look identical in the UI and the user cannot distinguish "I haven't used this lately" from "this will not work."

**Correction**

1. **Surface expiry on the model.** Promote `SessionValidationResult` to public, add `DateTime? SessionExpiresUtc` and `SessionHealth { Healthy, ExpiringSoon, Expired, Unknown }` to `Account`, populated by a background validation pass over `EG\<Id>` on load.
2. **Render it.** Replace or supplement `RecencyLabel` with a health chip: green "valid for 24 days", amber "expires in 2 days", red "expired — re-capture needed", grey "unknown".
3. **Refresh proactively.** Watch for `EpicGamesLauncher.exe` exit (a `Process.Exited` subscription, or the existing 20-second timer) and, when the live account matches a saved profile, run `RefreshSnapshotOfCurrentSavedAccount` automatically. This is the single change that most reduces "my saved account stopped working" — sessions stay fresh as a side effect of normal use.
4. **Prompt before rot.** When an account crosses into `ExpiringSoon`, offer a one-click "switch to this account and back" refresh cycle.
5. Depends on finding 5 for the expiry to be truthful rather than a file-mtime proxy.

---

### 39. No update mechanism

**Severity:** S4
**Evidence:** no update-check code exists in any project; `Globals.Version` (Globals.cs line 25) is compared against nothing. Distribution is a manually-published `compiled\Epic-Games_Account_Switcher.exe` (Build.cmd lines 55–61).

**Why this matters**

The application performs surgical manipulation of an authentication surface that Epic changes without notice — the `PersistentAuthStorage.json` → `OC_*.dat` transition visible in this codebase is exactly such a change. When Epic changes format again, every installed copy breaks simultaneously, and there is no channel to ship a fix. Users must notice, find the repository, and manually reinstall.

**Correction**

1. Add a lightweight update check against the GitHub Releases API on startup (opt-out in settings), comparing `Globals.Version` to the latest tag.
2. Surface it as a non-modal banner with release notes and a download link — not an auto-updater, which would reintroduce the elevated-copy problems of finding 12.
3. If MSIX is adopted (finding 26), App Installer provides automatic updates with no custom code and no elevation.
4. Add a **compatibility guard**: if `SelectModernAuthBlob` (lines 297–336) finds no recognizable auth artifacts at all, that likely means Epic changed the format. Detect it, log it distinctly, and tell the user their version may be out of date rather than reporting a generic validation failure.

---

### 40. `project_structure.txt` makes claims the code does not support

**Severity:** S3 (a misleading map is worse than no map — it is the first file an agent or new contributor reads)
**File:** `C:\Full_Control\Epic-Games_Account_Switcher\project_structure.txt`

| Line | Claim | Reality |
|---|---|---|
| 30 | *"`Registry.cs` — Encapsulated logic for reading/writing complex registry value types under **explicit Registry64 views**"* | `grep -c "Registry64" Registry.cs` → **0**. The file uses `Registry.CurrentUser` / `Registry.LocalMachine` (lines 12–17), which resolve via the process's default view. `Registry64` appears only in `App.xaml.cs` (lines 485, 543, 673) and `AccountService.cs` (line 860). |
| 9 | *"Dead code ... has been purged"* | 8 unreferenced private members remain (finding 23). |
| 9 | *"All DPAPI encryption operations, registry modifications, and live Epic log extraction routines implement robust rollback, retry loops, and shadow copy-on-read fallback mechanics **without silent crashes**"* | 127 empty catch blocks (finding 21); `AccountModel.Password` returns `""` on decryption failure (AccountModel.cs line 159). |
| 10 | *"Session validity (OAuth expiration) is verified before restoration"* | The modern binary path uses file mtime + a settings constant, not OAuth expiry (finding 5). |
| 38 | *"validate modern binary auth blobs against OAuth expiration timestamps"* | Same as above. |
| 39 | *"Epic processes must be identified by installation path"* | `GetEpicProcesses` short-circuits on image name before the path check (finding 34); `taskkill /F /IM` ignores path entirely. |
| 6 | *"PROJECT STATUS (2026-04-27)"* | Three months stale as of this audit. |
| 33 | *"MSTest coverage for ... atomic move safety"* | One reflection-based test for a read-only file move (`AccountServiceSafetyTests.cs` lines 108–121); no coverage of the atomic switch (finding 31). |

**Correction**

1. Rewrite §2 (PROJECT STATUS) as **verified** claims only. Each bullet should name the test or file that substantiates it. Anything not substantiated moves to a "Known gaps" section.
2. Fix §3's `Registry.cs` description, §4 principles 2 and 3, and §2's dead-code and no-silent-crash claims — either by correcting the document or by fixing the code (findings 5, 21, 23, 34). Prefer fixing the code where the claim describes the intended design.
3. Add a **Known Limitations** section covering: cross-machine credential portability (finding 9), heuristic token expiry (finding 5), unsigned binary and AV heuristics (finding 14), and single-user-per-machine assumptions.
4. Add a release-checklist item: *"§2 is a claim about the current tree — re-verify before tagging."* Better still, generate the status section from test results in CI so it cannot drift.

---

## PRIORITIZED REMEDIATION SEQUENCE

**Phase 1 — Stop the bleeding (no refactoring required)**

| # | Finding | Est. effort |
|---|---|---|
| 1 | `CopyFilesRecursive` failure accounting | 4 h |
| 2 | Convert `_switch_rb` purge into a recovery journal | 2 d |
| 13 | Fully qualify `taskkill` / `schtasks` / `cmd.exe` | 30 m |
| 34 | Remove generic UE images; enforce path check | 2 h |
| 12 | ACL the extraction directory + payload hash manifest | 1 d |
| 10 | Gate legacy ZIP restore; stop importing `appsettings.json` | 4 h |
| 11 | `SettingsPath` → computed property | 15 m |
| 14 | Randomize + ACL the uninstall batch path | 1 h |

**Phase 2 — Make it verifiable**

| # | Finding | Est. effort |
|---|---|---|
| 29 | Add `dotnet test` to `Build.cmd` + GitHub Actions CI | 4 h |
| 20 | Introduce `IFileSystem` / `IRegistryStore` / `IProcessHost` / `IClock` | 3 d |
| 22 | Replace `Tuple` returns and static side channels with result types | 1 d |
| 31 | Fault-injection test suite for switch and rollback | 3 d |
| 21 | Triage the 15 highest-impact empty catches | 1 d |
| 3 | Unify the account-store lock; add revision-based concurrency | 1 d |
| 4 | `RollbackOutcome` + `LiveSessionIntact` escalation | 1 d |

**Phase 3 — Modernize**

| # | Finding | Est. effort |
|---|---|---|
| 30 | Retarget to `net9.0-windows`; enable nullable + warnings-as-errors | 3 d |
| 30 | `PublishSingleFile`; delete `SingleFilePacker` | 1 d |
| 26 | WiX installer + Authenticode signing | 5 d |
| 20 | Decompose `AccountService` collaborator by collaborator | 2 w |
| 27 | `IDialogService` / `IShellService`; view-model tests | 3 d |
| 9 | Portable backup format (`EGASBKP3`) with rewrapped secrets | 2 d |
| 16 | Bounded KDF parameters; Argon2id profile; AES-GCM | 2 d |

**Phase 4 — Product**

| # | Finding | Est. effort |
|---|---|---|
| 38 | Session health + expiry surfacing + proactive refresh | 1 w |
| 37 | Recovery UI and differentiated failure messaging | 3 d |
| 5 | Parse `OC_*.dat` for authoritative expiry | 3 d |
| 39 | Update check | 2 d |
| 32/33/35/36 | Performance pass on the switch critical path | 3 d |
| 40 | Documentation truth pass | 4 h |
