using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIPlayerBehindAcrylic;

internal interface IVideoFramePresenter : IDisposable
{
    event EventHandler MediaEnded;

    void Initialize(Grid host, CompositionStretch stretch = CompositionStretch.UniformToFill);

    void ToggleVideo(bool isEnable);

    Task OpenAsync(Uri uri, bool isLoop = true, CancellationToken token = default);
}