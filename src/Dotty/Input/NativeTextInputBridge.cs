using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.GLFW;

namespace Dotty.Silk;

public readonly record struct ImePreeditState(string Text, int CaretIndex, int SelectionStart, int SelectionLength)
{
    public static readonly ImePreeditState Empty = new(string.Empty, 0, 0, 0);
    public bool IsActive => !string.IsNullOrEmpty(Text);


    /// <summary>Copies callback-scoped UTF-32 and block data into an immutable UTF-16 snapshot.</summary>
    internal static unsafe bool TryCreate(uint* scalars, int scalarCount, int* blockSizes, int blockCount, int focusedBlock, out ImePreeditState state)
    {
        state = Empty;
        if (scalarCount < 0 || blockCount < 0 || (scalarCount != 0 && scalars == null) || (blockCount != 0 && blockSizes == null))
        {
            state = Empty;
            return false;
        }
        return TryCreate(new ReadOnlySpan<uint>(scalars, scalarCount), new ReadOnlySpan<int>(blockSizes, blockCount), focusedBlock, out state);
    }

    internal static bool TryCreate(ReadOnlySpan<uint> scalars, ReadOnlySpan<int> blockSizes, int focusedBlock, out ImePreeditState state)
    {
        state = Empty;
        int scalarCount = scalars.Length;
        int blockCount = blockSizes.Length;

        if (blockCount == 0)
        {
            if (focusedBlock != -1 && focusedBlock != 0)
                return false;
        }
        else if ((uint)focusedBlock >= (uint)blockCount)
        {
            return false;
        }

        int total = 0;
        int selectionStartScalar = 0;
        int selectionLengthScalar = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int size = blockSizes[i];
            if (size < 0 || size > scalarCount - total)
                return false;
            if (i < focusedBlock)
                selectionStartScalar += size;
            if (i == focusedBlock)
                selectionLengthScalar = size;
            total += size;
        }


        int utf16Length = 0;
        for (int i = 0; i < scalarCount; i++)
        {
            uint scalar = scalars[i];
            if (scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
                return false;
            int scalarLength = scalar > 0xFFFF ? 2 : 1;
            if (utf16Length > int.MaxValue - scalarLength)
                return false;
            utf16Length += scalarLength;
        }

        string composition = CreateComposition(scalars, utf16Length);
        int selectionStartUtf16 = Utf16IndexAtScalar(composition, selectionStartScalar);
        int selectionEndUtf16 = Utf16IndexAtScalar(composition, selectionStartScalar + selectionLengthScalar);
        int caretUtf16 = blockCount == 0 ? composition.Length : selectionEndUtf16;
        state = new ImePreeditState(composition, caretUtf16, selectionStartUtf16, selectionEndUtf16 - selectionStartUtf16);
        return true;
    }

    internal static int Utf16IndexAtScalar(string text, int scalarIndex)
    {
        int utf16 = 0;
        for (int i = 0; i < scalarIndex; i++)
            utf16 += char.IsHighSurrogate(text[utf16]) ? 2 : 1;
        return utf16;
    }

    private static unsafe string CreateComposition(ReadOnlySpan<uint> scalars, int utf16Length)
    {
        if (utf16Length == 0)
            return string.Empty;

        fixed (uint* scalarPointer = scalars)
        {
            return string.Create(utf16Length, (pointer: (nint)scalarPointer, count: scalars.Length), static (destination, source) =>
            {
                uint* values = (uint*)source.pointer;
                int offset = 0;
                for (int i = 0; i < source.count; i++)
                    offset += new Rune((int)values[i]).EncodeToUtf16(destination[offset..]);
            });
        }
    }

}

/// <summary>Managed adapter for the preedit extensions in the already-loaded GLFW fork.</summary>
internal sealed unsafe class NativeTextInputBridge : IDisposable
{
    private static NativeTextInputBridge? _bound;
    private readonly nint _window;
    private readonly Action<ImePreeditState> _changed;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, void> _setPreeditCallback;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int, int, void> _setCursorRectangle;
    private readonly delegate* unmanaged[Cdecl]<nint, int, void> _setTextInputFocus;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _resetPreeditText;
    private readonly bool _isWayland;
    private ImePreeditState _preedit = ImePreeditState.Empty;
    private ExceptionDispatchInfo? _callbackError;
    private bool _focused;
    private bool _disposed;
    private bool _hasCursorRectangle;
    private int _cursorX, _cursorY, _cursorWidth, _cursorHeight;

    internal NativeTextInputBridge(Glfw api, nint window, Action<ImePreeditState> changed)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(changed);
        if (window == 0)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (_bound is not null)
            throw new InvalidOperationException("Only one GLFW text-input bridge may be bound to the native callback.");

