using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace Aviary.App;

internal static class GuestCursor
{
    // WinUI treats a null ProtectedCursor as inheritance, not an invisible cursor.
    // Supply a real transparent cursor so parent controls cannot restore an arrow.
    public static InputCursor CreateTransparent()
    {
        var andMask = Enumerable.Repeat((byte)255, 128).ToArray();
        var xorMask = new byte[128];
        nint cursor = CreateCursor(0, 0, 0, 32, 32, andMask, xorMask);
        if (cursor == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint className = 0, factory = 0, result = 0;
        try
        {
            const string runtimeClass = "Microsoft.UI.Input.InputCursor";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out className));
            var iid = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref iid, out factory));
            // IInputCursorStaticsInterop derives from IInspectable (six vtable slots).
            nint method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 6 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateFromHCursor>(method);
            Marshal.ThrowExceptionForHR(create(factory, cursor, out result));
            return WinRT.MarshalInspectable<InputCursor>.FromAbi(result);
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) WindowsDeleteString(className);
            // CreateFromHCursor copies custom cursors; the WinRT cursor owns its copy.
            DestroyCursor(cursor);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateFromHCursor(nint instance, nint cursor, out nint result);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint CreateCursor(nint instance, int x, int y, int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyCursor(nint cursor);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(nint className, ref Guid iid, out nint factory);
}
