

















#pragma once
#include <fstream>
#include <iostream>
#include <sstream>
#include <vector>
#include <Windows.h>

#include <iostream>
#include <string>
#include <tchar.h>

#include "versions.h"

inline void split_version(const std::string& str, std::vector<int>& arr, const char& delimiter)
{
	std::string s_num;
	for (const char i : str)
	{
		if (i != delimiter)
		{
			if (std::isdigit(i)) s_num += i;
			else if (i == '\0') break; 
		}
		else
		{
			arr.push_back(std::stoi(s_num));
			s_num = "";
		}
	}
	
	arr.push_back(std::stoi(s_num));
}





inline bool compare_versions(std::string v1, std::string v2, const char& delimiter)
{
	if (v1 == v2) return true;
	try
	{
		std::vector<int> v1_arr;
		std::vector<int> v2_arr;


		split_version(v1, v1_arr, delimiter);
		split_version(v2, v2_arr, delimiter);

		for (int i = 0; i < min(v1_arr.size(), v2_arr.size()); ++i)
		{
			if (v2_arr[i] < v1_arr[i]) return false;
			if (v2_arr[i] > v1_arr[i]) return true;
		}
		return true;
	} catch (std::exception &err)
	{
		std::cout << "Version conversion failed!" << std::endl << "Args: (" << v1 << ") and (" << v2 << ")" << std::endl << "Details:" << std::endl << err.what() << std::endl;
	}

	return true;
}




inline void find_installed_c_runtimes(bool &min_vc_met)
{
	
	HKEY key = nullptr;
	
	DWORD dw_type = KEY_ALL_ACCESS;
	WCHAR version[1024];
	DWORD dw_v_buffer_size = sizeof(version);
	const auto subkey = L"SOFTWARE\\Microsoft\\DevDiv\\VC\\Servicing\\14.0\\RuntimeMinimum";
	if (!(RegOpenKeyEx(HKEY_LOCAL_MACHINE, subkey, 0, KEY_READ, &key) == ERROR_SUCCESS || RegOpenKeyEx(HKEY_CURRENT_USER, subkey, 0, KEY_READ, &key) == ERROR_SUCCESS))
	{
		RegCloseKey(key);
		return;
	}

	if (RegQueryValueEx(key, L"Version", nullptr, &dw_type, reinterpret_cast<unsigned char*>(version), &dw_v_buffer_size) == ERROR_SUCCESS)
	{
		wprintf(L" - C++ Redistributable 2015-2022 [%s]\n", version);
		
		int requiredSize = WideCharToMultiByte(CP_UTF8, 0, version, -1, nullptr, 0, nullptr, nullptr);
		std::string s_version(requiredSize - 1, '\0');
		if (requiredSize > 1) WideCharToMultiByte(CP_UTF8, 0, version, -1, &s_version[0], requiredSize, nullptr, nullptr);
		min_vc_met = compare_versions(required_min_vc, s_version, '.');
	}
	RegCloseKey(key);
}





inline void check_webview2_edgeupdate_registry(bool &min_webview_met, const bool output = true)
{
	const auto webview2_guid = L"{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
	const wchar_t* edgeupdate_paths[] = {
		L"SOFTWARE\\Microsoft\\EdgeUpdate\\Clients",
		L"SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients"
	};
	const int path_count = sizeof(edgeupdate_paths) / sizeof(edgeupdate_paths[0]);
	
	for (int i = 0; i < path_count; i++)
	{
		const wchar_t* base_path = edgeupdate_paths[i];
		HKEY h_base_key = nullptr;
		HKEY h_client_key = nullptr;
		
		
		if (RegOpenKeyEx(HKEY_CURRENT_USER, base_path, 0, KEY_READ, &h_base_key) != ERROR_SUCCESS)
		{
			if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, base_path, 0, KEY_READ, &h_base_key) != ERROR_SUCCESS)
			{
				continue; 
			}
		}
		
		
		if (RegOpenKeyEx(h_base_key, webview2_guid, 0, KEY_READ, &h_client_key) == ERROR_SUCCESS)
		{
			WCHAR version[1024];
			DWORD dw_type = REG_SZ;
			DWORD dw_v_buffer_size = sizeof(version);
			
			
			if (RegQueryValueEx(h_client_key, L"pv", nullptr, &dw_type, reinterpret_cast<unsigned char*>(version), &dw_v_buffer_size) == ERROR_SUCCESS)
			{
				
				int vsize = WideCharToMultiByte(CP_UTF8, 0, version, -1, nullptr, 0, nullptr, nullptr);
				std::string s_version(vsize - 1, '\0');
				if (vsize > 1) WideCharToMultiByte(CP_UTF8, 0, version, -1, &s_version[0], vsize, nullptr, nullptr);
				
				
				if (compare_versions(required_min_webview, s_version, '.'))
				{
					min_webview_met = true;
					if (output)
					{
						printf(" - Microsoft WebView2 Runtime (EdgeUpdate) [%s]\n", s_version.c_str());
					}
				}
			}
			RegCloseKey(h_client_key);
		}
		RegCloseKey(h_base_key);
		
		
		if (min_webview_met) break;
	}
}




