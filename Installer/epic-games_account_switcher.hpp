#pragma once

#pragma comment(lib, "urlmon.lib")
#pragma comment (lib, "User32.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "secur32.lib")
#define CRT_SECURE_NO_WARNINGS
#include <algorithm>
#include <chrono>
#include <iostream>
#include <memory>
#include <Windows.h>
#include <conio.h>

#include <iostream>
#include <string>
#include <tchar.h>
#include <urlmon.h>
#include "progress_bar.hpp"

#include "runtime_check.hpp"
#include "versions.h"
using namespace std;

class DownloadCallback : public IBindStatusCallback {
private:
    ProgressBar* m_bar;
    unsigned long m_last_progress = 0;
public:
    DownloadCallback(ProgressBar* bar) : m_bar(bar) {}

    STDMETHOD(OnProgress)(ULONG ulProgress, ULONG ulProgressMax, ULONG ulStatusCode, LPCWSTR szStatusText) {
        if (ulProgressMax > 0 && m_bar) {
            unsigned long percentage = static_cast<unsigned long>((static_cast<double>(ulProgress) * 100.0) / static_cast<double>(ulProgressMax));
            if (percentage != m_last_progress) {
                m_bar->Progressed(percentage);
                m_last_progress = percentage;
            }
        }
        return S_OK;
    }

    STDMETHOD(OnStartBinding)(DWORD dwReserved, IBinding* pib) { return S_OK; }
    STDMETHOD(GetPriority)(LONG* pnPriority) { return S_OK; }
    STDMETHOD(OnLowResource)(DWORD reserved) { return S_OK; }
    STDMETHOD(OnStopBinding)(HRESULT hresult, LPCWSTR szError) { return S_OK; }
    STDMETHOD(GetBindInfo)(DWORD* grfBINDF, BINDINFO* pbindinfo) { return S_OK; }
    STDMETHOD(OnDataAvailable)(DWORD grfBSCF, DWORD dwSize, FORMATETC* pformatetc, STGMEDIUM* pstgmed) { return S_OK; }
    STDMETHOD(OnObjectAvailable)(REFIID riid, IUnknown* punk) { return S_OK; }
    STDMETHOD_(ULONG, AddRef)() { return 1; }
    STDMETHOD_(ULONG, Release)() { return 1; }
    STDMETHOD(QueryInterface)(REFIID riid, void** ppvObject) {
        if (riid == IID_IUnknown || riid == IID_IBindStatusCallback) {
            *ppvObject = static_cast<IBindStatusCallback*>(this);
            return S_OK;
        }
        return E_NOINTERFACE;
    }
};

inline bool min_vc_met = false,
	min_webview_met = false,
	min_desktop_runtime_met = false,
	min_aspcore_met = false;

#pragma region Misc Functions
inline void insert_empty_line()
{
	CONSOLE_SCREEN_BUFFER_INFO buffer_info;
	GetConsoleScreenBufferInfo(GetStdHandle(STD_OUTPUT_HANDLE), &buffer_info);

	const int width = buffer_info.srWindow.Right - buffer_info.srWindow.Left;
	const std::string s(static_cast<size_t>(width), ' ');
	std::cout << s << '\r';
}

inline double round_off(const double n) {
	double d = n * 100.0;
	const int i = static_cast<int>((double)0.5 + d);
	d = static_cast<double>(i) / 100.0;
	return d;
}

inline void wait_for_input()
{
	_getch();
}
#pragma endregion

#pragma region File Operations
inline string current_download;
inline chrono::time_point<chrono::system_clock> last_time = std::chrono::system_clock::now();

inline string convert_size(size_t size) {
	static const char* sizes[] = { "B", "KB", "MB", "GB" };
	int div = 0;
	size_t rem = 0;

	while (size >= 1024 && div < std::size(sizes)) {
		rem = (size % 1024);
		div++;
		size /= 1024;
	}

	const double size_d = static_cast<float>(size) + static_cast<float>(rem) / 1024.0;
	string result = to_string(round_off(size_d)) + " " + sizes[div];
	return result;
}

inline bool download_file(const char* url, const char* dest) {
    ProgressBar bar(100, "Downloading", cout); 
    DownloadCallback callback(&bar);
    
    HRESULT hr = URLDownloadToFileA(NULL, url, dest, 0, &callback);
    
    insert_empty_line();
    if (SUCCEEDED(hr)) {
        cout << " Finished downloading " << current_download << endl;
        return true;
    }
    return false;
}

inline bool file_exists(const std::string& name) {
	struct stat buffer{};
	return stat(name.c_str(), &buffer) == 0;
}
#pragma endregion

