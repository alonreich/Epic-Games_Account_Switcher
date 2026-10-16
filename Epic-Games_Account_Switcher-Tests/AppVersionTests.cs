using Epic_Games_Account_Switcher_Globals;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Epic_Games_Account_Switcher_Tests
{
    [TestClass]
    public class AppVersionTests
    {
        [DataTestMethod]
        [DataRow("3.0.1", "3.0.0", 1)]
        [DataRow("v3.0.0", "3.0.0", 0)]
        [DataRow("3.0.0", "3.0.0-beta.1", 1)]
        [DataRow("3.0.0-beta.2", "3.0.0-beta.10", -1)]
        [DataRow("3.10.0", "3.9.9", 1)]
        [DataRow("2026.08.05.1", "3.0.0", -1)]
        [DataRow("v2026.04.01.1310", "2026.08.05.1", -1)]
        [DataRow("garbage", "3.0.0", -1)]
        public void Compare_OrdersVersionsCorrectly(string a, string b, int expectedSign)
        {
            var result = AppVersion.Compare(a, b);
            Assert.AreEqual(expectedSign, System.Math.Sign(result), a + " vs " + b);
        }

        [TestMethod]
        public void LegacyCalendarVersions_AreAlwaysOlderThanSemVer()
        {
            Assert.IsTrue(AppVersion.IsNewer("3.0.0", "2026.12.31.9999"));
            Assert.IsFalse(AppVersion.IsNewer("2026.12.31.9999", "3.0.0"));
        }

        [TestMethod]
        public void CurrentVersion_IsValidSemVer()
        {
            Assert.IsTrue(AppVersion.IsValid(Globals.Version), "Globals.Version must come from Directory.Build.props: " + Globals.Version);
            Assert.IsFalse(Globals.Version.StartsWith("20"), "Calendar version leaked into the build: " + Globals.Version);
        }
    }
}
