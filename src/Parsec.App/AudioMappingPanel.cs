using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Parsec.App;

/// <summary>
/// Sidebar panel for configuring audio-to-parameter modulation mappings.
/// Shows current mappings as editable rows, plus an "Add mapping" form.
/// Built entirely in code-behind to match the project's panel style.
/// </summary>
public sealed class AudioMappingPanel : UserControl
{
    private readonly AudioModulationController _controller;
    private ParamSchema? _schema;

    private readonly TextBlock _statusText;
    private readonly StackPanel _mappingRows;
    private readonly ComboBox _paramCombo;
    private readonly ComboBox _featureCombo;
    private readonly Button _addButton;

    private static readonly AudioFeatureSource[] AllFeatures =
    [
        AudioFeatureSource.Rms,
        AudioFeatureSource.BassEnergy,
        AudioFeatureSource.MidEnergy,
        AudioFeatureSource.TrebleEnergy,
        AudioFeatureSource.OnsetStrength,
        AudioFeatureSource.SpectrumCentroid,
    ];

    private static readonly string[] FeatureLabels =
    [
        "RMS",
        "Bass",
        "Mid",
        "Treble",
        "Onset",
        "Centroid",
    ];

    public AudioMappingPanel(AudioModulationController controller)
    {
        _controller = controller;
        _controller.Changed += OnControllerChanged;

        var root = new StackPanel { Spacing = 4 };

        root.Children.Add(new TextBlock
        {
            Text = "AUDIO MAPPINGS",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        });

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xa8, 0xc0)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_statusText);

        // Existing mappings, in a bounded scroll area.
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 220,
        };
        _mappingRows = new StackPanel { Spacing = 2 };
        scroll.Content = _mappingRows;
        root.Children.Add(scroll);

        // Add-mapping form.
        root.Children.Add(new TextBlock
        {
            Text = "ADD MAPPING",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _paramCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Parameter…",
            IsEnabled = false,
        };
        root.Children.Add(_paramCombo);

        _featureCombo = BuildFeatureCombo();
        root.Children.Add(_featureCombo);

        _addButton = new Button
        {
            Content = "Add Mapping",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            IsEnabled = false,
        };
        _addButton.Click += OnAddClick;
        root.Children.Add(_addButton);

        Content = root;

        DetachedFromVisualTree += (_, _) => _controller.Changed -= OnControllerChanged;

        UpdateStatus();
    }

    /// <summary>
    /// Called by MainWindow when the active fractal changes, providing a fresh schema.
    /// Resets the parameter picker to the new schema's descriptors.
    /// </summary>
    public void SetSchema(ParamSchema? schema)
    {
        _schema = schema;
        RebuildParamCombo();
        RebuildMappingRows();
    }

    // --- event handlers ---

    private void OnControllerChanged(AudioModulationController _)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => { UpdateStatus(); RebuildMappingRows(); });
            return;
        }
        UpdateStatus();
        RebuildMappingRows();
    }

    private void OnAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_schema == null) return;
        if (_paramCombo.SelectedIndex < 0) return;

        var descriptors = _schema.Parameters;
        if (_paramCombo.SelectedIndex >= descriptors.Count) return;

        var target = descriptors[_paramCombo.SelectedIndex];
        var feature = AllFeatures[_featureCombo.SelectedIndex];
        _controller.AddMapping(target, feature);
    }

    // --- rebuild helpers ---

    private void UpdateStatus()
    {
        _statusText.Text = _controller.AnalysisStatus;
        bool ready = _controller.HasTrack && _schema != null;
        _paramCombo.IsEnabled = ready;
        _addButton.IsEnabled = ready && _paramCombo.SelectedIndex >= 0;
    }

    private void RebuildParamCombo()
    {
        _paramCombo.Items.Clear();
        if (_schema == null)
        {
            _paramCombo.IsEnabled = false;
            _addButton.IsEnabled = false;
            return;
        }

        foreach (var d in _schema.Parameters)
            _paramCombo.Items.Add($"{d.Group}: {d.Label}");

        if (_paramCombo.Items.Count > 0)
            _paramCombo.SelectedIndex = 0;

        bool ready = _controller.HasTrack;
        _paramCombo.IsEnabled = ready;
        _addButton.IsEnabled = ready && _paramCombo.SelectedIndex >= 0;
    }

    private void RebuildMappingRows()
    {
        _mappingRows.Children.Clear();

        var mappings = _controller.Mappings;
        if (mappings.Count == 0)
        {
            _mappingRows.Children.Add(new TextBlock
            {
                Text = "No mappings.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x70)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (var m in mappings)
            _mappingRows.Children.Add(BuildMappingRow(m));
    }

    private Control BuildMappingRow(AudioModulationMapping mapping)
    {
        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(4, 3, 4, 3),
        };

        var inner = new StackPanel { Spacing = 2 };

        // Row 1: enable checkbox + label + feature selector + remove button.
        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
        };

        var enableBox = new CheckBox
        {
            IsChecked = mapping.Enabled,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        enableBox.IsCheckedChanged += (_, _) =>
        {
            mapping.Enabled = enableBox.IsChecked == true;
        };

        var label = new TextBlock
        {
            Text = $"{mapping.Target.Label}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var featureBox = BuildFeatureCombo();
        featureBox.Width = 70;
        featureBox.FontSize = 10;
        featureBox.SelectedIndex = Array.IndexOf(AllFeatures, mapping.Source);
        featureBox.SelectionChanged += (_, _) =>
        {
            if (featureBox.SelectedIndex >= 0)
                mapping.Source = AllFeatures[featureBox.SelectedIndex];
        };

        var removeBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 14,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        removeBtn.Click += (_, _) => _controller.RemoveMapping(mapping);

        Grid.SetColumn(enableBox, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(featureBox, 2);
        Grid.SetColumn(removeBtn, 3);
        topRow.Children.Add(enableBox);
        topRow.Children.Add(label);
        topRow.Children.Add(featureBox);
        topRow.Children.Add(removeBtn);

        // Row 2: depth slider + readout.
        var depthRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,40"),
            Margin = new Thickness(20, 0, 0, 0),
        };
        depthRow.Children.Add(new TextBlock
        {
            Text = "depth",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });

        var depthReadout = new TextBlock
        {
            Text = mapping.Depth.ToString("F2"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xa0, 0xc0, 0xe0)),
            FontSize = 10,
            FontFamily = new FontFamily("monospace"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var depthSlider = new Slider
        {
            Minimum = 0.0,
            Maximum = 1.0,
            Value = mapping.Depth,
        };
        depthSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            mapping.Depth = depthSlider.Value;
            depthReadout.Text = mapping.Depth.ToString("F2");
        };

        Grid.SetColumn(depthSlider, 1);
        Grid.SetColumn(depthReadout, 2);
        depthRow.Children.Add(depthSlider);
        depthRow.Children.Add(depthReadout);

        inner.Children.Add(topRow);
        inner.Children.Add(depthRow);
        container.Child = inner;
        return container;
    }

    private static ComboBox BuildFeatureCombo()
    {
        var combo = new ComboBox { SelectedIndex = 0 };
        foreach (var label in FeatureLabels)
            combo.Items.Add(label);
        return combo;
    }
}
