#include "VirtualCamGuids.h"

#include <windows.h>
#include <strsafe.h>

HRESULT CreateClassFactory(REFCLSID clsid, REFIID riid, void** ppv);
ULONG GetFactoryLockCount();

namespace
{
    HMODULE g_module = nullptr;
    std::atomic<ULONG> g_objectCount{0};

    HRESULT RegisterServer(bool registerServer)
    {
        wchar_t modulePath[MAX_PATH] = {};
        if (!GetModuleFileNameW(g_module, modulePath, MAX_PATH))
            return HRESULT_FROM_WIN32(GetLastError());

        wchar_t clsidString[64] = {};
        StringFromGUID2(CLSID_PhoneCamVirtualCamSource, clsidString, ARRAYSIZE(clsidString));

        wchar_t keyPath[256] = {};
        StringCchPrintfW(keyPath, ARRAYSIZE(keyPath), L"Software\\Classes\\CLSID\\%s", clsidString);

        if (registerServer)
        {
            HKEY clsidKey = nullptr;
            LONG status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &clsidKey, nullptr);
            if (status != ERROR_SUCCESS)
                return HRESULT_FROM_WIN32(status);

            RegSetValueExW(clsidKey, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(kPhoneCamVirtualCamFriendlyName),
                static_cast<DWORD>((wcslen(kPhoneCamVirtualCamFriendlyName) + 1) * sizeof(wchar_t)));

            HKEY inprocKey = nullptr;
            status = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &inprocKey, nullptr);
            if (status == ERROR_SUCCESS)
            {
                RegSetValueExW(inprocKey, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(modulePath),
                    static_cast<DWORD>((wcslen(modulePath) + 1) * sizeof(wchar_t)));

                const wchar_t threadingModel[] = L"Both";
                RegSetValueExW(inprocKey, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(threadingModel),
                    static_cast<DWORD>((wcslen(threadingModel) + 1) * sizeof(wchar_t)));

                RegCloseKey(inprocKey);
            }

            RegCloseKey(clsidKey);
            return status == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(status);
        }
        else
        {
            LONG status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath);
            if (status == ERROR_FILE_NOT_FOUND)
                return S_OK;
            return HRESULT_FROM_WIN32(status);
        }
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    return CreateClassFactory(rclsid, riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (GetFactoryLockCount() == 0 && g_objectCount.load() == 0) ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer()
{
    return RegisterServer(true);
}

STDAPI DllUnregisterServer()
{
    return RegisterServer(false);
}

extern "C" void __stdcall DllAddRef()
{
    ++g_objectCount;
}

extern "C" void __stdcall DllRelease()
{
    --g_objectCount;
}
