#include <windows.h>
#include <initguid.h>   // define the KS/MF GUIDs used below (PINNAME_VIDEO_CAPTURE, KSCAMERAPROFILE_Legacy)
#include "MediaSource.h"
#include <wrl/module.h>
#include <sddl.h>
#include <propvarutil.h>
#include <cstring>

#pragma comment(lib, "propsys.lib")

#define RIF(x) do { HRESULT _hr = (x); if (FAILED(_hr)) return _hr; } while (0)

namespace {
struct Mode { UINT32 w, h, fps; };
constexpr Mode kModes[] = { {1280, 720, 30}, {1920, 1080, 30}, {640, 360, 30} };

HRESULT MakeNv12Type(const Mode& m, IMFMediaType** out) {
    ComPtr<IMFMediaType> t;
    RIF(MFCreateMediaType(&t));
    t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, m.w, m.h);
    MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, m.fps, 1);
    MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    t->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)(m.w * m.h * 12ull * m.fps));
    t->SetUINT32(MF_MT_DEFAULT_STRIDE, m.w);
    t->SetUINT32(MF_MT_SAMPLE_SIZE, m.w * m.h * 3 / 2);
    *out = t.Detach();
    return S_OK;
}

ULONGLONG QpcHns() {
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (ULONGLONG)(c.QuadPart * 10'000'000.0 / f.QuadPart);
}
} // namespace

// ---------------------------------------------------------------------------------------------
// SharedFrameReader

SharedFrameReader::~SharedFrameReader() {
    if (m_hdr) UnmapViewOfFile(m_hdr);
    if (m_map) CloseHandle(m_map);
}

bool SharedFrameReader::EnsureOpen() {
    if (m_hdr) return true;
    ULONGLONG now = GetTickCount64();
    if (now - m_lastOpenAttempt < 1000) return false;
    m_lastOpenAttempt = now;

    // SYSTEM, LOCAL SERVICE and Administrators full access; interactive users read/write (the app writes frames).
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)",
            SDDL_REVISION_1, &sd, nullptr)) {
        sa.lpSecurityDescriptor = sd;
    }
    HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE, 0, PB_SHARED_BYTES, PB_SHARED_NAME);
    bool created = h && GetLastError() != ERROR_ALREADY_EXISTS;
    if (!h) h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, PB_SHARED_NAME);
    if (!h) {
        // Not in a service (no SeCreateGlobalPrivilege): in-process tests use the session-local name.
        h = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE, 0, PB_SHARED_BYTES, PB_SHARED_NAME_LOCAL);
        created = h && GetLastError() != ERROR_ALREADY_EXISTS;
    }
    if (sd) LocalFree(sd);
    if (!h) return false;
    auto hdr = static_cast<PbSharedHeader*>(MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, PB_SHARED_BYTES));
    if (!hdr) { CloseHandle(h); return false; }
    if (created || hdr->magic != PB_MAGIC) {
        memset(hdr, 0, sizeof(PbSharedHeader));
        hdr->version = PB_VERSION;
        hdr->state = PB_STATE_OFFLINE;
        MemoryBarrier();
        hdr->magic = PB_MAGIC;
    }
    m_map = h;
    m_hdr = hdr;
    return true;
}

bool SharedFrameReader::Fill(BYTE* dst, LONG pitch, UINT32 w, UINT32 h) {
    if (EnsureOpen()) {
        m_hdr->readerTick = GetTickCount64();
        const BYTE* payload = reinterpret_cast<const BYTE*>(m_hdr) + PB_HEADER_BYTES;
        for (int attempt = 0; attempt < 3; attempt++) {
            int64_t s1 = m_hdr->seq;
            MemoryBarrier();
            UINT32 sw = m_hdr->width, sh = m_hdr->height, st = m_hdr->state;
            bool fresh = GetTickCount64() - m_hdr->writerTick < 1000;
            if ((s1 & 1) || m_hdr->magic != PB_MAGIC) { Sleep(1); continue; }
            if (st != PB_STATE_LIVE || !fresh || sw == 0 || sh == 0 || sw > PB_MAX_W || sh > PB_MAX_H) {
                Placeholder(dst, pitch, w, h, st == PB_STATE_PAUSED);
                return false;
            }
            const BYTE* sy = payload;
            const BYTE* suv = payload + (size_t)sw * sh;
            BYTE* duv = dst + (size_t)pitch * h;
            if (sw == w && sh == h) {
                for (UINT32 y = 0; y < h; y++) memcpy(dst + (size_t)y * pitch, sy + (size_t)y * sw, w);
                for (UINT32 y = 0; y < h / 2; y++) memcpy(duv + (size_t)y * pitch, suv + (size_t)y * sw, w);
            } else {
                // Nearest-neighbour scale (consumer picked a different mode than the phone stream).
                for (UINT32 y = 0; y < h; y++) {
                    const BYTE* srow = sy + (size_t)(y * sh / h) * sw;
                    BYTE* drow = dst + (size_t)y * pitch;
                    for (UINT32 x = 0; x < w; x++) drow[x] = srow[x * sw / w];
                }
                for (UINT32 y = 0; y < h / 2; y++) {
                    const BYTE* srow = suv + (size_t)(y * (sh / 2) / (h / 2)) * sw;
                    BYTE* drow = duv + (size_t)y * pitch;
                    for (UINT32 x = 0; x < w / 2; x++) {
                        UINT32 sx = (x * (sw / 2) / (w / 2)) * 2;
                        drow[2 * x] = srow[sx];
                        drow[2 * x + 1] = srow[sx + 1];
                    }
                }
            }
            MemoryBarrier();
            if (m_hdr->seq == s1) { m_hdr->framesRead++; return true; }
        }
    }
    Placeholder(dst, pitch, w, h, false);
    return false;
}

