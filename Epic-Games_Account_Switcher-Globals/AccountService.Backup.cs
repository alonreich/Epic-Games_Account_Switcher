using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Encrypted backup, restore and verification.
    /// File format "EGASBKP2" (unchanged, so every version can read every backup):
    ///   magic | mode:int | iterations:int | salt | protectedKey | iv (each int length + bytes) | cipherLen:int | AES-256-CBC(zip) | HMAC-SHA256(all before it)
    /// Backups are written in ONE streaming pass (files -> zip -> AES -> disk) with no temporary copies, and skip
    /// throw-away browser cache (<see cref="CacheFilter"/>). Shipped avatars are referenced by name, uploaded photos are included.
    /// </summary>
    public partial class AccountService
    {
        public enum BackupProtectionMode
        {
            CurrentUser,
            Password
        }

        public sealed class BackupPasswordRequiredException : Exception
        {
            public BackupPasswordRequiredException() : base("This backup is password-protected. Enter the backup password to continue.") { }
        }

        public sealed class BackupPasswordInvalidException : Exception
        {
            public BackupPasswordInvalidException() : base("The backup password is incorrect or the encrypted backup is damaged.") { }
        }

        private static readonly byte[] EncryptedBackupMagic = Encoding.ASCII.GetBytes("EGASBKP2");
        private const int EncryptedBackupPasswordMode = 1;
        private const int EncryptedBackupCurrentUserMode = 2;
        private const int BackupKeyBytes = 32;
        private const int BackupHmacBytes = 32;
        private const int BackupPasswordIterations = 150000;
        private const int BackupBufferBytes = 1024 * 1024;
        /// <summary>Decrypted backups up to this size are handled in memory; larger (old, cache-heavy) ones use a temp file.</summary>
        private const long InMemoryBackupLimit = 64L * 1024 * 1024;
        private const string BackupSkinFolder = "Skin_Icons";
        private const string BackupCustomSkinFolder = "Skin_Icons/Custom";

        public static void BackupAccounts(string zip, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            BackupAccounts(zip, null, BackupProtectionMode.CurrentUser, log, cancellationToken);
        }

        public static void BackupAccounts(string zip, string password, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            BackupAccounts(zip, password, BackupProtectionMode.Password, log, cancellationToken);
        }

        public static void BackupAccounts(string zip, string password, BackupProtectionMode protectionMode, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            if (protectionMode == BackupProtectionMode.Password && string.IsNullOrWhiteSpace(password))
                throw new BackupPasswordRequiredException();

            SessionOperationLock.Wait(cancellationToken);
            var outputTmp = zip + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(BaseDataPath);
                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zip));
                if (!string.IsNullOrWhiteSpace(zipDir)) Directory.CreateDirectory(zipDir);

                var accounts = GetAccounts();
                var portableAccounts = CloneAccounts(accounts);
                var fileCount = 0;
                using (var output = new FileStream(outputTmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BackupBufferBytes))
                {
                    WriteEncryptedBackup(output, password, protectionMode, plain =>
                    {
                        using (var archive = new ZipArchive(plain, ZipArchiveMode.Create, true))
                        {
                            var settingsPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");
                            if (AddFileToArchive(archive, settingsPath, "appsettings.json")) fileCount++;

                            foreach (var account in accounts)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                fileCount += AddFolderToArchive(archive, Path.Combine(BaseDataPath, account.Id), account.Id, cancellationToken);
                            }

                            var included = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            fileCount += AddCustomSkinsToArchive(archive, included, cancellationToken);
                            foreach (var account in portableAccounts)
                                account.ProfileImagePath = MakePortableProfileImagePath(archive, account, included, log);

                            var accountsEntry = archive.CreateEntry("accounts.json", CompressionLevel.Optimal);
                            using (var writer = new StreamWriter(accountsEntry.Open(), new UTF8Encoding(false)))
                                writer.Write(JsonConvert.SerializeObject(portableAccounts, Formatting.Indented));
                            fileCount++;
                        }
                    }, cancellationToken);
                    output.Flush(true);
                }

                AtomicReplaceFile(outputTmp, zip);
                Globals.WriteToLog("[BACKUP] Created. Profiles=" + accounts.Count + "; Files=" + fileCount + "; Size=" + new FileInfo(zip).Length / 1024 + " KB");
            }
            finally
            {
                if (File.Exists(outputTmp)) try { File.Delete(outputTmp); } catch { }
                SessionOperationLock.Release();
            }
        }

        public static void RestoreAccounts(string zip, CancellationToken cancellationToken = default)
        {
            RestoreAccounts(zip, null, cancellationToken);
        }

        public static void RestoreAccounts(string zip, string password, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(zip)) throw new FileNotFoundException("Backup file not found: " + zip);
            var lockTaken = false;
            // Staged next to the live folder (same drive), so the final swap is a fast rename instead of a copy.
            var staging = Path.Combine(Globals.UserDataFolder, "EG_restore_" + GetShortId());
            var rb = BaseDataPath + "_rb";
            var rollbackAvailable = false;
            try
            {
                using (var plainZip = OpenBackupArchiveStream(zip, password, cancellationToken))
                {
                    SessionOperationLock.Wait(cancellationToken);
                    lockTaken = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    Globals.WriteToLog("[RESTORE] Starting account restore. Zip=" + zip + "; Target=" + BaseDataPath);
                    if (Directory.Exists(staging)) try { Globals.ForceDeleteDirectory(staging); } catch { }
                    Directory.CreateDirectory(staging);

                    var extractedEntryCount = 0;
                    try
                    {
                        using (var archive = new ZipArchive(plainZip, ZipArchiveMode.Read, true))
                        {
                            foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var fullPath = GetSafeExtractionPath(staging, entry.FullName);
                                if (CacheFilter.IsDisposable(fullPath)) continue;
                                var longPath = fullPath.StartsWith(@"\\") ? fullPath : @"\\?\" + fullPath;
                                Directory.CreateDirectory(Path.GetDirectoryName(longPath));
                                using (var stream = entry.Open())
                                using (var fs = new FileStream(longPath, FileMode.Create, FileAccess.Write, FileShare.None, BackupBufferBytes))
                                    stream.CopyTo(fs, BackupBufferBytes);
                                extractedEntryCount++;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { throw new IOException("Failed to extract backup safely. [LONG_PATH?] Error: " + ex.Message, ex); }
                    Globals.WriteToLog("[RESTORE] Backup extracted. Entries=" + extractedEntryCount + "; Staging=" + staging);
                }

                var aJson = Path.Combine(staging, "accounts.json");
                if (!File.Exists(aJson)) throw new InvalidDataException("Backup is invalid: accounts.json is missing.");

                // Avatars and settings live outside the account folder: take them out of the staging tree.
                RestoreBackupSkins(staging);
                var skinsInStaging = Path.Combine(staging, BackupSkinFolder);
                var stagedSettings = Path.Combine(staging, "appsettings.json");
                string settingsCopy = null;
                if (File.Exists(stagedSettings))
                {
                    settingsCopy = Path.Combine(Globals.UserDataFolder, "appsettings.restore_" + GetShortId() + ".json");
                    File.Move(stagedSettings, settingsCopy);
                }

                var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(aJson)) ?? new List<Account>();
                Globals.WriteToLog("[RESTORE] Backup accounts parsed. Count=" + accs.Count);
                foreach (var a in accs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    a.ProfileImagePath = ResolveRestoredProfileImagePath(a.ProfileImagePath, staging);
                    NormalizeAccount(a);
                }
                if (Directory.Exists(skinsInStaging)) try { Globals.ForceDeleteDirectory(skinsInStaging); } catch { }

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
                if (Directory.Exists(BaseDataPath))
                {
                    try
                    {
                        SafeMovePathBlocking(BaseDataPath, rb);
                        rollbackAvailable = Directory.Exists(rb);
                        Globals.WriteToLog("[RESTORE] Existing account data moved to rollback. Rollback=" + rb);
                    }
                    catch (Exception ex)
                    {
                        rollbackAvailable = false;
                        Globals.WriteToLog("[RESTORE] Existing account data could not be moved to rollback. Falling back to in-place overwrite.", ex);
                    }
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(BaseDataPath))
                    {
                        Directory.Move(staging, BaseDataPath);
                    }
                    else if (!Globals.CopyFilesRecursive(staging, BaseDataPath, true))
                    {
                        throw new IOException("Failed to copy restored account files into " + BaseDataPath + ".");
                    }

                    if (settingsCopy != null)
                    {
                        File.Copy(settingsCopy, Path.Combine(Globals.UserDataFolder, "appsettings.json"), true);
                        AppSettings.LoadSettings();
                    }

                    // The user chose to replace the account list with the backup, so an unreadable old list may be overwritten.
                    lock (AccountsFileLock) _accountsFileUnreadableAndUnsaved = false;
                    SaveAccounts(accs);
                    Globals.WriteToLog("[RESTORE] Account restore completed. Count=" + accs.Count + "; RollbackAvailable=" + rollbackAvailable);
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[RESTORE] Restore copy/save failed.", ex);
                    if (rollbackAvailable)
                    {
                        if (Directory.Exists(BaseDataPath)) try { Globals.ForceDeleteDirectory(BaseDataPath); } catch { }
                        try
                        {
                            if (Directory.Exists(rb)) SafeMovePathBlocking(rb, BaseDataPath);
                            Globals.WriteToLog("[RESTORE] Rollback restored after restore failure.");
                        }
                        catch (Exception rollbackEx)
                        {
                            Globals.WriteToLog("[RESTORE] Rollback restore failed after restore failure.", rollbackEx);
                        }
                    }
                    else
                    {
                        Globals.WriteToLog("[RESTORE] Rollback was unavailable because the existing account data folder was locked. Original data was left in place where possible.");
                    }
                    throw;
                }
                finally
                {
                    if (settingsCopy != null && File.Exists(settingsCopy)) try { File.Delete(settingsCopy); } catch { }
                }
                if (rollbackAvailable && Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
            }
            finally
            {
                if (Directory.Exists(staging)) try { Globals.ForceDeleteDirectory(staging); } catch { }
                if (lockTaken) SessionOperationLock.Release();
            }
        }

        /// <summary>
        /// Proves a backup is usable: the file exists, its HMAC matches (so it is complete and undamaged), the current user
        /// can decrypt it, and it contains accounts.json. Returns the number of account profiles inside.
        /// Throws with a readable reason when the backup is not usable. Works in memory for normal-size backups.
        /// </summary>
        public static int VerifyBackupFile(string path, string password = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Backup file was not created: " + path);
            if (new FileInfo(path).Length == 0) throw new InvalidDataException("Backup file is empty: " + path);
            using (var plainZip = OpenBackupArchiveStream(path, password, CancellationToken.None))
            using (var archive = new ZipArchive(plainZip, ZipArchiveMode.Read, true))
            {
                var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, "accounts.json", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidDataException("Backup does not contain accounts.json.");
                using (var reader = new StreamReader(entry.Open()))
                    return Newtonsoft.Json.Linq.JArray.Parse(reader.ReadToEnd()).Count;
            }
        }

        public static bool IsEncryptedBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < EncryptedBackupMagic.Length) return false;
                    var buffer = new byte[EncryptedBackupMagic.Length];
                    fs.ReadExactly(buffer, 0, buffer.Length);
                    return buffer.AsSpan().SequenceEqual(EncryptedBackupMagic);
                }
            }
            catch { return false; }
        }

        /// <summary>Returns a seekable stream with the backup's plain ZIP (decrypted and authenticated when encrypted).</summary>
        private static Stream OpenBackupArchiveStream(string path, string password, CancellationToken cancellationToken)
        {
            if (!IsEncryptedBackup(path))
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BackupBufferBytes);

            cancellationToken.ThrowIfCancellationRequested();
            Stream plain = null;
            try
            {
                plain = DecryptBackup(path, password, cipherLen => cipherLen <= InMemoryBackupLimit
                    ? new MemoryStream((int)cipherLen)
                    : new FileStream(Path.Combine(Path.GetTempPath(), "e_res_zip_" + GetShortId() + ".zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BackupBufferBytes, FileOptions.DeleteOnClose),
                    cancellationToken);
                plain.Position = 0;
                return plain;
            }
            catch
            {
                plain?.Dispose();
                throw;
            }
        }

        /// <summary>Writes header, encrypted content and HMAC. <paramref name="writePlain"/> streams the plain ZIP into the cipher.</summary>
        private static void WriteEncryptedBackup(FileStream output, string password, BackupProtectionMode protectionMode, Action<Stream> writePlain, CancellationToken cancellationToken)
        {
            var mode = protectionMode == BackupProtectionMode.Password ? EncryptedBackupPasswordMode : EncryptedBackupCurrentUserMode;
            byte[] salt = Array.Empty<byte>();
            byte[] protectedKey = Array.Empty<byte>();
            byte[] keyMaterial = null;
            try
            {
                if (mode == EncryptedBackupPasswordMode)
                {
                    salt = RandomNumberGenerator.GetBytes(16);
                    keyMaterial = DeriveBackupKeyMaterial(password, salt);
                }
                else
                {
                    keyMaterial = RandomNumberGenerator.GetBytes(BackupKeyBytes + BackupHmacBytes);
                    protectedKey = ProtectedData.Protect(keyMaterial, null, DataProtectionScope.CurrentUser);
                }
                var iv = RandomNumberGenerator.GetBytes(16);

                long cipherLenPosition;
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    writer.Write(EncryptedBackupMagic);
                    writer.Write(mode);
                    writer.Write(BackupPasswordIterations);
                    WriteLengthPrefixed(writer, salt);
                    WriteLengthPrefixed(writer, protectedKey);
                    WriteLengthPrefixed(writer, iv);
                    cipherLenPosition = output.Position;
                    writer.Write(0); // patched below once the cipher length is known
                }
                var cipherStart = output.Position;

                using (var aes = Aes.Create())
                {
                    aes.Key = keyMaterial.AsSpan(0, BackupKeyBytes).ToArray();
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var cs = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, true))
                    {
                        writePlain(cs);
                        cancellationToken.ThrowIfCancellationRequested();
                        cs.FlushFinalBlock();
                    }
                }

                var cipherLen = output.Position - cipherStart;
                if (cipherLen > int.MaxValue) throw new IOException("Backup is too large (over 2 GB).");
                var lenBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(lenBytes, (int)cipherLen);
                output.Position = cipherLenPosition;
                output.Write(lenBytes, 0, 4);

                // HMAC over header + cipher, read back from disk (the header changed after the cipher was written).
                var bodyLength = cipherStart + cipherLen;
                output.Position = 0;
                var hash = ComputeHmac(output, keyMaterial, bodyLength, cancellationToken);
                output.Position = bodyLength;
                output.Write(hash, 0, hash.Length);
                output.SetLength(output.Position);
            }
            finally
            {
                if (keyMaterial != null) CryptographicOperations.ZeroMemory(keyMaterial);
            }
        }

        /// <summary>Authenticates the whole file first, then decrypts into a stream from <paramref name="createOutput"/> (given the cipher length).</summary>
        private static Stream DecryptBackup(string encryptedPath, string password, Func<long, Stream> createOutput, CancellationToken cancellationToken)
        {
            byte[] keyMaterial = null;
            Stream output = null;
            try
            {
                using (var fs = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BackupBufferBytes))
                {
                    if (fs.Length < EncryptedBackupMagic.Length + BackupHmacBytes)
                        throw new InvalidDataException("Not an encrypted Epic Switcher backup.");

                    var savedHmac = new byte[BackupHmacBytes];
                    fs.Seek(-BackupHmacBytes, SeekOrigin.End);
                    fs.ReadExactly(savedHmac, 0, BackupHmacBytes);
                    fs.Seek(0, SeekOrigin.Begin);

                    byte[] iv;
                    int cipherLen;
                    long cipherStart;
                    using (var reader = new BinaryReader(fs, Encoding.UTF8, true))
                    {
                        var magic = reader.ReadBytes(EncryptedBackupMagic.Length);
                        if (!magic.AsSpan().SequenceEqual(EncryptedBackupMagic)) throw new InvalidDataException("Not an encrypted Epic Switcher backup.");

                        var mode = reader.ReadInt32();
                        var iterations = reader.ReadInt32();
                        var salt = reader.ReadBytes(reader.ReadInt32());
                        var protectedKey = reader.ReadBytes(reader.ReadInt32());
                        iv = reader.ReadBytes(reader.ReadInt32());
                        cipherLen = reader.ReadInt32();
                        cipherStart = fs.Position;
                        if (cipherLen < 0 || cipherStart + cipherLen > fs.Length - BackupHmacBytes)
                            throw new InvalidDataException("Corrupted backup file cipher length.");

                        if (mode == EncryptedBackupPasswordMode)
                        {
                            if (string.IsNullOrWhiteSpace(password)) throw new BackupPasswordRequiredException();
                            keyMaterial = DeriveBackupKeyMaterial(password, salt, iterations);
                        }
                        else if (mode == EncryptedBackupCurrentUserMode)
                        {
                            keyMaterial = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
                        }
                        else
                        {
                            throw new InvalidDataException("Unsupported encrypted backup mode.");
                        }
                    }

                    fs.Seek(0, SeekOrigin.Begin);
                    var actualHmac = ComputeHmac(fs, keyMaterial, fs.Length - BackupHmacBytes, cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(savedHmac, actualHmac)) throw new BackupPasswordInvalidException();

                    fs.Seek(cipherStart, SeekOrigin.Begin);
                    output = createOutput(cipherLen);
                    using (var bounded = new BoundedStream(fs, cipherLen))
                    using (var aes = Aes.Create())
                    {
                        aes.Key = keyMaterial.AsSpan(0, BackupKeyBytes).ToArray();
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        using (var cs = new CryptoStream(bounded, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            var buffer = new byte[BackupBufferBytes];
                            int read;
                            while ((read = cs.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                            }
                        }
                    }
                    return output;
                }
            }
            catch (Exception ex)
            {
                output?.Dispose();
                if (ex is BackupPasswordRequiredException || ex is BackupPasswordInvalidException) throw;
                if (ex is CryptographicException || ex is EndOfStreamException) throw new BackupPasswordInvalidException();
                throw;
            }
            finally
            {
                if (keyMaterial != null) CryptographicOperations.ZeroMemory(keyMaterial);
            }
        }

        private static byte[] ComputeHmac(Stream stream, byte[] keyMaterial, long length, CancellationToken cancellationToken)
        {
            using (var hmac = new HMACSHA256(keyMaterial.AsSpan(BackupKeyBytes, BackupHmacBytes).ToArray()))
            {
                var buffer = new byte[BackupBufferBytes];
                var remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var r = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (r <= 0) throw new EndOfStreamException();
                    hmac.TransformBlock(buffer, 0, r, null, 0);
                    remaining -= r;
                }
                hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return hmac.Hash;
            }
        }

        private sealed class BoundedStream : Stream
        {
            private readonly Stream _base;
            private long _remaining;
            public BoundedStream(Stream baseStream, long length) { _base = baseStream; _remaining = length; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _remaining;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                var read = _base.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { } // never closes the underlying file
        }

        private static byte[] DeriveBackupKeyMaterial(string password, byte[] salt, int iterations = BackupPasswordIterations)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt ?? Array.Empty<byte>(), Math.Max(10000, iterations), HashAlgorithmName.SHA256, BackupKeyBytes + BackupHmacBytes);
        }

        private static void WriteLengthPrefixed(BinaryWriter writer, byte[] bytes)
        {
            bytes = bytes ?? Array.Empty<byte>();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static bool AddFileToArchive(ZipArchive archive, string source, string entryName)
        {
            if (!File.Exists(source)) return false;
            var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(source);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BackupBufferBytes))
            using (var target = entry.Open())
                input.CopyTo(target, BackupBufferBytes);
            return true;
        }

        /// <summary>Adds a folder tree (minus browser cache) under <paramref name="entryPrefix"/>. Returns the number of files added.</summary>
        private static int AddFolderToArchive(ZipArchive archive, string folder, string entryPrefix, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(folder)) return 0;
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            var count = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                foreach (var sub in SafeList(() => Directory.GetDirectories(dir)))
                    if (!CacheFilter.IsDisposable(sub)) pending.Push(sub);
                foreach (var file in SafeList(() => Directory.GetFiles(dir)))
                {
                    if (CacheFilter.IsDisposable(file)) continue;
                    var rel = file.Substring(root.Length).TrimStart('\\', '/');
                    try
                    {
                        if (AddFileToArchive(archive, file, entryPrefix + "/" + rel)) count++;
                    }
                    catch (IOException ex)
                    {
                        // A file Epic keeps locked (rare: Epic is normally closed) must not sink the whole backup.
                        Globals.WriteToLog("[BACKUP] Skipped unreadable file: " + file + " (" + ex.Message + ")");
                    }
                }
            }
            return count;
        }

        private static string[] SafeList(Func<string[]> list)
        {
            try { return list(); } catch { return Array.Empty<string>(); }
        }

        /// <summary>Adds every uploaded photo, so photos not in use by any profile also come back after a restore.</summary>
        private static int AddCustomSkinsToArchive(ZipArchive archive, Dictionary<string, string> included, CancellationToken cancellationToken)
        {
            var count = 0;
            foreach (var file in SafeList(() => Directory.Exists(SkinLibrary.CustomFolder) ? Directory.GetFiles(SkinLibrary.CustomFolder) : Array.Empty<string>()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SkinLibrary.IsImageFile(file)) continue;
                var entryName = BackupCustomSkinFolder + "/" + Path.GetFileName(file);
                if (AddFileToArchive(archive, file, entryName))
                {
                    included[Path.GetFullPath(file)] = entryName;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Makes an account's avatar portable: shipped avatars are stored by name only (every install has them);
        /// any other image is put into the backup under Skin_Icons/Custom.
        /// </summary>
        private static string MakePortableProfileImagePath(ZipArchive archive, Account account, Dictionary<string, string> included, Action<string> log)
        {
            var imagePath = account?.ProfileImagePath;
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;
            try
            {
                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;
                var builtIn = SkinLibrary.FindBuiltInFor(imagePath);
                if (builtIn != null) return Path.Combine(BackupSkinFolder, Path.GetFileName(builtIn));
                if (!File.Exists(imagePath)) return imagePath;

                var full = Path.GetFullPath(imagePath);
                if (!included.TryGetValue(full, out var entryName))
                {
                    entryName = BackupCustomSkinFolder + "/" + fileName;
                    if (included.ContainsValue(entryName))
                        entryName = BackupCustomSkinFolder + "/" + MakeSafeFileName(account.Id) + "_" + fileName;
                    AddFileToArchive(archive, full, entryName);
                    included[full] = entryName;
                }
                return entryName.Replace('/', Path.DirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                log?.Invoke("WARNING: Failed to preserve selected profile image for " + (account == null ? "unknown" : account.Name) + ": " + ex.Message);
            }
            return imagePath;
        }

        /// <summary>
        /// Copies avatars from an extracted backup into the user's avatar folder. Copies of shipped avatars (older backups
        /// carried all of them) are skipped: the installed app already has them.
        /// </summary>
        private static void RestoreBackupSkins(string extractedRoot)
        {
            try
            {
                var source = Path.Combine(extractedRoot, BackupSkinFolder);
                if (!Directory.Exists(source)) return;
                Directory.CreateDirectory(SkinLibrary.UserFolder);
                var sourceRoot = Path.GetFullPath(source).TrimEnd('\\');
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = file.Substring(sourceRoot.Length).TrimStart('\\');
                    var isTopLevel = !rel.Contains('\\');
                    if (isTopLevel && SkinLibrary.IsBuiltInName(rel)) continue;
                    var target = Path.Combine(SkinLibrary.UserFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                }
            }
            catch (Exception ex) { Globals.WriteToLog("[RESTORE] Could not restore avatars.", ex); }
        }

        private static string ResolveRestoredProfileImagePath(string imagePath, string extractedRoot)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;

            try
            {
                if (Path.IsPathRooted(imagePath) && File.Exists(imagePath)) return Path.GetFullPath(imagePath);

                if (!Path.IsPathRooted(imagePath))
                {
                    var restoredRelative = Path.Combine(Globals.UserDataFolder, imagePath);
                    if (File.Exists(restoredRelative)) return Path.GetFullPath(restoredRelative);
                }

                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;

                var builtIn = SkinLibrary.FindBuiltInFor(imagePath);
                if (builtIn != null) return builtIn;

                if (Directory.Exists(SkinLibrary.UserFolder))
                {
                    var restoredMatch = Directory.GetFiles(SkinLibrary.UserFolder, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(restoredMatch)) return Path.GetFullPath(restoredMatch);
                }
            }
            catch { }

            return imagePath;
        }

        private static List<Account> CloneAccounts(List<Account> accounts)
        {
            var clones = new List<Account>();
            foreach (var account in accounts ?? new List<Account>())
            {
                var clone = new Account(account.Id, account.Name)
                {
                    Email = account.Email,
                    Description = account.Description,
                    ProfileImagePath = account.ProfileImagePath,
                    AccentColor = account.AccentColor,
                    IsActive = account.IsActive,
                    RememberMeData = account.RememberMeData,
                    RememberMeDataByPath = account.RememberMeDataByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RememberMeDataByPath),
                    LauncherWindowSettings = account.LauncherWindowSettings,
                    LauncherWindowSettingsByPath = account.LauncherWindowSettingsByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.LauncherWindowSettingsByPath),
                    Credentials = account.Credentials == null ? new List<CredentialService.SavedCredential>() : account.Credentials.Select(c => new CredentialService.SavedCredential
                    {
                        TargetName = c.TargetName,
                        UserName = c.UserName,
                        Blob = c.Blob == null ? null : c.Blob.ToArray(),
                        Persist = c.Persist,
                        Type = c.Type,
                        AccountId = c.AccountId,
                        CapturedAtUtc = c.CapturedAtUtc
                    }).ToList(),
                    RegistryData = account.RegistryData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RegistryData),
                    EncryptedPassword = account.EncryptedPassword,
                    LastPlayed = account.LastPlayed,
                    UsageCount = account.UsageCount
                };
                clones.Add(clone);
            }

            return clones;
        }

        private static string MakeSafeFileName(string value)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? "account" : value;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return safe;
        }

        private static string GetSafeExtractionPath(string extractionRoot, string entryKey)
        {
            if (string.IsNullOrWhiteSpace(entryKey)) throw new InvalidDataException("Backup contains an empty entry name.");

            var root = Path.GetFullPath(extractionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entryPath = entryKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, entryPath));

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path: " + entryKey);

            return fullPath;
        }
    }
}
