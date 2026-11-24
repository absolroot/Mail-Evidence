using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MailViewer.App;

public partial class App : Application
{
    static App() =>
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(ReleasePointerFocus), handledEventsToo: true);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MailText.LoadPreference();
        bool? requestedDark = e.Args.Contains("--light", StringComparer.OrdinalIgnoreCase) ? false
            : e.Args.Contains("--dark", StringComparer.OrdinalIgnoreCase) ? true : null;
        Appearance.Apply(this, requestedDark ?? Appearance.MatchWindows());
        string[] inputs = e.Args.Where(value => !value.Equals("--light", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("--dark", StringComparison.OrdinalIgnoreCase)).ToArray();
        MainWindow window = new(inputs, persistLanguage: true);
        Rect workArea = SystemParameters.WorkArea;
        window.Width = Math.Clamp(workArea.Width - 32, window.MinWidth, 1480);
        window.Height = Math.Clamp(workArea.Height - 32, window.MinHeight, 940);
        MainWindow = window;
        window.Show();
        if (inputs.Length == 0)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(window.ShowStartupIntake));
    }

    private static void ReleasePointerFocus(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window { Content: UIElement surface } window ||
            e.ChangedButton is not (MouseButton.Left or MouseButton.Right) ||
            Keyboard.FocusedElement is not DependencyObject focused || ReferenceEquals(focused, surface) ||
            !ReferenceEquals(Window.GetWindow(focused), window) || e.OriginalSource is not DependencyObject target)
            return;

        for (DependencyObject? node = target; node is not null; node = FocusParent(node))
            if (ReferenceEquals(node, focused) || node is MenuItem or ScrollBar or Thumb or ComboBoxItem ||
                node is ComboBox { IsDropDownOpen: true })
                return;

        if (!surface.Focusable)
        {
            surface.SetCurrentValue(UIElement.FocusableProperty, true);
            KeyboardNavigation.SetIsTabStop(surface, false);
        }
        Keyboard.Focus(surface);
    }

    private static DependencyObject? FocusParent(DependencyObject node) =>
        (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
}
