using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Sshelfy.App.Behaviors;

/// <summary>
/// Attached properties that route view-only input (double tap, initial focus) to view model
/// commands, so views need no code-behind.
/// </summary>
public static class InputBehaviors
{
    public static readonly AttachedProperty<ICommand?> DoubleTapCommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("DoubleTapCommand", typeof(InputBehaviors));

    public static readonly AttachedProperty<object?> DoubleTapCommandParameterProperty =
        AvaloniaProperty.RegisterAttached<InputElement, object?>("DoubleTapCommandParameter", typeof(InputBehaviors));

    /// <summary>Runs on a single tap/click (with <see cref="DoubleTapCommandParameterProperty"/> as the parameter).</summary>
    public static readonly AttachedProperty<ICommand?> TapCommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("TapCommand", typeof(InputBehaviors));

    public static readonly AttachedProperty<bool> FocusOnAttachProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusOnAttach", typeof(InputBehaviors));

    /// <summary>Runs when keyboard focus moves into the element or one of its children, or it is clicked.</summary>
    public static readonly AttachedProperty<ICommand?> FocusWithinCommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("FocusWithinCommand", typeof(InputBehaviors));

    public static readonly AttachedProperty<object?> FocusWithinCommandParameterProperty =
        AvaloniaProperty.RegisterAttached<InputElement, object?>("FocusWithinCommandParameter", typeof(InputBehaviors));

    /// <summary>Bind to a counter: every change focuses the control (and selects a text box's content).</summary>
    public static readonly AttachedProperty<int> FocusTriggerProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("FocusTrigger", typeof(InputBehaviors));

    static InputBehaviors()
    {
        DoubleTapCommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            element.DoubleTapped -= OnDoubleTapped;
            if (e.NewValue is not null)
                element.DoubleTapped += OnDoubleTapped;
        });

        TapCommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            element.Tapped -= OnTapped;
            if (e.NewValue is not null)
                element.Tapped += OnTapped;
        });

        FocusOnAttachProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.AttachedToVisualTree -= OnAttachedForFocus;
            if (e.NewValue is true)
            {
                control.AttachedToVisualTree += OnAttachedForFocus;
                if (TopLevel.GetTopLevel(control) is not null)
                    FocusSoon(control);
            }
        });

        FocusWithinCommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            element.RemoveHandler(InputElement.GotFocusEvent, OnFocusWithin);
            element.RemoveHandler(InputElement.PointerPressedEvent, OnFocusWithin);
            if (e.NewValue is not null)
            {
                element.AddHandler(InputElement.GotFocusEvent, OnFocusWithin, handledEventsToo: true);
                element.AddHandler(InputElement.PointerPressedEvent, OnFocusWithin, RoutingStrategies.Tunnel, handledEventsToo: true);
            }
        });

        FocusTriggerProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not int value || value == 0)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                control.Focus(NavigationMethod.Tab);
                (control as TextBox)?.SelectAll();
            }, DispatcherPriority.Loaded);
        });
    }

    public static ICommand? GetDoubleTapCommand(InputElement element) => element.GetValue(DoubleTapCommandProperty);

    public static void SetDoubleTapCommand(InputElement element, ICommand? value) => element.SetValue(DoubleTapCommandProperty, value);

    public static object? GetDoubleTapCommandParameter(InputElement element) => element.GetValue(DoubleTapCommandParameterProperty);

    public static void SetDoubleTapCommandParameter(InputElement element, object? value) =>
        element.SetValue(DoubleTapCommandParameterProperty, value);

    public static ICommand? GetTapCommand(InputElement element) => element.GetValue(TapCommandProperty);

    public static void SetTapCommand(InputElement element, ICommand? value) => element.SetValue(TapCommandProperty, value);

    public static bool GetFocusOnAttach(Control control) => control.GetValue(FocusOnAttachProperty);

    public static void SetFocusOnAttach(Control control, bool value) => control.SetValue(FocusOnAttachProperty, value);

    public static ICommand? GetFocusWithinCommand(InputElement element) => element.GetValue(FocusWithinCommandProperty);

    public static void SetFocusWithinCommand(InputElement element, ICommand? value) => element.SetValue(FocusWithinCommandProperty, value);

    public static object? GetFocusWithinCommandParameter(InputElement element) => element.GetValue(FocusWithinCommandParameterProperty);

    public static void SetFocusWithinCommandParameter(InputElement element, object? value) =>
        element.SetValue(FocusWithinCommandParameterProperty, value);

    public static int GetFocusTrigger(Control control) => control.GetValue(FocusTriggerProperty);

    public static void SetFocusTrigger(Control control, int value) => control.SetValue(FocusTriggerProperty, value);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not InputElement element || GetDoubleTapCommand(element) is not { } command)
            return;

        var parameter = GetDoubleTapCommandParameter(element);
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not InputElement element || GetTapCommand(element) is not { } command)
            return;

        var parameter = GetDoubleTapCommandParameter(element);
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }

    private static void OnFocusWithin(object? sender, RoutedEventArgs e)
    {
        if (sender is not InputElement element || GetFocusWithinCommand(element) is not { } command)
            return;

        var parameter = GetFocusWithinCommandParameter(element);
        if (command.CanExecute(parameter))
            command.Execute(parameter);
    }

    private static void OnAttachedForFocus(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            FocusSoon(control);
    }


    private static void FocusSoon(Control control) =>
        Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
}
