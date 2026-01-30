#include "SharedFrameReader.h"
#include "VirtualCamGuids.h"

#include <cstring>

namespace
{
    constexpr DWORD kHeaderSize = 64;
    constexpr DWORD kMagic = 0x464D4350; // 'PCMF'
    constexpr DWORD kVersion = 1;
    constexpr DWORD kPixelFormatArgb32 = 1;
    constexpr wchar_t kMapName[] = L"Global\\PhoneCam.VirtualCam.FrameBuffer";
    constexpr wchar_t kEventName[] = L"Global\\PhoneCam.VirtualCam.FrameReady";

#pragma pack(push, 1)
    struct FrameHeader
    {
        uint32_t magic;
        uint32_t version;
        uint32_t width;
        uint32_t height;
        uint32_t stride;
        uint32_t pixelFormat;
        uint64_t frameId;
        int64_t timestampQpc;
        uint32_t bufferBytes;
        uint32_t activeIndex;
        uint64_t qpcFrequency;
    };
#pragma pack(pop)
}

SharedFrameReader::SharedFrameReader() = default;

SharedFrameReader::~SharedFrameReader()
{
    Close();
}

bool SharedFrameReader::EnsureOpen()
{
    if (m_mapping && m_view)
        return true;

    m_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kMapName);
    if (!m_mapping)
        return false;

    m_view = static_cast<BYTE*>(MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, 0));
    if (!m_view)
    {
        Close();
        return false;
    }

    m_event = OpenEventW(SYNCHRONIZE, FALSE, kEventName);
    return true;
}

HANDLE SharedFrameReader::GetEventHandle() const
{
    return m_event;
}

bool SharedFrameReader::TryCopyLatestFrame(BYTE* destination, DWORD destinationSize, bool& hadFrame)
{
    hadFrame = false;
    if (!destination || destinationSize == 0)
        return false;

    if (!EnsureOpen())
        return false;

    auto header = reinterpret_cast<const FrameHeader*>(m_view);
    if (header->magic != kMagic || header->version != kVersion || header->pixelFormat != kPixelFormatArgb32)
        return false;

    if (header->bufferBytes == 0 || header->bufferBytes > destinationSize)
        return false;

    if (header->frameId == 0)
        return true;

    const uint32_t activeIndex = header->activeIndex & 1u;
    const BYTE* src = m_view + kHeaderSize + (activeIndex * header->bufferBytes);

    std::memcpy(destination, src, header->bufferBytes);
    hadFrame = header->frameId != m_lastFrameId;
    m_lastFrameId = header->frameId;
    return true;
}

void SharedFrameReader::Close()
{
    if (m_view)
    {
        UnmapViewOfFile(m_view);
        m_view = nullptr;
    }
    if (m_mapping)
    {
        CloseHandle(m_mapping);
        m_mapping = nullptr;
    }
    if (m_event)
    {
        CloseHandle(m_event);
        m_event = nullptr;
    }
}