        _setPreeditCallback = (delegate* unmanaged[Cdecl]<nint, nint, void>)Resolve(api, "glfwSetPreeditCallback");
        _setCursorRectangle = (delegate* unmanaged[Cdecl]<nint, int, int, int, int, void>)Resolve(api, "glfwSetPreeditCursorRectangle");
        _setTextInputFocus = (delegate* unmanaged[Cdecl]<nint, int, void>)Resolve(api, "glfwSetTextInputFocus");
        _resetPreeditText = (delegate* unmanaged[Cdecl]<nint, void>)Resolve(api, "glfwResetPreeditText");
        var getPlatform = (delegate* unmanaged[Cdecl]<int>)Resolve(api, "glfwGetPlatform");
        _isWayland = getPlatform() == 0x00060003;
        _window = window;
        _changed = changed;
        _bound = this;
        _setPreeditCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, int, uint*, int, int*, int, int, void>)&OnNativePreedit);
    }

    internal bool IsComposing
    {
        get
        {
            ThrowPendingCallbackError();
            return _preedit.IsActive;
        }
    }

    internal ImePreeditState Preedit
    {
        get
        {
            ThrowPendingCallbackError();
            return _preedit;
        }
    }

    internal void SetFocus(bool focused)
    {
        ThrowIfDisposed();
        if (_focused == focused)
            return;
        if (!focused)
        {
            ResetPreeditIfSupported();
            _setTextInputFocus(_window, 0);
            ClearPreedit();
        }
        else
        {
            _setTextInputFocus(_window, 1);
        }
        _focused = focused;
    }

    internal void SetCursorRectangle(int x, int y, int width, int height)
    {
        ThrowIfDisposed();
        if (width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(width < 0 ? nameof(width) : nameof(height));
        if (_hasCursorRectangle && _cursorX == x && _cursorY == y && _cursorWidth == width && _cursorHeight == height)
            return;
        _cursorX = x;
        _cursorY = y;
        _cursorWidth = width;
        _cursorHeight = height;
        _hasCursorRectangle = true;
        _setCursorRectangle(_window, x, y, width, height);
    }

    internal void Cancel()
    {
        ThrowIfDisposed();
        ResetPreeditIfSupported();
        if (_focused)
        {
            _setTextInputFocus(_window, 0);
            _setTextInputFocus(_window, 1);
        }
        ClearPreedit();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _setPreeditCallback(_window, 0);
        if (_focused)
            _setTextInputFocus(_window, 0);
        _focused = false;
        ClearPreedit();
        _bound = null;
        _disposed = true;
    }

    private static nint Resolve(Glfw api, string name)
    {
        if (!api.Context.TryGetProcAddress(name, out nint address) || address == 0)
            throw new PlatformNotSupportedException($"The loaded GLFW library does not export required text-input function {name}.");
        return address;
    }

    private void ResetPreeditIfSupported()
    {
        if (!_isWayland)
            _resetPreeditText(_window);
    }

    internal void ThrowPendingCallbackError()
    {
        ExceptionDispatchInfo? error = Interlocked.Exchange(ref _callbackError, null);
        error?.Throw();
    }

    private static void OnPreedit(nint window, int scalarCount, uint* scalars, int blockCount, int* blockSizes, int focusedBlock, int caret)
    {
        NativeTextInputBridge? bridge = _bound;
        if (bridge is null || bridge._window != window || bridge._disposed)
            return;

        try
        {
            if (caret < -1 || (scalarCount >= 0 && caret > scalarCount))
                throw new InvalidDataException("GLFW supplied a preedit caret outside its UTF-32 text range.");
            if (!ImePreeditState.TryCreate(scalars, scalarCount, blockSizes, blockCount, focusedBlock, out ImePreeditState state))
                throw new InvalidDataException("GLFW supplied malformed preedit text or block data.");
            // Fork callbacks use UTF-32 code-point indices for caret offsets.
            if (caret >= 0)
                state = state with { CaretIndex = ImePreeditState.Utf16IndexAtScalar(state.Text, caret) };
            if (bridge._preedit == state)
                return;
            bridge._preedit = state;
            bridge._changed(state);
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref bridge._callbackError, ExceptionDispatchInfo.Capture(error), null);
        }
    }


    private void ClearPreedit()
    {
        if (_preedit == ImePreeditState.Empty)
            return;
        _preedit = ImePreeditState.Empty;
        _changed(_preedit);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowPendingCallbackError();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativePreedit(nint window, int preeditCount, uint* preeditString, int blockCount, int* blockSizes, int focusedBlock, int caret)
        => OnPreedit(window, preeditCount, preeditString, blockCount, blockSizes, focusedBlock, caret);
}
