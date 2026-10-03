using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    private const int KittyKeyboardStackCapacity = 16;
    private readonly int[] _kittyMainStack = new int[KittyKeyboardStackCapacity];
    private readonly int[] _kittyAlternateStack = new int[KittyKeyboardStackCapacity];
    private int _kittyMainStackDepth;
    private int _kittyAlternateStackDepth;
    private int _kittyMainFlags;
    private int _kittyAlternateFlags;
    private bool _kittyAlternateScreen;

    public int KittyKeyboardFlags => _kittyAlternateScreen ? _kittyAlternateFlags : _kittyMainFlags;
    public const int KittyKeyboardSupportedFlags = 0x1f;

    public void OnKittyKeyboardCommand(char introducer, int flags, int argument)
    {
        switch (introducer)
        {
            case '?':
                SendNumberReply("\x1b[?".AsSpan(), KittyKeyboardFlags, 'u');
                break;
            case '=':
                SetKittyFlags(flags, argument);
                break;
            case '>':
                PushKittyFlags(flags);
                break;
            case '<':
                PopKittyFlags(argument);
                break;
        }
    }

    private void SetKittyFlags(int flags, int mode)
    {
        int selected = flags & KittyKeyboardSupportedFlags;
        int current = KittyKeyboardFlags;
        int updated = mode switch
        {
            1 => selected,
            2 => current | selected,
            3 => current & ~selected,
            _ => current,
        };
        SetActiveKittyFlags(updated);
    }

    private void PushKittyFlags(int flags)
    {
        int[] stack = _kittyAlternateScreen ? _kittyAlternateStack : _kittyMainStack;
        ref int depth = ref (_kittyAlternateScreen ? ref _kittyAlternateStackDepth : ref _kittyMainStackDepth);
        if (depth == KittyKeyboardStackCapacity)
        {
            Array.Copy(stack, 1, stack, 0, KittyKeyboardStackCapacity - 1);
            depth--;
        }
        stack[depth++] = KittyKeyboardFlags;
        SetActiveKittyFlags(flags & KittyKeyboardSupportedFlags);
    }

    private void PopKittyFlags(int count)
    {
        if (count < 1) count = 1;
        int[] stack = _kittyAlternateScreen ? _kittyAlternateStack : _kittyMainStack;
        ref int depth = ref (_kittyAlternateScreen ? ref _kittyAlternateStackDepth : ref _kittyMainStackDepth);
        int restored = 0;
        while (count-- > 0 && depth > 0)
            restored = stack[--depth];
        SetActiveKittyFlags(restored);
    }

    private void SetActiveKittyFlags(int flags)
    {
        if (_kittyAlternateScreen) _kittyAlternateFlags = flags;
        else _kittyMainFlags = flags;
    }

    private void SetKittyAlternateScreen(bool enabled) => _kittyAlternateScreen = enabled;

    private void ResetKittyKeyboardState()
    {
        _kittyMainFlags = _kittyAlternateFlags = 0;
        _kittyMainStackDepth = _kittyAlternateStackDepth = 0;
        _kittyAlternateScreen = false;
    }
}
