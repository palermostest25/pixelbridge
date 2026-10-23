// vcamtest: exercises the PixelBridge media source.
//   vcamtest list                         list Windows video capture devices (Media Foundation)
//   vcamtest system  [sec] [out.bmp]      open "PixelBridge Camera" through Windows (Frame Server path)
//   vcamtest inproc <dll> [sec] [out.bmp] load the media source DLL directly (no registration needed)
// Prints frame count, fps, mean luma and mean inter-frame difference (motion) so live video can be
// distinguished from the static "camera unavailable" placeholder.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <cstdio>
#include <vector>
#include <string>
#include <cmath>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

static void WriteBmp(const char* path, const std::vector<BYTE>& nv12, UINT32 w, UINT32 h) {
    std::vector<BYTE> bgra((size_t)w * h * 4);
    const BYTE* uv = nv12.data() + (size_t)w * h;
    for (UINT32 y = 0; y < h; y++) for (UINT32 x = 0; x < w; x++) {
        int c = nv12[(size_t)y * w + x] - 16, d = uv[(size_t)(y / 2) * w + (x & ~1u)] - 128, e = uv[(size_t)(y / 2) * w + (x & ~1u) + 1] - 128;
        auto cl = [](int v) { return (BYTE)(v < 0 ? 0 : v > 255 ? 255 : v); };
        BYTE* p = &bgra[((size_t)y * w + x) * 4];
        p[0] = cl((298 * c + 516 * d + 128) >> 8); p[1] = cl((298 * c - 100 * d - 208 * e + 128) >> 8);
        p[2] = cl((298 * c + 409 * e + 128) >> 8); p[3] = 255;
    }
    FILE* f = nullptr;
    if (fopen_s(&f, path, "wb") || !f) return;
    int size = (int)bgra.size();
    BITMAPFILEHEADER fh{ 0x4D42, (DWORD)(54 + size), 0, 0, 54 };
    BITMAPINFOHEADER ih{ sizeof(ih), (LONG)w, -(LONG)h, 1, 32, BI_RGB, (DWORD)size, 2835, 2835, 0, 0 };
    fwrite(&fh, sizeof(fh), 1, f); fwrite(&ih, sizeof(ih), 1, f); fwrite(bgra.data(), 1, size, f); fclose(f);
}

static HRESULT ReadFrames(IMFMediaSource* source, int seconds, const char* bmp) {
    ComPtr<IMFAttributes> ra;
    MFCreateAttributes(&ra, 1);
    ComPtr<IMFSourceReader> reader;
    HRESULT hr = MFCreateSourceReaderFromMediaSource(source, ra.Get(), &reader);
    if (FAILED(hr)) { printf("MFCreateSourceReaderFromMediaSource failed 0x%08X\n", hr); return hr; }
    for (DWORD i = 0;; i++) {
        ComPtr<IMFMediaType> t;
        if (FAILED(reader->GetNativeMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, i, &t))) break;
        UINT32 w = 0, h = 0, n = 0, d = 0; GUID sub{};
        MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &w, &h);
        MFGetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, &n, &d);
        t->GetGUID(MF_MT_SUBTYPE, &sub);
        printf("MEDIATYPE %u: %s %ux%u @ %u/%u\n", i, sub == MFVideoFormat_NV12 ? "NV12" : "other", w, h, n, d);
    }
    ComPtr<IMFMediaType> cur;
    reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &cur);
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(cur.Get(), MF_MT_FRAME_SIZE, &w, &h);
    std::vector<BYTE> prev, frame;
    int frames = 0; double sumLuma = 0, sumDiff = 0; int diffs = 0;
    ULONGLONG t0 = GetTickCount64();
    while (GetTickCount64() - t0 < (ULONGLONG)seconds * 1000) {
        DWORD stream = 0, flags = 0; LONGLONG ts = 0;
        ComPtr<IMFSample> s;
        hr = reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &stream, &flags, &ts, &s);
        if (FAILED(hr)) { printf("ReadSample failed 0x%08X\n", hr); return hr; }
        if (!s) continue;
        ComPtr<IMFMediaBuffer> b;
        s->ConvertToContiguousBuffer(&b);
        BYTE* p = nullptr; DWORD len = 0;
        b->Lock(&p, nullptr, &len);
        if (len >= w * h * 3 / 2) {
            frame.assign(p, p + (size_t)w * h * 3 / 2);
            double luma = 0; for (size_t i = 0; i < (size_t)w * h; i += 97) luma += frame[i];
            sumLuma += luma / ((double)w * h / 97);
            if (!prev.empty()) {
                double dsum = 0; for (size_t i = 0; i < (size_t)w * h; i += 97) dsum += std::abs((int)frame[i] - (int)prev[i]);
                sumDiff += dsum / ((double)w * h / 97); diffs++;
            }
            prev = frame;
            frames++;
        }
        b->Unlock();
    }
    double el = (GetTickCount64() - t0) / 1000.0;
    printf("FRAMES %d in %.1fs = %.1f fps, %ux%u, meanLuma=%.1f, meanMotion=%.2f\n", frames, el, frames / el, w, h,
        frames ? sumLuma / frames : 0, diffs ? sumDiff / diffs : 0);
    if (bmp && !frame.empty()) WriteBmp(bmp, frame, w, h); // last frame
    return frames > 0 ? S_OK : E_FAIL;
}

