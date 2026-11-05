using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    internal const int MaxTitleStackDepth = 10;

    private TitleFrame[]? _titleFrames;
    private int _titleFrameCount;
    private int _nextTitleFrame;

    private struct TitleFrame
    {
        public string? Icon;
        public string? Window;
    }

    /// <summary>
    /// Saves selected current title values. Selector 0 saves both, 1 the icon
    /// title, and 2 the window title. Slot 0 (or less) pushes onto the bounded
    /// stack; positive slots address a paired ring frame directly, modulo its depth.
    /// </summary>
    public void OnPushTitle(int selector = 0, int slot = 0)
    {
        _titleFrames ??= new TitleFrame[MaxTitleStackDepth];
        int index;
        if (slot <= 0)
        {
            index = _nextTitleFrame;
            _nextTitleFrame = (_nextTitleFrame + 1) % MaxTitleStackDepth;
            _titleFrameCount++;
        }
        else
        {
            index = (slot - 1) % MaxTitleStackDepth;
        }

        ref TitleFrame frame = ref _titleFrames[index];
        frame = default;
        if (selector is 0 or 1)
            frame.Icon = _iconTitle ?? string.Empty;
        if (selector is 0 or 2)
            frame.Window = _windowTitle ?? string.Empty;
    }

    /// <summary>
    /// Restores selected values from the newest frame and consumes that whole
    /// paired frame. Missing fields inherit from older ring entries. A positive
    /// slot restores that frame without consuming it.
    /// </summary>
    public void OnPopTitle(int selector = 0, int slot = 0)
    {
        int index;
        bool consume;
        if (slot <= 0)
        {
            if (_titleFrameCount == 0 || _titleFrames is null)
                return;
            index = (_nextTitleFrame + MaxTitleStackDepth - 1) % MaxTitleStackDepth;
            _nextTitleFrame = index;
            _titleFrameCount--;
            consume = true;
        }
        else
        {
            index = (slot - 1) % MaxTitleStackDepth;
            if (_titleFrames is null)
                return;
            consume = false;
        }

        if (selector is 0 or 1 && TryGetSavedIcon(index, out string? icon))
            RestoreIconTitle(icon);
        if (selector is 0 or 2 && TryGetSavedWindow(index, out string? window))
            RestoreWindowTitle(window);

        if (consume)
            _titleFrames![index] = default;
    }

    private bool TryGetSavedIcon(int index, out string? value)
    {
        TitleFrame[] frames = _titleFrames!;
        for (int offset = 0; offset < MaxTitleStackDepth; offset++)
        {
            int candidate = (index - offset + MaxTitleStackDepth) % MaxTitleStackDepth;
            if (frames[candidate].Icon is not null)
            {
                value = frames[candidate].Icon;
                return true;
            }
        }
        value = null;
        return false;
    }

    private bool TryGetSavedWindow(int index, out string? value)
    {
        TitleFrame[] frames = _titleFrames!;
        for (int offset = 0; offset < MaxTitleStackDepth; offset++)
        {
            int candidate = (index - offset + MaxTitleStackDepth) % MaxTitleStackDepth;
            if (frames[candidate].Window is not null)
            {
                value = frames[candidate].Window;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void RestoreIconTitle(string? title)
    {
        if (string.Equals(_iconTitle, title, StringComparison.Ordinal))
            return;
        _iconTitle = title;
    }

    private void RestoreWindowTitle(string? title)
    {
        if (string.Equals(_windowTitle, title, StringComparison.Ordinal))
            return;
        _windowTitle = title;
        TitleChanged?.Invoke(title ?? string.Empty);
        RequestRender();
    }
}
