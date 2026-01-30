#pragma once

#include <windows.h>
#include <cstdint>

class SharedFrameReader
{
public:
    SharedFrameReader();
    ~SharedFrameReader();

    bool EnsureOpen();
    bool TryCopyLatestFrame(BYTE* destination, DWORD destinationSize, bool& hadFrame);
    HANDLE GetEventHandle() const;

private:
    void Close();

    HANDLE m_mapping = nullptr;
    HANDLE m_event = nullptr;
    BYTE* m_view = nullptr;
    ULONG64 m_lastFrameId = 0;
};
