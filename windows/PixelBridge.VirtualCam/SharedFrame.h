#pragma once
// Shared-memory frame contract between PixelBridge.App (writer) and the virtual camera media source
// (reader, loaded by the Windows Camera Frame Server service). Mirrored in PixelBridge.Media/VirtualCamFeed.cs.
//
// The media source runs in session 0 as LOCAL SERVICE, so it creates the Global\ section (creating
// Global\ objects needs SeCreateGlobalPrivilege, which a normal user process lacks) with a DACL that
// lets interactive users open it for writing. The app opens it, never creates it.
#include <cstdint>

#define PB_SHARED_NAME L"Global\\PixelBridgeVirtualCamera.v1"
#define PB_SHARED_NAME_LOCAL L"Local\\PixelBridgeVirtualCamera.v1"   // fallback for in-process tests only
#define PB_MAGIC 0x43564250u /* 'PBVC' */
#define PB_VERSION 1u
#define PB_MAX_W 1920u
#define PB_MAX_H 1080u
#define PB_HEADER_BYTES 4096u
#define PB_SHARED_BYTES (PB_HEADER_BYTES + PB_MAX_W * PB_MAX_H * 3u / 2u)

enum PbState : uint32_t { PB_STATE_OFFLINE = 0, PB_STATE_LIVE = 1, PB_STATE_PAUSED = 2 };

#pragma pack(push, 4)
struct PbSharedHeader {
    uint32_t magic;         // PB_MAGIC, written by the creator
    uint32_t version;       // PB_VERSION
    volatile int64_t seq;   // seqlock: odd while the writer is copying a frame
    uint32_t width;         // NV12 frame dimensions of the current payload (even, <= PB_MAX_*)
    uint32_t height;
    uint32_t state;         // PbState
    uint32_t reserved0;
    uint64_t writerTick;    // GetTickCount64() at last write; readers treat > 1000 ms old as offline
    int64_t  frameTime100ns;
    uint64_t framesWritten;
    volatile uint64_t readerTick;  // GetTickCount64() of the last consumer read (lets the app show "in use")
    uint64_t framesRead;
};
#pragma pack(pop)
static_assert(sizeof(PbSharedHeader) <= PB_HEADER_BYTES, "header too large");
