using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Parsec.Audio.Midi;

namespace Parsec.App;

/// <summary>
/// Improvement M3b: sidebar editor for the geometry→MIDI mapping table. One row per continuous
/// CC signal (enable, CC#, channel, output min/max, invert) plus the three note-group channels
/// (gestures, 4×4 spatial, fine 8×6 grid). Edits the live <see cref="MidiMappingConfig"/> in
/// place, so they take effect on the next frame with no restart. Built in code-behind to match
/// the project's panel style (cf. <see cref="AudioMappingPanel"/>).
/// </summary>
public sealed class MidiMappingPanel : UserControl
{
    private readonly MidiMappingConfig _config;

    public MidiMappingPanel(MidiMappingConfig config)
    {
        _config = config;

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(Header("MIDI MAPPINGS"));
        root.Children.Add(new TextBlock
        {
            Text = "Signal → CC / channel / range. Edits apply live.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xa8, 0xc0)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        });

        // Column legend.
        var legend = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,46,38,46,46,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        AddCell(legend, 0, LegendText(""));
        AddCell(legend, 1, LegendText("signal"));
        AddCell(legend, 2, LegendText("CC"));
        AddCell(legend, 3, LegendText("ch"));
        AddCell(legend, 4, LegendText("min"));
        AddCell(legend, 5, LegendText("max"));
        AddCell(legend, 6, LegendText("inv"));
        root.Children.Add(legend);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 280,
        };
        var rows = new StackPanel { Spacing = 1 };
        foreach (var m in _config.Cc)
            rows.Children.Add(BuildCcRow(m));
        scroll.Content = rows;
        root.Children.Add(scroll);

        // Note-group channels.
        root.Children.Add(Header("NOTE GROUPS", small: true));
        root.Children.Add(BuildNoteGroupRow("Gestures (60–71)", _config.Gestures));
        root.Children.Add(BuildNoteGroupRow("Spatial 4×4 (36–51)", _config.Spatial4x4));
        root.Children.Add(BuildNoteGroupRow("Fine 8×6 (36–83)", _config.FineGrid));

        Content = root;
    }

    private Control BuildCcRow(MidiCcMapping m)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,46,38,46,46,Auto"),
            Margin = new Thickness(0, 1, 0, 1),
        };

        var enable = new CheckBox { IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        enable.IsCheckedChanged += (_, _) => m.Enabled = enable.IsChecked == true;
        AddCell(grid, 0, enable);

        AddCell(grid, 1, new TextBlock
        {
            Text = m.Label + (m.Signed ? " ±" : ""),
            Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        AddCell(grid, 2, Spin(0, 127, m.Cc, v => m.Cc = v));
        AddCell(grid, 3, Spin(1, 16, m.Channel + 1, v => m.Channel = v - 1));   // display 1-based
        AddCell(grid, 4, Spin(0, 127, m.OutMin, v => m.OutMin = v));
        AddCell(grid, 5, Spin(0, 127, m.OutMax, v => m.OutMax = v));

        var invert = new CheckBox { IsChecked = m.Invert, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        invert.IsCheckedChanged += (_, _) => m.Invert = invert.IsChecked == true;
        AddCell(grid, 6, invert);

        return grid;
    }

    private Control BuildNoteGroupRow(string label, MidiNoteGroupConfig g)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,38"), Margin = new Thickness(0, 1, 0, 1) };

        var enable = new CheckBox { IsChecked = g.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        enable.IsCheckedChanged += (_, _) => g.Enabled = enable.IsChecked == true;
        AddCell(grid, 0, enable);

        AddCell(grid, 1, new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        AddCell(grid, 2, new TextBlock
        {
            Text = "ch",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
        });
        AddCell(grid, 3, Spin(1, 16, g.Channel + 1, v => g.Channel = v - 1));
        return grid;
    }

    private static NumericUpDown Spin(int min, int max, int value, Action<int> onChange)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Increment = 1,
            FormatString = "0",
            FontSize = 10,
            Margin = new Thickness(1, 0, 1, 0),
            ShowButtonSpinner = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        n.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } d) onChange((int)d);
        };
        return n;
    }

    private static void AddCell(Grid g, int col, Control c)
    {
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    private static TextBlock LegendText(string t) => new()
    {
        Text = t,
        Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x80)),
        FontSize = 9,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Header(string t, bool small = false) => new()
    {
        Text = t,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
        FontSize = small ? 10 : 11,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, small ? 6 : 0, 0, 0),
    };
}
