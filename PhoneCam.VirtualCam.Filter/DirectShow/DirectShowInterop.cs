using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PhoneCam.VirtualCam.Filter.DirectShow
{
    // NOTE:
    // This file contains a minimal subset of DirectShow interop definitions needed for
    // a "source (capture) filter" skeleton. It's intentionally not exhaustive.

    public static class HResult
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;

        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

        public const int VFW_E_ALREADY_CONNECTED = unchecked((int)0x80040204);
        public const int VFW_E_NOT_CONNECTED = unchecked((int)0x80040209);
        public const int VFW_E_NO_ACCEPTABLE_TYPES = unchecked((int)0x80040207);
        public const int VFW_E_TYPE_NOT_ACCEPTED = unchecked((int)0x8004022A);
        public const int VFW_E_WRONG_STATE = unchecked((int)0x80040227);

        public static bool Succeeded(int hr) => hr >= 0;
        public static bool Failed(int hr) => hr < 0;
    }

    public enum FilterState
    {
        Stopped = 0,
        Paused = 1,
        Running = 2
    }

    public enum PinDirection
    {
        Input = 0,
        Output = 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEOINFOHEADER
    {
        public RECT rcSource;
        public RECT rcTarget;
        public int dwBitRate;
        public int dwBitErrorRate;
        public long AvgTimePerFrame;
        public BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AMMediaType
    {
        public Guid majorType;
        public Guid subType;
        [MarshalAs(UnmanagedType.Bool)] public bool fixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool temporalCompression;
        public int sampleSize;
        public Guid formatType;
        public IntPtr unkPtr;
        public int formatSize;
        public IntPtr formatPtr;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FilterInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string achName;
        public IFilterGraph pGraph;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PinInfo
    {
        public IBaseFilter pFilter;
        public PinDirection dir;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string achName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ALLOCATOR_PROPERTIES
    {
        public int cBuffers;
        public int cbBuffer;
        public int cbAlign;
        public int cbPrefix;
    }

    [ComVisible(true)]
    [Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBaseFilter
    {
        // IPersist
        int GetClassID(out Guid pClassID);

        // IMediaFilter
        int Stop();
        int Pause();
        int Run(long tStart);
        int GetState(int dwMilliSecsTimeout, out FilterState filtState);
        int SetSyncSource(IReferenceClock pClock);
        int GetSyncSource(out IReferenceClock pClock);

        // IBaseFilter
        int EnumPins(out IEnumPins ppEnum);
        int FindPin([MarshalAs(UnmanagedType.LPWStr)] string Id, out IPin ppPin);
        int QueryFilterInfo(out FilterInfo pInfo);
        int JoinFilterGraph(IFilterGraph pGraph, [MarshalAs(UnmanagedType.LPWStr)] string pName);
        int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string pVendorInfo);
    }

    [ComVisible(true)]
    [Guid("56A86899-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFilterGraph
    {
        // We don't need the full interface for the skeleton.
    }

    [ComVisible(true)]
    [Guid("56A86897-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IReferenceClock
    {
        // Not used in the skeleton.
    }

    [ComVisible(true)]
    [Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumPins
    {
        int Next(int cPins, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] ppPins, IntPtr pcFetched);
        int Skip(int cPins);
        int Reset();
        int Clone(out IEnumPins ppEnum);
    }

    [ComVisible(true)]
    [Guid("56A86893-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumMediaTypes
    {
        int Next(int cMediaTypes, [Out] IntPtr[] ppMediaTypes, IntPtr pcFetched);
        int Skip(int cMediaTypes);
        int Reset();
        int Clone(out IEnumMediaTypes ppEnum);
    }

    [ComVisible(true)]
    [Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPin
    {
        int Connect(IPin pReceivePin, IntPtr pmt);
        int ReceiveConnection(IPin pConnector, IntPtr pmt);
        int Disconnect();
        int ConnectedTo(out IPin ppPin);
        int ConnectionMediaType(out AMMediaType pmt);
        int QueryPinInfo(out PinInfo pInfo);
        int QueryDirection(out PinDirection pPinDir);
        int QueryId([MarshalAs(UnmanagedType.LPWStr)] out string Id);
        int QueryAccept(IntPtr pmt);
        int EnumMediaTypes(out IEnumMediaTypes ppEnum);
        int QueryInternalConnections(IntPtr apPin, ref int nPin);
        int EndOfStream();
        int BeginFlush();
        int EndFlush();
        int NewSegment(long tStart, long tStop, double dRate);
    }

    [ComVisible(true)]
    [Guid("56A8689D-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMemInputPin
    {
        int GetAllocator(out IMemAllocator ppAllocator);
        int NotifyAllocator(IMemAllocator pAllocator, [MarshalAs(UnmanagedType.Bool)] bool bReadOnly);
        int GetAllocatorRequirements(out ALLOCATOR_PROPERTIES pProps);
        int Receive(IMediaSample pSample);
        int ReceiveMultiple([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IMediaSample[] pSamples, int nSamples, out int nSamplesProcessed);
        int ReceiveCanBlock();
    }

    [ComVisible(true)]
    [Guid("56A8689C-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMemAllocator
    {
        int SetProperties(ref ALLOCATOR_PROPERTIES pRequest, out ALLOCATOR_PROPERTIES pActual);
        int GetProperties(out ALLOCATOR_PROPERTIES pProps);
        int Commit();
        int Decommit();
        int GetBuffer(out IMediaSample ppBuffer, IntPtr pStartTime, IntPtr pEndTime, int dwFlags);
        int ReleaseBuffer(IMediaSample pBuffer);
    }

    [ComVisible(true)]
    [Guid("56A8689A-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMediaSample
    {
        int GetPointer(out IntPtr ppBuffer);
        int GetSize();
        int GetTime(out long pTimeStart, out long pTimeEnd);
        int SetTime([In] ref long pTimeStart, [In] ref long pTimeEnd);
        int IsSyncPoint();
        int SetSyncPoint([MarshalAs(UnmanagedType.Bool)] bool bIsSyncPoint);
        int IsPreroll();
        int SetPreroll([MarshalAs(UnmanagedType.Bool)] bool bIsPreroll);
        int GetActualDataLength();
        int SetActualDataLength(int len);
        int GetMediaType(out IntPtr ppMediaType);
        int SetMediaType([In] IntPtr pMediaType);
        int IsDiscontinuity();
        int SetDiscontinuity([MarshalAs(UnmanagedType.Bool)] bool bDiscontinuity);
        int GetMediaTime(out long pTimeStart, out long pTimeEnd);
        int SetMediaTime([In] ref long pTimeStart, [In] ref long pTimeEnd);
    }

    public static class MediaTypeHelper
    {
        public const int BI_RGB = 0;
        public const long AvgTimePerFrame30Fps = 333333; // 10,000,000 / 30 in 100ns units (rounded)

        public static AMMediaType CreateRgb24_1280x720_30()
        {
            int width = 1280;
            int height = 720;
            int stride = width * 3;
            int imageSize = stride * height;

            var vih = new VIDEOINFOHEADER
            {
                rcSource = new RECT { left = 0, top = 0, right = width, bottom = height },
                rcTarget = new RECT { left = 0, top = 0, right = width, bottom = height },
                dwBitRate = imageSize * 8 * 30,
                dwBitErrorRate = 0,
                AvgTimePerFrame = AvgTimePerFrame30Fps,
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = width,
                    biHeight = height, // positive => bottom-up DIB (BGR layout)
                    biPlanes = 1,
                    biBitCount = 24,
                    biCompression = BI_RGB,
                    biSizeImage = imageSize,
                    biXPelsPerMeter = 0,
                    biYPelsPerMeter = 0,
                    biClrUsed = 0,
                    biClrImportant = 0
                }
            };

            int vihSize = Marshal.SizeOf(typeof(VIDEOINFOHEADER));
            IntPtr formatPtr = Marshal.AllocCoTaskMem(vihSize);
            Marshal.StructureToPtr(vih, formatPtr, fDeleteOld: false);

            return new AMMediaType
            {
                majorType = DirectShowGuids.MEDIATYPE_Video,
                subType = DirectShowGuids.MEDIASUBTYPE_RGB24,
                fixedSizeSamples = true,
                temporalCompression = false,
                sampleSize = imageSize,
                formatType = DirectShowGuids.FORMAT_VideoInfo,
                unkPtr = IntPtr.Zero,
                formatSize = vihSize,
                formatPtr = formatPtr
            };
        }

        public static IntPtr AllocMediaTypePtr(in AMMediaType mt)
        {
            AMMediaType clone = CloneMediaType(mt);
            IntPtr ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(AMMediaType)));
            try
            {
                Marshal.StructureToPtr(clone, ptr, fDeleteOld: false);
                return ptr;
            }
            catch
            {
                FreeMediaType(ref clone);
                Marshal.FreeCoTaskMem(ptr);
                throw;
            }
        }

        public static AMMediaType CloneMediaType(in AMMediaType mt)
        {
            AMMediaType clone = mt;
            clone.formatPtr = IntPtr.Zero;
            clone.formatSize = 0;

            if (mt.formatPtr != IntPtr.Zero && mt.formatSize > 0)
            {
                clone.formatPtr = Marshal.AllocCoTaskMem(mt.formatSize);
                var buffer = new byte[mt.formatSize];
                Marshal.Copy(mt.formatPtr, buffer, 0, mt.formatSize);
                Marshal.Copy(buffer, 0, clone.formatPtr, mt.formatSize);
                clone.formatSize = mt.formatSize;
            }

            if (clone.unkPtr != IntPtr.Zero)
            {
                Marshal.AddRef(clone.unkPtr);
            }

            return clone;
        }

        public static void FreeMediaType(ref AMMediaType mt)
        {
            if (mt.formatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mt.formatPtr);
                mt.formatPtr = IntPtr.Zero;
                mt.formatSize = 0;
            }

            if (mt.unkPtr != IntPtr.Zero)
            {
                Marshal.Release(mt.unkPtr);
                mt.unkPtr = IntPtr.Zero;
            }
        }

        public static void FreeMediaTypePtr(IntPtr pmt)
        {
            if (pmt == IntPtr.Zero) return;
            try
            {
                var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
                FreeMediaType(ref mt);
            }
            catch
            {
                // best-effort cleanup
            }
            finally
            {
                Marshal.FreeCoTaskMem(pmt);
            }
        }
    }

    internal static class Ole32
    {
        public const int COINIT_MULTITHREADED = 0x0;

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();
    }
}
