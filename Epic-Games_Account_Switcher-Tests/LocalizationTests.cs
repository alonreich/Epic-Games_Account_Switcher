using Epic_Games_Account_Switcher_Globals;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Epic_Games_Account_Switcher_Tests
{
    [TestClass]
    public class LocalizationTests
    {
        [TestMethod]
        public void T_WithoutTranslation_ReturnsTheSourceText()
        {
            Assert.AreEqual("A text that is in no resource file.", L.T("A text that is in no resource file."));
            Assert.AreEqual("", L.T(null));
        }

        [TestMethod]
        public void T_FillsPlaceholders()
        {
            Assert.AreEqual("Switch to Main", L.T("Switch to {0}", "Main"));
            Assert.AreEqual("3 saved accounts", L.T("{0} saved accounts", 3));
        }

        [TestMethod]
        public void T_KeepsTheCaseOfTheSourceText()
        {
            // "Later" and "LATER" share one resource entry (resource names are case-insensitive).
            Assert.AreEqual("LATER", L.T("LATER"));
            Assert.AreEqual("Later", L.T("Later"));
        }

        [TestMethod]
        public void AccountLabels_DescribeStateInWords()
        {
            var account = new Account("id-1", "Main");
            Assert.AreEqual("Main", account.AccessibleName);
            Assert.AreEqual("Switch to Main", account.SwitchActionName);
            Assert.AreEqual("Not used yet", account.LastUsedLine);
            Assert.IsFalse(account.HasSavedPassword);
            Assert.AreEqual("Main", account.ToString());

            account.IsActive = true;
            Assert.AreEqual("Main, active now", account.AccessibleName);
            Assert.AreEqual("Main, active account", account.SwitchActionName);

            account.Password = "secret";
            Assert.IsTrue(account.HasSavedPassword);
            Assert.AreEqual("secret", account.Password);
        }
    }
}
