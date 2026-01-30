#include "VirtualCamMediaSource.h"
#include "VirtualCamGuids.h"

#include <atomic>
#include <windows.h>

namespace
{
    std::atomic<ULONG> g_factoryRefCount{0};
}

class VirtualCamClassFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;

        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return ++m_refCount;
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG count = --m_refCount;
        if (count == 0)
        {
            delete this;
        }
        return count;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;

        if (pUnkOuter)
            return CLASS_E_NOAGGREGATION;

        auto* source = new (std::nothrow) VirtualCamMediaSource();
        if (!source)
            return E_OUTOFMEMORY;

        HRESULT hr = source->QueryInterface(riid, ppv);
        source->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) override
    {
        if (fLock)
        {
            ++g_factoryRefCount;
        }
        else
        {
            --g_factoryRefCount;
        }
        return S_OK;
    }

private:
    std::atomic<ULONG> m_refCount{1};
};

HRESULT CreateClassFactory(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_PhoneCamVirtualCamSource)
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) VirtualCamClassFactory();
    if (!factory)
        return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

ULONG GetFactoryLockCount()
{
    return g_factoryRefCount.load();
}
