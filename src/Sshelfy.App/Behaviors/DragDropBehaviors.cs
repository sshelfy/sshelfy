using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sshelfy.App.ViewModels.Tabs;

namespace Sshelfy.App.Behaviors;

/// <summary>
/// Drag and drop wiring for views:
/// <list type="bullet">
/// <item><c>FilesCommand</c>: files dropped from the OS → <see cref="FileDropRequest"/>.</item>
/// <item><c>ItemsCommand</c>: an in-app item dragged here → <see cref="EntryDropRequest"/>.</item>
/// <item><c>DragPayload</c>: makes the element a drag source for that object.</item>
/// <item><c>Target</c>: what the drop lands on (defaults to the element's DataContext).</item>
/// </list>
/// Drop targets get the <c>:dragover</c>-like class <c>dropTarget</c> while hovered.
/// </summary>
public static class DragDropBehaviors
{
    private const double DragThreshold = 6;

    public static readonly DataFormat<object> ItemFormat = DataFormat.CreateInProcessFormat<object>("sshelfy.item");

    public static readonly AttachedProperty<ICommand?> FilesCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("FilesCommand", typeof(DragDropBehaviors));

    public static readonly AttachedProperty<ICommand?> ItemsCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("ItemsCommand", typeof(DragDropBehaviors));

    public static readonly AttachedProperty<object?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Target", typeof(DragDropBehaviors));

    public static readonly AttachedProperty<object?> DragPayloadProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("DragPayload", typeof(DragDropBehaviors));

    private static readonly AttachedProperty<PendingDrag?> PendingDragProperty =
        AvaloniaProperty.RegisterAttached<Control, PendingDrag?>("PendingDrag", typeof(DragDropBehaviors));

    static DragDropBehaviors()
    {
        FilesCommandProperty.Changed.AddClassHandler<Control>((c, _) => AttachDropHandlers(c));
        ItemsCommandProperty.Changed.AddClassHandler<Control>((c, _) => AttachDropHandlers(c));
        DragPayloadProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(InputElement.PointerPressedEvent, OnSourcePressed);
            c.RemoveHandler(InputElement.PointerMovedEvent, OnSourceMoved);
            if (e.NewValue is not null)
            {
                c.AddHandler(InputElement.PointerPressedEvent, OnSourcePressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                c.AddHandler(InputElement.PointerMovedEvent, OnSourceMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            }
        });
    }

    public static ICommand? GetFilesCommand(Control c) => c.GetValue(FilesCommandProperty);
    public static void SetFilesCommand(Control c, ICommand? value) => c.SetValue(FilesCommandProperty, value);
    public static ICommand? GetItemsCommand(Control c) => c.GetValue(ItemsCommandProperty);
    public static void SetItemsCommand(Control c, ICommand? value) => c.SetValue(ItemsCommandProperty, value);
    public static object? GetTarget(Control c) => c.GetValue(TargetProperty);
    public static void SetTarget(Control c, object? value) => c.SetValue(TargetProperty, value);
    public static object? GetDragPayload(Control c) => c.GetValue(DragPayloadProperty);
    public static void SetDragPayload(Control c, object? value) => c.SetValue(DragPayloadProperty, value);

    private static void AttachDropHandlers(Control control)
    {
        control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        control.RemoveHandler(DragDrop.DropEvent, OnDrop);

        var enabled = GetFilesCommand(control) is not null || GetItemsCommand(control) is not null;
        DragDrop.SetAllowDrop(control, enabled);
        if (!enabled)
            return;

        control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        control.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static object? TargetOf(Control control) =>
        control.IsSet(TargetProperty) ? GetTarget(control) : control.DataContext;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
            return;

        var effect = DragDropEffects.None;
        if (GetFilesCommand(control) is not null && e.DataTransfer.Contains(DataFormat.File))
        {
            effect = DragDropEffects.Copy;
        }
        else if (GetItemsCommand(control) is { } command && e.DataTransfer.TryGetValue(ItemFormat) is { } payload)
        {
            var request = new EntryDropRequest(payload, TargetOf(control));
            if (payload != request.Target && command.CanExecute(request))
                effect = DragDropEffects.Move;
        }

        if (effect == DragDropEffects.None)
            return; // Let an outer drop target handle it.

        e.DragEffects = effect;
        control.Classes.Set("dropTarget", true);
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control control)
            control.Classes.Set("dropTarget", false);
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
            return;

        control.Classes.Set("dropTarget", false);
        var target = TargetOf(control);

        if (GetFilesCommand(control) is { } filesCommand && e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            var request = new FileDropRequest(paths, target);
            if (paths.Count > 0 && filesCommand.CanExecute(request))
            {
                filesCommand.Execute(request);
                e.Handled = true;
            }
        }
        else if (GetItemsCommand(control) is { } itemsCommand && e.DataTransfer.TryGetValue(ItemFormat) is { } payload && payload != target)
        {
            var request = new EntryDropRequest(payload, target);
            if (itemsCommand.CanExecute(request))
            {
                itemsCommand.Execute(request);
                e.Handled = true;
            }
        }
    }

    private static void OnSourcePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            control.SetValue(PendingDragProperty, new PendingDrag(e, e.GetPosition(control)));
    }

    private static async void OnSourceMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || control.GetValue(PendingDragProperty) is not { } pending)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            control.ClearValue(PendingDragProperty);
            return;
        }

        var delta = e.GetPosition(control) - pending.Origin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        control.ClearValue(PendingDragProperty);
        if (GetDragPayload(control) is not { } payload)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ItemFormat, payload));
        await DragDrop.DoDragDropAsync(pending.Press, data, DragDropEffects.Move);
    }

    private sealed record PendingDrag(PointerPressedEventArgs Press, Point Origin);
}
