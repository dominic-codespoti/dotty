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

    public void Resize(int rows, int columns)
    {
        // When alternate screen is active, _main and _savedMain reference the SAME Screen
        // object.  Resize it once and keep both pointers in sync; resizing via _savedMain
        // after already disposing the old object via _main would be a use-after-free.
        bool mainIsSaved = _savedMain != null && ReferenceEquals(_main, _savedMain);

        var oldMain = _main;
        _main = _main.Resize(rows, columns);
        if (!ReferenceEquals(oldMain, _main))
            oldMain.Dispose();

        if (mainIsSaved)
        {
            // Synchronise _savedMain to the newly resized screen without a second resize.
            _savedMain = _main;
        }
        else if (_savedMain != null)
        {
            var oldSaved = _savedMain;
            _savedMain = _savedMain.Resize(rows, columns);
            if (!ReferenceEquals(oldSaved, _savedMain))
                oldSaved.Dispose();
        }

        if (_alt != null)
        {
            var oldAlt = _alt;
            _alt = _alt.Resize(rows, columns);
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
