namespace Dotty.Terminal.Adapter;

/// <summary>
/// Manages main/alternate screens and optional saved main when alternate is active.
/// </summary>
internal sealed class ScreenManager
{
    private Screen _main;
    private Screen _alt;
    private Screen? _savedMain;
    private bool _usingAlt;

    public ScreenManager(int rows, int columns)
    {
        _main = new Screen(rows, columns);
        _alt = new Screen(rows, columns);
    }

    public Screen Active => _usingAlt ? _alt : _main;

    public void ClearAll()
    {
        _main.Clear();
        _alt.Clear();
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
        _main = _main.Resize(rows, columns);
        _alt = _alt.Resize(rows, columns);
        if (_savedMain != null)
        {
            _savedMain = _savedMain.Resize(rows, columns);
        }
    }
}