void SharedFrameReader::Placeholder(BYTE* dst, LONG pitch, UINT32 w, UINT32 h, bool paused) {
    // Dark frame with a crossed-out camera glyph: an explicit "camera unavailable" image, never stale video.
    const BYTE bg = paused ? 60 : 32, fg = 200;
    for (UINT32 y = 0; y < h; y++) memset(dst + (size_t)y * pitch, bg, w);
    BYTE* uv = dst + (size_t)pitch * h;
    for (UINT32 y = 0; y < h / 2; y++) memset(uv + (size_t)y * pitch, 128, w);
    const UINT32 bw = w / 5, bh = h / 5, x0 = (w - bw) / 2, y0 = (h - bh) / 2, t = (std::max)(2u, h / 120);
    auto px = [&](UINT32 x, UINT32 y) { if (x < w && y < h) dst[(size_t)y * pitch + x] = fg; };
    for (UINT32 i = 0; i < bw; i++) for (UINT32 k = 0; k < t; k++) { px(x0 + i, y0 + k); px(x0 + i, y0 + bh - 1 - k); }
    for (UINT32 j = 0; j < bh; j++) for (UINT32 k = 0; k < t; k++) { px(x0 + k, y0 + j); px(x0 + bw - 1 - k, y0 + j); }
    UINT32 lens = bh / 3;
    for (UINT32 j = 0; j < lens; j++) for (UINT32 i = 0; i < lens; i++) {
        int dx = (int)i - (int)lens / 2, dy = (int)j - (int)lens / 2;
        int r2 = dx * dx + dy * dy, R = (int)lens / 2;
        if (r2 <= R * R && r2 >= (R - (int)t) * (R - (int)t)) px(x0 + bw / 2 - lens / 2 + i, y0 + bh / 2 - lens / 2 + j);
    }
    for (UINT32 i = 0; i < bw + bh / 2; i++) for (UINT32 k = 0; k < t; k++)
        px(x0 - bh / 4 + i, y0 - bh / 4 + i * (bh + bh / 2) / (bw + bh / 2) + k);
}

// ---------------------------------------------------------------------------------------------
// PbMediaStream

