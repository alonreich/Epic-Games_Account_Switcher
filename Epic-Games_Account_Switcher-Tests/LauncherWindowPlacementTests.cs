using Epic_Games_Account_Switcher_Globals;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Epic_Games_Account_Switcher_Tests
{
    [TestClass]
    public class LauncherWindowPlacementTests
    {
        // Shape of the value Epic Games Launcher writes to [WindowSettings] MainLauncherWindow.
        private const string Stored = "WindowRect=\"Left=-1900.000 Top=49.000 Right=-130.000 Bottom=939.000\",ScreenRect=\"Left=-1920.000 Top=0.000 Right=1920.000 Bottom=1080.000\",ScreenDPI=1.000000,IsMaximised=\"false\",IsMinimised=\"true\",CurrentWindowSize=\"X=1770.000 Y=890.000\"";

        [TestMethod]
        public void Apply_WritesTheLiveRectangleAndSize()
        {
            var live = new LauncherWindowPlacement.Snapshot { Left = 100, Top = 80, Right = 1380, Bottom = 800 };
            var result = LauncherWindowPlacement.Apply(Stored, live);

            StringAssert.Contains(result, "WindowRect=\"Left=100.000 Top=80.000 Right=1380.000 Bottom=800.000\"");
            StringAssert.Contains(result, "CurrentWindowSize=\"X=1280.000 Y=720.000\"");
            StringAssert.Contains(result, "IsMaximised=\"false\"");
            StringAssert.Contains(result, "IsMinimised=\"false\"");
            StringAssert.Contains(result, "ScreenRect=\"Left=-1920.000 Top=0.000 Right=1920.000 Bottom=1080.000\"", "The screen rectangle must be left alone.");
        }

        [TestMethod]
        public void Apply_Maximized_KeepsTheRestoreRectangle()
        {
            var live = new LauncherWindowPlacement.Snapshot { Left = 0, Top = 0, Right = 1920, Bottom = 1032, Maximized = true };
            var result = LauncherWindowPlacement.Apply(Stored, live);

            StringAssert.Contains(result, "WindowRect=\"Left=-1900.000 Top=49.000 Right=-130.000 Bottom=939.000\"");
            StringAssert.Contains(result, "IsMaximised=\"true\"");
        }

        [TestMethod]
        public void Apply_LeavesUncertainValuesUntouched()
        {
            var live = new LauncherWindowPlacement.Snapshot { Left = 100, Top = 80, Right = 1380, Bottom = 800 };

            Assert.AreEqual(Stored, LauncherWindowPlacement.Apply(Stored, null), "No live window: keep the stored value.");
            Assert.AreEqual("SomethingElse=1", LauncherWindowPlacement.Apply("SomethingElse=1", live), "Unknown shape: keep it.");

            var scaled = Stored.Replace("ScreenDPI=1.000000", "ScreenDPI=1.250000");
            Assert.AreEqual(scaled, LauncherWindowPlacement.Apply(scaled, live), "Scaled display: units are not plain pixels, keep it.");

            var tiny = new LauncherWindowPlacement.Snapshot { Left = 0, Top = 0, Right = 200, Bottom = 100 };
            Assert.AreEqual(Stored, LauncherWindowPlacement.Apply(Stored, tiny), "A tiny window is not the launcher's main window.");
        }
    }
}
