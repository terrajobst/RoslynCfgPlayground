using System.Runtime.InteropServices;

namespace Test
{
    class Program
    {
        static void Main(string[] args)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !SomeCondition())
                return;

            WindowsApi();
        }

        static void WindowsApi()
        {
        }

        static bool SomeCondition()
        {
            return false;
        }

        static bool SomeOtherCondition()
        {
            return false;
        }
    }
}
