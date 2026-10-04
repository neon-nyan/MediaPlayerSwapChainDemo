using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;
using FFmpegInteropX;
using Hi3Helper.Win32.ManagedTools;
using Hi3Helper.Win32.Native.Enums.D3D;
using Hi3Helper.Win32.Native.Enums.DXGI;
using Hi3Helper.Win32.Native.Interfaces.D3D;
using Hi3Helper.Win32.Native.Interfaces.DXGI;
using Hi3Helper.Win32.Native.LibraryImport;
using Hi3Helper.Win32.Native.Structs.D3D;
using Hi3Helper.Win32.Native.Structs.DXGI;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Utility.Log;
using WinRT;
using WinUIPlayerBehindAcrylic.Native;

// ReSharper disable InconsistentNaming

namespace WinUIPlayerBehindAcrylic;

internal sealed class MediaFoundationPresenter : IVideoFramePresenter
{
    public event EventHandler? MediaEnded;

    private int RenderWidth;
    private int RenderHeight;

    private readonly Lock _renderLock = new();

    private Grid? _host;

    private Compositor?                        _compositor;
    private SpriteVisual?                      _videoVisual;
    private CompositionSurfaceBrush?           _videoBrush;
    private CompositionDrawingSurface?         _compositionSurface;
    private CompositionGraphicsDevice?         _compositionGraphicsDevice;
    private ICompositionDrawingSurfaceInterop? _drawingSurfaceInterop;

    public  MediaPlayer?       MediaPlayer;
    private FFmpegMediaSource? _ffmpegSource;

    private ID3D11Device?        _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private nint                 _frameTexture;
    private IDirect3DSurface?    _frameSurface;

    private bool _disposed;
    private bool _recreating;
    private bool _hasFrame;

    public void Initialize(Grid host, CompositionStretch stretch = CompositionStretch.UniformToFill)
    {
        _host = host;

        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _videoBrush = _compositor.CreateSurfaceBrush();

        _videoBrush.Stretch                  = stretch;
        _videoBrush.HorizontalAlignmentRatio = 0.5f;
        _videoBrush.VerticalAlignmentRatio   = 0.5f;

        _videoVisual       = _compositor.CreateSpriteVisual();
        _videoVisual.Brush = _videoBrush;

        // Fill VideoHost automatically.
        _videoVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _videoVisual);

        CreateDeviceResources();

