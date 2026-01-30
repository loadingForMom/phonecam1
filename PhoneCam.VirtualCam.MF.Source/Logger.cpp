#include "Logger.h"

#include <windows.h>
#include <shlobj.h>
#include <sstream>
#include <fstream>
#include <iomanip>

namespace
{
    std::wstring GetLogPath()
    {
        wchar_t path[MAX_PATH] = {};
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path)))
        {
            std::wstring dir = std::wstring(path) + L"\\PhoneCam";
            CreateDirectoryW(dir.c_str(), nullptr);
            return dir + L"\\virtualcam_mf.log";
        }

        return L"virtualcam_mf.log";
    }

    void AppendLine(const std::wstring& line)
    {
        std::wofstream file(GetLogPath(), std::ios::app);
        if (!file.is_open())
            return;

        SYSTEMTIME st{};
        GetLocalTime(&st);
        file << std::setfill(L'0')
             << st.wYear << L"-" << std::setw(2) << st.wMonth << L"-" << std::setw(2) << st.wDay
             << L" " << std::setw(2) << st.wHour << L":" << std::setw(2) << st.wMinute
             << L":" << std::setw(2) << st.wSecond << L"." << std::setw(3) << st.wMilliseconds
             << L" " << line << std::endl;
    }
}

void LogInfo(const std::wstring& message)
{
    AppendLine(message);
}

void LogHr(const std::wstring& message, HRESULT hr)
{
    std::wstringstream ss;
    ss << message << L" hr=0x" << std::hex << hr;
    AppendLine(ss.str());
}
