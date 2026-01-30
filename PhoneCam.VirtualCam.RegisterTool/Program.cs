using System;
using PhoneCam.VirtualCam.Filter.DirectShow;

namespace PhoneCam.VirtualCam.RegisterTool
{
    internal static class Program
    {
        // Usage:
        //   PhoneCam.VirtualCam.RegisterTool.exe register
        //   PhoneCam.VirtualCam.RegisterTool.exe unregister
        //
        // Must be run elevated (admin) to register in device category.
        public static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return PrintUsage();
            }

            string cmd = args[0].Trim().ToLowerInvariant();

            try
            {
                switch (cmd)
                {
                    case "register":
                    case "reg":
                        DirectShowRegistration.RegisterWithFilterMapper2();
                        Console.WriteLine("Registered in Video Capture Sources category.");
                        return 0;

                    case "unregister":
                    case "unreg":
                        DirectShowRegistration.UnregisterWithFilterMapper2();
                        Console.WriteLine("Unregistered from Video Capture Sources category.");
                        return 0;

                    default:
                        return PrintUsage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 2;
            }
        }

        private static int PrintUsage()
        {
            Console.WriteLine("PhoneCam.VirtualCam.RegisterTool");
            Console.WriteLine("Usage:");
            Console.WriteLine("  PhoneCam.VirtualCam.RegisterTool.exe register");
            Console.WriteLine("  PhoneCam.VirtualCam.RegisterTool.exe unregister");
            return 1;
        }
    }
}
