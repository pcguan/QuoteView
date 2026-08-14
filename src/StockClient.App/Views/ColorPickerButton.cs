using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StockClient.App.Views;

/// <summary>
/// A colour swatch that opens a real picker instead of a seven-entry dropdown:
/// a saturation/value square, a hue strip beside it, a hex box for typing an
/// exact value, and the old fixed palette kept as one-click presets.
///
/// Colour is applied live, the same way every other control in the settings
/// window behaves — <c>onPick</c> fires on every move of the thumb, so the panel
/// redraws while dragging and there is nothing to confirm.
///
/// Stored as <c>#RRGGBB</c>, which is what the config already holds and what
/// <see cref="ColorConverter"/> reads back, so any hex the user types round-trips
/// through the existing settings file unchanged.
///
/// <b>A self-drawn Border, not a Button.</b> App.xaml merges WPF UI's
/// ControlsDictionary, whose implicit Button/TextBox styles carry a much taller
/// template than the plain ones — that's why every ui:TextBox in this codebase
/// is written Height="36". Pinning a real Button to 24px squeezed the swatch and
/// the hex out of view behind the template's own padding. Drawing the face here
/// keeps it the size this row has room for, and matches the dark panel besides.
/// </summary>
public sealed class ColorPickerButton : Border
{
    /// <summary>
    /// Quick presets. The first seven are the palette this control replaced, so
    /// an existing configuration is still one click away.
    /// </summary>
    private static readonly string[] Presets =
    {
        "#FFFFFF", "#EF5350", "#26A69A", "#FFC107", "#4C8DFF", "#8B93A3", "#000000",
        "#EDF1F7", "#FF7043", "#66BB6A", "#FFEE58", "#42A5F5", "#AB47BC", "#5F6672",
    };

    private const double FieldWidth = 200;
    private const double FieldHeight = 140;
    private const double HueWidth = 18;
    private const double ThumbSize = 12;

    private readonly Action<string> _onPick;
    private readonly Rectangle _swatch;
    private readonly TextBlock _label;

    private Popup? _popup;
    private Rectangle _fieldBase = null!;   // pure hue under the two gradients
    private Ellipse _fieldThumb = null!;
    private Rectangle _hueThumb = null!;
    private TextBox _hexBox = null!;
    private Rectangle _preview = null!;

    private double _hue;          // 0..360
    private double _saturation;   // 0..1
    private double _value;        // 0..1

    /// <summary>Guards the hex box against being rewritten by its own edit.</summary>
    private bool _syncing;

