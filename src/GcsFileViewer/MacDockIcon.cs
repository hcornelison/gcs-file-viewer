using System.Runtime.InteropServices;

namespace GcsFileViewer;

/// <summary>
/// Sets the app's icon in the macOS Dock. An app run outside a .app bundle, as with "dotnet run", otherwise gets
/// the generic one, and macOS has no window icon to show instead.
/// </summary>
internal static partial class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    /// <summary>Shows a PNG as the Dock icon. Does nothing on other systems, or if macOS can't read the image.</summary>
    public static void Set(byte[] png)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // [[NSApplication sharedApplication] setApplicationIconImage:[[NSImage alloc] initWithData:[NSData dataWithBytes:length:]]]
        var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
        try
        {
            var data = Send(Class("NSData"), Selector("dataWithBytes:length:"), handle.AddrOfPinnedObject(), (nuint)png.Length);
            var image = Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
            if (image != IntPtr.Zero)
            {
                Send(Send(Class("NSApplication"), Selector("sharedApplication")), Selector("setApplicationIconImage:"), image);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);
}
