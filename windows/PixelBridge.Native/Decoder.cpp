// PixelBridge.Native: Media Foundation H.264 decode (Microsoft H264 decoder MFT) + NV12->BGRA for preview.
// Flat C exports consumed by PixelBridge.Media via P/Invoke.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <vector>
#include <mutex>
#include <cstring>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "wmcodecdspuuid.lib")

using Microsoft::WRL::ComPtr;

namespace {

struct Decoder {
    ComPtr<IMFTransform> mft;
    UINT32 codedW = 0, codedH = 0, stride = 0, dispW = 0, dispH = 0;
    DWORD outBufSize = 0;
    std::vector<BYTE> latest;   // tightly packed NV12 (dispW x dispH)
    LONGLONG latestTs = 0;
    UINT64 frameCounter = 0;
    UINT64 readCounter = 0;
    std::mutex lock;
};

HRESULT SetNv12Output(Decoder* d) {
    for (DWORD i = 0;; i++) {
        ComPtr<IMFMediaType> t;
        HRESULT hr = d->mft->GetOutputAvailableType(0, i, &t);
        if (FAILED(hr)) return hr;
        GUID sub{};
        t->GetGUID(MF_MT_SUBTYPE, &sub);
        if (sub != MFVideoFormat_NV12) continue;
        hr = d->mft->SetOutputType(0, t.Get(), 0);
        if (FAILED(hr)) return hr;
        MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &d->codedW, &d->codedH);
        UINT32 s = 0;
        d->stride = SUCCEEDED(t->GetUINT32(MF_MT_DEFAULT_STRIDE, &s)) && (INT32)s > 0 ? s : d->codedW;
        MFVideoArea area{};
        UINT32 cb = 0;
        if (SUCCEEDED(t->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8*)&area, sizeof(area), &cb)) && cb == sizeof(area)) {
            d->dispW = area.Area.cx; d->dispH = area.Area.cy;
        } else {
            d->dispW = d->codedW; d->dispH = d->codedH;
        }
        MFT_OUTPUT_STREAM_INFO info{};
        d->mft->GetOutputStreamInfo(0, &info);
        d->outBufSize = info.cbSize ? info.cbSize : d->stride * d->codedH * 3 / 2;
        return S_OK;
    }
}

HRESULT Drain(Decoder* d) {
    for (;;) {
        // Before the first SPS the output type is unknown; a 1080p-sized buffer covers the first call.
        if (d->outBufSize == 0) d->outBufSize = 1920 * 1088 * 3 / 2;
        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buf;
        HRESULT hr = MFCreateSample(&sample);
        if (FAILED(hr)) return hr;
        hr = MFCreateMemoryBuffer(d->outBufSize, &buf);
        if (FAILED(hr)) return hr;
        sample->AddBuffer(buf.Get());
        MFT_OUTPUT_DATA_BUFFER out{};
        out.dwStreamID = 0;
        out.pSample = sample.Get();
        DWORD status = 0;
        hr = d->mft->ProcessOutput(0, 1, &out, &status);
        if (out.pEvents) out.pEvents->Release();
        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            hr = SetNv12Output(d);
            if (FAILED(hr)) return hr;
            continue;
        }
        if (FAILED(hr)) return hr;

        BYTE* p = nullptr; DWORD maxLen = 0, len = 0;
        if (SUCCEEDED(buf->Lock(&p, &maxLen, &len))) {
            const UINT32 w = d->dispW & ~1u, h = d->dispH & ~1u;
            const size_t need = (size_t)w * h * 3 / 2;
            if ((size_t)d->stride * d->codedH * 3 / 2 <= len) {
                std::lock_guard<std::mutex> g(d->lock);
                d->latest.resize(need);
                BYTE* dst = d->latest.data();
                for (UINT32 y = 0; y < h; y++) memcpy(dst + (size_t)y * w, p + (size_t)y * d->stride, w);
                const BYTE* uv = p + (size_t)d->stride * d->codedH;
                BYTE* dstUv = dst + (size_t)w * h;
                for (UINT32 y = 0; y < h / 2; y++) memcpy(dstUv + (size_t)y * w, uv + (size_t)y * d->stride, w);
                LONGLONG t = 0;
                sample->GetSampleTime(&t);
                d->latestTs = t;
                d->frameCounter++;
            }
            buf->Unlock();
        }
    }
}

} // namespace

