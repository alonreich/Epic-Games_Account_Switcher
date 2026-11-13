using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Epic_Games_Account_Switcher_Globals;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Epic_Games_Account_Switcher_Tests
{
    [TestClass]
    public class AccountServiceSafetyTests
    {
        private static string _root;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _root = Path.Combine(Path.GetTempPath(), "egas_tests_" + Guid.NewGuid().ToString("N"));
            Globals.UserDataFolderOverride = Path.Combine(_root, "user");
            Globals.LogFolderOverride = Path.Combine(_root, "logs");
            Directory.CreateDirectory(Globals.UserDataFolder);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Globals.UserDataFolderOverride = null;
            Globals.LogFolderOverride = null;
            TryDeleteDirectory(_root);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            TryDeleteDirectory(Globals.UserDataFolder);
            Directory.CreateDirectory(Globals.UserDataFolder);
        }

        [TestMethod]
        public void PasswordEncryptedBackupRoundTripsAndRejectsWrongPassword()
        {
            AccountService.SaveAccounts(new List<Account>
            {
                new Account("account123", "Main Account") { Email = "main@example.test", Description = "test profile" }
            });

            var backup = Path.Combine(_root, "accounts.egbackup");
            AccountService.BackupAccounts(backup, "correct horse battery staple", null, CancellationToken.None);

            Assert.IsTrue(AccountService.IsEncryptedBackup(backup));
            CollectionAssert.AreNotEqual(new byte[] { (byte)'P', (byte)'K' }, ReadPrefix(backup, 2));
            Assert.ThrowsException<AccountService.BackupPasswordInvalidException>(() =>
                AccountService.RestoreAccounts(backup, "wrong password", CancellationToken.None));

            TryDeleteDirectory(Path.Combine(Globals.UserDataFolder, "EG"));
            AccountService.RestoreAccounts(backup, "correct horse battery staple", CancellationToken.None);

            var restored = AccountService.GetAccounts();
            Assert.AreEqual(1, restored.Count);
            Assert.AreEqual("account123", restored[0].Id);
        }

        [TestMethod]
        public void LegacyPlainZipRestoreStillWorks()
        {
            var backup = Path.Combine(_root, "legacy.zip");
            CreatePlainAccountsZip(backup, new List<Account> { new Account("legacy123", "Legacy") });

            AccountService.RestoreAccounts(backup, CancellationToken.None);

            var restored = AccountService.GetAccounts();
            Assert.AreEqual(1, restored.Count);
            Assert.AreEqual("legacy123", restored[0].Id);
        }

        [TestMethod]
        public void RestoreRejectsZipTraversalEntries()
        {
            var backup = Path.Combine(_root, "unsafe.zip");
            using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
            {
                var accounts = archive.CreateEntry("accounts.json");
                using (var writer = new StreamWriter(accounts.Open()))
                {
                    writer.Write("[]");
                }
                archive.CreateEntry("../evil.txt");
            }

            Assert.ThrowsException<IOException>(() => AccountService.RestoreAccounts(backup, CancellationToken.None));
        }

        [TestMethod]
        public void ModernAuthBlobValidatesByExpectedAccountId()
        {
            var sessionRoot = Path.Combine(_root, "session");
            var dataRoot = Path.Combine(sessionRoot, "Data");
            Directory.CreateDirectory(dataRoot);
            var blob = Path.Combine(dataRoot, "OC_account123.dat");
            File.WriteAllText(blob, "auth");
            File.SetLastWriteTimeUtc(blob, DateTime.UtcNow);

            var result = InvokeValidateSession(sessionRoot, "account123");

            Assert.IsTrue((bool)result.GetType().GetProperty("IsValid").GetValue(result, null));
            Assert.AreEqual("account123", result.GetType().GetProperty("AccountId").GetValue(result, null));
        }

        [TestMethod]
        public void SafeMovePathAllowsReadOnlySourceFiles()
        {
            var source = Path.Combine(_root, "move_source");
            var target = Path.Combine(_root, "move_target");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "read_only.txt");
            File.WriteAllText(file, "locked probe regression");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            InvokeSafeMovePath(source, target);

            Assert.IsFalse(Directory.Exists(source));
            Assert.IsTrue(File.Exists(Path.Combine(target, "read_only.txt")));
            File.SetAttributes(Path.Combine(target, "read_only.txt"), FileAttributes.Normal);
        }

        [TestMethod]
        public void CacheFilterOnlyMatchesBrowserCache()
        {
            Assert.IsTrue(CacheFilter.IsDisposable(@"C:\EG\a1\WebCaches\webcache_4430\Cache\data_1"));
            Assert.IsTrue(CacheFilter.IsDisposable(@"C:\EG\a1\EOS_Data\EOSOverlay\BrowserCache\0\Default\Service Worker\CacheStorage"));
            Assert.IsTrue(CacheFilter.IsDisposable(@"C:\EG\a1\EOS_Data\EOSOverlay\BrowserCache\0\BrowserMetrics-spare.pma"));
            Assert.IsFalse(CacheFilter.IsDisposable(@"C:\EG\a1\WebCaches\webcache_4430\Cookies"));
            Assert.IsFalse(CacheFilter.IsDisposable(@"C:\EG\a1\EOS_Data\EOSOverlay\BrowserCache\0\Default\Local Storage\leveldb\0003.log"));
            Assert.IsFalse(CacheFilter.IsDisposable(@"C:\EG\a1\Config\Cache\settings.ini"));
        }

        [TestMethod]
        public void BackupSkipsBrowserCacheAndRoundTripsUploadedPhoto()
        {
            var profile = Path.Combine(Globals.UserDataFolder, "EG", "acc42");
            var cache = Path.Combine(profile, "EOS_Data", "EOSOverlay", "BrowserCache", "0", "Default", "Cache");
            var cookies = Path.Combine(profile, "WebCaches", "webcache_1", "Cookies");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(cookies));
            File.WriteAllBytes(Path.Combine(cache, "big.bin"), new byte[4 * 1024 * 1024]);
            File.WriteAllText(cookies, "cookie");
            var photo = SkinLibrary.SaveCustom(new byte[] { 1, 2, 3, 4 }, "My Photo");
            AccountService.SaveAccounts(new List<Account> { new Account("acc42", "Photo Account") { ProfileImagePath = photo } });

            var backup = Path.Combine(_root, "small.egbackup");
            AccountService.BackupAccounts(backup, null, CancellationToken.None);
            Assert.IsTrue(new FileInfo(backup).Length < 64 * 1024, "browser cache must not be in the backup");
            Assert.AreEqual(1, AccountService.VerifyBackupFile(backup));

            TryDeleteDirectory(Globals.UserDataFolder);
            Directory.CreateDirectory(Globals.UserDataFolder);
            AccountService.RestoreAccounts(backup, CancellationToken.None);

            var restored = AccountService.GetAccounts();
            Assert.AreEqual(1, restored.Count);
            Assert.IsTrue(SkinLibrary.IsCustom(restored[0].ProfileImagePath), restored[0].ProfileImagePath);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(restored[0].ProfileImagePath));
            Assert.IsTrue(File.Exists(Path.Combine(Globals.UserDataFolder, "EG", "acc42", "WebCaches", "webcache_1", "Cookies")));
            Assert.IsFalse(Directory.Exists(Path.Combine(Globals.UserDataFolder, "EG", "acc42", "EOS_Data", "EOSOverlay", "BrowserCache", "0", "Default", "Cache")));
            Assert.IsFalse(Directory.Exists(Path.Combine(Globals.UserDataFolder, "EG", "Skin_Icons")), "avatars must not land inside the account folder");
        }

        [TestMethod]
        public void LegacyPerAccountAvatarCopyMapsToShippedAvatar()
        {
            Directory.CreateDirectory(SkinLibrary.BuiltInFolder);
            var shipped = Path.Combine(SkinLibrary.BuiltInFolder, "Test_Shipped_Skin.jpg");
            File.WriteAllBytes(shipped, new byte[] { 9 });
            try
            {
                var legacy = Path.Combine(Globals.UserDataFolder, "EG", "Skin_Icons", "Selected", "0123456789abcdef0123456789abcdef_0123456789abcdef0123456789abcdef_Test_Shipped_Skin.jpg");
                Assert.AreEqual(shipped, SkinLibrary.FindBuiltInFor(legacy));
                Assert.IsNull(SkinLibrary.FindBuiltInFor(Path.Combine(SkinLibrary.CustomFolder, "Test_Shipped_Skin.jpg")));
            }
            finally { File.Delete(shipped); }
        }

        private static object InvokeValidateSession(string folder, string expectedAccountId)
        {
            var method = typeof(AccountService).GetMethod("ValidateSession", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return method.Invoke(null, new object[] { folder, expectedAccountId });
        }

        private static void InvokeSafeMovePath(string source, string target)
        {
            var method = typeof(AccountService).GetMethod("SafeMovePathAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            var task = (Task)method.Invoke(null, new object[] { source, target, CancellationToken.None });
            task.GetAwaiter().GetResult();
        }

        private static void CreatePlainAccountsZip(string path, List<Account> accounts)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("accounts.json");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write(JsonConvert.SerializeObject(accounts));
                }
            }
        }

        private static byte[] ReadPrefix(string path, int length)
        {
            var buffer = new byte[length];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Read(buffer, 0, length);
            }
            return buffer;
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
