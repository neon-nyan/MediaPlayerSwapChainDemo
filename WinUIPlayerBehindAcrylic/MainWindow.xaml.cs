using FFmpegInteropX;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Microsoft.UI.Xaml.Controls;
using WinRT;
// ReSharper disable InconsistentNaming
// ReSharper disable AccessToModifiedClosure
// ReSharper disable AsyncVoidMethod

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

    public unsafe MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    private async void Grid_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string searchPath } element ||
            string.IsNullOrEmpty(searchPath))
        {
            return;
        }

        bool isMuted = element.Resources.TryGetValue("IsMuted", out object isMutedObj) && isMutedObj is true;

        // Initialize Grid as the video frame host.
        IVideoFramePresenter presenter = new MediaFoundationPresenter();
        presenter.Initialize(element);

        string[] samples = MergeSamples(searchPath);

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Path.IsPathFullyQualified(samples[i]) ? samples[i] : Path.GetFullPath(samples[i]);
        }

        int    index          = 0;
        double initialOpacity = element.Opacity;
        int    initialZIndex  = Canvas.GetZIndex(element);

        Random.Shared.Shuffle(samples);

        // Subscribe to MediaEnded event so we can loop to the next sample.
        MediaPlayer mediaPlayer = new()
        {
            IsVideoFrameServerEnabled = true,
            IsLoopingEnabled          = false,
            IsMuted                   = isMuted
        };
        ((IWinRTObject)mediaPlayer).NativeObject.TryAs(IID_IMediaPlayer5, out nint mediaPlayerAbi);

        FFmpegMediaSource? previousMediaSource = null;
        element.Unloaded                += ElementOnUnloaded;
        element.PointerEntered          += ElementOnPointerEntered;
        element.PointerExited           += ElementOnPointerExited;
        mediaPlayer.MediaEnded          += PlayNextLoop;
        mediaPlayer.VideoFrameAvailable += DrawMediaFrameToPresenter;

        previousMediaSource = await PlayAsync(new Uri(samples[index]), mediaPlayer);
        return;

        static string[] MergeSamples(string searchPath)
        {
            List<string> samples = [];
            foreach (Range splits in searchPath.AsSpan().SplitAny(",;|"))
            {
                string thisSearchPath = searchPath[splits].Trim();
                string searchPattern  = Path.GetFileName(thisSearchPath);
                string dirPath        = Path.GetDirectoryName(thisSearchPath) ?? "";
                samples.AddRange(Directory.GetFiles(dirPath, searchPattern, SearchOption.TopDirectoryOnly));
            }

            return [.. samples];
        }

        void ElementOnPointerEntered(object s, PointerRoutedEventArgs args)
        {
            mediaPlayer.IsMuted = false;
            Canvas.SetZIndex(element, 1);

            Storyboard sb = new();
            DoubleAnimation opacityAnimation = new()
            {
                From     = initialOpacity,
                To       = 1,
                Duration = new Duration(TimeSpan.FromSeconds(0.10))
            };

            Storyboard.SetTarget(opacityAnimation, element);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            sb.Children.Add(opacityAnimation);

            CompositeTransform transform = (CompositeTransform)element.RenderTransform;
            CubicEase cubicEaseOut = new()
            {
                EasingMode = EasingMode.EaseOut
            };

            DoubleAnimation scaleXAnim = new()
            {
                From           = 1,
                To             = 1.08,
                Duration       = new Duration(TimeSpan.FromSeconds(0.1)),
                EasingFunction = cubicEaseOut
            };
            Storyboard.SetTarget(scaleXAnim, transform);
            Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
            sb.Children.Add(scaleXAnim);

            DoubleAnimation scaleYAnim = new()
            {
                From           = 1,
                To             = 1.08,
                Duration       = new Duration(TimeSpan.FromSeconds(0.1)),
                EasingFunction = cubicEaseOut
            };
            Storyboard.SetTarget(scaleYAnim, transform);
            Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
            sb.Children.Add(scaleYAnim);

            sb.Begin();
        }

        void ElementOnPointerExited(object s, PointerRoutedEventArgs args)
        {
            mediaPlayer.IsMuted = true;
            Canvas.SetZIndex(element, initialZIndex);

            Storyboard sb = new();
            DoubleAnimation opacityAnimation = new()
            {
                From     = 1,
                To       = initialOpacity,
                Duration = new Duration(TimeSpan.FromSeconds(0.10))
            };

            Storyboard.SetTarget(opacityAnimation, element);
            Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
            sb.Children.Add(opacityAnimation);

            CompositeTransform transform = (CompositeTransform)element.RenderTransform;
            CubicEase cubicEaseOut = new()
            {
                EasingMode = EasingMode.EaseOut
            };

            DoubleAnimation scaleXAnim = new()
            {
                From           = 1.08,
                To             = 1,
                Duration       = new Duration(TimeSpan.FromSeconds(0.1)),
                EasingFunction = cubicEaseOut
            };
            Storyboard.SetTarget(scaleXAnim, transform);
            Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
            sb.Children.Add(scaleXAnim);

            DoubleAnimation scaleYAnim = new()
            {
                From           = 1.08,
                To             = 1,
                Duration       = new Duration(TimeSpan.FromSeconds(0.1)),
                EasingFunction = cubicEaseOut
            };
            Storyboard.SetTarget(scaleYAnim, transform);
            Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
            sb.Children.Add(scaleYAnim);

            sb.Begin();
        }

        async void PlayNextLoop(MediaPlayer source, object args)
        {
            index++;
            if (index > samples.Length - 1)
            {
                index = 0;
                Random.Shared.Shuffle(samples);
            }

            previousMediaSource?.Dispose();
            previousMediaSource = await PlayAsync(new Uri(samples[index]), source);
        }

        unsafe void DrawMediaFrameToPresenter(MediaPlayer player, object args)
            // Perform draw to the presenter. Use CopyFrameToVideoSurface as the consumer
            // to copy the current frame to the presenter.
            => presenter.DrawUnsafe(DrawConsumer,
                                    (int)player.PlaybackSession.NaturalVideoWidth,
                                    (int)player.PlaybackSession.NaturalVideoHeight);

        unsafe void DrawConsumer(nint surfaceAbi)
            => ((delegate* unmanaged[MemberFunction]<nint, nint, int>)(*(*(void***)mediaPlayerAbi + 10)))(mediaPlayerAbi, surfaceAbi);
        
        void ElementOnUnloaded(object s, RoutedEventArgs args)
        {
            presenter.Dispose();
            previousMediaSource?.Dispose();
            mediaPlayer.Dispose();
        }
    }

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
