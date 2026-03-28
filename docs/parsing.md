# Parsing Mechanism

Located in `src/Dotty.Terminal/Parser/BasicAnsiParser.cs` and related abstraction files:

* **State Machine Design**: The parser translates raw incoming PTY bytes directly into `ITerminalHandler` abstraction events (like moving a cursor, erasing a display, or changing text colors via SGR codes).
* **No-Allocation Buffers**: Byte streams are manipulated purely using `ReadOnlySpan<byte>` and pre-allocated arrays to eliminate runtime Garbage Collection (GC) overhead.
* **Fragmented Sequence Resilience**: It safely bounds incoming CSI/Escape sequence fragments across disparate socket read events by tracking unparsed `_leftover` buffers securely.
