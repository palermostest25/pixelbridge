#include "MediaSource.h"
#include <wrl/module.h>
#include <string>

#pragma comment(lib, "runtimeobject.lib") // WRL error origination

static HMODULE g_module = nullptr;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv) {
    return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().GetClassObject(clsid, riid, ppv);
}

STDAPI DllCanUnloadNow() {
    return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

// Registration is machine-wide (HKLM) because the Frame Server service, not the user, loads the
// source. regsvr32 therefore needs elevation; the installer does this with explicit consent.
static const wchar_t* kKey = L"SOFTWARE\\Classes\\CLSID\\" PB_VCAM_CLSID_STRING;

STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    HKEY key = nullptr, inproc = nullptr;
    LSTATUS s = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (s != ERROR_SUCCESS) return HRESULT_FROM_WIN32(s);
    const wchar_t name[] = L"PixelBridge Virtual Camera Source";
    RegSetValueExW(key, nullptr, 0, REG_SZ, (const BYTE*)name, sizeof(name));
    s = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &inproc, nullptr);
    if (s == ERROR_SUCCESS) {
        RegSetValueExW(inproc, nullptr, 0, REG_SZ, (const BYTE*)path, (DWORD)((wcslen(path) + 1) * sizeof(wchar_t)));
        const wchar_t model[] = L"Both";
        RegSetValueExW(inproc, L"ThreadingModel", 0, REG_SZ, (const BYTE*)model, sizeof(model));
        RegCloseKey(inproc);
    }
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(s);
}

STDAPI DllUnregisterServer() {
    LSTATUS s = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kKey);
    return (s == ERROR_SUCCESS || s == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(s);
}