inline void check_dotnet_list_runtimes(bool &min_desktop_runtime_met, bool &min_aspcore_met, const bool output = true)
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
	
	std::wstring cmd_line = L"cmd.exe /c dotnet --list-runtimes";
	wchar_t* cmd_line_writable = new wchar_t[cmd_line.length() + 1];
	wcscpy_s(cmd_line_writable, cmd_line.length() + 1, cmd_line.c_str());
	
	if (CreateProcessW(nullptr, cmd_line_writable, nullptr, nullptr, TRUE, 
		CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi))
	{
		CloseHandle(h_write);
		WaitForSingleObject(pi.hProcess, 10000); 
		
		char buffer[8192];
		DWORD bytes_read;
		std::string output_text;
		while (ReadFile(h_read, buffer, sizeof(buffer) - 1, &bytes_read, nullptr) && bytes_read > 0)
		{
			buffer[bytes_read] = '\0';
			output_text += buffer;
		}
		
		
		std::istringstream iss(output_text);
		std::string line;
		while (std::getline(iss, line))
		{
			
			if (line.find("Microsoft.AspNetCore.App 8.") != std::string::npos)
			{
				
				size_t space_pos = line.find(' ', 0);
				if (space_pos != std::string::npos)
				{
					size_t bracket_pos = line.find(' ', space_pos + 1);
					if (bracket_pos != std::string::npos)
					{
						std::string version = line.substr(space_pos + 1, bracket_pos - space_pos - 1);
						if (compare_versions(required_min_aspcore, version, '.'))
						{
							min_aspcore_met = true;
							if (output)
							{
								printf(" - Microsoft.AspNetCore.App %s\n", version.c_str());
							}
						}
					}
				}
			}
			
			
			if (line.find("Microsoft.WindowsDesktop.App 8.") != std::string::npos)
			{
				
				size_t space_pos = line.find(' ', 0);
				if (space_pos != std::string::npos)
				{
					size_t bracket_pos = line.find(' ', space_pos + 1);
					if (bracket_pos != std::string::npos)
					{
						std::string version = line.substr(space_pos + 1, bracket_pos - space_pos - 1);
						if (compare_versions(required_min_desktop_runtime, version, '.'))
						{
							min_desktop_runtime_met = true;
							if (output)
							{
								printf(" - Microsoft.WindowsDesktop.App %s\n", version.c_str());
							}
						}
					}
				}
			}
		}
		
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
}




