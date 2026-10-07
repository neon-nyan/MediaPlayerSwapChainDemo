using Hi3Helper.Win32.ManagedTools;
using Hi3Helper.Win32.Native.Enums.D3D;
using Hi3Helper.Win32.Native.Enums.DXGI;
using Hi3Helper.Win32.Native.Interfaces.CompositorInterop;
using Hi3Helper.Win32.Native.Interfaces.D3D;
using Hi3Helper.Win32.Native.Interfaces.DXGI;
using Hi3Helper.Win32.Native.LibraryImport;
using Hi3Helper.Win32.Native.Structs;
using Hi3Helper.Win32.Native.Structs.D3D;
using Hi3Helper.Win32.Native.Structs.DXGI;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Rect = Windows.Foundation.Rect;

// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming

namespace WinUIPlayerBehindAcrylic;

/// <summary>
/// A XAML element that presents video frames through its own composition surface.
/// Owns GPU resources only while loaded, visible, and intersecting its effective viewport.
/// </summary>
public sealed partial class MediaFoundationPresenter : FrameworkElement, IVideoFramePresenter
{
    private static readonly Guid IID_IDXGISurface                      = typeof(IDXGISurface).GUID;
    private static readonly Guid IID_ID3D11Texture2D                   = typeof(ID3D11Texture2D).GUID;
    private static readonly Guid IID_IDirect3DSurface                  = typeof(IDirect3DSurface).GUID;
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = typeof(ICompositionDrawingSurfaceInterop).GUID;

    private int RenderWidth;
    private int RenderHeight;

    private readonly Lock _renderLock = new();

    private readonly DispatcherQueue _dispatcherQueue;

    private Compositor?                        _compositor;
    private SpriteVisual?                      _videoVisual;
    private CompositionSurfaceBrush?           _videoBrush;
    private CompositionDrawingSurface?         _compositionSurface;
    private CompositionGraphicsDevice?         _compositionGraphicsDevice;
    private ICompositionDrawingSurfaceInterop? _drawingSurfaceInterop;
    private nint                               _drawingSurfaceInteropAbi;

    private ID3D11Device?        _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private nint                 _d3dContextAbi;
    private nint                 _frameTexture;
    private IDirect3DSurface?    _frameSurface;
    private nint                 _frameSurfaceAbi;

