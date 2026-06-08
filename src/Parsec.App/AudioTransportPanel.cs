using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Parsec.Audio;

namespace Parsec.App;

public sealed class AudioTransportPanel : UserControl
{
    private readonly AudioTransportController _controller;
    private readonly DispatcherTimer _pollTimer;
    private readonly TextBlock _trackText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _timeText;
    private readonly Button _loadButton;
    private readonly Button _playButton;
    private readonly Button _pauseButton;
    private readonly Button _stopButton;
    private readonly Slider _seekSlider;
    private bool _syncingSlider;

    public AudioTransportPanel(AudioTransportController controller)
    {
        _controller = controller;
        _controller.StateChanged += OnStateChanged;

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(new TextBlock
        {
            Text = "AUDIO",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        });

        _trackText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_trackText);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xa0, 0xc0, 0xe0)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_statusText);

        var buttonRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        _loadButton = BuildButton("Load...");
        _playButton = BuildButton("Play");
        _pauseButton = BuildButton("Pause");
        _stopButton = BuildButton("Stop");
        AddButton(buttonRow, _loadButton, 0);
        AddButton(buttonRow, _playButton, 1);
        AddButton(buttonRow, _pauseButton, 2);
        AddButton(buttonRow, _stopButton, 3);
        root.Children.Add(buttonRow);

        _timeText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 11,
            FontFamily = new FontFamily("monospace"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        root.Children.Add(_timeText);

        _seekSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
        };
        _seekSlider.PropertyChanged += OnSeekSliderChanged;
        root.Children.Add(_seekSlider);

        Content = root;

        _loadButton.Click += OnLoadClick;
        _playButton.Click += async (_, _) => await _controller.PlayAsync();
        _pauseButton.Click += async (_, _) => await _controller.PauseAsync();
        _stopButton.Click += async (_, _) => await _controller.StopAsync();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += (_, _) => _controller.Refresh();
        _pollTimer.Start();

        DetachedFromVisualTree += (_, _) =>
        {
            _pollTimer.Stop();
            _controller.StateChanged -= OnStateChanged;
        };

        ApplyState(_controller.State);
    }

    private static Button BuildButton(string text) =>
        new()
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0),
        };

    private static void AddButton(Grid grid, Button button, int column)
    {
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private async void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select audio file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wave Audio")
                {
                    Patterns = ["*.wav", "*.wave"],
                    AppleUniformTypeIdentifiers = ["com.microsoft.waveform-audio"],
                    MimeTypes = ["audio/wav", "audio/wave", "audio/x-wav"],
                },
            ],
        });
        if (files.Count != 1 || files[0].Path is not { } source) return;

        await _controller.OpenAsync(source);
    }

    private async void OnSeekSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncingSlider || e.Property != RangeBase.ValueProperty) return;

        var state = _controller.State;
        if (!state.CanSeek || state.Duration is not { } duration) return;

        double fraction = Math.Clamp(_seekSlider.Value, 0.0, 1.0);
        await _controller.SeekAsync(TimeSpan.FromTicks((long)(duration.Ticks * fraction)));
    }

    private void OnStateChanged(AudioTransportState state) => Dispatcher.UIThread.Post(() => ApplyState(state));

    private void ApplyState(AudioTransportState state)
    {
        _trackText.Text = state.DisplayName ?? $"{state.BackendName} ready";
        _statusText.Text = state.StatusText;

        _loadButton.IsEnabled = state.CanLoad;
        _playButton.IsEnabled = state.CanPlay;
        _pauseButton.IsEnabled = state.CanPause;
        _stopButton.IsEnabled = state.CanStop;
        _seekSlider.IsEnabled = state.CanSeek && state.Duration is { } duration && duration > TimeSpan.Zero;

        _syncingSlider = true;
        _seekSlider.Value = state.Duration is { } knownDuration && knownDuration > TimeSpan.Zero
            ? state.Position.TotalSeconds / knownDuration.TotalSeconds
            : 0;
        _syncingSlider = false;

        _timeText.Text = $"{FormatTime(state.Position)} / {FormatTime(state.Duration)}";
    }

    private static string FormatTime(TimeSpan? time)
    {
        if (time == null) return "--:--";
        var value = time.Value;
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}
