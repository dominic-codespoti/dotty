using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Predicts, from the state BEFORE a resize and the frozen <see cref="LegacyDenseReflowMapping"/> oracle, everything that
/// <c>TerminalBuffer.ResizeWithReflow</c> derives from its position mapping: the active cursor, the saved cursor, the
/// alternate-screen saved cursor, the rebased prompt marks and the new scrollback counters. The consumer logic
/// (<c>MapCursorState</c>, <c>CapturePromptAnchors</c>, <c>RebasePromptMarks</c> and the counter updates in
/// <c>TerminalBuffer.Reflow.cs</c>) is reproduced here on purpose: the prediction path must not call any production
/// mapper, so a rewrite of the mapper (anchor-only resolution, streaming emit, deferred hidden-main reflow) is compared
/// against the dense behavior it replaced. Frozen test code, like <see cref="LegacyDenseReflowMapping"/>.
///
/// The only production calls are the ones whose behavior the rewrite does not change by contract:
/// <c>Screen.BuildSourceLayout</c> and <c>Screen.GetReflowAnchor</c>. If the rewrite changes either signature, adapt the
/// two call sites in <see cref="Predict"/>.
/// </summary>
internal sealed class LegacyReflowPrediction
{
    private readonly int _rows;
    private readonly int _columns;
    private readonly bool _alternateActive;
    private readonly CursorState _cursor;
    private readonly CursorState? _savedCursor;
    private readonly CursorState? _alternateSavedCursor;
    private readonly int _totalScrolled;
    private readonly int _savedTotalScrolled;
    private readonly int _scrollbackCount;
    private readonly List<(int Row, PromptKind Kind, int Column)> _promptMarks;

    private LegacyReflowPrediction(
        int rows,
        int columns,
        bool alternateActive,
        CursorState cursor,
        CursorState? savedCursor,
        CursorState? alternateSavedCursor,
        int totalScrolled,
        int savedTotalScrolled,
        int scrollbackCount,
        List<(int Row, PromptKind Kind, int Column)> promptMarks)
    {
        _rows = rows;
        _columns = columns;
        _alternateActive = alternateActive;
        _cursor = cursor;
        _savedCursor = savedCursor;
        _alternateSavedCursor = alternateSavedCursor;
        _totalScrolled = totalScrolled;
        _savedTotalScrolled = savedTotalScrolled;
        _scrollbackCount = scrollbackCount;
        _promptMarks = promptMarks;
    }

    private static T Required<T>(TerminalBuffer buffer, string name) =>
        ReflowCanonicalDump.Field(buffer, name) is T value
            ? value
            : throw new InvalidOperationException($"TerminalBuffer field '{name}' is missing or has an unexpected type.");