    private          bool _disposed;
    private          bool _recreating;
    private          bool _isLoaded;
    private          bool _hasViewport;
    private          Rect _effectiveViewport;
    private readonly long _visibilityCallbackToken;

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
     nameof(Stretch),
     typeof(CompositionStretch),
     typeof(MediaFoundationPresenter),
     new PropertyMetadata(CompositionStretch.UniformToFill, OnStretchChanged));

    public static readonly DependencyProperty PixelFormatProperty = DependencyProperty.Register(
     nameof(PixelFormat),
     typeof(DirectXPixelFormat),
     typeof(MediaFoundationPresenter),
     new PropertyMetadata(DirectXPixelFormat.B8G8R8A8UIntNormalized, OnPixelFormatOrAlphaModeChanged));

    public static readonly DependencyProperty AlphaModeProperty = DependencyProperty.Register(
     nameof(AlphaMode),
     typeof(DirectXAlphaMode),
     typeof(MediaFoundationPresenter),
     new PropertyMetadata(DirectXAlphaMode.Premultiplied, OnPixelFormatOrAlphaModeChanged));

    public CompositionStretch Stretch
    {
        get => (CompositionStretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public DirectXPixelFormat PixelFormat
    {
        get => (DirectXPixelFormat)GetValue(PixelFormatProperty);
        set => SetValue(PixelFormatProperty, value);
    }

    public DirectXAlphaMode AlphaMode
    {
        get => (DirectXAlphaMode)GetValue(AlphaModeProperty);
        set => SetValue(AlphaModeProperty, value);
    }

    public ILogger? Logger { get; set; }

    /// <summary>True while this element intersects its effective viewport and owns rendering resources.</summary>
    public bool IsPresentationActive { get; private set; }

    /// <summary>
    /// Raised on the UI thread when viewport visibility changes resource ownership.
    /// The playback owner should close its player and decoder when inactive, and reopen them when active.
    /// </summary>
    public event EventHandler? PresentationStateChanged;

    public MediaFoundationPresenter()
    {
        _dispatcherQueue         =  DispatcherQueue;
        Loaded                   += OnLoaded;
        Unloaded                 += OnUnloaded;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        SizeChanged              += OnSizeChanged;
        _visibilityCallbackToken =  RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    private static void OnStretchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MediaFoundationPresenter presenter = (MediaFoundationPresenter)sender;
        if (presenter._videoBrush != null)
            presenter._videoBrush.Stretch = (CompositionStretch)args.NewValue;
    }

    private static void OnPixelFormatOrAlphaModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MediaFoundationPresenter presenter = (MediaFoundationPresenter)sender;
        if (!presenter.IsLoaded)
            return;

        presenter.ReleaseFrameSurface();
        presenter.RecreateDeviceResources();
        presenter.CreateFrameSurface();
    }

    // Fill the offered layout slot. In an unconstrained panel the caller can set Width/Height.
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsPositiveInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsPositiveInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = true;
        // Wait for viewport information before allocating, including on the first load offscreen.
        UpdatePresentationState();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        _effectiveViewport = args.EffectiveViewport;
        _hasViewport       = true;
        UpdatePresentationState();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePresentationState();

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property) => UpdatePresentationState();

    private void UpdatePresentationState()
    {
        Rect visibleBounds = _effectiveViewport;
        visibleBounds.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
        bool active = !_disposed && _isLoaded && _hasViewport && Visibility == Visibility.Visible &&
                      visibleBounds is { IsEmpty: false, Width: > 0, Height: > 0 };

        using (_renderLock.EnterScope())
        {
            if (IsPresentationActive == active)
                return;

            if (active)
                CreateCompositionResources();
            else
                ReleaseCompositionResources();

            IsPresentationActive = active;
        }

        // Playback owners may wait for their own frame callbacks; never notify under the render lock.
        PresentationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CreateCompositionResources()
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed || _videoVisual != null)
                return;

            try
            {
                _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
                _videoBrush = _compositor.CreateSurfaceBrush();
                _videoBrush.Stretch                  = Stretch;
                _videoBrush.HorizontalAlignmentRatio = 0.5f;
                _videoBrush.VerticalAlignmentRatio   = 0.5f;

                _videoVisual       = _compositor.CreateSpriteVisual();
                _videoVisual.Brush = _videoBrush;
                _videoVisual.RelativeSizeAdjustment = Vector2.One;

                CreateDeviceResources();

                // This element owns the visual; no other XAML element's visual is replaced.
                ElementCompositionPreview.SetElementChildVisual(this, _videoVisual);
            }
            catch
            {
                ReleaseCompositionResources();
                throw;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isLoaded = false;
        // Keep the last viewport: WinUI only reports changes, so reloading at the same
        // bounds may not produce another notification. _isLoaded still gates allocation.
        UpdatePresentationState();
    }

    private void CalculateFrameSize(int requestedWidth, int requestedHeight)
    {
        if (requestedWidth <= 0 || requestedHeight <= 0)
            return;

        if (_frameSurface != null && requestedWidth == RenderWidth && requestedHeight == RenderHeight)
            return;

        ResizeDrawingSurface(requestedWidth, requestedHeight);
    }

    /// <summary>Permanently releases this presenter. Call on the UI thread.</summary>
    public void Dispose()
    {
        using (_renderLock.EnterScope())
        {
            if (_disposed)
                return;

            _disposed                =  true;
            Loaded                   -= OnLoaded;
            Unloaded                 -= OnUnloaded;
            EffectiveViewportChanged -= OnEffectiveViewportChanged;
            SizeChanged              -= OnSizeChanged;
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
        }

        UpdatePresentationState();
    }

    private void ReleaseCompositionResources()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        ReleaseDeviceResources();
        _videoVisual?.Dispose();
        _videoBrush?.Dispose();
        _videoVisual = null;
        _videoBrush  = null;
        _compositor  = null;
        _recreating  = false;
    }

    private unsafe void CreateDeviceResources()
    {
        using (_renderLock.EnterScope())
        {
            _d3dDevice = CreateD3DDeviceFromSharedCanvasDevice(Logger) ??
                         CreateD3DDevice(out _d3dContext, Logger);

            Exception? ex;
            if (_d3dContext == null)
            {
                nint deviceAbi = (nint)ComInterfaceMarshaller<ID3D11Device>.ConvertToUnmanaged(_d3dDevice);
                try
                {
                    // ID3D11Device::GetImmediateContext (slot 40). Own a unique wrapper so
                    // its references can be released without waiting for a managed GC.
                    ((delegate* unmanaged[MemberFunction]<nint, out nint, void>)(*(*(void***)deviceAbi + 40)))
                        (deviceAbi, out _d3dContextAbi);
                    if (!ComMarshal<ID3D11DeviceContext>
                            .TryCreateComObjectFromReference(_d3dContextAbi,
                                                             out _d3dContext,
                                                             out ex,
                                                             false,
                                                             true))
                    {
                        throw ex;
                    }
                }
                finally
                {
                    Marshal.Release(deviceAbi);
                }
            }
            else
            {
                _d3dContextAbi = (nint)ComInterfaceMarshaller<ID3D11DeviceContext>.ConvertToUnmanaged(_d3dContext);
            }

            // MediaPlayer and the compositor also use this immediate context.
            if (ComMarshal<ID3D11DeviceContext>.TryCastComObjectAs(_d3dContext,
                                                                   out ID3D11Multithread? d3d11Mt,
                                                                   out _,
                                                                   useUnique: true))
            {
                try
                {
                    d3d11Mt.SetMultithreadProtected(1);
                }
                finally
                {
                    ReleaseOwnedComObject(d3d11Mt);
                }
            }

            _compositionGraphicsDevice = CreateCompositionGraphicsDevice(_compositor!, _d3dDevice, Logger);
            _compositionSurface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(0, 0),
                GetValue<DirectXPixelFormat>(PixelFormatProperty),
                GetValue<DirectXAlphaMode>(AlphaModeProperty));

            // This reference is borrowed from the projected surface.
            nint surfaceP = ((IWinRTObject)_compositionSurface).NativeObject.ThisPtr;
            if (!ComMarshal<ICompositionDrawingSurfaceInterop>
                    .TryCreateComObjectFromReference(surfaceP,
                                                     out _drawingSurfaceInterop,
                                                     out ex,
                                                     releaseReference: false, useUnique: true))
            {
                throw ex;
            }

            // Query borrowed reference from drawing surface interop (+1 ref).
            int hr = Marshal.QueryInterface(surfaceP, in IID_ICompositionDrawingSurfaceInterop, out _drawingSurfaceInteropAbi);
            Marshal.ThrowExceptionForHR(hr);
            _videoBrush!.Surface = _compositionSurface;
        }
    }

    private static unsafe ID3D11Device? CreateD3DDeviceFromSharedCanvasDevice(ILogger? logger)
    {
        CanvasDevice sharedCanvasDevice  = CanvasDevice.GetSharedDevice();
        nint         sharedCanvasDeviceP = ((IWinRTObject)sharedCanvasDevice).NativeObject.ThisPtr;

        // -- Obtain an existing D3D11 Device from a shared CanvasDevice.
        if (!ComMarshal<IDirect3DDxgiInterfaceAccess>
                .TryCreateComObjectFromReference(sharedCanvasDeviceP,
                                                 out IDirect3DDxgiInterfaceAccess? access,
                                                 out _,
                                                 releaseReference: false,
                                                 useUnique: true))
        {
            return null;
        }

        try
        {
            int hr = access.GetInterface(typeof(ID3D11Device).GUID, out nint d3d11DeviceFromSharedCanvasP);
            if (hr != 0) return null;

            // Release only our references, never dispose the shared CanvasDevice.
            if (!ComMarshal<ID3D11Device>
                    .TryCreateComObjectFromReference(d3d11DeviceFromSharedCanvasP,
                                                     out ID3D11Device? d3d11Device,
                                                     out _,
                                                     useUnique: true))
            {
                return null;
            }

            logger?.LogDebug("D3D11 Device Obtained from a Shared Win2D Device!");
            return d3d11Device;
        }
        finally
        {
            ReleaseOwnedComObject(access);
        }
    }

    private static unsafe ID3D11Device CreateD3DDevice(
        out ID3D11DeviceContext? context,
        ILogger?                 logger)
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
                                                     releaseReference: false,
                                                     useUnique: true) ||
                !ComMarshal<ID3D11Device>
                    .TryCreateComObjectFromReference(deviceP,
                                                     out ID3D11Device? d3d11Device,
                                                     out ex,
                                                     releaseReference: false,
                                                     useUnique: true))
            {
                throw ex;
            }

            logger?.LogDebug("D3D11 Device Created with flags: {flags}!", flags);
            return d3d11Device;
        }
        finally
        {
            if (contextP != nint.Zero) Marshal.Release(contextP);
            if (deviceP != nint.Zero) Marshal.Release(deviceP);
        }
    }

    private static unsafe CompositionGraphicsDevice CreateCompositionGraphicsDevice(
        Compositor   compositor,
        ID3D11Device device,
        ILogger?     logger)
    {
        nint compositorAbi = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        if (!ComMarshal<ICompositorInterop>
                .TryCreateComObjectFromReference(compositorAbi,
                                                 out ICompositorInterop? compositorInterop,
                                                 out Exception? ex,
                                                 releaseReference: false,
                                                 useUnique: true))
        {
            throw ex;
        }

        nint graphicsDeviceP = nint.Zero;
        nint d3dDeviceP      = nint.Zero;
        try
        {
            d3dDeviceP = (nint)ComInterfaceMarshaller<ID3D11Device>.ConvertToUnmanaged(device);
            Marshal.ThrowExceptionForHR(compositorInterop.CreateGraphicsDevice(d3dDeviceP, out graphicsDeviceP));

            logger?.LogDebug("D3D11 Graphics Created from the Compositor!");
            return MarshalInterface<CompositionGraphicsDevice>.FromAbi(graphicsDeviceP);
        }
        finally
        {
            if (graphicsDeviceP != nint.Zero) Marshal.Release(graphicsDeviceP);
            if (d3dDeviceP != nint.Zero) Marshal.Release(d3dDeviceP);
            ReleaseOwnedComObject(compositorInterop);
        }
    }

    public void Draw(Direct3DSurfaceConsumer surfaceConsumer,
                     int                     canvasWidth,
                     int                     canvasHeight)
    {
        using Lock.Scope renderScope = _renderLock.EnterScope();
        try
        {
            if (_disposed || _recreating || _drawingSurfaceInterop is null ||
                canvasWidth <= 0 || canvasHeight <= 0)
                return;

            CalculateFrameSize(canvasWidth, canvasHeight);
            if (_frameSurface == null)
                return;

            surfaceConsumer(_frameSurface);
            int hr = _drawingSurfaceInterop.BeginDraw(nint.Zero,
                                                      in IID_IDXGISurface,
                                                      out nint updateP,
                                                      out POINTL offset);
            Marshal.ThrowExceptionForHR(hr);

            nint updateTextureP = nint.Zero;
            try
            {
                hr = Marshal.QueryInterface(updateP, in IID_ID3D11Texture2D, out updateTextureP);
                Marshal.ThrowExceptionForHR(hr);

                _d3dContext!.CopySubresourceRegion(updateTextureP,
                                                   0,
                                                   (uint)offset.x,
                                                   (uint)offset.y,
                                                   0,
                                                   _frameTexture,
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

    public unsafe void DrawUnsafe(
        Direct3DSurfaceConsumerUnsafe surfaceConsumerUnsafe,
        int                           canvasWidth,
        int                           canvasHeight)
    {
        using Lock.Scope renderScope = _renderLock.EnterScope();
        try
        {
            if (_disposed || _recreating || _drawingSurfaceInteropAbi == nint.Zero ||
                canvasWidth <= 0 || canvasHeight <= 0)
                return;

            CalculateFrameSize(canvasWidth, canvasHeight);
            if (_frameSurfaceAbi == nint.Zero ||
                _drawingSurfaceInteropAbi == nint.Zero)
                return;

            surfaceConsumerUnsafe(_frameSurfaceAbi);
            int hr = ((delegate* unmanaged[MemberFunction]<nint, nint, ref readonly Guid, out nint, out POINTL, int>)(*(*(void***)_drawingSurfaceInteropAbi + 3)))
                (_drawingSurfaceInteropAbi, nint.Zero, in IID_IDXGISurface, out nint updateP, out POINTL offset);
            Marshal.ThrowExceptionForHR(hr);

            nint updateTextureP = nint.Zero;
            try
            {
                hr = Marshal.QueryInterface(updateP, in IID_ID3D11Texture2D, out updateTextureP);
                Marshal.ThrowExceptionForHR(hr);
                ((delegate* unmanaged[MemberFunction]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)(*(*(void***)_d3dContextAbi + 46)))
                    (_d3dContextAbi, updateTextureP, 0, (uint)offset.x, (uint)offset.y, 0, _frameTexture, 0, nint.Zero);
                ((delegate* unmanaged[MemberFunction]<nint, void>)(*(*(void***)_d3dContextAbi + 111)))
                    (_d3dContextAbi);
            }
            finally
            {
                // The update texture belongs to this BeginDraw/EndDraw pair only.
                if (updateTextureP != nint.Zero) Marshal.Release(updateTextureP);
                if (updateP != nint.Zero) Marshal.Release(updateP);
                hr = ((delegate* unmanaged[MemberFunction]<nint, int>)(*(*(void***)_drawingSurfaceInteropAbi + 4)))
                    (_drawingSurfaceInteropAbi);
                Marshal.ThrowExceptionForHR(hr);
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

        // Ignore a queued recovery if the element was unloaded (or reloaded) meanwhile.
        CompositionSurfaceBrush? brush = _videoBrush;
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                using (_renderLock.EnterScope())
                {
                    if (!_disposed && brush != null && ReferenceEquals(brush, _videoBrush))
                        RecreateDeviceResources();
                }
            }))
            _recreating = false;
    }

    private unsafe void CreateFrameSurface()
    {
        DXGI_FORMAT format = (DXGI_FORMAT)GetValue<DirectXPixelFormat>(PixelFormatProperty);
        D3D11_TEXTURE2D_DESC desc = new()
        {
            Width      = (uint)RenderWidth,
            Height     = (uint)RenderHeight,
            MipLevels  = 1,
            ArraySize  = 1,
            Format     = format,
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

            int hr = Marshal.QueryInterface(textureP, in IID_IDXGISurface, out dxgiSurfaceP);
            Marshal.ThrowExceptionForHR(hr);

            hr = PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfaceP, out graphicsSurfaceP);
            Marshal.ThrowExceptionForHR(hr);

            hr = Marshal.QueryInterface(textureP, in IID_ID3D11Texture2D, out _frameTexture);
            Marshal.ThrowExceptionForHR(hr);

            _frameSurface = MarshalInterface<IDirect3DSurface>.FromAbi(graphicsSurfaceP);
            Marshal.QueryInterface(graphicsSurfaceP, in IID_IDirect3DSurface, out _frameSurfaceAbi);

            Logger?.LogDebug("D3D11 Texture Created! {width}x{height} with format: {format}", RenderWidth, RenderHeight, desc.Format);
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
        if (_frameTexture != nint.Zero) Marshal.Release(Interlocked.Exchange(ref _frameTexture,       nint.Zero));
        if (_frameSurfaceAbi != nint.Zero) Marshal.Release(Interlocked.Exchange(ref _frameSurfaceAbi, nint.Zero));

        _frameSurface?.Dispose();
        _frameSurface = null;
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
        int hr = _drawingSurfaceInterop!.Resize(new SIZEL { Width = width, Height = height });
        Marshal.ThrowExceptionForHR(hr);

        RenderWidth  = width;
        RenderHeight = height;
        CreateFrameSurface();

        Logger?.LogDebug("Video Surface Resized: {width}x{height}", width, height);
    }

    private void ReleaseDeviceResources()
    {
        _videoBrush?.Surface = null;
        ReleaseFrameSurface();
        ReleaseOwnedComObject(_drawingSurfaceInterop);
        _drawingSurfaceInterop = null;

        // As we performed QueryInterface from the origin ABI. We release this one (but not with the origin too).
        if (_drawingSurfaceInteropAbi != nint.Zero) Marshal.Release(Interlocked.Exchange(ref _drawingSurfaceInteropAbi, nint.Zero));

        _compositionSurface?.Dispose();
        _compositionSurface = null;
        _compositionGraphicsDevice?.Dispose();
        _compositionGraphicsDevice = null;

        // Release the queried ID3D11DeviceContext
        if (_d3dContextAbi != nint.Zero) Marshal.Release(Interlocked.Exchange(ref _d3dContextAbi, nint.Zero));
        ReleaseOwnedComObject(_d3dContext);
        ReleaseOwnedComObject(_d3dDevice);
        _d3dContext = null;
        _d3dDevice  = null;
        RenderWidth = RenderHeight = 0;
    }

    // All native wrappers owned by this presenter are created with useUnique: true.
    private static void ReleaseOwnedComObject(object? value) => (value as ComObject)?.FinalRelease();

    private T GetValue<T>(DependencyProperty property)
    {
        if (DispatcherQueue.HasThreadAccess)
            return (T)GetValue(property);

        TaskCompletionSource<T> tcs = new();
        return !DispatcherQueue.TryEnqueue(GetValueInner)
            ? throw new Exception("Cannot enqueue the value getter")
            : tcs.Task.Result;

        void GetValueInner()
        {
            try
            {
                tcs.SetResult((T)GetValue(property));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }
}
