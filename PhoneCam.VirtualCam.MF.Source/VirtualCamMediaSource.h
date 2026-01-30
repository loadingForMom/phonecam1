#pragma once

#include <atomic>
#include <mutex>
#include <mfidl.h>
#include "VirtualCamMediaStream.h"

class VirtualCamMediaSource final : public IMFMediaSource, public IMFGetService
{
public:
    VirtualCamMediaSource();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaSource
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) override;
    STDMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Shutdown() override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

private:
    ~VirtualCamMediaSource();
    HRESULT CheckShutdown() const;
    HRESULT Initialize();

    std::atomic<ULONG> m_refCount{1};
    std::mutex m_mutex;
    IMFMediaEventQueue* m_eventQueue = nullptr;
    IMFPresentationDescriptor* m_presentationDescriptor = nullptr;
    IMFStreamDescriptor* m_streamDescriptor = nullptr;
    IMFMediaType* m_mediaType = nullptr;
    VirtualCamMediaStream* m_stream = nullptr;
    bool m_shutdown = false;
};
