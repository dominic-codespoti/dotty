using System;
using Dotty.Abstractions.Config;

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
    private readonly ScrollbackPolicy _policy;
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


    public ScreenManager(int rows, int columns)
        : this(rows, columns, ScrollbackPolicy.Default)
    {
    }

    /// <summary>
    /// The ring capacity is <c>policy.CapacityFor(rows, columns)</c> at construction and again after every resize: under
    /// a byte cap, wider screens hold fewer history lines.
    /// </summary>
    public ScreenManager(int rows, int columns, ScrollbackPolicy policy)
    {
        _policy = policy;
        _main = new Screen(rows, columns, policy.CapacityFor(rows, columns));
    }

    /// <summary>The policy that sizes every ring this manager creates (see <see cref="ScrollbackPolicy.CapacityFor"/>).</summary>
    internal ScrollbackPolicy Policy => _policy;

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
                _alt = new Screen(_savedMain.Rows, _savedMain.Columns, _savedMain.ScrollbackCapacity);
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
        // Every ring of the new size shares one capacity (the alternate screen keeps the main screen's).
        int capacity = _policy.CapacityFor(rows, columns);
        bool mainIsSaved = _savedMain != null && ReferenceEquals(_main, _savedMain);

        var oldMain = _main;
        _main = ReflowScreen(
            oldMain,
            rows,
            columns,
            capacity,
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
                capacity,
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
        int capacity,
        Screen.SourceLayout layout,
        int scrollbackRows,
        bool includeScrollback,
        ReflowMapping mapping)
    {
        Screen destination = TakeSpareScreen(rows, columns, capacity);
        Screen resized = source.ReflowWithOptions(
            rows,
            columns,
            layout,
            _reflowWorkspace,
            mapping,
            scrollbackRows,
            includeScrollback,
            capacity,
            destination);
        // The spare keeps its virtual reservation (so the next resize allocates nothing) but holds no pages. It is
        // parked with the size and capacity just served, whose reservation the next resize back and forth fits.
        source.ResetForReuse(rows, columns, capacity);
        _spareScreens.Add(source);
        return resized;
    }


    // Any parked spare will do: ReflowWithOptions resets it to the requested size and capacity (a spare's own
    // capacity is whatever it last served with), so a wrong-capacity ring is never used as is.
    private Screen TakeSpareScreen(int rows, int columns, int capacity)
    {
        if (_spareScreens.Count > 0)
        {
            int index = _spareScreens.Count - 1;
            var screen = _spareScreens[index];
            _spareScreens.RemoveAt(index);
            return screen;
        }

        return new Screen(rows, columns, capacity);
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
