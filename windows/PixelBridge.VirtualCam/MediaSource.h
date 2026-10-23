#pragma once
// PixelBridge virtual camera media source. Architecture follows Microsoft's Windows-Camera
// VirtualCamera "SimpleMediaSource" sample (IMFActivate -> IMFMediaSourceEx -> IMFMediaStream2,
// frame-server provided sample allocator, sensor profiles), rewritten on WRL without WIL/C++/WinRT.
#include <windows.h>
#include <ole2.h>
#include <cguid.h>  // must precede ks.h, which redefines GUID_NULL as a __uuidof macro
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <mfvirtualcamera.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <mutex>
#include "SharedFrame.h"

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ChainInterfaces;

// {8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}
#define PB_VCAM_CLSID_STRING L"{8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60}"

class PbMediaSource;

/// Reads frames from the shared section, or synthesises the "camera unavailable" placeholder.
class SharedFrameReader {
public:
    ~SharedFrameReader();
    /// Fill an NV12 buffer of w x h with the given pitch. Returns true if a live frame was copied.
    bool Fill(BYTE* dst, LONG pitch, UINT32 w, UINT32 h);
private:
    bool EnsureOpen();
    void Placeholder(BYTE* dst, LONG pitch, UINT32 w, UINT32 h, bool paused);
    HANDLE m_map = nullptr;
    PbSharedHeader* m_hdr = nullptr;
    ULONGLONG m_lastOpenAttempt = 0;
};

class PbMediaStream : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>> {
public:
    HRESULT RuntimeClassInitialize(PbMediaSource* parent, DWORD streamId);

    // IMFMediaEventGenerator
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override;
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID ext, HRESULT hr, const PROPVARIANT* v) override;
    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** sd) override;
    IFACEMETHODIMP RequestSample(IUnknown* token) override;
    // IMFMediaStream2
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    HRESULT Start(IMFMediaType* type);
    HRESULT Stop(bool sendEvent);
    void Shutdown();
    HRESULT SetAllocator(IMFVideoSampleAllocator* a);
    DWORD Id() const { return m_id; }
    ComPtr<IMFAttributes> m_attributes;

private:
    HRESULT StartInternal(bool sendEvent, IMFMediaType* type);
    HRESULT CheckShutdown() const { return m_shutdown ? MF_E_SHUTDOWN : (m_queue ? S_OK : E_UNEXPECTED); }

    std::recursive_mutex m_lock;
    ComPtr<IMFMediaSource> m_parent;
    ComPtr<IMFMediaEventQueue> m_queue;
    ComPtr<IMFStreamDescriptor> m_sd;
    ComPtr<IMFMediaType> m_type;
    ComPtr<IMFVideoSampleAllocator> m_allocator;
    MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;
    DWORD m_id = 0;
    bool m_shutdown = false;
    UINT32 m_w = 1280, m_h = 720;
    LONGLONG m_frameDuration = 333333;
    ULONGLONG m_lastDeliverQpc = 0;   // scheduled delivery time of the previous sample (hns)
    HANDLE m_timer = nullptr;
    SharedFrameReader m_reader;
public:
    ~PbMediaStream() { if (m_timer) CloseHandle(m_timer); }
};

class PbMediaSource : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>, IMFGetService, IKsControl, IMFSampleAllocatorControl> {
public:
    HRESULT RuntimeClassInitialize(IMFAttributes* activateAttributes);

    // IMFMediaEventGenerator
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override;
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID ext, HRESULT hr, const PROPVARIANT* v) override;
    // IMFMediaSource
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override;
    IFACEMETHODIMP GetCharacteristics(DWORD* c) override;
    IFACEMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }
    IFACEMETHODIMP Shutdown() override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) override;
    IFACEMETHODIMP Stop() override;
    // IMFMediaSourceEx
    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** a) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD id, IMFAttributes** a) override;
    IFACEMETHODIMP SetD3DManager(IUnknown*) override { return E_NOTIMPL; }
    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID, REFIID, LPVOID* ppv) override { if (ppv) *ppv = nullptr; return MF_E_UNSUPPORTED_SERVICE; }
    // IKsControl: no custom controls; mimic a driver without handlers.
    IFACEMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    IFACEMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    IFACEMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    // IMFSampleAllocatorControl
    IFACEMETHODIMP SetDefaultAllocator(DWORD streamId, IUnknown* allocator) override;
    IFACEMETHODIMP GetAllocatorUsage(DWORD streamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

private:
    enum class State { Invalid, Stopped, Started, Shutdown };
    HRESULT CheckShutdown() const { return m_state == State::Shutdown ? MF_E_SHUTDOWN : (m_queue ? S_OK : E_UNEXPECTED); }

    std::recursive_mutex m_lock;
    State m_state = State::Invalid;
    ComPtr<IMFAttributes> m_attributes;
    ComPtr<IMFMediaEventQueue> m_queue;
    ComPtr<IMFPresentationDescriptor> m_pd;
    ComPtr<PbMediaStream> m_stream;
};

