#include "VirtualCamMediaSource.h"
#include "Logger.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfobjects.h>
#include <propvarutil.h>

namespace
{
    constexpr UINT32 kStreamId = 1;
    constexpr UINT32 kWidth = 1280;
    constexpr UINT32 kHeight = 720;
    constexpr UINT32 kFrameRateNum = 30;
    constexpr UINT32 kFrameRateDen = 1;
}

extern "C" void __stdcall DllAddRef();
extern "C" void __stdcall DllRelease();

VirtualCamMediaSource::VirtualCamMediaSource()
{
    DllAddRef();
    Initialize();
    LogInfo(L"MF media source created.");
}

VirtualCamMediaSource::~VirtualCamMediaSource()
{
    Shutdown();
    LogInfo(L"MF media source destroyed.");
    DllRelease();
}

HRESULT VirtualCamMediaSource::Initialize()
{
    HRESULT hr = MFCreateEventQueue(&m_eventQueue);
    if (FAILED(hr))
        return hr;

    hr = MFCreateMediaType(&m_mediaType);
    if (FAILED(hr))
        return hr;

    m_mediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    m_mediaType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_ARGB32);
    m_mediaType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    m_mediaType->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    m_mediaType->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    MFSetAttributeSize(m_mediaType, MF_MT_FRAME_SIZE, kWidth, kHeight);
    MFSetAttributeRatio(m_mediaType, MF_MT_FRAME_RATE, kFrameRateNum, kFrameRateDen);
    MFSetAttributeRatio(m_mediaType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

    const UINT32 stride = kWidth * 4;
    m_mediaType->SetUINT32(MF_MT_DEFAULT_STRIDE, stride);
    m_mediaType->SetUINT32(MF_MT_SAMPLE_SIZE, stride * kHeight);

    IMFMediaType* types[] = { m_mediaType };
    hr = MFCreateStreamDescriptor(kStreamId, 1, types, &m_streamDescriptor);
    if (FAILED(hr))
        return hr;

    IMFMediaTypeHandler* handler = nullptr;
    hr = m_streamDescriptor->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr))
    {
        handler->SetCurrentMediaType(m_mediaType);
        handler->Release();
    }

    hr = MFCreatePresentationDescriptor(1, &m_streamDescriptor, &m_presentationDescriptor);
    if (FAILED(hr))
        return hr;

    m_stream = new VirtualCamMediaStream(kStreamId, m_streamDescriptor, this);
    LogInfo(L"MF media source initialized.");
    return S_OK;
}

HRESULT VirtualCamMediaSource::CheckShutdown() const
{
    return m_shutdown ? MF_E_SHUTDOWN : S_OK;
}

STDMETHODIMP VirtualCamMediaSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;

    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaSource))
    {
        *ppv = static_cast<IMFMediaSource*>(this);
        AddRef();
        return S_OK;
    }

    if (riid == __uuidof(IMFGetService))
    {
        *ppv = static_cast<IMFGetService*>(this);
        AddRef();
        return S_OK;
    }

    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) VirtualCamMediaSource::AddRef()
{
    return ++m_refCount;
}

STDMETHODIMP_(ULONG) VirtualCamMediaSource::Release()
{
    ULONG count = --m_refCount;
    if (count == 0)
    {
        delete this;
    }
    return count;
}

STDMETHODIMP VirtualCamMediaSource::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->GetEvent(dwFlags, ppEvent);
}

STDMETHODIMP VirtualCamMediaSource::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->BeginGetEvent(pCallback, punkState);
}

STDMETHODIMP VirtualCamMediaSource::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->EndGetEvent(pResult, ppEvent);
}

STDMETHODIMP VirtualCamMediaSource::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
}

STDMETHODIMP VirtualCamMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor)
{
    if (!ppPresentationDescriptor)
        return E_POINTER;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (FAILED(CheckShutdown()))
        return MF_E_SHUTDOWN;

    if (!m_presentationDescriptor)
        return E_UNEXPECTED;

    *ppPresentationDescriptor = m_presentationDescriptor;
    m_presentationDescriptor->AddRef();
    return S_OK;
}

STDMETHODIMP VirtualCamMediaSource::GetCharacteristics(DWORD* pdwCharacteristics)
{
    if (!pdwCharacteristics)
        return E_POINTER;

    if (FAILED(CheckShutdown()))
        return MF_E_SHUTDOWN;

    *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP VirtualCamMediaSource::Pause()
{
    if (FAILED(CheckShutdown()))
        return MF_E_SHUTDOWN;

    return MF_E_INVALIDREQUEST;
}

STDMETHODIMP VirtualCamMediaSource::Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition)
{
    UNREFERENCED_PARAMETER(pguidTimeFormat);
    UNREFERENCED_PARAMETER(pvarStartPosition);

    std::lock_guard<std::mutex> lock(m_mutex);
    if (FAILED(CheckShutdown()))
        return MF_E_SHUTDOWN;

    if (pPresentationDescriptor)
    {
        pPresentationDescriptor->SelectStream(0);
    }

    if (m_stream)
    {
        m_stream->Start();
    }

    LogInfo(L"MF media source started.");
    return QueueEvent(MESourceStarted, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP VirtualCamMediaSource::Stop()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (FAILED(CheckShutdown()))
        return MF_E_SHUTDOWN;

    if (m_stream)
    {
        m_stream->Stop();
    }

    LogInfo(L"MF media source stopped.");
    return QueueEvent(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP VirtualCamMediaSource::Shutdown()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return S_OK;

    m_shutdown = true;

    if (m_stream)
    {
        m_stream->Shutdown();
        m_stream->Release();
        m_stream = nullptr;
    }

    if (m_presentationDescriptor)
    {
        m_presentationDescriptor->Release();
        m_presentationDescriptor = nullptr;
    }

    if (m_streamDescriptor)
    {
        m_streamDescriptor->Release();
        m_streamDescriptor = nullptr;
    }

    if (m_mediaType)
    {
        m_mediaType->Release();
        m_mediaType = nullptr;
    }

    if (m_eventQueue)
    {
        m_eventQueue->Shutdown();
        m_eventQueue->Release();
        m_eventQueue = nullptr;
    }

    LogInfo(L"MF media source shutdown.");
    return S_OK;
}

STDMETHODIMP VirtualCamMediaSource::GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject)
{
    UNREFERENCED_PARAMETER(guidService);
    UNREFERENCED_PARAMETER(riid);
    if (!ppvObject)
        return E_POINTER;

    *ppvObject = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}
