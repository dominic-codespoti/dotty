using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    private readonly record struct PromptAnchor(PromptMark Mark, ReflowCursorAnchor Anchor);

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
        var activeAnchor = oldActiveScreen.GetReflowAnchor(
            cursorState.Row,
            cursorState.Column,
            cursorState.WrapPending,
            oldActiveScrollback);

        int oldMainScrollback = _isAlternate
            ? Math.Min(_savedTotalScrolled, oldMainScreen.ScrollbackCapacity)
            : oldActiveScrollback;
        var mainAnchor = _isAlternate
            ? oldMainScreen.GetReflowAnchor(
                _alternateSavedCursorState.Row,
                _alternateSavedCursorState.Column,
                _alternateSavedCursorState.WrapPending,
                oldMainScrollback)
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
                oldActiveScrollback);
        }

        var promptAnchors = CapturePromptAnchors(
            oldActiveScreen,
            oldActiveScrollback,
            oldRows,
            oldTotalScrolled);


        ReflowMapping? mainMapping = null;
        ReflowMapping? alternateMapping = null;
        _screens.Reflow(rows, columns, (screen, isAlternate) =>
        {
            if (isAlternate)
            {
                var resized = screen.ReflowWithOptions(
                    rows,
                    columns,
                    alternateAnchor,
                    out alternateMapping,
                    scrollbackRows: 0,
                    includeScrollback: false);
                return resized;
            }

            var resizedMain = screen.ReflowWithOptions(
                rows,
                columns,
                mainAnchor,
                out mainMapping,
                scrollbackRows: oldMainScrollback,
                includeScrollback: true);
            return resizedMain;
        });

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
        Array.Resize(ref _rowGenerations, rows);
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

    private List<PromptAnchor> CapturePromptAnchors(
        Screen screen,
        int scrollbackRows,
        int rows,
        int totalScrolled)
    {
        var anchors = new List<PromptAnchor>(_promptMarks.Count);
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
                scrollbackRows);
            anchors.Add(new PromptAnchor(mark, anchor));
        }
        return anchors;
    }

    private void RebasePromptMarks(
        IReadOnlyList<PromptAnchor> anchors,
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

        Array.Resize(ref _tabStops, columns);
        for (int column = oldColumns; column < columns; column++)
        {
            if (column % 8 == 0)
                _tabStops[column] = true;
        }
    }
}
