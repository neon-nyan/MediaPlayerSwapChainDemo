using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinUIPlayerBehindAcrylic.Native;

// Microsoft.UI.Composition.interop.h; the Windows.UI interface has a different IID.
[GeneratedComInterface]
[Guid("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICompositionDrawingSurfaceInterop
{
    [PreserveSig]
    int BeginDraw(nint updateRect, in Guid iid, out nint updateObject, out CompositionPoint updateOffset);

    [PreserveSig]
    int EndDraw();

    [PreserveSig]
    int Resize(CompositionSize sizePixels);

    [PreserveSig]
    int Scroll(nint scrollRect, nint clipRect, int offsetX, int offsetY);

    [PreserveSig]
    int ResumeDraw();

    [PreserveSig]
    int SuspendDraw();
}

[StructLayout(LayoutKind.Sequential)]
internal struct CompositionPoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CompositionSize
{
    public int Width;
    public int Height;
}
