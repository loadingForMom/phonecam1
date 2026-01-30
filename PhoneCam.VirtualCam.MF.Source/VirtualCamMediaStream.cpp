#include "VirtualCamMediaStream.h"
#include "SharedFrameReader.h"
#include "Logger.h"

#include <mfapi.h>
#include <mferror.h>
#include <propvarutil.h>
#include <windows.h>
#include <cstring>

namespace
{
    constexpr LONGLONG kHundredNsPerSec = 10'000'000;
}

extern "C" void __stdcall DllAddRef();
extern "C" void __stdcall DllRelease();

VirtualCamMediaStream::VirtualCamMediaStream(DWORD streamId, IMFStreamDescriptor* descriptor, IMFMediaSource* parent)
    : m_streamDescriptor(descriptor), m_parent(parent), m_streamId(streamId)
{
    DllAddRef();
    if (m_streamDescriptor)
    {
        m_streamDescriptor->AddRef();
    }
    if (m_parent)
    {
        m_parent->AddRef();
    }

    MFCreateEventQueue(&m_eventQueue);
    m_frameDuration = kHundredNsPerSec / 30;
    m_reader = std::make_unique<SharedFrameReader>();
}

VirtualCamMediaStream::~VirtualCamMediaStream()
{
    Shutdown();
    DllRelease();
}

HRESULT VirtualCamMediaStream::Start()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return MF_E_SHUTDOWN;

    m_started = true;
    m_frameIndex = 0;
    LogInfo(L"MF stream started.");
    return QueueEvent(MEStreamStarted, GUID_NULL, S_OK, nullptr);
}

HRESULT VirtualCamMediaStream::Stop()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return MF_E_SHUTDOWN;

    m_started = false;
    LogInfo(L"MF stream stopped.");
    return QueueEvent(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

HRESULT VirtualCamMediaStream::Shutdown()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return S_OK;

    m_shutdown = true;
    m_reader.reset();
    if (m_eventQueue)
    {
        m_eventQueue->Shutdown();
        m_eventQueue->Release();
        m_eventQueue = nullptr;
    }

    if (m_streamDescriptor)
    {
        m_streamDescriptor->Release();
        m_streamDescriptor = nullptr;
    }

    if (m_parent)
    {
        m_parent->Release();
        m_parent = nullptr;
    }

    return S_OK;
}

STDMETHODIMP VirtualCamMediaStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;

    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaStream))
    {
        *ppv = static_cast<IMFMediaStream*>(this);
        AddRef();
        return S_OK;
    }

    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) VirtualCamMediaStream::AddRef()
{
    return ++m_refCount;
}

STDMETHODIMP_(ULONG) VirtualCamMediaStream::Release()
{
    ULONG count = --m_refCount;
    if (count == 0)
    {
        delete this;
    }
    return count;
}

STDMETHODIMP VirtualCamMediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->GetEvent(dwFlags, ppEvent);
}

STDMETHODIMP VirtualCamMediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->BeginGetEvent(pCallback, punkState);
}

STDMETHODIMP VirtualCamMediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->EndGetEvent(pResult, ppEvent);
}

STDMETHODIMP VirtualCamMediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    if (!m_eventQueue)
        return MF_E_SHUTDOWN;

    return m_eventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);
}

STDMETHODIMP VirtualCamMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
    if (!ppMediaSource)
        return E_POINTER;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return MF_E_SHUTDOWN;

    if (!m_parent)
        return E_UNEXPECTED;

    *ppMediaSource = m_parent;
    m_parent->AddRef();
    return S_OK;
}

STDMETHODIMP VirtualCamMediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
    if (!ppStreamDescriptor)
        return E_POINTER;

    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return MF_E_SHUTDOWN;

    if (!m_streamDescriptor)
        return E_UNEXPECTED;

    *ppStreamDescriptor = m_streamDescriptor;
    m_streamDescriptor->AddRef();
    return S_OK;
}

STDMETHODIMP VirtualCamMediaStream::RequestSample(IUnknown* pToken)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_shutdown)
        return MF_E_SHUTDOWN;

    if (!m_started)
        return MF_E_INVALIDREQUEST;

    IMFSample* sample = nullptr;
    HRESULT hr = CreateSample(&sample);
    if (FAILED(hr))
    {
        LogHr(L"MF stream: CreateSample failed.", hr);
        return hr;
    }

    if (pToken)
    {
        sample->SetUnknown(MFSampleExtension_Token, pToken);
    }

    hr = m_eventQueue->QueueEventParamUnk(MEStreamSample, GUID_NULL, S_OK, sample);
    sample->Release();
    return hr;
}

HRESULT VirtualCamMediaStream::CreateSample(IMFSample** sample)
{
    if (!sample)
        return E_POINTER;

    IMFSample* localSample = nullptr;
    IMFMediaBuffer* buffer = nullptr;
    const DWORD bufferLength = m_stride * m_height;

    HRESULT hr = MFCreateSample(&localSample);
    if (FAILED(hr))
        return hr;

    hr = MFCreateMemoryBuffer(bufferLength, &buffer);
    if (FAILED(hr))
    {
        localSample->Release();
        return hr;
    }

    hr = FillSampleBuffer(buffer);
    if (FAILED(hr))
    {
        buffer->Release();
        localSample->Release();
        return hr;
    }

    hr = buffer->SetCurrentLength(bufferLength);
    if (FAILED(hr))
    {
        buffer->Release();
        localSample->Release();
        return hr;
    }

    hr = localSample->AddBuffer(buffer);
    buffer->Release();
    if (FAILED(hr))
    {
        localSample->Release();
        return hr;
    }

    const LONGLONG sampleTime = m_frameIndex * m_frameDuration;
    localSample->SetSampleTime(sampleTime);
    localSample->SetSampleDuration(m_frameDuration);
    m_frameIndex++;

    *sample = localSample;
    return S_OK;
}

HRESULT VirtualCamMediaStream::FillSampleBuffer(IMFMediaBuffer* buffer)
{
    if (!buffer)
        return E_POINTER;

    BYTE* data = nullptr;
    DWORD maxLength = 0;
    DWORD currentLength = 0;
    HRESULT hr = buffer->Lock(&data, &maxLength, &currentLength);
    if (FAILED(hr))
        return hr;

    bool hadFrame = false;
    bool copied = false;
    if (m_reader)
    {
        HANDLE evt = m_reader->GetEventHandle();
        if (evt)
        {
            WaitForSingleObject(evt, 15);
        }
        copied = m_reader->TryCopyLatestFrame(data, maxLength, hadFrame);
    }
    if (!copied)
    {
        LogInfo(L"MF stream: failed to copy shared frame, using black frame.");
        FillBlack(data, maxLength);
    }
    else if (!hadFrame)
    {
        FillBlack(data, maxLength);
    }

    buffer->Unlock();
    return S_OK;
}

void VirtualCamMediaStream::FillBlack(BYTE* data, DWORD length)
{
    if (!data || length == 0)
        return;
    std::memset(data, 0, length);
}
