using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;

namespace Sshelfy.App.Controls;

/// <summary>
/// A thin drag handle that resizes a panel by updating a bound size value.
/// Unlike GridSplitter it works with Auto-sized tracks, so the size can live in a view model.
/// </summary>
public sealed class ResizeHandle : Thumb
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ResizeHandle, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ResizeHandle, double>(nameof(Minimum), 120);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ResizeHandle, double>(nameof(Maximum), 800);

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<ResizeHandle, Orientation>(nameof(Orientation), Orientation.Vertical);

    /// <summary>When true, dragging left/up grows the panel (handle sits on the panel's leading edge).</summary>
    public static readonly StyledProperty<bool> IsInvertedProperty =
        AvaloniaProperty.Register<ResizeHandle, bool>(nameof(IsInverted));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool IsInverted
    {
        get => GetValue(IsInvertedProperty);
        set => SetValue(IsInvertedProperty, value);
    }

    protected override void OnDragDelta(VectorEventArgs e)
    {
        base.OnDragDelta(e);
        var delta = Orientation == Orientation.Vertical ? e.Vector.X : e.Vector.Y;
        if (IsInverted)
            delta = -delta;
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
    }
}