int main(int argc, char** argv) {
    if (argc < 2) { printf("usage: vcamtest list | system [sec] [bmp] | inproc <dll> [sec] [bmp]\n"); return 64; }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);
    std::string mode = argv[1];
    HRESULT hr = S_OK;
    if (mode == "list" || mode == "system") {
        ComPtr<IMFAttributes> a;
        MFCreateAttributes(&a, 1);
        a->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        IMFActivate** devs = nullptr; UINT32 n = 0;
        hr = MFEnumDeviceSources(a.Get(), &devs, &n);
        if (FAILED(hr)) { printf("MFEnumDeviceSources failed 0x%08X\n", hr); return 1; }
        IMFActivate* found = nullptr;
        for (UINT32 i = 0; i < n; i++) {
            wchar_t* name = nullptr; UINT32 cch = 0;
            devs[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &cch);
            printf("DEVICE %u: %ls\n", i, name ? name : L"?");
            if (name && wcsncmp(name, L"PixelBridge Camera", 18) == 0) found = devs[i];
            CoTaskMemFree(name);
        }
        if (mode == "system") {
            if (!found) { printf("PixelBridge Camera NOT ENUMERATED\n"); return 2; }
            ComPtr<IMFMediaSource> src;
            hr = found->ActivateObject(IID_PPV_ARGS(&src));
            if (FAILED(hr)) { printf("ActivateObject failed 0x%08X\n", hr); return 3; }
            hr = ReadFrames(src.Get(), argc > 2 ? atoi(argv[2]) : 5, argc > 3 ? argv[3] : nullptr);
            src->Shutdown();
        }
        for (UINT32 i = 0; i < n; i++) devs[i]->Release();
        CoTaskMemFree(devs);
    } else if (mode == "inproc" && argc > 2) {
        HMODULE dll = LoadLibraryA(argv[2]);
        if (!dll) { printf("LoadLibrary failed %lu\n", GetLastError()); return 1; }
        using GetCO = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, LPVOID*);
        auto getco = (GetCO)GetProcAddress(dll, "DllGetClassObject");
        const CLSID clsid = { 0x8E3B4F1A, 0x6C2D, 0x4E8B, { 0x9A, 0x7F, 0x3D, 0x5C, 0x1B, 0x2E, 0x4F, 0x60 } };
        ComPtr<IClassFactory> cf;
        hr = getco(clsid, IID_PPV_ARGS(&cf));
        if (FAILED(hr)) { printf("DllGetClassObject failed 0x%08X\n", hr); return 1; }
        ComPtr<IMFActivate> act;
        hr = cf->CreateInstance(nullptr, IID_PPV_ARGS(&act));
        if (FAILED(hr)) { printf("CreateInstance failed 0x%08X\n", hr); return 1; }
        ComPtr<IMFMediaSource> src;
        hr = act->ActivateObject(IID_PPV_ARGS(&src));
        if (FAILED(hr)) { printf("ActivateObject failed 0x%08X\n", hr); return 1; }
        hr = ReadFrames(src.Get(), argc > 3 ? atoi(argv[3]) : 5, argc > 4 ? argv[4] : nullptr);
        src->Shutdown();
    }
    MFShutdown();
    printf("RESULT 0x%08X\n", hr);
    return SUCCEEDED(hr) ? 0 : 1;
}