class __declspec(uuid("8E3B4F1A-6C2D-4E8B-9A7F-3D5C1B2E4F60"))
PbActivate : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFActivate, IMFAttributes>> {
public:
    HRESULT RuntimeClassInitialize();
    IFACEMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    IFACEMETHODIMP ShutdownObject() override { return S_OK; }
    IFACEMETHODIMP DetachObject() override { m_source.Reset(); return S_OK; }

#define PB_FWD(sig, call) IFACEMETHODIMP sig override { return m_attrs->call; }
    PB_FWD(GetItem(REFGUID k, PROPVARIANT* v), GetItem(k, v))
    PB_FWD(GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t), GetItemType(k, t))
    PB_FWD(CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r), CompareItem(k, v, r))
    PB_FWD(Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r), Compare(t, m, r))
    PB_FWD(GetUINT32(REFGUID k, UINT32* v), GetUINT32(k, v))
    PB_FWD(GetUINT64(REFGUID k, UINT64* v), GetUINT64(k, v))
    PB_FWD(GetDouble(REFGUID k, double* v), GetDouble(k, v))
    PB_FWD(GetGUID(REFGUID k, GUID* v), GetGUID(k, v))
    PB_FWD(GetStringLength(REFGUID k, UINT32* l), GetStringLength(k, l))
    PB_FWD(GetString(REFGUID k, LPWSTR s, UINT32 c, UINT32* l), GetString(k, s, c, l))
    PB_FWD(GetAllocatedString(REFGUID k, LPWSTR* s, UINT32* l), GetAllocatedString(k, s, l))
    PB_FWD(GetBlobSize(REFGUID k, UINT32* s), GetBlobSize(k, s))
    PB_FWD(GetBlob(REFGUID k, UINT8* b, UINT32 c, UINT32* s), GetBlob(k, b, c, s))
    PB_FWD(GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s), GetAllocatedBlob(k, b, s))
    PB_FWD(GetUnknown(REFGUID k, REFIID r, LPVOID* p), GetUnknown(k, r, p))
    PB_FWD(SetItem(REFGUID k, REFPROPVARIANT v), SetItem(k, v))
    PB_FWD(DeleteItem(REFGUID k), DeleteItem(k))
    PB_FWD(DeleteAllItems(), DeleteAllItems())
    PB_FWD(SetUINT32(REFGUID k, UINT32 v), SetUINT32(k, v))
    PB_FWD(SetUINT64(REFGUID k, UINT64 v), SetUINT64(k, v))
    PB_FWD(SetDouble(REFGUID k, double v), SetDouble(k, v))
    PB_FWD(SetGUID(REFGUID k, REFGUID v), SetGUID(k, v))
    PB_FWD(SetString(REFGUID k, LPCWSTR v), SetString(k, v))
    PB_FWD(SetBlob(REFGUID k, const UINT8* b, UINT32 c), SetBlob(k, b, c))
    PB_FWD(SetUnknown(REFGUID k, IUnknown* u), SetUnknown(k, u))
    PB_FWD(LockStore(), LockStore())
    PB_FWD(UnlockStore(), UnlockStore())
    PB_FWD(GetCount(UINT32* c), GetCount(c))
    PB_FWD(GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v), GetItemByIndex(i, k, v))
    PB_FWD(CopyAllItems(IMFAttributes* d), CopyAllItems(d))
#undef PB_FWD

private:
    ComPtr<IMFAttributes> m_attrs;
    ComPtr<PbMediaSource> m_source;
};
