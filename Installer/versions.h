

















#pragma once
#include <string>

inline std::string required_min_webview = "98.0",
                   required_min_desktop_runtime = "8.0.0",
                   required_min_aspcore = "8.0.0",
                   required_min_vc = "14.30.30704";

const std::string w_runtime_name = "Microsoft WebView2 Runtime",
	w_runtime = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
	w_exe = "MicrosoftEdgeWebview2Setup.exe",

	d_runtime_name = "Microsoft .NET 8.0.22 Desktop Runtime",
	d_runtime = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.22/windowsdesktop-runtime-8.0.22-win-x64.exe",
	d_exe = "windowsdesktop-runtime-8.0.22-win-x64.exe",

	a_runtime_name = "Microsoft ASP.NET Core 8.0.22 Runtime",
	a_runtime = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/8.0.22/aspnetcore-runtime-8.0.22-win-x64.exe",
	a_exe = "aspnetcore-runtime-8.0.22-win-x64.exe",

	c_runtime = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
	c_runtime_name = "C++ Redistributable 2015-2022",
	c_exe = "VC_redist.x64.exe";