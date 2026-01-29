using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PhoneCam.VirtualCam.Filter.DirectShow;

namespace PhoneCam.VirtualCam.Filter.Filter
{
    /// <summary>
    /// Single output pin for the virtual camera source.
    ///
    /// Streaming model: push mode.
    /// When connected to a downstream input pin (IMemInputPin), we allocate samples via IMemAllocator
    /// and call IMemInputPin.Receive() at ~30 fps.
    /// </summary>
    internal sealed class VirtualCamOutputPin : IPin
    {
        public const string PinName = "Capture";
        public const string PinId = "Capture";

        private readonly VirtualCamSourceFilter _filter;
        private readonly object _sync = new object();

        private IPin _connectedPin;
        private IMemInputPin _memInput;
        private IMemAllocator _allocator;

        private Thread _thread;
        private volatile bool _stop;

        private AMMediaType _mt; // fixed media type (includes allocated formatPtr)

        public VirtualCamOutputPin(VirtualCamSourceFilter filter)
        {
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            _mt = MediaTypeHelper.CreateRgb24_1280x720_30();
        }

        public bool IsConnected
        {
            get { lock (_sync) return _connectedPin != null; }
        }

        public int Connect(IPin pReceivePin, IntPtr pmt)
        {
            if (pReceivePin == null) return HResult.E_POINTER;

            lock (_sync)
            {
                if (_connectedPin != null)
                    return HResult.VFW_E_ALREADY_CONNECTED;

                // Only accept our fixed media type.
                IntPtr mtPtr = MediaTypeHelper.AllocMediaTypePtr(_mt);
                try
                {
                    int hr = pReceivePin.ReceiveConnection(this, mtPtr);
                    if (HResult.Failed(hr))
                        return hr;

                    _connectedPin = pReceivePin;
                    _memInput = pReceivePin as IMemInputPin;

                    if (_memInput == null)
                        return HResult.VFW_E_NO_ACCEPTABLE_TYPES;

                    hr = SetupAllocator();
                    return hr;
                }
                finally
                {
                    MediaTypeHelper.FreeMediaTypePtr(mtPtr);
                }
            }
        }

        public int ReceiveConnection(IPin pConnector, IntPtr pmt)
        {
            // Output pin does not accept incoming connections.
            return HResult.E_NOTIMPL;
        }

        public int Disconnect()
        {
            lock (_sync)
            {
                StopStreaming();

                _memInput = null;
                _allocator = null;
                _connectedPin = null;

                return HResult.S_OK;
            }
        }

        public int ConnectedTo(out IPin ppPin)
        {
            lock (_sync)
            {
                if (_connectedPin == null)
                {
                    ppPin = null;
                    return HResult.VFW_E_NOT_CONNECTED;
                }

                ppPin = _connectedPin;
                return HResult.S_OK;
            }
        }

        public int ConnectionMediaType(out AMMediaType pmt)
        {
            lock (_sync)
            {
                if (_connectedPin == null)
                {
                    pmt = default;
                    return HResult.VFW_E_NOT_CONNECTED;
                }

                // Return a copy; caller is responsible for freeing the format block in native scenarios.
                // Here we return a managed struct copy (formatPtr still points to our allocated block).
                pmt = _mt;
                return HResult.S_OK;
            }
        }

        public int QueryPinInfo(out PinInfo pInfo)
        {
            pInfo = new PinInfo
            {
                pFilter = _filter,
                dir = PinDirection.Output,
                achName = PinName
            };
            return HResult.S_OK;
        }

        public int QueryDirection(out PinDirection pPinDir)
        {
            pPinDir = PinDirection.Output;
            return HResult.S_OK;
        }

        public int QueryId(out string Id)
        {
            Id = PinId;
            return HResult.S_OK;
        }

        public int QueryAccept(IntPtr pmt)
        {
            if (pmt == IntPtr.Zero)
                return HResult.E_POINTER;

            try
            {
                var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
                if (mt.majorType == _mt.majorType &&
                    mt.subType == _mt.subType &&
                    mt.formatType == _mt.formatType &&
                    mt.sampleSize == _mt.sampleSize)
                    return HResult.S_OK;

                return HResult.VFW_E_TYPE_NOT_ACCEPTED;
            }
            catch
            {
                return HResult.VFW_E_TYPE_NOT_ACCEPTED;
            }
        }

        public int EnumMediaTypes(out IEnumMediaTypes ppEnum)
        {
            ppEnum = new VirtualCamEnumMediaTypes(_mt);
            return HResult.S_OK;
        }

        public int QueryInternalConnections(IntPtr apPin, ref int nPin)
        {
            // No internal connections.
            nPin = 0;
            return HResult.S_OK;
        }

        public int EndOfStream() => HResult.S_OK;
        public int BeginFlush() => HResult.S_OK;
        public int EndFlush() => HResult.S_OK;

        public int NewSegment(long tStart, long tStop, double dRate) => HResult.S_OK;

