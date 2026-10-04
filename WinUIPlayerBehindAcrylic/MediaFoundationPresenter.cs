using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;
using FFmpegInteropX;
using Hi3Helper.Win32.ManagedTools;
using Hi3Helper.Win32.Native.Enums.D2D;
using Hi3Helper.Win32.Native.Enums.D3D;
using Hi3Helper.Win32.Native.Enums.DXGI;
using Hi3Helper.Win32.Native.Interfaces.D2D;
using Hi3Helper.Win32.Native.Interfaces.D3D;
using Hi3Helper.Win32.Native.Interfaces.DXGI;
using Hi3Helper.Win32.Native.LibraryImport;
using Hi3Helper.Win32.Native.Structs.D2D;
using Hi3Helper.Win32.Native.Structs.DXGI;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Utility.Log;
using WinRT;

// ReSharper disable InconsistentNaming

namespace WinUIPlayerBehindAcrylic;

internal sealed class MediaFoundationPresenter : IVideoFramePresenter
{
    private static readonly Guid IID_ICompositorInterop = new("FAB19398-6D19-4D8A-B752-8F096C396069");
    private static readonly Guid IID_ID3D11Device       = new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");

    public event EventHandler? MediaEnded;

    private       int RenderWidth   = 1920;
    private       int RenderHeight  = 1080;
    private       int RenderOffsetX = 0;
    private       int RenderOffsetY = 0;
    private const int BufferCount   = 2;

    private readonly Lock _renderLock = new();

    private Grid? _host;

    private Compositor?              _compositor;
    private SpriteVisual?            _videoVisual;
    private CompositionSurfaceBrush? _videoBrush;
    private ICompositionSurface?     _compositionSurface;

    public  MediaPlayer?       MediaPlayer;
    private FFmpegMediaSource? _ffmpegSource;

    private ID3D11Device?    _d3dDevice;
    private IDXGISwapChain3? _swapChain;

    private IDirect3DSurface?[]? _backBufferSurfaces;

    private bool _disposed;
    private bool _recreating;

    public void Initialize(Grid host, CompositionStretch stretch = CompositionStretch.UniformToFill)
    {
        _host = host;

        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _videoBrush = _compositor.CreateSurfaceBrush();

        _videoBrush.Stretch                  = stretch;
        _videoBrush.HorizontalAlignmentRatio = 0.5f;
        _videoBrush.VerticalAlignmentRatio   = 0.5f;

        RenderWidth  = (int)host.ActualWidth;
        RenderHeight = (int)host.ActualHeight;

        _videoVisual       = _compositor.CreateSpriteVisual();
        _videoVisual.Brush = _videoBrush;

        // Fill VideoHost automatically.
        _videoVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _videoVisual);

        CreateDeviceResources();

