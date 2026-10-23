// Creates/removes the "PixelBridge Camera" session-lifetime virtual camera (MFCreateVirtualCamera).
// Session lifetime + current-user access needs no elevation; the camera disappears when the app
// releases it or exits. The media source COM class must already be registered (installer, elevated).
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>
#include <mutex>

#pragma comment(lib, "mfsensorgroup.lib")

using Microsoft::WRL::ComPtr;

namespace {
std::mutex g_lock;
ComPtr<IMFVirtualCamera> g_cam;
}

extern "C" {

__declspec(dllexport) HRESULT __stdcall pb_vcam_start(const wchar_t* friendlyName, const wchar_t* clsid) {
    std::lock_guard g(g_lock);
    if (g_cam) return S_FALSE;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return hr;
    hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) return hr;
    ComPtr<IMFVirtualCamera> cam;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
        MFVirtualCameraAccess_CurrentUser, friendlyName, clsid, nullptr, 0, &cam);
    if (FAILED(hr)) return hr;
    hr = cam->Start(nullptr);
    if (FAILED(hr)) { cam->Shutdown(); return hr; }
    g_cam = cam;
    return S_OK;
}

__declspec(dllexport) HRESULT __stdcall pb_vcam_stop() {
    std::lock_guard g(g_lock);
    if (!g_cam) return S_FALSE;
    g_cam->Stop();
    HRESULT hr = g_cam->Remove();
    g_cam->Shutdown();
    g_cam.Reset();
    return hr;
}

/// 1 if the media source CLSID is registered under HKLM (installer step done), else 0.
__declspec(dllexport) int __stdcall pb_vcam_is_registered(const wchar_t* clsid) {
    wchar_t key[128];
    wsprintfW(key, L"SOFTWARE\\Classes\\CLSID\\%s\\InprocServer32", clsid);
    HKEY h = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_READ, &h) != ERROR_SUCCESS) return 0;
    RegCloseKey(h);
    return 1;
}

} // extern "C"