extern "C" {

__declspec(dllexport) HRESULT __stdcall pb_dec_create(void** handle) {
    if (!handle) return E_POINTER;
    *handle = nullptr;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return hr;
    hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) return hr;
    auto d = new Decoder();
    hr = CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&d->mft));
    if (FAILED(hr)) { delete d; return hr; }
    ComPtr<IMFAttributes> attrs;
    if (SUCCEEDED(d->mft->GetAttributes(&attrs))) attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
    ComPtr<ICodecAPI> codecApi;
    if (SUCCEEDED(d->mft.As(&codecApi))) {
        VARIANT v; v.vt = VT_UI4; v.ulVal = 1;
        codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &v);
    }
    ComPtr<IMFMediaType> in;
    MFCreateMediaType(&in);
    in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    in->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    hr = d->mft->SetInputType(0, in.Get(), 0);
    if (FAILED(hr)) { delete d; return hr; }
    SetNv12Output(d); // may fail until SPS seen; handled via STREAM_CHANGE
    d->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    *handle = d;
    return S_OK;
}

/// Feed one Annex-B access unit (or SPS/PPS). ts100ns is carried through to the decoded frame.
__declspec(dllexport) HRESULT __stdcall pb_dec_decode(void* handle, const BYTE* data, UINT32 len, LONGLONG ts100ns) {
    auto d = static_cast<Decoder*>(handle);
    if (!d || !data || len == 0) return E_INVALIDARG;
    ComPtr<IMFMediaBuffer> buf;
    HRESULT hr = MFCreateMemoryBuffer(len, &buf);
    if (FAILED(hr)) return hr;
    BYTE* p = nullptr;
    buf->Lock(&p, nullptr, nullptr);
    memcpy(p, data, len);
    buf->Unlock();
    buf->SetCurrentLength(len);
    ComPtr<IMFSample> s;
    MFCreateSample(&s);
    s->AddBuffer(buf.Get());
    s->SetSampleTime(ts100ns);
    hr = d->mft->ProcessInput(0, s.Get(), 0);
    if (hr == MF_E_NOTACCEPTING) {
        Drain(d);
        hr = d->mft->ProcessInput(0, s.Get(), 0);
    }
    if (FAILED(hr)) return hr;
    return Drain(d);
}

/// Copy the newest decoded NV12 frame if one arrived since the last call. Returns S_OK if copied, S_FALSE if nothing new.
__declspec(dllexport) HRESULT __stdcall pb_dec_latest(void* handle, BYTE* dst, UINT32 capacity, UINT32* width, UINT32* height, LONGLONG* ts100ns) {
    auto d = static_cast<Decoder*>(handle);
    if (!d || !width || !height) return E_INVALIDARG;
    std::lock_guard<std::mutex> g(d->lock);
    *width = d->dispW & ~1u; *height = d->dispH & ~1u;
    if (d->frameCounter == d->readCounter || d->latest.empty()) return S_FALSE;
    if (!dst || capacity < d->latest.size()) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    memcpy(dst, d->latest.data(), d->latest.size());
    if (ts100ns) *ts100ns = d->latestTs;
    d->readCounter = d->frameCounter;
    return S_OK;
}

__declspec(dllexport) HRESULT __stdcall pb_dec_flush(void* handle) {
    auto d = static_cast<Decoder*>(handle);
    if (!d) return E_INVALIDARG;
    return d->mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
}

__declspec(dllexport) void __stdcall pb_dec_destroy(void* handle) {
    auto d = static_cast<Decoder*>(handle);
    if (!d) return;
    d->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
    d->mft.Reset();
    delete d;
    MFShutdown();
}

/// BT.601 limited-range NV12 -> BGRA32 (preview only). Optional horizontal mirror.
__declspec(dllexport) void __stdcall pb_nv12_to_bgra(const BYTE* nv12, UINT32 w, UINT32 h, BYTE* bgra, UINT32 dstStride, BOOL mirror) {
    const BYTE* uvPlane = nv12 + (size_t)w * h;
    for (UINT32 y = 0; y < h; y++) {
        const BYTE* yRow = nv12 + (size_t)y * w;
        const BYTE* uvRow = uvPlane + (size_t)(y / 2) * w;
        BYTE* out = bgra + (size_t)y * dstStride;
        for (UINT32 x = 0; x < w; x++) {
            int c = yRow[x] - 16, dd = uvRow[x & ~1u] - 128, e = uvRow[(x & ~1u) + 1] - 128;
            int r = (298 * c + 409 * e + 128) >> 8;
            int g = (298 * c - 100 * dd - 208 * e + 128) >> 8;
            int b = (298 * c + 516 * dd + 128) >> 8;
            BYTE* px = out + (size_t)(mirror ? (w - 1 - x) : x) * 4;
            px[0] = (BYTE)(b < 0 ? 0 : b > 255 ? 255 : b);
            px[1] = (BYTE)(g < 0 ? 0 : g > 255 ? 255 : g);
            px[2] = (BYTE)(r < 0 ? 0 : r > 255 ? 255 : r);
            px[3] = 255;
        }
    }
}

} // extern "C"