    internal static LegacyReflowPrediction Predict(TerminalBuffer buffer, int newRows, int newColumns)
    {
        int rows = Math.Max(1, newRows);
        int columns = Math.Max(1, newColumns);
        bool alternateActive = buffer.IsAlternateScreenActive;
        int oldRows = buffer.Rows;
        int oldTotalScrolled = Required<int>(buffer, "_totalScrolled");
        int oldSavedTotalScrolled = Required<int>(buffer, "_savedTotalScrolled");
        var manager = Required<ScreenManager>(buffer, "_screens");
        var cursorState = Required<CursorController>(buffer, "_cursor").CaptureState();

        Screen main = manager.Main;
        int mainScrollback = alternateActive
            ? Math.Min(oldSavedTotalScrolled, main.ScrollbackCapacity)
            : buffer.ScrollbackCount;
        var mainLayout = main.BuildSourceLayout(mainScrollback, new Screen.SourceLayout());
        var mainReplay = LegacyDenseReflowReplay.Replay(
            mainLayout, main.Columns, main.ScrollbackCapacity, rows, columns, mainScrollback, includeScrollback: true);

        Screen active = buffer.ActiveBuffer;
        int activeScrollback = alternateActive ? 0 : mainScrollback;
        var activeLayout = mainLayout;
        var activeReplay = mainReplay;
        if (alternateActive)
        {
            activeLayout = active.BuildSourceLayout(0, new Screen.SourceLayout());
            activeReplay = LegacyDenseReflowReplay.Replay(
                activeLayout, active.Columns, active.ScrollbackCapacity, rows, columns, 0, includeScrollback: false);
        }

        var activeAnchor = active.GetReflowAnchor(
            cursorState.Row, cursorState.Column, cursorState.WrapPending, activeScrollback, activeLayout);
        var mappedCursor = MapCursorState(cursorState, activeAnchor, activeReplay, rows, columns);

        CursorState? mappedSaved = null;
        if (Required<bool>(buffer, "_hasSavedCursor"))
        {
            var saved = Required<CursorState>(buffer, "_savedCursorState");
            var savedAnchor = active.GetReflowAnchor(
                saved.Row, saved.Column, saved.WrapPending, activeScrollback, activeLayout);
            mappedSaved = MapCursorState(saved, savedAnchor, activeReplay, rows, columns);
        }

        CursorState? mappedAlternateSaved = null;
        if (alternateActive && Required<bool>(buffer, "_hasAlternateSavedCursor"))
        {
            var alternateSaved = Required<CursorState>(buffer, "_alternateSavedCursorState");
            var mainAnchor = main.GetReflowAnchor(
                alternateSaved.Row, alternateSaved.Column, alternateSaved.WrapPending, mainScrollback, mainLayout);
            mappedAlternateSaved = MapCursorState(alternateSaved, mainAnchor, mainReplay, rows, columns);
        }

        var promptMarks = new List<(int Row, PromptKind Kind, int Column)>();
        foreach (var mark in buffer.GetPromptMarks())
        {
            int chronologicalIndex = mark.AbsoluteRow - oldTotalScrolled + activeScrollback;
            if (chronologicalIndex < 0 || chronologicalIndex >= activeScrollback + oldRows)
                continue;

            var anchor = active.GetReflowAnchor(
                chronologicalIndex - activeScrollback, mark.AbsoluteColumn, false, activeScrollback, activeLayout);
            if (!activeReplay.TryMap(anchor, out var position))
                continue;

            int retainedIndex = position.OutputIndex - activeReplay.RetainedStart;
            if (retainedIndex >= 0 && retainedIndex < activeReplay.NewScrollbackRows + rows)
                promptMarks.Add((retainedIndex, mark.Kind, position.Column));
        }

        int newMainScrollback = mainReplay.NewScrollbackRows;
        int totalScrolled = alternateActive ? 0 : newMainScrollback;
        int savedTotalScrolled = alternateActive ? newMainScrollback : oldSavedTotalScrolled;
        int scrollbackCount = Math.Min(totalScrolled, active.ScrollbackCapacity);
        return new LegacyReflowPrediction(
            rows, columns, alternateActive, mappedCursor, mappedSaved, mappedAlternateSaved,
            totalScrolled, savedTotalScrolled, scrollbackCount, promptMarks);
    }

    // Reproduction of TerminalBuffer.MapCursorState against the legacy mapper.
    private static CursorState MapCursorState(
        CursorState original,
        ReflowCursorAnchor anchor,
        LegacyDenseReflowMapping mapping,
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

    /// <summary>Asserts that <paramref name="buffer"/>, after the resize this prediction was made for, matches it.</summary>
    internal void AssertMatches(TerminalBuffer buffer, string context)
    {
        Assert.True(buffer.Rows == _rows && buffer.Columns == _columns,
            $"{context}: size {buffer.Rows}x{buffer.Columns}, expected {_rows}x{_columns}");
        Assert.True(buffer.IsAlternateScreenActive == _alternateActive, $"{context}: alternate-screen flag changed");

        var cursor = Required<CursorController>(buffer, "_cursor").CaptureState();
        Assert.True(cursor == _cursor, $"{context}: active cursor {cursor}, expected {_cursor}");

        if (_savedCursor is { } savedExpected)
        {
            var saved = Required<CursorState>(buffer, "_savedCursorState");
            Assert.True(saved == savedExpected, $"{context}: saved cursor {saved}, expected {savedExpected}");
        }

        if (_alternateSavedCursor is { } alternateExpected)
        {
            var alternateSaved = Required<CursorState>(buffer, "_alternateSavedCursorState");
            Assert.True(alternateSaved == alternateExpected,
                $"{context}: alternate-saved cursor {alternateSaved}, expected {alternateExpected}");
        }

        int totalScrolled = Required<int>(buffer, "_totalScrolled");
        int savedTotalScrolled = Required<int>(buffer, "_savedTotalScrolled");
        Assert.True(totalScrolled == _totalScrolled, $"{context}: _totalScrolled {totalScrolled}, expected {_totalScrolled}");
        Assert.True(savedTotalScrolled == _savedTotalScrolled,
            $"{context}: _savedTotalScrolled {savedTotalScrolled}, expected {_savedTotalScrolled}");
        Assert.True(buffer.ScrollbackCount == _scrollbackCount,
            $"{context}: ScrollbackCount {buffer.ScrollbackCount}, expected {_scrollbackCount}");

        var marks = buffer.GetPromptMarks();
        string actualMarks = string.Join(";", marks.Select(m => $"{m.AbsoluteRow}/{m.Kind}/{m.AbsoluteColumn}"));
        string expectedMarks = string.Join(";", _promptMarks.Select(m => $"{m.Row}/{m.Kind}/{m.Column}"));
        Assert.True(actualMarks == expectedMarks, $"{context}: prompt marks [{actualMarks}], expected [{expectedMarks}]");
    }
}
