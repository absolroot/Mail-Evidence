using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MailViewer.App;

public static class InteractionFeedback
{
    public static readonly DependencyProperty HoverVisibleProperty = DependencyProperty.RegisterAttached(
        "HoverVisible", typeof(bool), typeof(InteractionFeedback), new PropertyMetadata(false, OnHoverChanged));

    public static bool GetHoverVisible(DependencyObject item) => (bool)item.GetValue(HoverVisibleProperty);
    public static void SetHoverVisible(DependencyObject item, bool value) => item.SetValue(HoverVisibleProperty, value);

    public static readonly DependencyProperty ChevronOpenProperty = DependencyProperty.RegisterAttached(
        "ChevronOpen", typeof(bool), typeof(InteractionFeedback), new PropertyMetadata(false, OnChevronChanged));
    public static bool GetChevronOpen(DependencyObject item) => (bool)item.GetValue(ChevronOpenProperty);
    public static void SetChevronOpen(DependencyObject item, bool value) => item.SetValue(ChevronOpenProperty, value);

    private static bool CanAnimate(FrameworkElement element) =>
        element.IsLoaded && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private static void OnChevronChanged(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not FrameworkElement icon) return;
        var rotation = icon.RenderTransform as RotateTransform ?? new RotateTransform();
        icon.RenderTransform = rotation;
        double from = rotation.Angle;
        double to = (bool)args.NewValue ? 90 : 0;
        rotation.Angle = to;
        rotation.BeginAnimation(RotateTransform.AngleProperty, CanAnimate(icon) ? Animate(from, to, 120) : null);
    }

    internal static void Acknowledge(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, CanAnimate(element) ? Animate(0.72, 1, 140) : null);
    }

    private static void OnHoverChanged(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not FrameworkElement layer) return;
        bool entering = (bool)args.NewValue;
        double end = entering ? 1 : 0;
        double start = layer.Opacity;
        layer.SetValue(UIElement.OpacityProperty, end);
        layer.BeginAnimation(UIElement.OpacityProperty, CanAnimate(layer) ? Animate(start, end, entering ? 110 : 80) : null);
    }

    private static DoubleAnimation Animate(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };
}

