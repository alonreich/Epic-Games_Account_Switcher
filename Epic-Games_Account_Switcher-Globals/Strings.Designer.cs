
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Epic_Games_Account_Switcher_Globals {

    [GeneratedCode("System.Resources.Tools.StronglyTypedResourceBuilder", "16.0.0.0")]
    [DebuggerNonUserCode()]
    [CompilerGenerated()]
    internal class Strings {

        private static ResourceManager resourceMan;

        private static CultureInfo resourceCulture;

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal Strings() {
        }

        [EditorBrowsable(EditorBrowsableState.Advanced)]
        internal static ResourceManager ResourceManager {
            get {
                if (ReferenceEquals(resourceMan, null)) {
                    ResourceManager temp = new ResourceManager("Epic_Games_Account_Switcher_Globals.Strings", typeof(Strings).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Advanced)]
        internal static CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }

        internal static string ErrSubmitCrashlog {
            get {
                return ResourceManager.GetString("ErrSubmitCrashlog", resourceCulture);
            }
        }

        internal static string ErrUnhandledCrash {
            get {
                return ResourceManager.GetString("ErrUnhandledCrash", resourceCulture);
            }
        }

        internal static string ErrUnhandledException {
            get {
                return ResourceManager.GetString("ErrUnhandledException", resourceCulture);
            }
        }

        internal static string ErrUnhandledExceptionHeader {
            get {
                return ResourceManager.GetString("ErrUnhandledExceptionHeader", resourceCulture);
            }
        }

        internal static string SteamSwitcherFuncs_PrepareProfileImages_ {
            get {
                return ResourceManager.GetString("SteamSwitcherFuncs_PrepareProfileImages_", resourceCulture);
            }
        }
    }
}

