#include <windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <wrl.h>
#include <string>
#include <iostream>

// {B0B7F5A3-3B72-4C5B-9F5F-2E4F9E5F2AE1}
static const GUID CLSID_PhoneCamVirtualCamSource =
{ 0xb0b7f5a3, 0x3b72, 0x4c5b, { 0x9f, 0x5f, 0x2e, 0x4f, 0x9e, 0x5f, 0x2a, 0xe1 } };

static constexpr wchar_t kFriendlyName[] = L"PhoneCam Virtual Camera";

std::wstring GuidToString(const GUID& guid)
{
    wchar_t buffer[64] = {};
    StringFromGUID2(guid, buffer, ARRAYSIZE(buffer));
    return buffer;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        std::wcout << L"Usage: PhoneCam.VirtualCam.MF.Driver.exe [register|unregister|remove]" << std::endl;
        return 1;
    }

    std::wstring cmd = argv[1];
    for (auto& ch : cmd)
        ch = towlower(ch);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr))
    {
        std::wcerr << L"CoInitializeEx failed: 0x" << std::hex << hr << std::endl;
        return 2;
    }

    hr = MFStartup(MF_VERSION);
    if (FAILED(hr))
    {
        std::wcerr << L"MFStartup failed: 0x" << std::hex << hr << std::endl;
        CoUninitialize();
        return 2;
    }

    Microsoft::WRL::ComPtr<IMFVirtualCamera> camera;
    const std::wstring symbolicLink = GuidToString(CLSID_PhoneCamVirtualCamSource);

    hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_System,
        MFVirtualCameraAccess_AllUsers,
        kFriendlyName,
        symbolicLink.c_str(),
        nullptr,
        0,
        &camera);

    if (FAILED(hr))
    {
        std::wcerr << L"MFCreateVirtualCamera failed: 0x" << std::hex << hr << std::endl;
        MFShutdown();
        CoUninitialize();
        return 2;
    }

    if (cmd == L"register")
    {
        hr = camera->Start(nullptr);
        if (FAILED(hr))
        {
            std::wcerr << L"IMFVirtualCamera::Start failed: 0x" << std::hex << hr << std::endl;
        }
        else
        {
            std::wcout << L"Virtual camera registered." << std::endl;
        }
    }
    else if (cmd == L"unregister" || cmd == L"remove")
    {
        hr = camera->Remove();
        if (FAILED(hr))
        {
            std::wcerr << L"IMFVirtualCamera::Remove failed: 0x" << std::hex << hr << std::endl;
        }
        else
        {
            std::wcout << L"Virtual camera removed." << std::endl;
        }
    }
    else
    {
        std::wcerr << L"Unknown command: " << cmd << std::endl;
        hr = E_INVALIDARG;
    }

    MFShutdown();
    CoUninitialize();
    return FAILED(hr) ? 2 : 0;
}
