using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Manages main/alternate screens and optional saved main when alternate is active.
/// </summary>
internal sealed class ScreenManager : IDisposable
{
    private Screen _main;
    private Screen? _alt;
    private Screen? _savedMain;
    private bool _usingAlt;
    private readonly int _scrollbackCapacity;
    private readonly List<Screen> _spareScreens = new();
    private readonly Screen.ReflowWorkspace _reflowWorkspace = new();
    private readonly Screen.SourceLayout _mainLayout = new();
    private readonly Screen.SourceLayout _alternateLayout = new();
    private readonly ReflowMapping _mainMapping = new();
    private readonly ReflowMapping _alternateMapping = new();
    private readonly ReflowMapping _scratchMapping = new();

    internal Screen.SourceLayout BuildMainLayout(Screen screen, int scrollbackRows) =>
        screen.BuildSourceLayout(scrollbackRows, _mainLayout);

    internal Screen.SourceLayout BuildAlternateLayout(Screen screen, int scrollbackRows) =>
        screen.BuildSourceLayout(scrollbackRows, _alternateLayout);


    public ScreenManager(int rows, int columns, int scrollbackCapacity = 10000)
    {
        _scrollbackCapacity = scrollbackCapacity;
        _main = new Screen(rows, columns, scrollbackCapacity);
    }

    public Screen Active => _usingAlt ? _alt! : _main;

    internal Screen Main => _main;
    internal Screen? Alternate => _alt;

    public void ClearAll()
    {
        _main.Clear();
        _alt?.Clear();
    }

    public void SetAlternate(bool enable)
    {
        if (enable == _usingAlt)
        {
            return;
        }

        if (enable)
        {
            _savedMain = _main;
            _usingAlt = true;
            if (_alt is null)
            {
                _alt = new Screen(_savedMain.Rows, _savedMain.Columns, _scrollbackCapacity);
            }
            _alt.Clear();
        }
        else
        {
            if (_savedMain != null)
            {
                _main = _savedMain;
                _savedMain = null;
            }
            _usingAlt = false;
        }
    }


    internal void Reflow(
        int rows,
        int columns,
        ReflowCursorAnchor mainAnchor,
        Screen.SourceLayout mainLayout,
        int mainScrollbackRows,
        ReflowCursorAnchor alternateAnchor,
        Screen.SourceLayout? alternateLayout,
        int alternateScrollbackRows,
        out ReflowMapping? mainMapping,
        out ReflowMapping? alternateMapping)
    {
        mainMapping = _mainMapping;
        alternateMapping = null;
        bool mainIsSaved = _savedMain != null && ReferenceEquals(_main, _savedMain);

        var oldMain = _main;
        _main = ReflowScreen(
            oldMain,
            rows,
            columns,
            mainAnchor,
            mainLayout,
            mainScrollbackRows,
            includeScrollback: true,
            _mainMapping);

        if (mainIsSaved)
        {
            _savedMain = _main;
        }
        else if (_savedMain != null)
        {
            var oldSaved = _savedMain;
            _savedMain = ReflowScreen(
                oldSaved,
                rows,
                columns,
                mainAnchor,
                mainLayout,
                mainScrollbackRows,
                includeScrollback: true,
                _scratchMapping);
        }

        if (_alt != null)
        {
            var oldAlt = _alt;
            alternateMapping = _alternateMapping;
            _alt = ReflowScreen(
                oldAlt,
                rows,
                columns,
                alternateAnchor,
                alternateLayout!,
                alternateScrollbackRows,
                includeScrollback: false,
                _alternateMapping);
        }
    }

    private Screen ReflowScreen(
        Screen source,
        int rows,
        int columns,
        ReflowCursorAnchor anchor,
        Screen.SourceLayout layout,
        int scrollbackRows,
        bool includeScrollback,
        ReflowMapping mapping)
    {
        Screen destination = TakeSpareScreen(rows, columns);
        Screen resized = source.ReflowWithOptions(
            rows,
            columns,
            anchor,
            layout,
            _reflowWorkspace,
            mapping,
            scrollbackRows,
            includeScrollback,
            destination);
        resized.TrimCapacity();
        source.ResetForReuseAndTrim(rows, columns);
        _spareScreens.Add(source);
        return resized;
    }


    private Screen TakeSpareScreen(int rows, int columns)
    {
        if (_spareScreens.Count > 0)
        {
            int index = _spareScreens.Count - 1;
            var screen = _spareScreens[index];
            _spareScreens.RemoveAt(index);
            return screen;
        }

        var created = new Screen(rows, columns, _scrollbackCapacity);
        return created;
    }

    public void Dispose()
    {
        _main.Dispose();
        _alt?.Dispose();
        _savedMain?.Dispose();
        foreach (var screen in _spareScreens)
            screen.Dispose();
        _spareScreens.Clear();
    }
}
