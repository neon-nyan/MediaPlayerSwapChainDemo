using Microsoft.UI.Composition;
using System;
using Windows.Graphics.DirectX.Direct3D11;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace WinUIPlayerBehindAcrylic;

public delegate void Direct3DSurfaceConsumer(IDirect3DSurface surface);
public delegate void Direct3DSurfaceConsumerUnsafe(nint surfaceAbi);

public interface IVideoFramePresenter : IDisposable
{
    void Initialize(FrameworkElement   host,
                    CompositionStretch stretch = CompositionStretch.UniformToFill,
                    ILogger?           logger  = null);

    void Toggle(bool isEnable);

    void Draw(Direct3DSurfaceConsumer surfaceConsumer,
              int                     canvasWidth,
              int                     canvasHeight);

    void DrawUnsafe(Direct3DSurfaceConsumerUnsafe surfaceConsumerUnsafe,
                    int                           canvasWidth,
                    int                           canvasHeight);
}