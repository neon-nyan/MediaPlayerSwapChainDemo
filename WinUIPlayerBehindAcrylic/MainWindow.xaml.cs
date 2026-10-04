using FFmpegInteropX;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Playback;
using WinRT;
// ReSharper disable InconsistentNaming

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIPlayerBehindAcrylic;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow
{
    private static ref readonly Guid IID_IMediaPlayer5
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ReadOnlySpan<byte> span = [
                253, 55,  229, 207, 106, 248, 70, 68, 191, 77,
                200, 231, 146, 183, 180, 179
            ];
            return ref Unsafe.As<byte, Guid>(ref MemoryMarshal.GetReference(span));
        }
    }

    private readonly IVideoFramePresenter _videoFramePresenter;
    private readonly MediaPlayer          _mediaPlayer;
    private readonly nint                 _mediaPlayerAbi;

    private readonly unsafe delegate* unmanaged[MemberFunction]<nint, nint, int> _mediaPlayerCopyToSurfaceFunc;

    private FFmpegMediaSource? _currentMediaSource;

    private bool IsVideoEnabled
    {
        get;
        set
        {
            field = value;
            _videoFramePresenter.Toggle(value);
            VideoHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    } = true;

    public unsafe MainWindow()
    {
        _videoFramePresenter = new MediaFoundationPresenter();
        _mediaPlayer = new MediaPlayer
        {
            IsLoopingEnabled          = false,
            IsVideoFrameServerEnabled = true // Use FrameServer mode
        };
        ((IWinRTObject)_mediaPlayer).NativeObject.TryAs(IID_IMediaPlayer5, out _mediaPlayerAbi);
        _mediaPlayerCopyToSurfaceFunc = (delegate* unmanaged[MemberFunction]<nint, nint, int>)(*(*(void***)_mediaPlayerAbi + 10));

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        Closed += (_, _) =>
        {
            _videoFramePresenter.Dispose();
        };
    }

    private async void Grid_OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialize Grid as the video frame host.
        _videoFramePresenter.Initialize((Grid)sender);

        string[] samples = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "Samples"),
                                              "vidSample*.*",
                                              SearchOption.TopDirectoryOnly);

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Path.IsPathFullyQualified(samples[i]) ? samples[i] : Path.GetFullPath(samples[i]);
        }

        int index = 0;
        Random.Shared.Shuffle(samples);

        // Subscribe to MediaEnded event so we can loop to the next sample.
        _mediaPlayer.MediaEnded          += PlayNextLoop;
        _mediaPlayer.VideoFrameAvailable += DrawMediaFrameToPresenter;

        _currentMediaSource = await PlayAsync(new Uri(samples[index]), _mediaPlayer);
        return;

        async void PlayNextLoop(MediaPlayer? mediaPlayer, object args)
        {
            index++;
            if (index > samples.Length - 1)
            {
                index = 0;
                Random.Shared.Shuffle(samples);
            }

            _currentMediaSource?.Dispose();
            _currentMediaSource = await PlayAsync(new Uri(samples[index]), _mediaPlayer);
        }
    }

    private unsafe void DrawMediaFrameToPresenter(MediaPlayer sender, object args)
        // Perform draw to the presenter. Use CopyFrameToVideoSurface as the consumer
        // to copy the current frame to the presenter.
        => _videoFramePresenter.DrawUnsafe(DrawConsumer,
                                           (int)sender.PlaybackSession.NaturalVideoWidth,
                                           (int)sender.PlaybackSession.NaturalVideoHeight);

    private unsafe void DrawConsumer(nint surfaceAbi)
        => _mediaPlayerCopyToSurfaceFunc(_mediaPlayerAbi, surfaceAbi);

    private static async Task<FFmpegMediaSource> PlayAsync(Uri uri, MediaPlayer mediaPlayer)
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
        mediaPlayer.Play();
        return source;
    }
}
