using System.Windows;
using System.Windows.Media;

namespace Lidget.App.Views;

/// <summary>
/// A ring with a track and an arc that starts at the top and turns clockwise. The Rings widget style
/// draws the session and weekly limits with two of them (plan decision D66).
/// </summary>
public sealed class RingArc : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RingArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The length of the arc, from 0 to 100.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var thickness = StrokeThickness;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= thickness)
        {
            return;
        }

        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Track is { } track)
        {
            drawingContext.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        }

        var percent = Math.Clamp(Percent, 0, 100);
        if (Stroke is not { } stroke || percent <= 0)
        {
            return;
        }

        if (percent >= 100)
        {
            drawingContext.DrawEllipse(null, new Pen(stroke, thickness), center, radius, radius);
            return;
        }

        var angle = percent / 100 * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: percent > 50, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        arc.Freeze();
        var pen = new Pen(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawGeometry(null, pen, arc);
    }
}
