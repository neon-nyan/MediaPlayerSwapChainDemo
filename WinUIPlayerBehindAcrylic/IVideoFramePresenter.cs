using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;

namespace WinUIPlayerBehindAcrylic;

internal interface IVideoFramePresenter : IDisposable
{
    event EventHandler MediaEnded;

    void Initialize(Grid host, CompositionStretch stretch = CompositionStretch.UniformToFill);

    void ToggleVideo(bool isEnable);

    void Open(Uri uri, bool isLoop = true);
}