inline void find_installed_net_runtimes(const bool x32, bool &min_webview_met, bool &min_desktop_runtime_met, bool &min_aspcore_met, const bool output = true)
{
	
	check_webview2_edgeupdate_registry(min_webview_met, output);
	
	
	check_dotnet_list_runtimes(min_desktop_runtime_met, min_aspcore_met, output);
	
	
	if (min_desktop_runtime_met && min_aspcore_met)
	{
		return;
	}
	
	
	
	const auto s_root1 = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
	const auto s_root2 = L"SOFTWARE\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

	HKEY h_uninst_key = nullptr;
	HKEY h_app_key = nullptr;
	long l_result = ERROR_SUCCESS;
	DWORD dw_type = KEY_ALL_ACCESS;
	DWORD dw_buffer_size = 0;
	DWORD dw_v_buffer_size = 0;

	
	if (x32)
	{
		
		if (!(RegOpenKeyEx(HKEY_LOCAL_MACHINE, s_root1, 0, KEY_READ, &h_uninst_key) == ERROR_SUCCESS ||
			RegOpenKeyEx(HKEY_CURRENT_USER, s_root1, 0, KEY_READ, &h_uninst_key) == ERROR_SUCCESS))
			return;
	} else if (!(RegOpenKeyEx(HKEY_LOCAL_MACHINE, s_root2, 0, KEY_READ, &h_uninst_key) == ERROR_SUCCESS ||
		RegOpenKeyEx(HKEY_CURRENT_USER, s_root2, 0, KEY_READ, &h_uninst_key) == ERROR_SUCCESS))
		return;
	
	for (DWORD dw_index = 0; l_result == ERROR_SUCCESS; dw_index++)
	{
		WCHAR s_app_key_name[1024];
		
		dw_buffer_size = sizeof(s_app_key_name);
		if ((l_result = RegEnumKeyEx(h_uninst_key, dw_index, s_app_key_name, &dw_buffer_size, nullptr, nullptr, nullptr,
			nullptr)) == ERROR_SUCCESS)
		{
			WCHAR version[1024];
			WCHAR s_display_name[1024];
			WCHAR s_sub_key[1024];
			
			if (x32) wsprintf(s_sub_key, L"%s\\%s", s_root1, s_app_key_name);
			else wsprintf(s_sub_key, L"%s\\%s", s_root2, s_app_key_name);
			if (!(RegOpenKeyEx(HKEY_LOCAL_MACHINE, s_sub_key, 0, KEY_READ, &h_app_key) == ERROR_SUCCESS ||
				RegOpenKeyEx(HKEY_CURRENT_USER, s_sub_key, 0, KEY_READ, &h_app_key) == ERROR_SUCCESS))
			{
				RegCloseKey(h_app_key);
				RegCloseKey(h_uninst_key);
				continue;
			}

			
			dw_buffer_size = sizeof(s_display_name);
			dw_v_buffer_size = sizeof(version);
			if (RegQueryValueEx(h_app_key, L"DisplayName", nullptr, &dw_type, reinterpret_cast<unsigned char*>(s_display_name), &dw_buffer_size) == ERROR_SUCCESS &&
                RegQueryValueEx(h_app_key, L"DisplayVersion", nullptr, &dw_type, reinterpret_cast<unsigned char*>(version), &dw_v_buffer_size) == ERROR_SUCCESS)
			{
				
                int vsize = WideCharToMultiByte(CP_UTF8, 0, version, -1, nullptr, 0, nullptr, nullptr);
                std::string s_version(vsize - 1, '\0');
                if (vsize > 1) WideCharToMultiByte(CP_UTF8, 0, version, -1, &s_version[0], vsize, nullptr, nullptr);

                
                int nsize = WideCharToMultiByte(CP_UTF8, 0, s_display_name, -1, nullptr, 0, nullptr, nullptr);
                std::string s_display(nsize - 1, '\0');
                if (nsize > 1) WideCharToMultiByte(CP_UTF8, 0, s_display_name, -1, &s_display[0], nsize, nullptr, nullptr);

                if (wcsstr(s_display_name, L"WebView2") != nullptr)
                {
                    
                    
                    if (!min_webview_met)
                    {
                        min_webview_met = compare_versions(required_min_webview, s_version, '.');
                    }
                    if (output)
                    {
                        wprintf(L" - %s ", s_display_name);
                        printf("[%s]\n", s_version.c_str());
                    }
                }

                if (wcsstr(s_display_name, L"Desktop Runtime") != nullptr && wcsstr(s_display_name, L"x64") != nullptr)
                {
                    min_desktop_runtime_met = min_desktop_runtime_met || compare_versions(required_min_desktop_runtime, s_version, '.');
                    if (output) wprintf(L" - %s\n", s_display_name);
                }

                if ((wcsstr(s_display_name, L"ASP.NET Core 8") != nullptr || 
                     (wcsstr(s_display_name, L"ASP.NET Core") != nullptr && wcsstr(s_display_name, L"8.") != nullptr)) && 
                    wcsstr(s_display_name, L"x64") != nullptr)
                {
                    min_aspcore_met = min_aspcore_met || compare_versions(required_min_aspcore, s_version, '.');
                    if (output) wprintf(L" - %s\n", s_display_name);
                }
			}
			RegCloseKey(h_app_key);
		}
	}
	RegCloseKey(h_uninst_key);
}