        private int SetupAllocator()
        {
            if (_memInput == null)
                return HResult.VFW_E_NO_ACCEPTABLE_TYPES;

            int hr = _memInput.GetAllocator(out var allocator);
            if (HResult.Failed(hr) || allocator == null)
            {
                // Fall back to default allocator
                try
                {
                    Type allocType = Type.GetTypeFromCLSID(DirectShowGuids.CLSID_MemoryAllocator);
                    allocator = allocType != null ? (IMemAllocator)Activator.CreateInstance(allocType) : null;
                    if (allocator == null) return HResult.E_FAIL;
                }
                catch
                {
                    return HResult.E_FAIL;
                }
            }

            var req = new ALLOCATOR_PROPERTIES
            {
                cBuffers = 4,
                cbBuffer = VirtualCamSourceFilter.FrameSize,
                cbAlign = 1,
                cbPrefix = 0
            };

            hr = allocator.SetProperties(ref req, out _);
            if (HResult.Failed(hr))
                return hr;

            hr = _memInput.NotifyAllocator(allocator, bReadOnly: false);
            if (HResult.Failed(hr))
                return hr;

            _allocator = allocator;
            return HResult.S_OK;
        }

        internal void StartStreaming()
        {
            lock (_sync)
            {
                if (_thread != null) return;
                if (_memInput == null || _allocator == null) return;

                _stop = false;

                // Commit allocator
                _allocator.Commit();

                _thread = new Thread(StreamThreadMain)
                {
                    IsBackground = true,
                    Name = "PhoneCam.VirtualCam Streamer"
                };
                _thread.Start();
            }
        }

        internal void StopStreaming()
        {
            lock (_sync)
            {
                _stop = true;
                if (_thread != null && !_thread.Join(1500))
                {
                    // best effort
                }
                _thread = null;

                try
                {
                    if (_allocator != null) _allocator.Decommit();
                }
                catch
                {
                    // ignore
                }
            }
        }

        private void StreamThreadMain()
        {
            // Use a monotonic clock to pace 30 fps, and to generate sample times.
            long frameDuration = MediaTypeHelper.AvgTimePerFrame30Fps; // in 100ns units
            var sw = Stopwatch.StartNew();
            long frameIndex = 0;

            while (!_stop)
            {
                IMemInputPin memInput;
                IMemAllocator allocator;
                lock (_sync)
                {
                    memInput = _memInput;
                    allocator = _allocator;
                }

                if (memInput == null || allocator == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Get frame payload from IPC queue (or generate black frame if none).
                byte[] frame;
                bool got = _filter.FrameQueue.TryDequeue(timeoutMs: 5, out frame);
                if (!got || frame == null || frame.Length != VirtualCamSourceFilter.FrameSize)
                {
                    frame = BlackFrameCache.Instance;
                }

                int hr = allocator.GetBuffer(out var sample, IntPtr.Zero, IntPtr.Zero, 0);
                if (HResult.Failed(hr) || sample == null)
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    hr = sample.GetPointer(out var bufPtr);
                    if (HResult.Succeeded(hr) && bufPtr != IntPtr.Zero)
                    {
                        Marshal.Copy(frame, 0, bufPtr, frame.Length);
                        sample.SetActualDataLength(frame.Length);

                        long start = frameIndex * frameDuration;
                        long end = start + frameDuration;
                        sample.SetTime(ref start, ref end);
                        sample.SetSyncPoint(true);
                    }

                    memInput.Receive(sample);
                }
                catch
                {
                    // If downstream fails, stop streaming.
                    break;
                }
                finally
                {
                    try { Marshal.ReleaseComObject(sample); } catch { }
                }

                frameIndex++;

                // Pace roughly at 30 fps.
                long targetTicks100ns = frameIndex * frameDuration;
                long elapsed100ns = sw.ElapsedTicks * 10_000_000 / Stopwatch.Frequency;
                long remaining100ns = targetTicks100ns - elapsed100ns;
                if (remaining100ns > 0)
                {
                    int sleepMs = (int)(remaining100ns / 10_000); // 1ms = 10,000 *100ns
                    if (sleepMs > 0) Thread.Sleep(Math.Min(10, sleepMs));
                    else Thread.Yield();
                }
                else
                {
                    Thread.Yield();
                }
            }
        }

        private sealed class VirtualCamEnumMediaTypes : IEnumMediaTypes
        {
            private readonly AMMediaType _mt;
            private int _index;

            public VirtualCamEnumMediaTypes(AMMediaType mt)
            {
                _mt = mt;
                _index = 0;
            }

            public int Next(int cMediaTypes, IntPtr[] ppMediaTypes, IntPtr pcFetched)
            {
                if (ppMediaTypes == null || ppMediaTypes.Length < cMediaTypes)
                    return HResult.E_INVALIDARG;

                int fetched = 0;
                while (fetched < cMediaTypes && _index < 1)
                {
                    // Allocate a native AMMediaType copy for the caller.
                    IntPtr mtPtr = MediaTypeHelper.AllocMediaTypePtr(_mt);
                    ppMediaTypes[fetched] = mtPtr;

                    fetched++;
                    _index++;
                }

                if (pcFetched != IntPtr.Zero)
                    Marshal.WriteInt32(pcFetched, fetched);

                return fetched == cMediaTypes ? HResult.S_OK : HResult.S_FALSE;
            }

            public int Skip(int cMediaTypes)
            {
                _index = Math.Min(1, _index + cMediaTypes);
                return _index < 1 ? HResult.S_OK : HResult.S_FALSE;
            }

            public int Reset()
            {
                _index = 0;
                return HResult.S_OK;
            }

            public int Clone(out IEnumMediaTypes ppEnum)
            {
                ppEnum = new VirtualCamEnumMediaTypes(_mt) { _index = _index };
                return HResult.S_OK;
            }
        }

        private sealed class BlackFrameCache
        {
            public static readonly byte[] Instance = new byte[VirtualCamSourceFilter.FrameSize];
        }
    }
}