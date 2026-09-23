using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    private readonly record struct PromptAnchor(PromptMark Mark, ReflowCursorAnchor Anchor);
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

        var activeAnchor = oldActiveScreen.GetReflowAnchor(
            cursorState.Row,
            cursorState.Column,
            cursorState.WrapPending,
            oldActiveScrollback,
            activeLayout);

        var mainAnchor = _isAlternate
            ? oldMainScreen.GetReflowAnchor(
                _alternateSavedCursorState.Row,
                _alternateSavedCursorState.Column,
                _alternateSavedCursorState.WrapPending,
                oldMainScrollback,
                mainLayout)
            : activeAnchor;

        var alternateAnchor = _isAlternate
            ? activeAnchor
            : new ReflowCursorAnchor(0, 0);
        ReflowCursorAnchor savedAnchor = default;
        if (_hasSavedCursor)
        {
            savedAnchor = oldActiveScreen.GetReflowAnchor(
                _savedCursorState.Row,
                _savedCursorState.Column,
                _savedCursorState.WrapPending,
                oldActiveScrollback,
                activeLayout);
        }

        var promptAnchors = CapturePromptAnchors(
            oldActiveScreen,
            activeLayout,
            oldActiveScrollback,
            oldRows,
            oldTotalScrolled);

        _screens.Reflow(
            rows,
            columns,
            mainAnchor,
            mainLayout,
            oldMainScrollback,
            alternateAnchor,
            alternateLayout,
            alternateScrollbackRows: 0,
            out ReflowMapping? mainMapping,
            out ReflowMapping? alternateMapping);

        Rows = rows;
        Columns = columns;

        var activeMapping = _isAlternate ? alternateMapping : mainMapping;
        if (activeMapping is null)
            throw new InvalidOperationException("Resize reflow did not produce an active mapping.");

        var mappedCursor = MapCursorState(cursorState, activeAnchor, activeMapping, rows, columns);
        _cursor.RestoreState(mappedCursor, rows, columns);

        if (_hasSavedCursor)
        {
            _savedCursorState = MapCursorState(
                _savedCursorState,
                savedAnchor,
                activeMapping,
                rows,
                columns);
        }

        if (_isAlternate && _hasAlternateSavedCursor && mainMapping is not null)
        {
            _alternateSavedCursorState = MapCursorState(
                _alternateSavedCursorState,
                mainAnchor,
                mainMapping,
                rows,
                columns);
        }

        int newMainScrollback = mainMapping?.NewScrollbackRows ?? 0;
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
    }

    private static CursorState MapCursorState(
        CursorState original,
        ReflowCursorAnchor anchor,
        ReflowMapping mapping,
        int rows,
        int columns)
    {
        if (mapping.TryMap(anchor, out var position) && !position.InScrollback)
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
                0,
                wrapPending: false,
                scrollbackRows,
                layout);
            anchors.Add(new PromptAnchor(mark, anchor));
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
            if (!mapping.TryMap(entry.Anchor, out var position))
                continue;

            int retainedIndex = position.OutputIndex - mapping.RetainedStart;
            if (retainedIndex >= 0
                && retainedIndex < mapping.NewScrollbackRows + newRows)
            {
                _promptMarks.Add(new PromptMark(retainedIndex, entry.Mark.Kind));
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