        _host!.SizeChanged += HostOnSizeChanged;
    }

    public void ToggleVideo(bool isEnable)
    {
        ElementCompositionPreview.SetElementChildVisual(_host, isEnable ? _videoVisual : null);
    }

    // ReSharper disable once AsyncVoidMethod
    public void Open(Uri uri, bool isLoop = true)
    {
        using (_renderLock.EnterScope())
        {
            DisposeMediaPlayer();
            MediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = isLoop
            };

            _ffmpegSource = GetMediaSource(uri, MediaPlayer).GetAwaiter().GetResult();

            MediaPlayer.MediaEnded                += MediaPlayer_OnMediaEnded;
            MediaPlayer.VideoFrameAvailable       += MediaPlayer_OnVideoFrameAvailable;
            MediaPlayer.IsVideoFrameServerEnabled =  true;
            MediaPlayer.Play();
        }
    }

    private static async Task<FFmpegMediaSource> GetMediaSource(Uri uri, MediaPlayer mediaPlayer)
    {
        MediaSourceConfig ffmpegConfig = new()
        {
            Video =
            {
                MaxDecoderThreads     = (uint)Environment.ProcessorCount,
                VideoOutputAllow10bit = true,
                VideoOutputAllowBgra8 = true,
                VideoOutputAllowNv12  = true
            },
            General =
            {
                ReadAheadBufferEnabled  = true,
                ReadAheadBufferDuration = TimeSpan.FromSeconds(15)
            }
        };

        FFmpegMediaSource source =
            await (uri.IsFile
                ? FFmpegMediaSource.CreateFromFileAsync(uri.LocalPath)
                : FFmpegMediaSource.CreateFromUriAsync(uri.ToString(), ffmpegConfig));
        source.OpenWithMediaPlayerAsync(mediaPlayer);
        return source;
    }

    private void CalculateHostToFrameSize()
    {
        if (_host == null)
            return;

        double hostWidth  = _host.ActualWidth;
        double hostHeight = _host.ActualHeight;

        /*
        if (_videoBrush!.Stretch == CompositionStretch.UniformToFill)
        {
            double videoWidth  = MediaPlayer?.PlaybackSession?.NaturalVideoWidth ?? 0;
            double videoHeight = MediaPlayer?.PlaybackSession?.NaturalVideoHeight ?? 0;

            double scale = Math.Max(hostWidth / videoWidth, hostHeight / videoHeight);

            double newWidth  = videoWidth * scale;
            double newHeight = videoHeight * scale;

            RenderWidth   = (int)newWidth;
            RenderHeight  = (int)newHeight;
            RenderOffsetX = Math.Abs((int)((hostWidth - newWidth) / 2));
            RenderOffsetY = Math.Abs((int)((hostHeight - newHeight) / 2));
            return;
        }
        */

        // RenderOffsetX = 0;
        // RenderOffsetY = 0;
        RenderWidth   = (int)hostWidth;
        RenderHeight  = (int)hostHeight;
    }

    private void HostOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CalculateHostToFrameSize();
        ResizeSwapChain(RenderWidth, RenderHeight);

        using (_renderLock.EnterScope())
        {
            DrawFrame(); // Re-draw frame
        }
    }

    private void DisposeMediaPlayer()
    {
        if (_ffmpegSource is not null)
        {
            _ffmpegSource.Dispose();
            _ffmpegSource = null;
        }

        if (MediaPlayer is not null)
        {
            MediaPlayer.MediaEnded          -= MediaPlayer_OnMediaEnded;
            MediaPlayer.VideoFrameAvailable -= MediaPlayer_OnVideoFrameAvailable;

            MediaPlayer.Pause();
            MediaPlayer.Dispose();
            MediaPlayer = null;
        }
    }

    public void Dispose()
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed)
                return;

            _disposed = true;
            DisposeMediaPlayer();

            if (_host != null)
                _host.SizeChanged -= HostOnSizeChanged;

            _videoBrush?.Surface = null;

            ReleaseBackBufferSurfaces();

            // _compositionSurface?.Dispose();
            _compositionSurface = null;

            // _swapChain?.Dispose();
            _swapChain = null;

            // _d3dDevice?.Dispose();
            _d3dDevice = null;

            ElementCompositionPreview.SetElementChildVisual(_host!, null);

            _videoVisual?.Dispose();
            _videoBrush?.Dispose();

            _videoVisual = null;
            _videoBrush  = null;
        }
    }

    private unsafe void CreateDeviceResources()
    {
        using (_renderLock.EnterScope())
        {
            _d3dDevice = CreateD3DDeviceFromSharedCanvasDevice() ??
                         CreateD3DDevice(out _, out _);

            DXGI_SWAP_CHAIN_DESC1 desc = CreateSwapChainDescription(RenderWidth, RenderHeight);

            _swapChain = CreateSwapChainForComposition(_d3dDevice!, desc);
            CreateBackBufferSurfaces();

            _compositionSurface  = CreateCompositionSurfaceForSwapChain(_compositor!, _swapChain);
            _videoBrush!.Surface = _compositionSurface;
        }
    }

    private static DXGI_SWAP_CHAIN_DESC1 CreateSwapChainDescription(int width, int height)
    {
        return new DXGI_SWAP_CHAIN_DESC1
        {
            Width  = (uint)width,
            Height = (uint)height,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo = 0,
            SampleDesc = new DXGI_SAMPLE_DESC
            {
                Count   = 1,
                Quality = 0
            },
            BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = BufferCount,
            Scaling     = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect  = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode   = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags       = 0
        };
    }

    private static unsafe ID3D11Device? CreateD3DDeviceFromSharedCanvasDevice()
    {
        CanvasDevice sharedCanvasDevice  = CanvasDevice.GetSharedDevice();
        nint         sharedCanvasDeviceP = ((IWinRTObject)sharedCanvasDevice).NativeObject.ThisPtr;

        // -- Obtain an existing D3D11 Device from a shared CanvasDevice.
        if (!ComMarshal<IDirect3DDxgiInterfaceAccess>
                .TryCreateComObjectFromReference(sharedCanvasDeviceP,
                                                 out IDirect3DDxgiInterfaceAccess? access,
                                                 out _,
                                                 false))
        {
            return null;
        }

        int hr = access.GetInterface(in IID_ID3D11Device, out nint d3d11DeviceFromSharedCanvasP);
        if (hr != 0) return null;

        return !ComMarshal<ID3D11Device>
            .TryCreateComObjectFromReference(d3d11DeviceFromSharedCanvasP,
                                             out ID3D11Device? d3d11Device,
                                             out _) ? null : d3d11Device;
    }

    private static unsafe ID3D11Device CreateD3DDevice(out ID2D1Factory2 d2d1Factory, out ID2D1Device1 d2d1Device)
    // private static unsafe ID3D11Device CreateD3DDevice()
    {
        // -- Create new D3D11 Device
        Unsafe.SkipInit(out d2d1Factory);
        Unsafe.SkipInit(out d2d1Device);

        const uint D3D11_SDK_VERSION = 7;
        Span<D3D_FEATURE_LEVEL> levels =
        [
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
        ];

        D3D11_CREATE_DEVICE_FLAG flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;

#if DEBUG
        flags |= D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG;
#endif

        D3D_FEATURE_LEVEL selectedFeatureLevel = 0;

        int hr = PInvoke.D3D11CreateDevice(nint.Zero,
                                           D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                                           0,
                                           flags,
                                           levels,
                                           4,
                                           D3D11_SDK_VERSION,
                                           out nint deviceP,
                                           ref selectedFeatureLevel,
                                           out nint _);

        if (hr < 0)
        {
            // Optional WARP fallback.
            hr = PInvoke.D3D11CreateDevice(nint.Zero,
                                           D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
                                           0,
                                           flags,
                                           levels,
                                           4,
                                           D3D11_SDK_VERSION,
                                           out deviceP,
                                           ref selectedFeatureLevel,
                                           out nint _);
        }
        Marshal.ThrowExceptionForHR(hr);

        // -- Create D2D1 Device
        D2D1_FACTORY_OPTIONS d2d1FactoryOpts = new()
        {
            debugLevel = D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_NONE
        };

        ComMarshal<IDXGIDevice>.TryCreateComObjectFromReference(deviceP, out IDXGIDevice? dxgiDevice, out _);
        hr = PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED,
                                       typeof(ID2D1Factory2).GUID,
                                       in d2d1FactoryOpts,
                                       out nint d2d1FactoryP);
        Marshal.ThrowExceptionForHR(hr);
        ComMarshal<ID2D1Factory2>.TryCreateComObjectFromReference(d2d1FactoryP, out d2d1Factory!, out _);
        d2d1Factory.CreateDevice(dxgiDevice!, out d2d1Device);

        ComMarshal<ID3D11Device>.TryCreateComObjectFromReference(deviceP, out ID3D11Device? device, out _);
        return device!;
    }

    private static IDXGISwapChain3 CreateSwapChainForComposition(
        ID3D11Device          device,
        DXGI_SWAP_CHAIN_DESC1 desc)
    {
        ComMarshal<ID3D11Device>.TryCastComObjectAs(device, out IDXGIDevice? dxgiDevice, out _);

        dxgiDevice!.GetAdapter(out IDXGIAdapter? adapter);
        adapter!.GetParent(typeof(IDXGIFactory2).GUID, out nint factoryP);

        ComMarshal<IDXGIFactory2>.TryCreateComObjectFromReference(factoryP, out IDXGIFactory2? factory, out _);
        factory!.CreateSwapChainForComposition(device, desc, null, out IDXGISwapChain1 swapChain);

        ComMarshal<IDXGISwapChain1>.TryCastComObjectAs(swapChain, out IDXGISwapChain3? swapChain3, out _);
        return swapChain3!;
    }

    public static unsafe ICompositionSurface CreateCompositionSurfaceForSwapChain(
        Compositor      compositor,
        IDXGISwapChain3 swapChain)
    {
        nint compositorAbi = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.QueryInterface(compositorAbi, IID_ICompositorInterop, out nint compositorInteropP);

        nint surfaceAbi     = nint.Zero;
        nint dxgiSwapChainP = nint.Zero;
        try
        {
            void** compositorInteropVtable = *(void***)compositorInteropP;

            dxgiSwapChainP = (nint)ComInterfaceMarshaller<IDXGISwapChain3>.ConvertToUnmanaged(swapChain);
            var createCompositionSurfaceForSwapChain =
                (delegate* unmanaged[Stdcall]<
                    nint,     // this
                    nint,     // IDXGISwapChain1*
                    out nint, // ICompositionSurface**
                    int       // HRESULT
                    >)compositorInteropVtable[5]; // UNDOCUMENTED: ICompositorInterop.CreateCompositionSurfaceForSwapChain

            createCompositionSurfaceForSwapChain(compositorInteropP, dxgiSwapChainP, out surfaceAbi);
            ICompositionSurface compositionSurface = MarshalInterface<ICompositionSurface>.FromAbi(surfaceAbi);
            return compositionSurface;
        }
        finally
        {
            if (surfaceAbi != nint.Zero) Marshal.Release(surfaceAbi);
            if (dxgiSwapChainP != nint.Zero) Marshal.Release(dxgiSwapChainP);
            if (compositorInteropP != nint.Zero) Marshal.Release(compositorInteropP);
        }
    }

    private void MediaPlayer_OnMediaEnded(MediaPlayer sender, object args)
        => MediaEnded?.Invoke(this, null!);

    private void MediaPlayer_OnVideoFrameAvailable(
        MediaPlayer sender,
        object      args)
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed ||
                _recreating ||
                _swapChain is null ||
                _backBufferSurfaces is null)
            {
                return;
            }

            DrawFrame();
        }
    }

    private void DrawFrame()
    {
        try
        {
            uint             index       = _swapChain!.GetCurrentBackBufferIndex();
            IDirect3DSurface destination = _backBufferSurfaces![index]!;
            MediaPlayer!.CopyFrameToVideoSurface(destination);

            _swapChain.Present(0, 0);
        }
        catch (Exception ex) when (IsDeviceLost(ex.HResult))
        {
            QueueDeviceRecreation();
        }
    }

    private static bool IsDeviceLost(int hr)
    {
        const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        const int DXGI_ERROR_DEVICE_HUNG    = unchecked((int)0x887A0006);
        const int DXGI_ERROR_DEVICE_RESET   = unchecked((int)0x887A0007);

        return hr is DXGI_ERROR_DEVICE_REMOVED
                  or DXGI_ERROR_DEVICE_HUNG
                  or DXGI_ERROR_DEVICE_RESET;
    }

    private void QueueDeviceRecreation()
    {
        if (_recreating)
            return;

        _recreating = true;

        _host!.DispatcherQueue.TryEnqueue(RecreateDeviceResources);
    }

    private unsafe void CreateBackBufferSurfaces()
    {
        _backBufferSurfaces = new IDirect3DSurface[BufferCount];

        for (uint i = 0; i < BufferCount; i++)
        {
            nint dxgiSurfaceP      = nint.Zero;
            nint ppGraphicsSurface = nint.Zero;
            try
            {
                _swapChain!.GetBuffer(i, typeof(IDXGISurface).GUID, out dxgiSurfaceP);
                PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfaceP, out ppGraphicsSurface);
                _backBufferSurfaces[i] = MarshalInterface<IDirect3DSurface>.FromAbi(ppGraphicsSurface);
            }
            finally
            {
                if (ppGraphicsSurface != nint.Zero) Marshal.Release(ppGraphicsSurface);
                if (dxgiSurfaceP != nint.Zero) Marshal.Release(dxgiSurfaceP);
            }
        }
    }

    private void ReleaseBackBufferSurfaces()
    {
        if (_backBufferSurfaces is null)
            return;

        foreach (IDirect3DSurface? surface in _backBufferSurfaces)
        {
            surface?.Dispose();
        }

        _backBufferSurfaces = null;
    }

    private void RecreateDeviceResources()
    {
        using (_renderLock.EnterScope())
        {
            try
            {
                _videoBrush?.Surface = null;
                ReleaseBackBufferSurfaces();

                // _compositionSurface?.Dispose();
                _compositionSurface = null;
                // _swapChain?.Dispose();
                _swapChain = null;
                // _d3dDevice?.Dispose();
                _d3dDevice = null;
                _d3dDevice = CreateD3DDevice(out _, out _);
                // _d3dDevice = CreateD3DDevice();

                DXGI_SWAP_CHAIN_DESC1 desc = CreateSwapChainDescription(RenderWidth, RenderHeight);
                _swapChain = CreateSwapChainForComposition(_d3dDevice!, desc);

                CreateBackBufferSurfaces();
                _compositionSurface = CreateCompositionSurfaceForSwapChain(_compositor!, _swapChain);
                _videoBrush!.Surface = _compositionSurface;
            }
            finally
            {
                _recreating = false;
            }
        }
    }

    private void ResizeSwapChain(
        int width,
        int height)
    {
        using (_renderLock.EnterScope())
        {
            ReleaseBackBufferSurfaces();

            Logger.LogWriteLine($"Canvas Resized: {RenderOffsetX}x{RenderOffsetY} {width}x{height}", LogType.Debug);
            _swapChain!.ResizeBuffers(BufferCount,
                                      (uint)width,
                                      (uint)height,
                                      DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                                      0);

            CreateBackBufferSurfaces();
        }
    }
}

[GeneratedComInterface]
[Guid("FAB19398-6D19-4D8A-B752-8F096C396069")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICompositorInterop
{
    [PreserveSig]
    int CreateCompositionSurfaceForHandle(
        nint     swapChainHandle,
        out nint result);

    [PreserveSig]
    int CreateCompositionSurfaceForSwapChain(
        nint     swapChain,
        out nint result);

    [PreserveSig]
    int CreateGraphicsDevice(
        nint     renderingDevice,
        out nint result);
}

[GeneratedComInterface]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe partial interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig]
    int GetInterface(
        in  Guid iid,
        out nint ppv);
}