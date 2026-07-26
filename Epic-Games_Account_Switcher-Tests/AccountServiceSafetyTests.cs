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
