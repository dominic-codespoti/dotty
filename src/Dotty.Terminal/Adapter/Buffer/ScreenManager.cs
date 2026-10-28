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

    internal Screen.SourceLayout BuildMainLayout(Screen screen, int scrollbackRows) =>
        screen.BuildSourceLayout(scrollbackRows, _mainLayout);

    internal Screen.SourceLayout BuildAlternateLayout(Screen screen, int scrollbackRows) =>
        screen.BuildSourceLayout(scrollbackRows, _alternateLayout);


    public ScreenManager(int rows, int columns, int scrollbackCapacity = global::Dotty.Abstractions.Config.DottyDefaults.ScrollbackLines)
    {
        _scrollbackCapacity = scrollbackCapacity;
        _main = new Screen(rows, columns, scrollbackCapacity);
    }

    public Screen Active => _usingAlt ? _alt! : _main;

    internal Screen Main => _main;
    internal Screen? Alternate => _alt;
    /// <summary>Parked screens kept for the next resize (test/diagnostic hook).</summary>
    internal IReadOnlyList<Screen> SpareScreens => _spareScreens;

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

            // Hand the alternate screen's pages back right away (they are O(dirty), and zero afterwards) so a
            // full-screen app that scrolled does not keep its ring resident, and a later resize does not
            // reflow stale alternate content. Re-entry clears it again, which is then nearly free.
            _alt?.Clear();
        }
    }


    /// <summary>Where the anchors registered before <see cref="Reflow"/> land on the reflowed main screen.</summary>
    internal ReflowMapping MainMapping => _mainMapping;

    /// <summary>Where the anchors registered before <see cref="Reflow"/> land on the reflowed alternate screen.</summary>
    internal ReflowMapping AlternateMapping => _alternateMapping;

    /// <summary>
    /// Reflows the main screen (with its history) and the alternate screen, if one exists, to the new size. Callers
    /// register the anchors they need on <see cref="MainMapping"/> / <see cref="AlternateMapping"/> first and read
    /// them back by slot afterwards.
    /// </summary>
    internal void Reflow(
        int rows,
        int columns,
        Screen.SourceLayout mainLayout,
        int mainScrollbackRows,
        Screen.SourceLayout? alternateLayout,
        int alternateScrollbackRows)
    {
        bool mainIsSaved = _savedMain != null && ReferenceEquals(_main, _savedMain);

        var oldMain = _main;
        _main = ReflowScreen(
            oldMain,
            rows,
            columns,
            mainLayout,
            mainScrollbackRows,
            includeScrollback: true,
            _mainMapping);

        // _savedMain is only ever set together with _main (entering the alternate screen) and cleared when leaving
        // it, so a saved main screen is always the main screen itself.
        if (mainIsSaved)
            _savedMain = _main;

        if (_alt != null)
        {
            var oldAlt = _alt;
            _alt = ReflowScreen(
                oldAlt,
                rows,
                columns,
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
        Screen.SourceLayout layout,
        int scrollbackRows,
        bool includeScrollback,
        ReflowMapping mapping)
    {
        Screen destination = TakeSpareScreen(rows, columns);
        Screen resized = source.ReflowWithOptions(
            rows,
            columns,
            layout,
            _reflowWorkspace,
            mapping,
            scrollbackRows,
            includeScrollback,
            destination);
        // The spare keeps its virtual reservation (so the next resize allocates nothing) but holds no pages.
        source.ResetForReuse(rows, columns);
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