#pragma region Runtime Operations
inline bool is_winget_available()
{
	STARTUPINFO si = { sizeof(STARTUPINFO) };
	si.cb = sizeof(STARTUPINFO);
	si.dwFlags = STARTF_USESTDHANDLES;
	si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
	si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
	PROCESS_INFORMATION pi;
	
	wstring cmd_line = L"cmd.exe /c winget --version";
	wchar_t* cmd_line_writable = new wchar_t[cmd_line.length() + 1];
	wcscpy_s(cmd_line_writable, cmd_line.length() + 1, cmd_line.c_str());
	
	bool success = CreateProcessW(nullptr, cmd_line_writable, nullptr, nullptr, FALSE, 
		CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi);
	
	delete[] cmd_line_writable;
	
	if (success)
	{
		WaitForSingleObject(pi.hProcess, 5000); 
		DWORD exit_code;
		GetExitCodeProcess(pi.hProcess, &exit_code);
		CloseHandle(pi.hProcess);
		CloseHandle(pi.hThread);
		return exit_code == 0;
	}
	return false;
}

inline bool is_winget_package_installed(const string& package_id)
{
	STARTUPINFO si = { sizeof(STARTUPINFO) };
	si.cb = sizeof(STARTUPINFO);
	si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
	si.wShowWindow = SW_HIDE;
	HANDLE h_read, h_write;
	SECURITY_ATTRIBUTES sa = { sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE };
	CreatePipe(&h_read, &h_write, &sa, 0);
	si.hStdOutput = h_write;
	si.hStdError = h_write;
	PROCESS_INFORMATION pi;
	
	wstring package_id_w = s2_ws(package_id);
	wstring cmd_line = L"cmd.exe /c winget list --id \"" + package_id_w + L"\" --accept-source-agreements --accept-package-agreements";
	wchar_t* cmd_line_writable = new wchar_t[cmd_line.length() + 1];
	wcscpy_s(cmd_line_writable, cmd_line.length() + 1, cmd_line.c_str());
	
	bool installed = false;
	if (CreateProcessW(nullptr, cmd_line_writable, nullptr, nullptr, TRUE, 
		CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi))
	{
		CloseHandle(h_write);
		WaitForSingleObject(pi.hProcess, 10000); 
		
		char buffer[4096];
		DWORD bytes_read;
		string output;
		while (ReadFile(h_read, buffer, sizeof(buffer) - 1, &bytes_read, nullptr) && bytes_read > 0)
		{
			buffer[bytes_read] = '\0';
			output += buffer;
		}
		
		DWORD exit_code;
		GetExitCodeProcess(pi.hProcess, &exit_code);
		installed = (exit_code == 0) && (output.find(package_id) != string::npos);
		
		CloseHandle(h_read);
		CloseHandle(pi.hProcess);
		CloseHandle(pi.hThread);
	}
	else
	{
		CloseHandle(h_read);
		CloseHandle(h_write);
	}
	
	delete[] cmd_line_writable;
	return installed;
}

inline bool install_winget_package(const string& package_id, const string& display_name)
{
	cout << "Installing " << display_name << " via winget..." << endl;
	
	STARTUPINFO si = { sizeof(STARTUPINFO) };
	si.cb = sizeof(STARTUPINFO);
	si.dwFlags = STARTF_USESTDHANDLES;
	si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
	si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
	PROCESS_INFORMATION pi;
	
	wstring package_id_w = s2_ws(package_id);
	wstring cmd_line = L"cmd.exe /c winget install \"" + package_id_w + L"\" --accept-source-agreements --accept-package-agreements --silent";
	wchar_t* cmd_line_writable = new wchar_t[cmd_line.length() + 1];
	wcscpy_s(cmd_line_writable, cmd_line.length() + 1, cmd_line.c_str());
	
	bool success = CreateProcessW(nullptr, cmd_line_writable, nullptr, nullptr, FALSE, 
		0, nullptr, nullptr, &si, &pi);
	
	delete[] cmd_line_writable;
	
	if (success)
	{
		WaitForSingleObject(pi.hProcess, INFINITE);
		DWORD exit_code;
		GetExitCodeProcess(pi.hProcess, &exit_code);
		CloseHandle(pi.hProcess);
		CloseHandle(pi.hThread);
		
		if (exit_code == 0)
		{
			cout << "Successfully installed " << display_name << " via winget." << endl;
			return true;
		}
		else
		{
			cout << "Failed to install " << display_name << " via winget (exit code: " << exit_code << ")." << endl;
			return false;
		}
	}
	
	cout << "Failed to launch winget for " << display_name << "." << endl;
	return false;
}