    public ColorPickerButton(string hex, Action<string> onPick, string tooltip, double width, double leftMargin = 0)
    {
        _onPick = onPick;

        Width = width;
        Height = 24;
        Padding = new Thickness(5, 0, 5, 0);
        Margin = new Thickness(leftMargin, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Frozen("#1A2030");
        BorderBrush = Frozen("#39435A");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Cursor = Cursors.Hand;
        ToolTip = tooltip;

        _swatch = new Rectangle
        {
            Width = 12, Height = 12,
            Stroke = Frozen("#5F6672"), StrokeThickness = 1,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _label = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = Frozen("#EDF1F7"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var face = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        face.Children.Add(_swatch);
        face.Children.Add(_label);
        Child = face;

        var color = Parse(hex);
        (_hue, _saturation, _value) = ToHsv(color);
        ShowOnFace(color);

        MouseEnter += (_, _) => BorderBrush = Frozen("#5C6B8A");
        MouseLeave += (_, _) => { BorderBrush = Frozen("#39435A"); _pressed = false; };

        // Opens on release, not on press. A popup opened on MouseDown would be
        // dismissed by the MouseUp of that very same click — the release lands
        // outside the popup, which is exactly what "click elsewhere to close"
        // looks like to WPF.
        MouseLeftButtonDown += (_, e) => { _pressed = true; e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            e.Handled = true;
            Toggle();
        };
    }

    /// <summary>Set between press and release on this swatch, so a drag that merely ends here doesn't open it.</summary>
    private bool _pressed;

    /// <summary>
    /// When an outside click dismisses the popup, the release of that same click
    /// can still land here — without this the swatch would reopen what the user
    /// just closed.
    /// </summary>
    private long _closedAt;

    /// <summary>The one picker whose popup is up; opening another closes it.</summary>
    private static ColorPickerButton? _openPicker;

    private Window? _owner;

    private void Toggle()
    {
        _popup ??= BuildPopup();

        if (_popup.IsOpen) { ClosePopup(); return; }
        if (Environment.TickCount64 - _closedAt < 250) return;

        OpenPopup();
    }

    /// <summary>
    /// Dismissal is handled here rather than by <c>StaysOpen=false</c>: that mode
    /// makes the popup grab mouse capture, which both swallows the opening click
    /// and fights the capture the saturation/value square needs for dragging.
    /// Watching the owner window for a click or a deactivate gives the same
    /// behaviour without touching capture at all.
    /// </summary>
    private void OpenPopup()
    {
        _openPicker?.ClosePopup();
        _openPicker = this;

        _owner = Window.GetWindow(this);
        if (_owner is not null)
        {
            _owner.PreviewMouseDown += OwnerMouseDown;
            // Wheel too: the popup is anchored to where the swatch was, and
            // scrolling the field list underneath would leave it floating over
            // an unrelated row.
            _owner.PreviewMouseWheel += OwnerMouseWheel;
            _owner.Deactivated += OwnerDeactivated;
            _owner.Closed += OwnerDeactivated;
        }

        _popup!.IsOpen = true;
    }

    private void ClosePopup()
    {
        if (_owner is not null)
        {
            _owner.PreviewMouseDown -= OwnerMouseDown;
            _owner.PreviewMouseWheel -= OwnerMouseWheel;
            _owner.Deactivated -= OwnerDeactivated;
            _owner.Closed -= OwnerDeactivated;
            _owner = null;
        }

        if (ReferenceEquals(_openPicker, this)) _openPicker = null;

        if (_popup is not null) _popup.IsOpen = false;
        _closedAt = Environment.TickCount64;
    }

    // Any click in the settings window itself — the popup lives in its own HWND,
    // so clicks inside the picker never come through here.
    private void OwnerMouseDown(object sender, MouseButtonEventArgs e) => ClosePopup();

    private void OwnerMouseWheel(object sender, MouseWheelEventArgs e) => ClosePopup();

    private void OwnerDeactivated(object? sender, EventArgs e) => ClosePopup();

    private Popup BuildPopup()
    {
        // The saturation/value square: pure hue, washed to white towards the left
        // and to black towards the bottom. Three stacked rectangles is all it
        // takes — no bitmap, so it stays sharp at any DPI.
        _fieldBase = new Rectangle { Fill = new SolidColorBrush(FromHsv(_hue, 1, 1)) };

        var whiteWash = new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0.5), new Point(1, 0.5)),
        };

        var blackWash = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black,
                new Point(0.5, 0), new Point(0.5, 1)),
        };

        _fieldThumb = new Ellipse
        {
            Width = ThumbSize, Height = ThumbSize,
            Stroke = Brushes.White, StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        var fieldOverlay = new Canvas { Background = Brushes.Transparent };
        fieldOverlay.Children.Add(_fieldThumb);
        fieldOverlay.MouseLeftButtonDown += (s, e) =>
        {
            fieldOverlay.CaptureMouse();
            PickField(e.GetPosition(fieldOverlay));
        };
        fieldOverlay.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && fieldOverlay.IsMouseCaptured)
                PickField(e.GetPosition(fieldOverlay));
        };
        fieldOverlay.MouseLeftButtonUp += (_, _) => fieldOverlay.ReleaseMouseCapture();

        var field = new Grid { Width = FieldWidth, Height = FieldHeight };
        field.Children.Add(_fieldBase);
        field.Children.Add(whiteWash);
        field.Children.Add(blackWash);
        field.Children.Add(fieldOverlay);

        // Hue strip: the six primaries in order, red repeated at the far end so
        // the wheel closes.
        var hueBar = new Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(255, 0, 0), 0.0),
                new(Color.FromRgb(255, 255, 0), 1 / 6.0),
                new(Color.FromRgb(0, 255, 0), 2 / 6.0),
                new(Color.FromRgb(0, 255, 255), 3 / 6.0),
                new(Color.FromRgb(0, 0, 255), 4 / 6.0),
                new(Color.FromRgb(255, 0, 255), 5 / 6.0),
                new(Color.FromRgb(255, 0, 0), 1.0),
            }, 90),
        };

        _hueThumb = new Rectangle
        {
            Width = HueWidth, Height = 3,
            Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        var hueOverlay = new Canvas { Background = Brushes.Transparent };
        hueOverlay.Children.Add(_hueThumb);
        hueOverlay.MouseLeftButtonDown += (_, e) =>
        {
            hueOverlay.CaptureMouse();
            PickHue(e.GetPosition(hueOverlay));
        };
        hueOverlay.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && hueOverlay.IsMouseCaptured)
                PickHue(e.GetPosition(hueOverlay));
        };
        hueOverlay.MouseLeftButtonUp += (_, _) => hueOverlay.ReleaseMouseCapture();

        var hue = new Grid { Width = HueWidth, Height = FieldHeight, Margin = new Thickness(8, 0, 0, 0) };
        hue.Children.Add(hueBar);
        hue.Children.Add(hueOverlay);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(field);
        top.Children.Add(hue);

        // Hex row: typed input is the only way to hit an exact brand colour, and
        // it is also how a value gets copied from one field to another.
        _preview = new Rectangle
        {
            Width = 26, Height = 26,
            Stroke = Frozen("#5F6672"), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        // No fixed height, and no fixed width beyond a minimum: the WPF UI theme
        // gives TextBox a tall template with generous padding, and forcing it
        // smaller hides the text inside its own chrome.
        _hexBox = new TextBox
        {
            MinWidth = 104,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Text = Hex(FromHsv(_hue, _saturation, _value)),
        };
        _hexBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;

            var text = _hexBox.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length != 7 || !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out _))
                return;   // half-typed: leave it alone until it parses

            var typed = Parse(text);
            (_hue, _saturation, _value) = ToHsv(typed);
            Emit(fromHexBox: true);
        };

        var done = new Button
        {
            Content = "完成",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        done.Click += (_, _) => ClosePopup();

        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) ClosePopup();
        };

        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        hexRow.Children.Add(_preview);
        hexRow.Children.Add(_hexBox);
        hexRow.Children.Add(done);

        var presets = new WrapPanel { Width = FieldWidth + HueWidth + 8, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var preset in Presets)
        {
            var cell = new Border
            {
                Width = 18, Height = 18,
                Margin = new Thickness(0, 0, 4, 4),
                Background = Frozen(preset),
                BorderBrush = Frozen("#3A4358"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = preset,
            };
            var captured = preset;
            cell.MouseLeftButtonDown += (_, _) =>
            {
                (_hue, _saturation, _value) = ToHsv(Parse(captured));
                Emit();
            };
            presets.Children.Add(cell);
        }

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(hexRow);
        body.Children.Add(presets);

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,            // dismissal is driven from OpenPopup/ClosePopup
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Frozen("#12161F"),
                BorderBrush = Frozen("#2A3244"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Child = body,
            },
        };

        popup.Opened += (_, _) => SyncThumbs();
        popup.Closed += (_, _) => _closedAt = Environment.TickCount64;
        return popup;
    }

    private void PickField(Point p)
    {
        _saturation = Math.Clamp(p.X / FieldWidth, 0, 1);
        _value = Math.Clamp(1 - p.Y / FieldHeight, 0, 1);
        Emit();
    }

    private void PickHue(Point p)
    {
        _hue = Math.Clamp(p.Y / FieldHeight, 0, 1) * 360;
        Emit();
    }

    /// <summary>Pushes the current HSV everywhere: thumbs, hex box, face, config.</summary>
    private void Emit(bool fromHexBox = false)
    {
        var color = FromHsv(_hue, _saturation, _value);
        var hex = Hex(color);

        ShowOnFace(color);
        SyncThumbs();

        if (!fromHexBox && _hexBox is not null)
        {
            _syncing = true;
            _hexBox.Text = hex;
            _syncing = false;
        }

        _onPick(hex);
    }

    private void SyncThumbs()
    {
        if (_fieldThumb is null) return;

        _fieldBase.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        _preview.Fill = new SolidColorBrush(FromHsv(_hue, _saturation, _value));

        Canvas.SetLeft(_fieldThumb, _saturation * FieldWidth - ThumbSize / 2);
        Canvas.SetTop(_fieldThumb, (1 - _value) * FieldHeight - ThumbSize / 2);

        // Dark thumb outline on light areas, light on dark, so it never vanishes.
        _fieldThumb.Stroke = _value > 0.6 && _saturation < 0.6 ? Brushes.Black : Brushes.White;

        Canvas.SetTop(_hueThumb, _hue / 360 * FieldHeight - 1.5);
    }

    private void ShowOnFace(Color color)
    {
        _swatch.Fill = new SolidColorBrush(color);
        _label.Text = Hex(color);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.White;
        }
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        double h = 0;
        if (span > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / span) % 6);
            else if (max == g) h = 60 * ((b - r) / span + 2);
            else h = 60 * ((r - g) / span + 4);
        }
        if (h < 0) h += 360;

        return (h, max <= 0 ? 0 : span / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;

        var chroma = v * s;
        var x = chroma * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - chroma;

        double r, g, b;
        if (h < 60) (r, g, b) = (chroma, x, 0);
        else if (h < 120) (r, g, b) = (x, chroma, 0);
        else if (h < 180) (r, g, b) = (0, chroma, x);
        else if (h < 240) (r, g, b) = (0, x, chroma);
        else if (h < 300) (r, g, b) = (x, 0, chroma);
        else (r, g, b) = (chroma, 0, x);

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));

        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
