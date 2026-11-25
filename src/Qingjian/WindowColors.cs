using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Qingjian;

internal static class WindowColors
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // Enable dark immersive frame (DWMWA_USE_IMMERSIVE_DARK_MODE)
        Set(handle, 20, 1);
        // COLORREF is 0x00BBGGRR:
        // Border #262932 -> 0x322926
        Set(handle, 34, 0x322926);
        // Caption #121419 -> 0x191412
        Set(handle, 35, 0x191412);
        // Caption text #EDEDEF -> 0xEFEDED
        Set(handle, 36, 0xEFEDED);
    }

    private static void Set(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
}