HRESULT PbMediaStream::RuntimeClassInitialize(PbMediaSource* parent, DWORD streamId) {
    m_parent = parent;
    m_id = streamId;
    constexpr DWORD n = ARRAYSIZE(kModes);
    IMFMediaType* types[n] = {};
    for (DWORD i = 0; i < n; i++) RIF(MakeNv12Type(kModes[i], &types[i]));
    HRESULT hr = MFCreateStreamDescriptor(m_id, n, types, &m_sd);
    if (SUCCEEDED(hr)) {
        ComPtr<IMFMediaTypeHandler> th;
        hr = m_sd->GetMediaTypeHandler(&th);
        if (SUCCEEDED(hr)) hr = th->SetCurrentMediaType(types[0]);
    }
    for (auto t : types) if (t) t->Release();
    RIF(hr);
    RIF(MFCreateAttributes(&m_attributes, 4));
    for (IMFAttributes* a : { m_attributes.Get(), static_cast<IMFAttributes*>(m_sd.Get()) }) {
        RIF(a->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
        RIF(a->SetUINT32(MF_DEVICESTREAM_STREAM_ID, m_id));
        RIF(a->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
        RIF(a->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
    }
    RIF(MFCreateEventQueue(&m_queue));
    return S_OK;
}

IFACEMETHODIMP PbMediaStream::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->BeginGetEvent(cb, state);
}
IFACEMETHODIMP PbMediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->EndGetEvent(result, ev);
}
IFACEMETHODIMP PbMediaStream::GetEvent(DWORD flags, IMFMediaEvent** ev) {
    ComPtr<IMFMediaEventQueue> q;
    { std::lock_guard g(m_lock); RIF(CheckShutdown()); q = m_queue; }
    return q->GetEvent(flags, ev);
}
IFACEMETHODIMP PbMediaStream::QueueEvent(MediaEventType met, REFGUID ext, HRESULT hr, const PROPVARIANT* v) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->QueueEventParamVar(met, ext, hr, v);
}
IFACEMETHODIMP PbMediaStream::GetMediaSource(IMFMediaSource** source) {
    if (!source) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    return m_parent.CopyTo(source);
}
IFACEMETHODIMP PbMediaStream::GetStreamDescriptor(IMFStreamDescriptor** sd) {
    if (!sd) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    return m_sd.CopyTo(sd);
}

