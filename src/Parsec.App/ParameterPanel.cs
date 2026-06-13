using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Parsec.App;

/// <summary>
/// Builds a right-dock parameter panel from a <see cref="ParamSchema"/>:
/// grouped, labeled sliders each with a live numeric readout. Moving a slider
/// mutates the underlying state (via the descriptor's setter) and invokes
/// <see cref="OnChanged"/> so the host can mark the view dirty and re-render.
/// </summary>
/// <remarks>
/// IMPORTANT: this is a UserControl that CONTAINS a ScrollViewer, not a
/// ScrollViewer subclass. A bare ScrollViewer placed in the host is laid out
/// with unbounded height (the Grid-with-columns measure issue) and so sizes to
/// its content and never scrolls. A UserControl fills its container and then
/// gives its inner ScrollViewer a bounded height. This wrapping layer is what
/// makes scrolling work, and matches the Helios panel's structure.
/// </remarks>
public sealed class ParameterPanel : UserControl
{
    public event Action? OnChanged;

    public ParameterPanel(ParamSchema schema)
    {
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 4 };

        foreach (var group in schema.Groups)
        {
            root.Children.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 12, 0, 4),
            });

            foreach (var p in schema.InGroup(group))
                root.Children.Add(BuildRow(p));
        }

        scroll.Content = root;
        Content = scroll;
    }

    private Control BuildRow(ParamDescriptor p)
    {
        if (p.IsToggle)
        {
            var cb = new CheckBox
            {
                Content = p.Label,
                IsChecked = p.Get() >= 0.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                p.Set(cb.IsChecked == true ? 1.0 : 0.0);
                OnChanged?.Invoke();
            };
            return cb;
        }

        var container = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };

        // Label + numeric readout on one line.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        var label = new TextBlock
        {
            Text = p.Label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd8)),
            FontSize = 12,
        };
        var readout = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xa0, 0xc0, 0xe0)),
            FontSize = 12,
            FontFamily = new FontFamily("monospace"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(readout, 1);
        header.Children.Add(label);
        header.Children.Add(readout);

        var slider = new Slider
        {
            Minimum = p.Min,
            Maximum = p.Max,
            Value = p.Get(),
            SmallChange = p.Step > 0 ? p.Step : (p.Max - p.Min) / 100.0,
            TickFrequency = p.Step > 0 ? p.Step : 0,
            IsSnapToTickEnabled = p.Step > 0,
        };

        void UpdateReadout() => readout.Text = p.Get().ToString("F" + p.Decimals);
        UpdateReadout();

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                p.Set(slider.Value);
                UpdateReadout();
                OnChanged?.Invoke();
            }
        };

        container.Children.Add(header);
        container.Children.Add(slider);
        return container;
    }
}
