using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Dotty.App.Views;

/// <summary>
/// Builds context menus for terminal selection operations.
/// </summary>
internal sealed class SelectionContextMenuBuilder
{
    private readonly SelectionController _selectionController;

    public SelectionContextMenuBuilder(SelectionController selectionController)
    {
        _selectionController = selectionController;
    }

    /// <summary>
    /// Actions that can be performed from the context menu.
    /// </summary>
    public record SelectionContextMenuActions(
        Func<System.Threading.Tasks.Task> CopyAsync,
        Func<System.Threading.Tasks.Task> PasteAsync,
        Action SelectAll,
        Action ClearSelection,
        Action NewTab
    );

    /// <summary>
    /// Builds and returns a context menu for terminal selection operations.
    /// </summary>
    public ContextMenu Build(SelectionContextMenuActions actions)
    {
        var menu = new ContextMenu();

        var copyItem = new MenuItem
        {
            Header = "Copy",
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            IsEnabled = _selectionController.HasSelection
        };
        copyItem.Click += async (s, args) => await actions.CopyAsync();
        menu.Items.Add(copyItem);

        var pasteItem = new MenuItem
        {
            Header = "Paste",
            InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift)
        };
        pasteItem.Click += async (s, args) => await actions.PasteAsync();
        menu.Items.Add(pasteItem);

        menu.Items.Add(new Separator());

        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (s, args) => actions.SelectAll();
        menu.Items.Add(selectAllItem);

        var clearSelectionItem = new MenuItem
        {
            Header = "Clear Selection",
            IsEnabled = _selectionController.HasSelection
        };
        clearSelectionItem.Click += (s, args) => actions.ClearSelection();
        menu.Items.Add(clearSelectionItem);

        menu.Items.Add(new Separator());

        var newTabItem = new MenuItem
        {
            Header = "New Tab",
            InputGesture = new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift)
        };
        newTabItem.Click += (s, args) => actions.NewTab();
        menu.Items.Add(newTabItem);

        return menu;
    }
}