IFACEMETHODIMP PbMediaStream::RequestSample(IUnknown* token) {
    // Pace delivery to the negotiated frame rate on a fixed deadline schedule (no drift), waiting on a
    // high-resolution timer without holding the lock.
    LONGLONG waitHns = 0;
    {
        std::lock_guard g(m_lock);
        RIF(CheckShutdown());
        if (m_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
        ULONGLONG now = QpcHns();
        if (m_lastDeliverQpc == 0 || now > m_lastDeliverQpc + 2 * (ULONGLONG)m_frameDuration) m_lastDeliverQpc = now; // (re)anchor
        else m_lastDeliverQpc += (ULONGLONG)m_frameDuration;
        if (m_lastDeliverQpc > now) waitHns = (LONGLONG)(m_lastDeliverQpc - now);
    }
    if (waitHns > 10'000) {
        if (!m_timer) m_timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        LARGE_INTEGER due; due.QuadPart = -(std::min)(waitHns, 1'000'000ll);
        if (m_timer && SetWaitableTimer(m_timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(m_timer, 200);
        else Sleep((DWORD)(waitHns / 10'000));
    }

    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    if (m_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
    ComPtr<IMFSample> sample;
    ComPtr<IMFMediaBuffer> buffer;
    if (m_allocator) {
        RIF(m_allocator->AllocateSample(&sample));
        RIF(sample->GetBufferByIndex(0, &buffer));
    } else {
        RIF(MFCreateSample(&sample));
        RIF(MFCreate2DMediaBuffer(m_w, m_h, MFVideoFormat_NV12.Data1, FALSE, &buffer));
        RIF(sample->AddBuffer(buffer.Get()));
    }
    ComPtr<IMF2DBuffer2> b2;
    BYTE* scan0 = nullptr; BYTE* start = nullptr; LONG pitch = 0; DWORD len = 0;
    if (SUCCEEDED(buffer.As(&b2)) && SUCCEEDED(b2->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &len))) {
        if (pitch > 0) m_reader.Fill(scan0, pitch, m_w, m_h);
        b2->Unlock2D();
    } else {
        BYTE* p = nullptr; DWORD maxLen = 0;
        RIF(buffer->Lock(&p, &maxLen, nullptr));
        if (maxLen >= m_w * m_h * 3 / 2) m_reader.Fill(p, (LONG)m_w, m_w, m_h);
        buffer->Unlock();
        buffer->SetCurrentLength(m_w * m_h * 3 / 2);
    }
    RIF(sample->SetSampleTime(MFGetSystemTime()));
    RIF(sample->SetSampleDuration(m_frameDuration));
    if (token) RIF(sample->SetUnknown(MFSampleExtension_Token, token));
    return m_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

IFACEMETHODIMP PbMediaStream::SetStreamState(MF_STREAM_STATE state) {
    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    if (state == m_state) return S_OK;
    switch (state) {
    case MF_STREAM_STATE_PAUSED:
        if (m_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALID_STATE_TRANSITION;
        m_state = state;
        return S_OK;
    case MF_STREAM_STATE_RUNNING: return StartInternal(false, nullptr);
    case MF_STREAM_STATE_STOPPED: m_state = state; return S_OK;
    default: return MF_E_INVALID_STATE_TRANSITION;
    }
}

IFACEMETHODIMP PbMediaStream::GetStreamState(MF_STREAM_STATE* state) {
    if (!state) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    *state = m_state;
    return S_OK;
}

HRESULT PbMediaStream::Start(IMFMediaType* type) {
    std::lock_guard g(m_lock);
    if (!type) return E_INVALIDARG;
    return StartInternal(true, type);
}

HRESULT PbMediaStream::StartInternal(bool sendEvent, IMFMediaType* type) {
    if (type) m_type = type;
    if (!m_type) {
        ComPtr<IMFMediaTypeHandler> th;
        RIF(m_sd->GetMediaTypeHandler(&th));
        RIF(th->GetCurrentMediaType(&m_type));
    }
    RIF(MFGetAttributeSize(m_type.Get(), MF_MT_FRAME_SIZE, &m_w, &m_h));
    UINT32 num = 30, den = 1;
    MFGetAttributeRatio(m_type.Get(), MF_MT_FRAME_RATE, &num, &den);
    m_frameDuration = num ? (LONGLONG)(10'000'000ll * den / num) : 333333;
    if (m_allocator) RIF(m_allocator->InitializeSampleAllocator(10, m_type.Get()));
    m_lastDeliverQpc = 0;
    if (sendEvent) RIF(m_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
    m_state = MF_STREAM_STATE_RUNNING;
    return S_OK;
}

HRESULT PbMediaStream::Stop(bool sendEvent) {
    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    m_state = MF_STREAM_STATE_STOPPED;
    if (sendEvent) RIF(m_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
    return S_OK;
}

void PbMediaStream::Shutdown() {
    std::lock_guard g(m_lock);
    m_shutdown = true;
    if (m_queue) m_queue->Shutdown();
    m_queue.Reset();
    m_parent.Reset();
    m_allocator.Reset();
    m_state = MF_STREAM_STATE_STOPPED;
}

HRESULT PbMediaStream::SetAllocator(IMFVideoSampleAllocator* a) {
    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    if (m_state == MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
    m_allocator = a;
    return S_OK;
}

// ---------------------------------------------------------------------------------------------
// PbMediaSource

HRESULT PbMediaSource::RuntimeClassInitialize(IMFAttributes* activateAttributes) {
    RIF(MFCreateAttributes(&m_attributes, 4));
    if (activateAttributes) RIF(activateAttributes->CopyAllItems(m_attributes.Get()));

    // Mandatory legacy sensor profile so profile-unaware apps work (as in the Microsoft sample).
    ComPtr<IMFSensorProfileCollection> profiles;
    ComPtr<IMFSensorProfile> profile;
    RIF(MFCreateSensorProfileCollection(&profiles));
    RIF(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile));
    RIF(profile->AddProfileFilter(0, L"((RES==;FRT<=30,1;SUT==))"));
    RIF(profiles->AddProfile(profile.Get()));
    RIF(m_attributes->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));

    RIF(MFCreateEventQueue(&m_queue));
    RIF(Microsoft::WRL::MakeAndInitialize<PbMediaStream>(&m_stream, this, 0u));
    ComPtr<IMFStreamDescriptor> sd;
    RIF(m_stream->GetStreamDescriptor(&sd));
    IMFStreamDescriptor* sds[] = { sd.Get() };
    RIF(MFCreatePresentationDescriptor(1, sds, &m_pd));
    RIF(m_pd->SelectStream(0)); // selected by default so plain IMFSourceReader consumers get video
    m_state = State::Stopped;
    return S_OK;
}

IFACEMETHODIMP PbMediaSource::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->BeginGetEvent(cb, state);
}
IFACEMETHODIMP PbMediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->EndGetEvent(result, ev);
}
IFACEMETHODIMP PbMediaSource::GetEvent(DWORD flags, IMFMediaEvent** ev) {
    ComPtr<IMFMediaEventQueue> q;
    { std::lock_guard g(m_lock); RIF(CheckShutdown()); q = m_queue; }
    return q->GetEvent(flags, ev);
}
IFACEMETHODIMP PbMediaSource::QueueEvent(MediaEventType met, REFGUID ext, HRESULT hr, const PROPVARIANT* v) {
    std::lock_guard g(m_lock); RIF(CheckShutdown()); return m_queue->QueueEventParamVar(met, ext, hr, v);
}
IFACEMETHODIMP PbMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** pd) {
    if (!pd) return E_POINTER;
    *pd = nullptr;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    return m_pd->Clone(pd);
}
IFACEMETHODIMP PbMediaSource::GetCharacteristics(DWORD* c) {
    if (!c) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    *c = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP PbMediaSource::Shutdown() {
    std::lock_guard g(m_lock);
    m_state = State::Shutdown;
    if (m_queue) m_queue->Shutdown();
    m_queue.Reset();
    if (m_stream) m_stream->Shutdown();
    m_stream.Reset();
    m_pd.Reset();
    m_attributes.Reset();
    return S_OK;
}

IFACEMETHODIMP PbMediaSource::Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) {
    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    if (!pd || !startPos) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
    if (m_state == State::Invalid) return MF_E_INVALID_STATE_TRANSITION;
    DWORD count = 0;
    RIF(pd->GetStreamDescriptorCount(&count));
    if (count != 1) return E_INVALIDARG;

    PROPVARIANT startTime;
    RIF(InitPropVariantFromInt64(MFGetSystemTime(), &startTime));
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> sd;
    RIF(pd->GetStreamDescriptorByIndex(0, &selected, &sd));
    BOOL wasSelected = FALSE;
    ComPtr<IMFStreamDescriptor> localSd;
    RIF(m_pd->GetStreamDescriptorByIndex(0, &wasSelected, &localSd));
    if (selected) {
        RIF(m_pd->SelectStream(0));
        ComPtr<IMFMediaTypeHandler> th;
        ComPtr<IMFMediaType> type;
        RIF(sd->GetMediaTypeHandler(&th));
        RIF(th->GetCurrentMediaType(&type));
        // Start the stream before announcing it, so a RequestSample that races the event sees RUNNING.
        RIF(m_stream->Start(type.Get()));
        ComPtr<IUnknown> unk;
        RIF(m_stream.As(&unk));
        RIF(m_queue->QueueEventParamUnk(wasSelected ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, unk.Get()));
    } else if (wasSelected) {
        RIF(m_pd->DeselectStream(0));
        RIF(m_stream->Stop(false));
    }
    HRESULT hr = m_queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &startTime);
    PropVariantClear(&startTime);
    RIF(hr);
    m_state = State::Started;
    return S_OK;
}

IFACEMETHODIMP PbMediaSource::Stop() {
    std::lock_guard g(m_lock);
    RIF(CheckShutdown());
    if (m_state != State::Started) return MF_E_INVALID_STATE_TRANSITION;
    m_state = State::Stopped;
    PROPVARIANT stopTime;
    RIF(InitPropVariantFromInt64(MFGetSystemTime(), &stopTime));
    RIF(m_stream->Stop(true));
    RIF(m_pd->DeselectStream(0));
    HRESULT hr = m_queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &stopTime);
    PropVariantClear(&stopTime);
    return hr;
}

IFACEMETHODIMP PbMediaSource::GetSourceAttributes(IMFAttributes** a) {
    if (!a) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    return m_attributes.CopyTo(a);
}

IFACEMETHODIMP PbMediaSource::GetStreamAttributes(DWORD id, IMFAttributes** a) {
    if (!a) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    if (id != m_stream->Id()) return MF_E_NOT_FOUND;
    return m_stream->m_attributes.CopyTo(a);
}

IFACEMETHODIMP PbMediaSource::SetDefaultAllocator(DWORD streamId, IUnknown* allocator) {
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    if (!allocator) return E_POINTER;
    if (streamId != m_stream->Id()) return MF_E_NOT_FOUND;
    ComPtr<IMFVideoSampleAllocator> a;
    RIF(allocator->QueryInterface(IID_PPV_ARGS(&a)));
    return m_stream->SetAllocator(a.Get());
}

IFACEMETHODIMP PbMediaSource::GetAllocatorUsage(DWORD streamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) {
    if (!inputStreamId || !usage) return E_POINTER;
    std::lock_guard g(m_lock); RIF(CheckShutdown());
    if (streamId != m_stream->Id()) return MF_E_NOT_FOUND;
    *inputStreamId = streamId;
    *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
    return S_OK;
}

// ---------------------------------------------------------------------------------------------
// PbActivate

HRESULT PbActivate::RuntimeClassInitialize() {
    RIF(MFCreateAttributes(&m_attrs, 2));
    return m_attrs->SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1);
}

IFACEMETHODIMP PbActivate::ActivateObject(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!m_source) RIF(Microsoft::WRL::MakeAndInitialize<PbMediaSource>(&m_source, m_attrs.Get()));
    return m_source->QueryInterface(riid, ppv);
}

CoCreatableClass(PbActivate);
