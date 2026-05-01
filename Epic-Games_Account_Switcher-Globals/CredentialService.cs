using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Epic_Games_Account_Switcher_Globals
{
    public class CredentialService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        public class SavedCredential
        {
            public string TargetName { get; set; }
            public string UserName { get; set; }
            public byte[] Blob { get; set; }
            public uint Persist { get; set; }
            public uint Type { get; set; }
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentialPtrs);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        private static readonly string[] CredentialNeedles = { "eos", "epic", "com.epicgames", "epicgameslauncher", "fortnite" };

        public static List<SavedCredential> CaptureCredentials()
        {
            var results = new List<SavedCredential>();
            Globals.WriteToLog("[CRED] Starting credential capture...");
            ForEachEpicCredential(cred =>
            {
                try
                {
                    var saved = new SavedCredential
                    {
                        TargetName = cred.TargetName,
                        UserName = cred.UserName,
                        Persist = cred.Persist,
                        Type = cred.Type,
                        Blob = new byte[cred.CredentialBlobSize]
                    };
                    if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0) Marshal.Copy(cred.CredentialBlob, saved.Blob, 0, (int)cred.CredentialBlobSize);
                    if (!results.Any(r => string.Equals(r.TargetName, saved.TargetName, StringComparison.OrdinalIgnoreCase))) results.Add(saved);
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[CRED] Failed to capture Epic credential.", ex);
                }
                return true;
            });
            Globals.WriteToLog("[CRED] Captured " + results.Count + " unique credentials.");
            return results;
        }

        public static bool DeleteCredentials()
        {
            Globals.WriteToLog("[CRED] Deleting current credentials...");
            var success = true;
            var deletedCount = 0;
            ForEachEpicCredential(cred =>
            {
                try
                {
                    if (CredDelete(cred.TargetName, cred.Type, 0)) deletedCount++;
                    else
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != 1168)
                        {
                            success = false;
                            Globals.WriteToLog("[CRED] Failed to delete credential. Error: " + err);
                        }
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Globals.WriteToLog("[CRED] Failed to delete Epic credential.", ex);
                }
                return true;
            });
            Globals.WriteToLog("[CRED] Deleted " + deletedCount + " credentials.");
            return success;
        }

        public static bool RestoreCredentials(List<SavedCredential> savedList)
        {
            if (savedList == null) return true;
            Globals.WriteToLog("[CRED] Restoring " + savedList.Count + " credentials...");
            var success = true;
            foreach (var saved in savedList)
            {
                IntPtr blobPtr = IntPtr.Zero;
                try
                {
                    if (saved == null || string.IsNullOrWhiteSpace(saved.TargetName) || saved.Blob == null) continue;
                    blobPtr = Marshal.AllocHGlobal(saved.Blob.Length);
                    if (saved.Blob.Length > 0) Marshal.Copy(saved.Blob, 0, blobPtr, saved.Blob.Length);
                    var cred = new CREDENTIAL
                    {
                        Type = saved.Type,
                        TargetName = saved.TargetName,
                        UserName = saved.UserName,
                        Persist = saved.Persist,
                        CredentialBlobSize = (uint)saved.Blob.Length,
                        CredentialBlob = blobPtr,
                        Flags = 0,
                        AttributeCount = 0,
                        Attributes = IntPtr.Zero,
                        Comment = null,
                        TargetAlias = null
                    };
                    if (!CredWrite(ref cred, 0))
                    {
                        success = false;
                        Globals.WriteToLog("[CRED] Failed to write credential " + saved.TargetName + ". Error: " + Marshal.GetLastWin32Error());
                    }
                    else
                    {
                        Globals.WriteToLog("[CRED] Restored: " + saved.TargetName);
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    Globals.WriteToLog("[CRED] Error restoring credential " + saved.TargetName, ex);
                }
                finally
                {
                    if (blobPtr != IntPtr.Zero) Marshal.FreeHGlobal(blobPtr);
                }
            }
            return success;
        }

        private static void ForEachEpicCredential(Func<CREDENTIAL, bool> action)
        {
            IntPtr credentialPtrs = IntPtr.Zero;
            try
            {
                int count;
                if (!CredEnumerate(null, 0, out count, out credentialPtrs)) return;
                for (int i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(credentialPtrs, i * IntPtr.Size);
                    var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                    if (IsEpicCredential(cred.TargetName))
                    {
                        if (!action(cred)) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[CRED] Failed to enumerate credentials.", ex);
            }
            finally
            {
                if (credentialPtrs != IntPtr.Zero) CredFree(credentialPtrs);
            }
        }

        private static bool IsEpicCredential(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return false;
            var n = targetName.ToLowerInvariant();
            return CredentialNeedles.Any(needle => n.Contains(needle));
        }
    }
}
