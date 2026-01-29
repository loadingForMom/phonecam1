using System;

namespace PhoneCam.VirtualCam.Filter.DirectShow
{
    internal static class DirectShowGuids
    {
        // Media types
        public static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71"); // 'vids'
        public static readonly Guid FORMAT_VideoInfo = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
        public static readonly Guid MEDIASUBTYPE_RGB24 = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770");

        // Device categories
        public static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");

        // Common DirectShow CLSIDs
        public static readonly Guid CLSID_FilterMapper2 = new Guid("CDA42200-BD88-11D0-BD4E-00A0C911CE86");
        public static readonly Guid CLSID_MemoryAllocator = new Guid("1E651CC0-B199-11D0-8212-00C04FC32C45");
    }
}