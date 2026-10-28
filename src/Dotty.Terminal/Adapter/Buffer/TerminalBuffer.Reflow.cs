using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    private readonly record struct PromptAnchor(PromptMark Mark, int Slot);
    private readonly List<PromptAnchor> _promptAnchorScratch = new();

    private void ResizeWithReflow(int requestedRows, int requestedColumns)
    {
        int rows = Math.Max(1, requestedRows);
        int columns = Math.Max(1, requestedColumns);
        int oldRows = Rows;
        int oldColumns = Columns;
        int oldScrollTop = _scrollTop;
        int oldScrollBottom = _scrollBottom;
        int oldTotalScrolled = _totalScrolled;
        bool fullScreenScroll = oldScrollTop == 0 && oldScrollBottom == oldRows - 1;

        var oldActiveScreen = _screens.Active;
        var oldMainScreen = _screens.Main;
        var cursorState = _cursor.CaptureState();
        int oldActiveScrollback = _isAlternate ? 0 : ScrollbackCount;
        int oldMainScrollback = _isAlternate
            ? Math.Min(_savedTotalScrolled, oldMainScreen.ScrollbackCapacity)
            : oldActiveScrollback;
        var mainLayout = _screens.BuildMainLayout(oldMainScreen, oldMainScrollback);
        var alternateScreen = _screens.Alternate;
        var alternateLayout = alternateScreen is null
            ? null
            : _screens.BuildAlternateLayout(alternateScreen, scrollbackRows: 0);
        var activeLayout = ReferenceEquals(oldActiveScreen, oldMainScreen)
            ? mainLayout
            : alternateLayout!;

        // The reflow resolves exactly the anchors registered here (the cursor, the saved cursors and the prompt
        // marks) and keeps no per-cell position table; each anchor is read back by its slot afterwards.
        var mainMapping = _screens.MainMapping;
        var alternateMapping = _screens.AlternateMapping;
        var activeMapping = _isAlternate ? alternateMapping : mainMapping;
        int markAnchors = _promptMarks.Count;
        mainMapping.BeginAnchors(_isAlternate ? 1 : 2 + markAnchors);
        alternateMapping.BeginAnchors(_isAlternate ? 2 + markAnchors : 0);

        int cursorSlot = activeMapping.AddAnchor(oldActiveScreen.GetReflowAnchor(
            cursorState.Row,
            cursorState.Column,
            cursorState.WrapPending,
            oldActiveScrollback,
            activeLayout));

        int savedSlot = -1;
        if (_hasSavedCursor)
        {
            savedSlot = activeMapping.AddAnchor(oldActiveScreen.GetReflowAnchor(
                _savedCursorState.Row,
                _savedCursorState.Column,
                _savedCursorState.WrapPending,
                oldActiveScrollback,
                activeLayout));
        }

        // While the alternate screen is active the cursor saved on entry belongs to the hidden main screen.
        int alternateSavedSlot = -1;
        if (_isAlternate && _hasAlternateSavedCursor)
        {
            alternateSavedSlot = mainMapping.AddAnchor(oldMainScreen.GetReflowAnchor(
                _alternateSavedCursorState.Row,
                _alternateSavedCursorState.Column,
                _alternateSavedCursorState.WrapPending,
                oldMainScrollback,
                mainLayout));
        }

        var promptAnchors = CapturePromptAnchors(
            oldActiveScreen,
            activeLayout,
            activeMapping,
            oldActiveScrollback,
            oldRows,
            oldTotalScrolled);

        _screens.Reflow(
            rows,
            columns,
            mainLayout,
            oldMainScrollback,
            alternateLayout,
            alternateScrollbackRows: 0);

        Rows = rows;
        Columns = columns;

        var mappedCursor = MapCursorState(cursorState, activeMapping, cursorSlot, rows, columns);
        _cursor.RestoreState(mappedCursor, rows, columns);

        if (_hasSavedCursor)
        {
            _savedCursorState = MapCursorState(
                _savedCursorState,
                activeMapping,
                savedSlot,
                rows,
                columns);
        }

        if (alternateSavedSlot >= 0)
        {
            _alternateSavedCursorState = MapCursorState(
                _alternateSavedCursorState,
                mainMapping,
                alternateSavedSlot,
                rows,
                columns);
        }

        int newMainScrollback = mainMapping.NewScrollbackRows;
        if (_isAlternate)
        {
            _savedTotalScrolled = newMainScrollback;
            _totalScrolled = 0;
        }
        else
        {
            _totalScrolled = newMainScrollback;
        }

        RebasePromptMarks(promptAnchors, activeMapping, rows);

        if (fullScreenScroll)
        {
            _scrollTop = 0;
            _scrollBottom = rows - 1;
        }
        else
        {
            _scrollTop = Math.Clamp(oldScrollTop, 0, rows - 1);
            _scrollBottom = Math.Clamp(oldScrollBottom, _scrollTop, rows - 1);
        }
        if (_originMode)
        {
            var state = _cursor.CaptureState();
            _cursor.RestoreState(
                state with { Row = Math.Clamp(state.Row, _scrollTop, _scrollBottom) },
                rows,
                columns);
        }

        ResizeTabStops(columns, oldColumns);
        ResizeRowGenerations(rows, oldRows);
        _writer.ResetRowDirtyCoalescing();
        unchecked { _globalGeneration++; }
        MarkAllRowsDirty();

        // Absolute rows were re-based above (_totalScrolled, prompt marks). History lives on the main screen, so its
        // mapping says whether the oldest rows were dropped to fit the capacity.
        RaiseHistoryInvalidated(mainMapping.RetainedStart > 0
            ? HistoryDiscardReason.CapacityShrunk
            : HistoryDiscardReason.Rebased);
    }

    private static CursorState MapCursorState(
        CursorState original,
        ReflowMapping mapping,
        int slot,
        int rows,
        int columns)
    {
        if (mapping.TryMap(slot, out var position) && !position.InScrollback)
        {
            return original with
            {
                Row = Math.Clamp(position.Row, 0, rows - 1),
                Column = Math.Clamp(position.Column, 0, columns - 1),
                WrapPending = position.WrapPending,
            };
        }

        return original with
        {
            Row = Math.Clamp(original.Row, 0, rows - 1),
            Column = Math.Clamp(original.Column, 0, columns - 1),
            WrapPending = false,
        };
    }

    private void ResizeRowGenerations(int rows, int oldRows)
    {
        if (rows < oldRows)
            Array.Clear(_rowGenerations, rows, oldRows - rows);
        if (rows > _rowGenerations.Length)
            Array.Resize(ref _rowGenerations, rows);
    }

    private List<PromptAnchor> CapturePromptAnchors(
        Screen screen,
        Screen.SourceLayout layout,
        ReflowMapping mapping,
        int scrollbackRows,
        int rows,
        int totalScrolled)
    {
        var anchors = _promptAnchorScratch;
        anchors.Clear();
        foreach (var mark in _promptMarks)
        {
            int chronologicalIndex = mark.AbsoluteRow - totalScrolled + scrollbackRows;
            if (chronologicalIndex < 0 || chronologicalIndex >= scrollbackRows + rows)
                continue;

            int sourceRow = chronologicalIndex - scrollbackRows;
            var anchor = screen.GetReflowAnchor(
                sourceRow,
                mark.AbsoluteColumn,
                wrapPending: false,
                scrollbackRows,
                layout);
            anchors.Add(new PromptAnchor(mark, mapping.AddAnchor(anchor)));
        }
        return anchors;
    }

    private void RebasePromptMarks(
        List<PromptAnchor> anchors,
        ReflowMapping mapping,
        int newRows)
    {
        _promptMarks.Clear();
        foreach (var entry in anchors)
        {
            if (!mapping.TryMap(entry.Slot, out var position))
                continue;

            int retainedIndex = position.OutputIndex - mapping.RetainedStart;
            if (retainedIndex >= 0
                && retainedIndex < mapping.NewScrollbackRows + newRows)
            {
                _promptMarks.Add(new PromptMark(retainedIndex, entry.Mark.Kind, position.Column));
            }
        }
    }

    private void ResizeTabStops(int columns, int oldColumns)
    {
        if (_tabStops == null)
        {
            InitializeTabStops();
            return;
        }

        if (columns < oldColumns)
            Array.Clear(_tabStops, columns, oldColumns - columns);
        if (columns > _tabStops.Length)
            Array.Resize(ref _tabStops, columns);
        for (int column = oldColumns; column < columns; column++)
        {
            if (column % 8 == 0)
                _tabStops[column] = true;
        }
    }
}
