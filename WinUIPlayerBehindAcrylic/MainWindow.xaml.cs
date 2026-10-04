using System;
using System.IO;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIPlayerBehindAcrylic;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IVideoFramePresenter _videoFramePresenter;

    private bool IsVideoEnabled
    {
        get;
        set
        {
            field = value;
            _videoFramePresenter.ToggleVideo(value);
            VideoHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    } = true;

    public MainWindow()
    {
        _videoFramePresenter = new MediaFoundationPresenter();
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        Closed += (_, _) =>
        {
            _videoFramePresenter.Dispose();
        };
    }

    private async void Grid_OnLoaded(object sender, RoutedEventArgs e)
    {
        string[] samples =
            Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "Samples"),
                               "vidSample*.*",
                               SearchOption.TopDirectoryOnly);

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Path.GetFullPath(samples[i]);
        }

        int index = 0;
        Random.Shared.Shuffle(samples);

        _videoFramePresenter.Initialize((Grid)sender);
        _videoFramePresenter.MediaEnded += PlayNextLoop;
        await _videoFramePresenter.OpenAsync(new Uri(samples[index]), false);
        return;

        async void PlayNextLoop(object? se, EventArgs args)
        {
            index++;
            if (index > samples.Length - 1)
            {
                index = 0;
                Random.Shared.Shuffle(samples);
            }

            await _videoFramePresenter.OpenAsync(new Uri(samples[index]), false);
        }
    }
}