inline void exec_process(const std::wstring& path, const std::wstring& exe, const std::wstring& param, const bool show_window = true, const bool wait_for_exit = false)
{
	
	
	


	

	
	

	
	
	
	

	

	
	
	

	
	
	


	SHELLEXECUTEINFO ShExecInfo = { 0 };
	ShExecInfo.cbSize = sizeof(SHELLEXECUTEINFO);
	ShExecInfo.fMask = SEE_MASK_NOCLOSEPROCESS;
	ShExecInfo.hwnd = nullptr;
	ShExecInfo.lpVerb = _T("open");
	
	std::wstring exe_full = exe;
	bool is_absolute = false;
	if (!exe.empty()) {
		
		if ((exe.size() > 1 && exe[1] == L':') || (exe.size() > 1 && exe[0] == L'\\' && exe[1] == L'\\')) is_absolute = true;
	}
	if (!is_absolute) {
		std::wstring dir = path;
		if (!dir.empty() && dir.back() != L'\\' && dir.back() != L'/') dir.push_back(L'\\');
		exe_full = dir + exe;
	}
	ShExecInfo.lpFile = exe_full.c_str();
	ShExecInfo.lpParameters = param.c_str();
	ShExecInfo.lpDirectory = path.c_str();
	ShExecInfo.nShow = show_window ? SW_SHOW : SW_HIDE;
	ShExecInfo.hInstApp = nullptr;

	DWORD dw_exit_code;
	if (ShellExecuteEx(&ShExecInfo)) {
		if (wait_for_exit){
			WaitForSingleObject(ShExecInfo.hProcess, INFINITE);
		}

		if (!GetExitCodeProcess(ShExecInfo.hProcess, &dw_exit_code)) {
			
			std::cout << dw_exit_code << std::endl;
		}

		CloseHandle(ShExecInfo.hProcess);
	}
	else {

		
	}
}


#pragma region Utilities

inline std::wstring s2_ws(const std::string& s)
{
	const int s_length = static_cast<int>(s.length()) + 1;
	const int len = MultiByteToWideChar(CP_ACP, 0, s.c_str(), s_length, nullptr, 0);
	const auto buf = new wchar_t[len];
	MultiByteToWideChar(CP_ACP, 0, s.c_str(), s_length, buf, len);
	std::wstring r(buf);
	delete[] buf;
	return r;
}

inline std::string get_self_location()
{
	const HMODULE h_module = GetModuleHandleW(nullptr);
	WCHAR pth[MAX_PATH];
	GetModuleFileNameW(h_module, pth, MAX_PATH);
	std::wstring ws(pth);
	
	int size = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, nullptr, 0, nullptr, nullptr);
	std::string path(size - 1, '\0');
	if (size > 1) WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, &path[0], size, nullptr, nullptr);
	return path;
}

inline std::string get_operating_path() {
	const std::string path(get_self_location());
	return path.substr(0, path.find_last_of('\\') + 1);
}

inline std::string get_self_name() {
	const std::string path(get_self_location());
	const size_t last_slash = path.find_last_of('\\');
	return path.substr(last_slash + 1, path.find_last_of('.') - last_slash - 1);
}

inline std::string dotnet_path()
{
	
	HKEY key = nullptr;
	
	DWORD dw_type = KEY_ALL_ACCESS;
	WCHAR path[1024];
	DWORD dw_v_buffer_size = sizeof(path);

	std::string ret;
	const auto sub_key = L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedhost";
	if (!(RegOpenKeyEx(HKEY_LOCAL_MACHINE, sub_key, 0, KEY_READ, &key) == ERROR_SUCCESS ||
		RegOpenKeyEx(HKEY_CURRENT_USER, sub_key, 0, KEY_READ, &key) == ERROR_SUCCESS))
	{
		RegCloseKey(key);
		return ret;
	}

	if (RegQueryValueEx(key, L"Path", nullptr, &dw_type, reinterpret_cast<unsigned char*>(path), &dw_v_buffer_size) == ERROR_SUCCESS)
	{
		
		int requiredSize = WideCharToMultiByte(CP_UTF8, 0, path, -1, nullptr, 0, nullptr, nullptr);
		std::string s_path(requiredSize - 1, '\0');
		if (requiredSize > 1) WideCharToMultiByte(CP_UTF8, 0, path, -1, &s_path[0], requiredSize, nullptr, nullptr);
		ret = s_path;
	}
	RegCloseKey(key);

	

	return ret;
}

#pragma endregion