inline void install_runtime(const string& path, const string& name, const bool& passive)
{
	cout << "Installing: " << name << endl;
	STARTUPINFO si = { sizeof(STARTUPINFO) };
	PROCESS_INFORMATION pi;

	wstring args;
	if (passive) args = L"/install /passive /norestart";

	LPWSTR cmd = args.empty() ? nullptr : args.data();
	if (!CreateProcessW(s2_ws(path).c_str(), cmd, nullptr,
		nullptr, 0, 0, nullptr, nullptr, &si, &pi))
	{
		cout << "Failed to start installer process for: " << name << endl;
		return;
	}

	WaitForSingleObject(pi.hProcess, INFINITE);
	CloseHandle(pi.hThread);
	CloseHandle(pi.hProcess);
}

inline void download_install_missing_runtimes()
{
	const string operating_path = get_operating_path();

	if (!min_vc_met || !min_webview_met || !min_desktop_runtime_met || !min_aspcore_met)
	{
		cout << "One or more runtimes were not installed:" << endl;
		int total = 0;
		if (!min_webview_met) {
			cout << " + Microsoft WebView2 Runtime [~1,70 MB + ~120 MB while installing]" << endl;
			total += 122;
		}
		if (!min_aspcore_met) {
			cout << " + Microsoft Microsoft ASP.NET Core 8.0 Runtime [~10,1 MB]" << endl;
			total += 8;
		}
		if (!min_desktop_runtime_met) {
			cout << " + Microsoft .NET 8.0 Desktop Runtime [~57,9 MB]" << endl;
			total += 52;
		}
		if (!min_vc_met) {
			cout << " + C++ Redistributable 2015-2022 [~24 MB]" << endl;
			total += 24;
		}

		cout << " = Total download size: ~" << total << " MB" << endl << endl <<
			"Press any key to start download..." << endl;

		wait_for_input();

		bool use_winget = is_winget_available();
		if (use_winget)
		{
			cout << "winget detected. Using winget to install missing packages..." << endl << endl;
		}
		else
		{
			cout << "winget not available. Falling back to direct download..." << endl << endl;
		}

		bool w_runtime_install = false,
			d_runtime_install = false,
			a_runtime_install = false,
			c_runtime_install = false;
		const string runtime_folder = operating_path + "runtime_installers\\";
		const string w_runtime_local = runtime_folder + w_exe,
			d_runtime_local = runtime_folder + d_exe,
			a_runtime_local = runtime_folder + a_exe,
			c_runtime_local = runtime_folder + c_exe;

		if (!use_winget)
		{
			if (!(CreateDirectoryA(runtime_folder.c_str(), nullptr) || ERROR_ALREADY_EXISTS == GetLastError()))
			{
				cout << "Failed to create folder: " << runtime_folder << endl;
				cout << "Press any key to exit..." << endl;
				_getch();
				return;
			}
		}

		if (!min_webview_met)
		{
			if (!use_winget)
			{
				current_download = w_runtime_name;
				if (!download_file(w_runtime.c_str(), w_runtime_local.c_str()))
				{
					cout << "Failed to download and install Microsoft WebView2 Runtime. To download: 1. Click the link below:" << endl <<
						"https://go.microsoft.com/fwlink/p/?LinkId=2124703" << endl;
				}
				else w_runtime_install = true;
			}
		}

		if (!min_desktop_runtime_met)
		{
			if (use_winget)
			{
				if (!is_winget_package_installed("Microsoft.DotNet.DesktopRuntime.8"))
				{
					d_runtime_install = install_winget_package("Microsoft.DotNet.DesktopRuntime.8", d_runtime_name);
				}
				else
				{
					cout << d_runtime_name << " is already installed via winget." << endl;
					d_runtime_install = true;
				}
			}
			else
			{
				current_download = d_runtime_name;
				if (!download_file(d_runtime.c_str(), d_runtime_local.c_str()))
				{
					cout << "Failed to download and install .NET 8.0 Desktop Runtime. Please download it here:" << endl <<
						"https://dotnet.microsoft.com/download/dotnet/8.0/runtime" << endl << endl;
				}
				else d_runtime_install = true;
			}
		}

		if (!min_aspcore_met)
		{
			if (use_winget)
			{
				if (!is_winget_package_installed("Microsoft.DotNet.AspNetCore.8"))
				{
					a_runtime_install = install_winget_package("Microsoft.DotNet.AspNetCore.8", a_runtime_name);
				}
				else
				{
					cout << a_runtime_name << " is already installed via winget." << endl;
					a_runtime_install = true;
				}
			}
			else
			{
				current_download = a_runtime_name;
				if (!download_file(a_runtime.c_str(), a_runtime_local.c_str()))
				{
					cout << "Failed to download and install ASP.NET Core 8.0 Runtime. To download: 1. Click the link below:" << endl <<
						"https://dotnet.microsoft.com/en-us/download/dotnet/8.0" << endl;
				}
				else a_runtime_install = true;
			}
		}

		if (!min_vc_met)
		{
			if (use_winget)
			{
				bool vc_x64_installed = is_winget_package_installed("Microsoft.VCRedist.2015+.x64");
				bool vc_x86_installed = is_winget_package_installed("Microsoft.VCRedist.2015+.x86");
				
				if (!vc_x64_installed)
				{
					if (install_winget_package("Microsoft.VCRedist.2015+.x64", "C++ Redistributable 2015-2022 (x64)"))
					{
						c_runtime_install = true;
					}
				}
				
				if (!vc_x86_installed)
				{
					if (install_winget_package("Microsoft.VCRedist.2015+.x86", "C++ Redistributable 2015-2022 (x86)"))
					{
						c_runtime_install = true;
					}
				}
				
				if (vc_x64_installed || vc_x86_installed || c_runtime_install)
				{
					c_runtime_install = true;
				}
			}
			else
			{
				current_download = c_runtime_name;
				if (!download_file(c_runtime.c_str(), c_runtime_local.c_str()))
				{
					cout << "Failed to download and install C++ Redistributable 2015-2022. To download: 1. Click the link below:" << endl <<
						"https://docs.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170" << endl;
				}
				else c_runtime_install = true;
			}
		}

		if (!use_winget && (w_runtime_install || d_runtime_install || a_runtime_install || c_runtime_install))
		{
			cout << "------------------------------------------------------------------------" << endl << endl <<
				"One or more are ready for install. If and when prompted if you would like to install, click 'Yes'." << endl << endl;

			if (w_runtime_install)
				install_runtime(w_runtime_local, w_runtime_name, false); 
			if (d_runtime_install)
				install_runtime(d_runtime_local, d_runtime_name, true);
			if (a_runtime_install)
				install_runtime(a_runtime_local, a_runtime_name, true);
			if (c_runtime_install)
				install_runtime(c_runtime_local, c_runtime_name, true);
		}

		system("cls");
		cout << "Currently installed runtimes:" << endl;
		find_installed_net_runtimes(false, min_webview_met, min_desktop_runtime_met, min_aspcore_met);
		find_installed_c_runtimes(min_vc_met);
		cout << "------------------------------------------------------------------------" << endl << endl <<
			"Verify you meet the minimum recommended requirements:" << endl;
	}
	else
	{
		cout << "It looks like everything is installed. Verify you meet the minimum recommended requirements:" << endl;
	}

	cout << " - Windows Desktop Runtime 8.0+" << endl <<
		" - ASP.NET Core 8.0+." << endl <<
		" - Edge WebView2 Runtime 91.0+" << endl <<
		" - C++ Redistributable 2015-2022 14.30.30704+" << endl <<
		"------------------------------------------------------------------------" << endl << endl <<
		"That should be everything. The main program, Epic-Games_Account_Switcher.exe, will auto-run." << endl << endl <<
		"If it doesn't work, please refer to install instructions, here:" << endl <<
		"https://github.com/Epic-Games/Epic-Games_Account_Switcher#required-runtimes-download-and-install" << endl << endl;
}

