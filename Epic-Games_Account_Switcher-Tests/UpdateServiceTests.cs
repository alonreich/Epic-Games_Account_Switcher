using Epic_Games_Account_Switcher_Globals;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Epic_Games_Account_Switcher_Tests
{
    [TestClass]
    public class UpdateServiceTests
    {
        private const string A = "0123456789ABCDEF0123456789ABCDEF01234567";
        private const string B = "89abcdef0123456789abcdef0123456789abcdef";

        [TestMethod]
        public void ParsesSeveralTrustedSigners()
        {
            var list = UpdateService.ParseThumbprints(A + ";" + B + "\r\n" + A);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(A, list[0]);
            Assert.AreEqual(B.ToUpperInvariant(), list[1]);
        }

        [TestMethod]
        public void IgnoresEmptyAndMalformedThumbprints()
        {
            Assert.AreEqual(0, UpdateService.ParseThumbprints(null).Count);
            Assert.AreEqual(0, UpdateService.ParseThumbprints("  ;  ").Count);
            Assert.AreEqual(1, UpdateService.ParseThumbprints("ABC;" + A).Count);
        }
    }
}
