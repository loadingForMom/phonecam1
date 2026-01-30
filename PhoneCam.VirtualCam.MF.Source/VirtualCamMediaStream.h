#pragma once

#include <atomic>
#include <mutex>
#include <mfidl.h>

class VirtualCamMediaStream final : public IMFMediaStream
{
public:
    VirtualCamMediaStream(DWORD streamId, IMFStreamDescriptor* descriptor, IMFMediaSource* parent);

    HRESULT Start();
    HRESULT Stop();
    HRESULT Shutdown();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override;
    STDMETHODIMP RequestSample(IUnknown* pToken) override;

private:
    ~VirtualCamMediaStream();

    HRESULT CreateSample(IMFSample** sample);
    HRESULT FillSampleBuffer(IMFMediaBuffer* buffer);

    std::atomic<ULONG> m_refCount{1};
    std::mutex m_mutex;
    IMFMediaEventQueue* m_eventQueue = nullptr;
    IMFStreamDescriptor* m_streamDescriptor = nullptr;
    IMFMediaSource* m_parent = nullptr;
    bool m_shutdown = false;
    bool m_started = false;

    LONGLONG m_frameDuration = 0; // in 100ns units
    LONGLONG m_frameIndex = 0;
    DWORD m_streamId = 0;
    UINT32 m_width = 1280;
    UINT32 m_height = 720;
    UINT32 m_stride = 1280 * 4;
};
