using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PhoneCam.VirtualCam.Filter.DirectShow
{
    internal static class DirectShowRegistration
    {
        // Human-friendly name shown in DirectShow/GraphStudioNext lists
        public const string FilterName = "PhoneCam VirtualCam (Capture Source)";

        // NOTE: Keep this GUID stable once published, because it is the COM identity of the filter.
        public static readonly Guid FilterClsid = new Guid("3064B0F9-E26E-4301-9AC8-2BDF7C8C8A6C");

        public static void RegisterWithFilterMapper2()
        {
            Type mapperType;
            try { mapperType = Type.GetTypeFromCLSID(DirectShowGuids.CLSID_FilterMapper2); }
            catch { return; }

            var mapper = (IFilterMapper2)Activator.CreateInstance(mapperType);

            // Minimal registration: add to VideoInputDeviceCategory.
            // For a full implementation you'd provide REGFILTER2 pins and media types.
            Guid cat = DirectShowGuids.CLSID_VideoInputDeviceCategory;
            Guid clsid = FilterClsid;

            int hr = mapper.RegisterFilter(ref clsid, FilterName, out IMoniker moniker, ref cat, null, IntPtr.Zero);
            try { Marshal.ReleaseComObject(moniker); } catch { }
            try { Marshal.ReleaseComObject(mapper); } catch { }

            if (HResult.Failed(hr))
                Marshal.ThrowExceptionForHR(hr);
        }

        public static void UnregisterWithFilterMapper2()
        {
            Type mapperType;
            try { mapperType = Type.GetTypeFromCLSID(DirectShowGuids.CLSID_FilterMapper2); }
            catch { return; }

            var mapper = (IFilterMapper2)Activator.CreateInstance(mapperType);

            Guid cat = DirectShowGuids.CLSID_VideoInputDeviceCategory;
            Guid clsid = FilterClsid;

            int hr = mapper.UnregisterFilter(ref cat, null, ref clsid);
            try { Marshal.ReleaseComObject(mapper); } catch { }

            // UnregisterFilter may return "not found" if already unregistered. Ignore.
            _ = hr;
        }

        [Guid("B79BB0B0-33C1-11D1-ABE1-00A0C905F375")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFilterMapper2
        {
            int CreateCategory(ref Guid clsidCategory, int dwCategoryMerit, [MarshalAs(UnmanagedType.LPWStr)] string Description);
            int UnregisterFilter(ref Guid pclsidCategory, [MarshalAs(UnmanagedType.LPWStr)] string szInstance, ref Guid pclsidFilter);
            int RegisterFilter(ref Guid pclsidFilter, [MarshalAs(UnmanagedType.LPWStr)] string Name, out IMoniker ppMoniker,
                ref Guid pclsidCategory, [MarshalAs(UnmanagedType.LPWStr)] string szInstance, IntPtr prf2);
        }
    }
}