using System;
using System.Runtime.InteropServices;
using PhoneCam.VirtualCam.Filter.DirectShow;
using PhoneCam.VirtualCam.Filter.Ipc;
using PhoneCam.VirtualCam.Filter.Util;

namespace PhoneCam.VirtualCam.Filter.Filter
{
    /// <summary>
    /// DirectShow "Video Capture Source" filter skeleton.
    /// </summary>
    [ComVisible(true)]
    [Guid("3064B0F9-E26E-4301-9AC8-2BDF7C8C8A6C")]
    [ProgId("PhoneCam.VirtualCam.Filter")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualCamSourceFilter : IBaseFilter
    {
        internal const int Width = 1280;
        internal const int Height = 720;
        internal const int BytesPerPixel = 3;
        internal const int FrameSize = Width * Height * BytesPerPixel;

        private readonly object _sync = new object();
        private readonly VirtualCamOutputPin _outputPin;

        private FilterState _state = FilterState.Stopped;
        private IFilterGraph _graph;
        private string _name = DirectShowRegistration.FilterName;

        internal readonly LatestFrameBuffer LatestFrame = new LatestFrameBuffer(FrameSize);
        internal readonly TcpFrameReceiver Receiver;

        public VirtualCamSourceFilter()
        {
            _outputPin = new VirtualCamOutputPin(this);
            Receiver = new TcpFrameReceiver(LatestFrame, FrameSize, FilterLog.Info);
            Receiver.Start();
        }

        // IMPORTANT:
        // Do NOT attempt to register with FilterMapper2 from RegAsm callbacks.
        // A wrong/partial marshaling of REGFILTER2 can crash RegAsm with AccessViolationException.
        // We keep COM registration via RegAsm only. Device-category registration can be done later
        // with a dedicated native helper or a correct REGFILTER2 implementation.
        [ComRegisterFunction]
        public static void Register(Type t)
        {
            // Intentionally empty (safe)
        }

        [ComUnregisterFunction]
        public static void Unregister(Type t)
        {
            // Intentionally empty (safe)
        }

        ~VirtualCamSourceFilter()
        {
            try { Receiver.Dispose(); } catch { }
        }

        internal FilterState State
        {
            get { lock (_sync) return _state; }
        }

        #region IPersist (GetClassID)
        public int GetClassID(out Guid pClassId)
        {
            pClassId = DirectShowRegistration.FilterClsid;
            return HResult.S_OK;
        }
        #endregion

        #region IMediaFilter
        public int Stop()
        {
            lock (_sync)
            {
                if (_state == FilterState.Stopped)
                    return HResult.S_OK;

                _outputPin.StopStreaming();
                _state = FilterState.Stopped;
                return HResult.S_OK;
            }
        }

        public int Pause()
        {
            lock (_sync)
            {
                if (_state == FilterState.Paused)
                    return HResult.S_OK;

                _state = FilterState.Paused;
                return HResult.S_OK;
            }
        }

        public int Run(long tStart)
        {
            lock (_sync)
            {
                if (_state == FilterState.Running)
                    return HResult.S_OK;

                if (_outputPin.IsConnected)
                    _outputPin.StartStreaming();

                _state = FilterState.Running;
                return HResult.S_OK;
            }
        }

        public int GetState(int dwMilliSecsTimeout, out FilterState filtState)
        {
            lock (_sync)
            {
                filtState = _state;
                return HResult.S_OK;
            }
        }

        public int SetSyncSource(IReferenceClock pClock)
        {
            return HResult.S_OK;
        }

        public int GetSyncSource(out IReferenceClock pClock)
        {
            pClock = null;
            return HResult.S_OK;
        }
        #endregion

        #region IBaseFilter
        public int EnumPins(out IEnumPins ppEnum)
        {
            ppEnum = new VirtualCamEnumPins(_outputPin);
            return HResult.S_OK;
        }

        public int FindPin(string id, out IPin ppPin)
        {
            if (string.Equals(id, VirtualCamOutputPin.PinId, StringComparison.OrdinalIgnoreCase))
            {
                ppPin = _outputPin;
                return HResult.S_OK;
            }

            ppPin = null;
            return HResult.VFW_E_NOT_CONNECTED;
        }

        public int QueryFilterInfo(out FilterInfo pInfo)
        {
            lock (_sync)
            {
                pInfo = new FilterInfo
                {
                    achName = _name,
                    pGraph = _graph
                };

                // AddRef on the graph interface as per DirectShow rules.
                if (_graph != null)
                {
                    IntPtr unk = Marshal.GetIUnknownForObject(_graph);
                    try
                    {
                        Marshal.AddRef(unk);
                    }
                    finally
                    {
                        Marshal.Release(unk);
                    }
                }

                return HResult.S_OK;
            }
        }

        public int JoinFilterGraph(IFilterGraph pGraph, string pName)
        {
            lock (_sync)
            {
                _graph = pGraph;
                if (!string.IsNullOrWhiteSpace(pName))
                    _name = pName;

                return HResult.S_OK;
            }
        }

        public int QueryVendorInfo(out string pVendorInfo)
        {
            pVendorInfo = "PhoneCam";
            return HResult.S_OK;
        }
        #endregion

        private sealed class VirtualCamEnumPins : IEnumPins
        {
            private readonly IPin[] _pins;
            private int _index;

            public VirtualCamEnumPins(IPin outputPin)
            {
                _pins = new[] { outputPin };
                _index = 0;
            }

            public int Next(int cPins, IPin[] ppPins, IntPtr pcFetched)
            {
                if (ppPins == null || ppPins.Length < cPins)
                    return HResult.E_INVALIDARG;

                int fetched = 0;
                while (fetched < cPins && _index < _pins.Length)
                {
                    ppPins[fetched] = _pins[_index];
                    fetched++;
                    _index++;
                }

                if (pcFetched != IntPtr.Zero)
                    Marshal.WriteInt32(pcFetched, fetched);

                return fetched == cPins ? HResult.S_OK : HResult.S_FALSE;
            }

            public int Skip(int cPins)
            {
                _index = Math.Min(_pins.Length, _index + cPins);
                return _index < _pins.Length ? HResult.S_OK : HResult.S_FALSE;
            }

            public int Reset()
            {
                _index = 0;
                return HResult.S_OK;
            }

            public int Clone(out IEnumPins ppEnum)
            {
                ppEnum = new VirtualCamEnumPins(_pins[0]) { _index = _index };
                return HResult.S_OK;
            }
        }
    }
}