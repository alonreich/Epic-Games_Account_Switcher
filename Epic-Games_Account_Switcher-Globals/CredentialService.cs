using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

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

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentialPtrs);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        private static readonly string[] CredentialFilters = 
        { 
            "eos-sdk:*", "EpicGames:*", "EpicGames.Launcher:*", "EOS:*", "Epic Games:*", 
            "com.epicgames*", "Epic*Account*", "Epic*Session*", "Epic*Auth*" 
        };

        public static List<SavedCredential> CaptureCredentials()
        {
            var results = new List<SavedCredential>();
            Globals.WriteToLog("[CRED] Starting credential capture...");
            foreach (var filter in CredentialFilters)
            {
                try
                {
                    if (CredEnumerate(filter, 0, out int count, out IntPtr credentialPtrs))
                    {
                        Globals.WriteToLog($"[CRED] Found {count} credentials for filter: {filter}");
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr ptr = Marshal.ReadIntPtr(credentialPtrs, i * IntPtr.Size);
                            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                            
                            var saved = new SavedCredential
                            {
                                TargetName = cred.TargetName,
                                UserName = cred.UserName,
                                Persist = cred.Persist,
                                Type = cred.Type,
                                Blob = new byte[cred.CredentialBlobSize]
                            };
                            Marshal.Copy(cred.CredentialBlob, saved.Blob, 0, (int)cred.CredentialBlobSize);
                            
                            if (!results.Any(r => r.TargetName == saved.TargetName))
                                results.Add(saved);
                        }
                        CredFree(credentialPtrs);
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog($"Failed to capture credentials for filter {filter}", ex);
                }
            }
            Globals.WriteToLog($"[CRED] Captured {results.Count} unique credentials.");
            return results;
        }

        public static void DeleteCredentials()
        {
            Globals.WriteToLog("[CRED] Deleting current credentials...");
            int deletedCount = 0;
            foreach (var filter in CredentialFilters)
            {
                try
                {
                    if (CredEnumerate(filter, 0, out int count, out IntPtr credentialPtrs))
                    {
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr ptr = Marshal.ReadIntPtr(credentialPtrs, i * IntPtr.Size);
                            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                            if (CredDelete(cred.TargetName, cred.Type, 0)) deletedCount++;
                        }
                        CredFree(credentialPtrs);
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog($"Failed to delete credentials for filter {filter}", ex);
                }
            }
            Globals.WriteToLog($"[CRED] Deleted {deletedCount} credentials.");
        }

        public static void RestoreCredentials(List<SavedCredential> savedList)
        {
            if (savedList == null) return;

            Globals.WriteToLog($"[CRED] Restoring {savedList.Count} credentials...");
            foreach (var saved in savedList)
            {
                try
                {
                    IntPtr blobPtr = Marshal.AllocHGlobal(saved.Blob.Length);
                    Marshal.Copy(saved.Blob, 0, blobPtr, saved.Blob.Length);

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
                        Globals.WriteToLog($"[CRED] Failed to write credential {saved.TargetName}. Error: {Marshal.GetLastWin32Error()}");
                    }
                    else
                    {
                        Globals.WriteToLog($"[CRED] Restored: {saved.TargetName}");
                    }

                    Marshal.FreeHGlobal(blobPtr);
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog($"Error restoring credential {saved.TargetName}", ex);
                }
            }
        }
    }
}