inline void verify_vc()
{
	cout << "Downloading C++ Redistributable 2015-2022" << endl;
	current_download = "Downloading C++ Redistributable 2015-2022";

	bool c_runtime_install = false;
	const string runtime_folder = get_operating_path() + "runtime_installers\\";
	const string c_runtime_local = runtime_folder + "VC_redist.x64.exe",
		c_runtime_name = "Downloading C++ Redistributable 2015-2022";

	if (!(CreateDirectoryA(runtime_folder.c_str(), nullptr) || ERROR_ALREADY_EXISTS == GetLastError()))
	{
		cout << "Failed to create folder: " << runtime_folder << endl;
		cout << "Press any key to exit..." << endl;
		_getch();
		return;
	}

	if (!download_file(c_runtime.c_str(), c_runtime_local.c_str()))
	{
		cout << "Failed to download and install C++ Redistributable 2015-2022. To download: 1. Click the link below:" << endl <<
			"https://docs.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170" << endl;
	}
	else c_runtime_install = true;

	if (c_runtime_install)
		install_runtime(c_runtime_local, c_runtime_name, true);
}

inline void verify_net()
{
	cout << "Checking computer for .NET Runtimes. Currently installed:" << endl;
	find_installed_net_runtimes(false, min_webview_met, min_desktop_runtime_met, min_aspcore_met);
	cout << "------------------------------------------------------------------------" << endl << endl;
	download_install_missing_runtimes();
}
#pragma endregion
