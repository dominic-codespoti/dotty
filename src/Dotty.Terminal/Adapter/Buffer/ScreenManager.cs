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
            _alt ??= new Screen(_savedMain.Rows, _savedMain.Columns, _scrollbackCapacity);
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

 
    internal void Reflow(int rows, int columns, Func<Screen, bool, Screen> transform)
    {
        bool mainIsSaved = _savedMain != null && ReferenceEquals(_main, _savedMain);

        var oldMain = _main;
        _main = transform(_main, false);
        if (!ReferenceEquals(oldMain, _main))
            oldMain.Dispose();

        if (mainIsSaved)
        {
            _savedMain = _main;
        }
        else if (_savedMain != null)
        {
            var oldSaved = _savedMain;
            _savedMain = transform(_savedMain, false);
            if (!ReferenceEquals(oldSaved, _savedMain))
                oldSaved.Dispose();
        }

        if (_alt != null)
        {
            var oldAlt = _alt;
            _alt = transform(_alt, true);
            if (!ReferenceEquals(oldAlt, _alt))
                oldAlt.Dispose();
        }
    }

    public void Dispose()
    {
        _main.Dispose();
        _alt?.Dispose();
    }
}
