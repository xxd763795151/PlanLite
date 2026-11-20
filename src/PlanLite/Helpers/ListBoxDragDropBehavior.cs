using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlanLite.Helpers;

/// <summary>
/// Attached behavior enabling drag-and-drop reordering on an ItemsControl / ListBox
/// without stealing normal clicks on checkboxes, sliders, or task titles.
/// </summary>
public static class ListBoxDragDropBehavior
{
    private const string DragDataFormat = "PlanLite.TaskDragItem";

    private static Point _dragStartPoint;
    private static object? _draggedItem;
    private static bool _isDragging;

    public static readonly DependencyProperty ReorderCommandProperty =
        DependencyProperty.RegisterAttached(
            "ReorderCommand",
            typeof(ICommand),
            typeof(ListBoxDragDropBehavior),
            new PropertyMetadata(null, OnReorderCommandChanged));

    public static ICommand? GetReorderCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(ReorderCommandProperty);

    public static void SetReorderCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(ReorderCommandProperty, value);

    private static void OnReorderCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl)
        {
            return;
        }

        itemsControl.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        itemsControl.PreviewMouseMove -= OnPreviewMouseMove;
        itemsControl.DragOver -= OnDragOver;
        itemsControl.Drop -= OnDrop;

        if (e.NewValue is not null)
        {
            itemsControl.AllowDrop = true;
            itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            itemsControl.PreviewMouseMove += OnPreviewMouseMove;
            itemsControl.DragOver += OnDragOver;
            itemsControl.Drop += OnDrop;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl itemsControl)
        {
            return;
        }

        if (IsInteractiveDescendant(e.OriginalSource as DependencyObject))
        {
            _draggedItem = null;
            return;
        }

        _dragStartPoint = e.GetPosition(itemsControl);
        var container = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        _draggedItem = container is not null ? itemsControl.ItemContainerGenerator.ItemFromContainer(container) : null;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null || _isDragging)
        {
            return;
        }

        if (sender is not ItemsControl itemsControl)
        {
            return;
        }

        var currentPos = e.GetPosition(itemsControl);
        var diff = _dragStartPoint - currentPos;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance + 3 ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance + 3)
        {
            _isDragging = true;
            try
            {
                var data = new DataObject(DragDataFormat, _draggedItem);
                DragDrop.DoDragDrop(itemsControl, data, DragDropEffects.Move);
            }
            finally
            {
                _isDragging = false;
                _draggedItem = null;
            }
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDataFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl itemsControl || !e.Data.GetDataPresent(DragDataFormat))
        {
            return;
        }

        var sourceItem = e.Data.GetData(DragDataFormat);
        if (sourceItem is null)
        {
            return;
        }

        var targetContainer = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        var targetItem = targetContainer is not null
            ? itemsControl.ItemContainerGenerator.ItemFromContainer(targetContainer)
            : null;

        if (targetItem is null || ReferenceEquals(sourceItem, targetItem))
        {
            return;
        }

        if (itemsControl.ItemsSource is not IList list)
        {
            return;
        }

        var oldIndex = list.IndexOf(sourceItem);
        var newIndex = list.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        // Move item in observable collection/list first, then invoke command so ViewModel persists order
        if (oldIndex < newIndex)
        {
            list.RemoveAt(oldIndex);
            list.Insert(newIndex, sourceItem);
        }
        else
        {
            list.RemoveAt(oldIndex);
            list.Insert(newIndex, sourceItem);
        }

        var command = GetReorderCommand(itemsControl);
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private static DependencyObject? FindItemContainer(ItemsControl itemsControl, DependencyObject? source)
    {
        var current = source;
        while (current is not null && current != itemsControl)
        {
            var item = itemsControl.ItemContainerGenerator.ItemFromContainer(current);
            if (item != DependencyProperty.UnsetValue)
            {
                return current;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsInteractiveDescendant(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase ||
                current is Slider ||
                current is System.Windows.Controls.Primitives.TextBoxBase ||
                current is ComboBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