        // The brush handles host resizing; video textures keep their source resolution.
    }

    public void ToggleVideo(bool isEnable)
    {
        if (_host != null && !_disposed)
            ElementCompositionPreview.SetElementChildVisual(_host, isEnable ? _videoVisual : null);
    }

    // ReSharper disable once AsyncVoidMethod
    public async Task OpenAsync(Uri uri, bool isLoop = true, CancellationToken token = default)
    {
        DisposeMediaPlayer();
        MediaPlayer = new MediaPlayer
        {
            IsLoopingEnabled = isLoop
        };

        _ffmpegSource = await GetMediaSourceAsync(uri, MediaPlayer);

        MediaPlayer.MediaEnded                += MediaPlayer_OnMediaEnded;
        MediaPlayer.VideoFrameAvailable       += MediaPlayer_OnVideoFrameAvailable;
        MediaPlayer.IsVideoFrameServerEnabled =  true;
        MediaPlayer.Play();
    }

    private static async Task<FFmpegMediaSource> GetMediaSourceAsync(Uri uri, MediaPlayer mediaPlayer)
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
        await source.OpenWithMediaPlayerAsync(mediaPlayer);
        return source;
    }

    private void CalculateFrameSize()
    {
        int width  = (int)(MediaPlayer?.PlaybackSession.NaturalVideoWidth ?? 0);
        int height = (int)(MediaPlayer?.PlaybackSession.NaturalVideoHeight ?? 0);

        if (width <= 0 || height <= 0)
            return;

        if (_frameSurface != null && width == RenderWidth && height == RenderHeight)
            return;

        ResizeDrawingSurface(width, height);
    }

    private void DisposeMediaPlayer()
    {
        MediaPlayer?       mediaPlayer;
        FFmpegMediaSource? ffmpegSource;
        using (_renderLock.EnterScope())
        {
            mediaPlayer   = MediaPlayer;
            ffmpegSource  = _ffmpegSource;
            MediaPlayer   = null;
            _ffmpegSource = null;
            _hasFrame     = false;

            if (mediaPlayer != null)
            {
                mediaPlayer.MediaEnded          -= MediaPlayer_OnMediaEnded;
                mediaPlayer.VideoFrameAvailable -= MediaPlayer_OnVideoFrameAvailable;
            }
        }

        // Closing playback may wait for a frame callback which needs _renderLock.
        try
        {
            mediaPlayer?.Dispose();
        }
        finally
        {
            ffmpegSource?.Dispose();
        }
    }

    public void Dispose()
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        try
        {
            DisposeMediaPlayer();
        }
        finally
        {
            using (_renderLock.EnterScope())
            {
                if (_host != null)
                    ElementCompositionPreview.SetElementChildVisual(_host, null);

                ReleaseDeviceResources();
                _videoVisual?.Dispose();
                _videoBrush?.Dispose();

                _videoVisual = null;
                _videoBrush  = null;
            }
        }
    }

    private void CreateDeviceResources()
    {
        using (_renderLock.EnterScope())
        {
            _d3dDevice = CreateD3DDeviceFromSharedCanvasDevice() ??
                         CreateD3DDevice(out _d3dContext);

            if (_d3dContext == null)
                _d3dDevice.GetImmediateContext(out _d3dContext);

            // MediaPlayer and the compositor also use this immediate context.
            if (ComMarshal<ID3D11DeviceContext>
                .TryCastComObjectAs(_d3dContext,
                                    out ID3D11Multithread? d3d11Mt,
                                    out _))
            {
                d3d11Mt.SetMultithreadProtected(1);
            }

            _compositionGraphicsDevice = CreateCompositionGraphicsDevice(_compositor!, _d3dDevice);
            _compositionSurface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(0, 0),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);

            nint surfaceP = ((IWinRTObject)_compositionSurface).NativeObject.ThisPtr;
            // This reference is borrowed from the projected surface.

            if (!ComMarshal<ICompositionDrawingSurfaceInterop>
                    .TryCreateComObjectFromReference(surfaceP,
                                                     out _drawingSurfaceInterop,
                                                     out Exception? ex,
                                                     false))
            {
                throw ex;
            }
            _videoBrush!.Surface = _compositionSurface;
        }
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

        int hr = access.GetInterface(typeof(ID3D11Device).GUID, out nint d3d11DeviceFromSharedCanvasP);
        if (hr != 0) return null;

        // Own this COM reference, not the shared CanvasDevice itself.
        return !ComMarshal<ID3D11Device>
            .TryCreateComObjectFromReference(d3d11DeviceFromSharedCanvasP,
                                             out ID3D11Device? d3d11Device,
                                             out _) ? null : d3d11Device;
    }

    private static unsafe ID3D11Device CreateD3DDevice(out ID3D11DeviceContext? context)
    {
        context = null;
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
        nint deviceP  = nint.Zero;
        nint contextP = nint.Zero;
        try
        {
            int hr = PInvoke.D3D11CreateDevice(nint.Zero,
                                               D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                                               0, flags, levels, levels.Length,
                                               D3D11_SDK_VERSION,
                                               out deviceP, ref selectedFeatureLevel, out contextP);
#if DEBUG
            if (hr == unchecked((int)0x887A002D)) // Debug layer is not installed.
            {
                flags &= ~D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG;
                hr = PInvoke.D3D11CreateDevice(nint.Zero,
                                               D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                                               0, flags, levels, levels.Length,
                                               D3D11_SDK_VERSION,
                                               out deviceP, ref selectedFeatureLevel, out contextP);
            }
#endif
            if (hr < 0)
            {
                // Optional WARP fallback.
                hr = PInvoke.D3D11CreateDevice(nint.Zero,
                                               D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
                                               0, flags, levels, levels.Length,
                                               D3D11_SDK_VERSION,
                                               out deviceP, ref selectedFeatureLevel, out contextP);
            }
            Marshal.ThrowExceptionForHR(hr);

            if (!ComMarshal<ID3D11DeviceContext>
                    .TryCreateComObjectFromReference(contextP,
                                                     out context,
                                                     out Exception? ex,
                                                     releaseReference: false))
            {
                throw ex;
            }

            return !ComMarshal<ID3D11Device>
                .TryCreateComObjectFromReference(deviceP,
                                                 out ID3D11Device? d3d11Device,
                                                 out ex,
                                                 releaseReference: false) ? throw ex : d3d11Device;
        }
        finally
        {
            if (contextP != nint.Zero) Marshal.Release(contextP);
            if (deviceP != nint.Zero) Marshal.Release(deviceP);
        }
    }

    private static unsafe CompositionGraphicsDevice CreateCompositionGraphicsDevice(
        Compositor   compositor,
        ID3D11Device device)
    {
        nint compositorAbi = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        if (!ComMarshal<ICompositorInterop>
                .TryCreateComObjectFromReference(compositorAbi,
                                                 out ICompositorInterop? compositorInterop,
                                                 out Exception? ex,
                                                 false))
        {
            throw ex;
        }

        nint graphicsDeviceP = nint.Zero;
        nint d3dDeviceP      = nint.Zero;
        try
        {
            d3dDeviceP = (nint)ComInterfaceMarshaller<ID3D11Device>.ConvertToUnmanaged(device);
            Marshal.ThrowExceptionForHR(compositorInterop.CreateGraphicsDevice(d3dDeviceP, out graphicsDeviceP));
            return MarshalInterface<CompositionGraphicsDevice>.FromAbi(graphicsDeviceP);
        }
        finally
        {
            if (graphicsDeviceP != nint.Zero) Marshal.Release(graphicsDeviceP);
            if (d3dDeviceP != nint.Zero) Marshal.Release(d3dDeviceP);
        }
    }

    private void MediaPlayer_OnMediaEnded(MediaPlayer sender, object args)
    {
        _host?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
                MediaEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void MediaPlayer_OnVideoFrameAvailable(
        MediaPlayer sender,
        object      args)
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed ||
                _recreating ||
                _drawingSurfaceInterop is null)
            {
                return;
            }

            _hasFrame = true;
            DrawFrame();
        }
    }

    private void DrawFrame()
    {
        try
        {
            CalculateFrameSize();
            if (_frameSurface == null)
                return;

            MediaPlayer!.CopyFrameToVideoSurface(_frameSurface);

            int hr = _drawingSurfaceInterop!.BeginDraw(nint.Zero,
                                                       typeof(IDXGISurface).GUID,
                                                       out nint updateP,
                                                       out CompositionPoint offset);
            Marshal.ThrowExceptionForHR(hr);

            nint updateTextureP = nint.Zero;
            try
            {
                hr = Marshal.QueryInterface(updateP, typeof(ID3D11Texture2D).GUID, out updateTextureP);
                Marshal.ThrowExceptionForHR(hr);

                _d3dContext!.CopySubresourceRegion(updateTextureP,
                                                   0,
                                                   (uint)offset.X,
                                                   (uint)offset.Y,
                                                   0,
                                                   _frameTexture!,
                                                   0,
                                                   nint.Zero);

                _d3dContext.Flush();
            }
            finally
            {
                // The update texture belongs to this BeginDraw/EndDraw pair only.
                if (updateTextureP != nint.Zero) Marshal.Release(updateTextureP);
                if (updateP != nint.Zero) Marshal.Release(updateP);
                Marshal.ThrowExceptionForHR(_drawingSurfaceInterop.EndDraw());
            }
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
        if (_recreating || _disposed)
            return;

        _recreating = true;

        if (!_host!.DispatcherQueue.TryEnqueue(RecreateDeviceResources))
            _recreating = false;
    }

    private unsafe void CreateFrameSurface()
    {
        D3D11_TEXTURE2D_DESC desc = new()
        {
            Width      = (uint)RenderWidth,
            Height     = (uint)RenderHeight,
            MipLevels  = 1,
            ArraySize  = 1,
            Format     = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage      = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags  = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE
        };

        nint textureP         = nint.Zero;
        nint dxgiSurfaceP     = nint.Zero;
        nint graphicsSurfaceP = nint.Zero;
        try
        {
            _d3dDevice!.CreateTexture2D(in desc, nint.Zero, out textureP);

            int hr = Marshal.QueryInterface(textureP, typeof(IDXGISurface).GUID, out dxgiSurfaceP);
            Marshal.ThrowExceptionForHR(hr);

            hr = PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfaceP, out graphicsSurfaceP);
            Marshal.ThrowExceptionForHR(hr);

            hr = Marshal.QueryInterface(textureP, typeof(ID3D11Texture2D).GUID, out _frameTexture);
            Marshal.ThrowExceptionForHR(hr);

            _frameSurface = MarshalInterface<IDirect3DSurface>.FromAbi(graphicsSurfaceP);
        }
        catch
        {
            ReleaseFrameSurface();
            throw;
        }
        finally
        {
            if (graphicsSurfaceP != nint.Zero) Marshal.Release(graphicsSurfaceP);
            if (dxgiSurfaceP != nint.Zero) Marshal.Release(dxgiSurfaceP);
            if (textureP != nint.Zero) Marshal.Release(textureP);
        }
    }

    private void ReleaseFrameSurface()
    {
        _frameSurface?.Dispose();
        _frameSurface = null;

        if (_frameTexture != nint.Zero) Marshal.Release(Interlocked.Exchange(ref _frameTexture, nint.Zero));
        _d3dContext?.Flush();
    }

    private void RecreateDeviceResources()
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed)
                return;

            try
            {
                ReleaseDeviceResources();
                CreateDeviceResources();
                if (_hasFrame)
                    DrawFrame();
            }
            finally
            {
                _recreating = false;
            }
        }
    }

    private void ResizeDrawingSurface(
        int width,
        int height)
    {
        ReleaseFrameSurface();
        int hr = _drawingSurfaceInterop!.Resize(new CompositionSize { Width = width, Height = height });
        Marshal.ThrowExceptionForHR(hr);

        RenderWidth  = width;
        RenderHeight = height;
        CreateFrameSurface();
        Logger.LogWriteLine($"Video Surface Resized: {width}x{height}", LogType.Debug);
    }

    private void ReleaseDeviceResources()
    {
        _videoBrush?.Surface = null;
        ReleaseFrameSurface();
        _drawingSurfaceInterop = null;

        _compositionSurface?.Dispose();
        _compositionSurface = null;
        _compositionGraphicsDevice?.Dispose();
        _compositionGraphicsDevice = null;

        _d3dContext = null;
        _d3dDevice  = null;
    }
}
