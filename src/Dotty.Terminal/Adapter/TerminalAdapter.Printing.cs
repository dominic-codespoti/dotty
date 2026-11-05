using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Dotty.Terminal.Adapter;

public enum PrinterStatus
{
    Unavailable = 0,
    Ready = 1,
    NotReady = 2,
}

public partial class TerminalAdapter
{
    private readonly object _printerSync = new();
    private FilePrinterSink? _printerSink;
    private string? _printerPath;
    private int _printerStatus = (int)PrinterStatus.Unavailable;
    private Task _retiredPrinterSinks = Task.CompletedTask;

    public bool PrintFormFeedEnabled { get; private set; }
    public bool PrintExtentFullPage { get; private set; }

    public PrinterStatus PrinterStatus
    {
        get
        {
            lock (_printerSync)
            {
                if (_printerSink is FilePrinterSink sink)
                    Volatile.Write(ref _printerStatus, (int)(sink.Failed
                        ? PrinterStatus.NotReady
                        : sink.CanAccept ? PrinterStatus.Ready : PrinterStatus.NotReady));
                return (PrinterStatus)Volatile.Read(ref _printerStatus);
            }
        }
    }

    /// <summary>Installs the trusted host-configured output file. A null or empty path disables printing.</summary>
    public void ConfigurePrinterOutput(string? path)
    {
        string? normalizedPath = null;
        bool disabled = string.IsNullOrWhiteSpace(path);
        if (!disabled)
        {
            try { normalizedPath = Path.GetFullPath(path!); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                SetPrinterConfigurationError();
                return;
            }
        }

        lock (_printerSync)
        {
            if (_printerPath == normalizedPath
                && (_printerSink is FilePrinterSink current ? !current.Failed
                    : disabled && Volatile.Read(ref _printerStatus) == (int)PrinterStatus.Unavailable))
                return;
        }

        FilePrinterSink? replacement = null;
        if (!disabled)
        {
            try { replacement = new FilePrinterSink(normalizedPath!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                SetPrinterConfigurationError();
                return;
            }
            PrinterOutputQueue.EnsureStarted();
        }

        lock (_printerSync)
        {
            if (_printerPath == normalizedPath
                && (_printerSink is FilePrinterSink current ? !current.Failed
                    : disabled && Volatile.Read(ref _printerStatus) == (int)PrinterStatus.Unavailable))
            {
                replacement?.Retire();
                return;
            }

            FilePrinterSink? previous = _printerSink;
            _printerSink = replacement;
            _printerPath = normalizedPath;
            Volatile.Write(ref _printerStatus, replacement is null
                ? (int)PrinterStatus.Unavailable
                : (int)PrinterStatus.Ready);
            if (previous is not null)
                RetirePrinterSink(previous);
        }
    }

    /// <summary>Drains and closes configured and superseded printer sinks. Safe to call repeatedly.</summary>
    public void DisposePrinterOutput()
    {
        Task retired;
        lock (_printerSync)
        {
            FilePrinterSink? sink = _printerSink;
            _printerSink = null;
            _printerPath = null;
            Volatile.Write(ref _printerStatus, (int)PrinterStatus.Unavailable);
            if (sink is not null)
                RetirePrinterSink(sink);
            retired = _retiredPrinterSinks;
            _retiredPrinterSinks = Task.CompletedTask;
        }
        retired.GetAwaiter().GetResult();
    }

    private void RetirePrinterSink(FilePrinterSink sink) =>
        _retiredPrinterSinks = Task.WhenAll(_retiredPrinterSinks, sink.Retire());

    private void SetPrinterConfigurationError()
    {
        lock (_printerSync)
        {
            FilePrinterSink? previous = _printerSink;
            _printerSink = null;
            _printerPath = null;
            Volatile.Write(ref _printerStatus, (int)PrinterStatus.NotReady);
            if (previous is not null)
                RetirePrinterSink(previous);
        }
    }

    public void OnSetPrinterMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 18: PrintFormFeedEnabled = enabled; break;
            case 19: PrintExtentFullPage = enabled; break;
        }
    }

    /// <summary>Queues a snapshot of the selected screen extent without blocking the terminal/render thread.</summary>
    public void OnPrintScreen()
    {
        lock (_printerSync)
        {
            if (_printerSink is null)
                return;
            if (_printerSink.Failed)
            {
                Volatile.Write(ref _printerStatus, (int)PrinterStatus.NotReady);
                return;
            }
        }

        var text = new StringBuilder();
        int first = PrintExtentFullPage ? 0 : _buffer.ScrollTop;
        int last = PrintExtentFullPage ? _buffer.Rows - 1 : _buffer.ScrollBottom;
        for (int row = first; row <= last; row++)
        {
            text.Append(RowText.Build(
                _buffer.GetRowCells(row),
                _buffer.GetRowColdCells(row),
                0,
                _buffer.Columns - 1,
                ContinuationPolicy.SkipCell).TrimEnd());
            text.Append('\n');
        }
        if (PrintFormFeedEnabled)
            text.Append('\f');
        byte[] payload = Encoding.UTF8.GetBytes(text.ToString());

        lock (_printerSync)
        {
            if (_printerSink is null)
                return;
            if (!_printerSink.TryQueue(payload))
                Volatile.Write(ref _printerStatus, (int)PrinterStatus.NotReady);
            else
                Volatile.Write(ref _printerStatus, (int)PrinterStatus.Ready);
        }
    }

    private sealed class FilePrinterSink
    {
        private readonly FileStream _stream;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _pending;
        private bool _retired;
        private bool _closed;
        private int _failed;

        public bool Failed => Volatile.Read(ref _failed) != 0;
        public bool CanAccept => !Failed && PrinterOutputQueue.CanAccept;

        public FilePrinterSink(string path)
        {
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public bool TryQueue(byte[] payload)
        {
            lock (_sync)
            {
                if (_retired || Failed)
                    return false;
                _pending++;
                if (PrinterOutputQueue.TryEnqueue(this, payload))
                    return true;
                _pending--;
                return false;
            }
        }

        public async Task WriteAsync(byte[] payload)
        {
            try
            {
                _stream.Seek(0, SeekOrigin.End);
                await _stream.WriteAsync(payload).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Volatile.Write(ref _failed, 1);
            }
            finally
            {
                CompleteJob();
            }
        }

        private void CompleteJob()
        {
            lock (_sync)
            {
                _pending--;
                CloseIfDrained();
            }
        }

        public Task Retire()
        {
            lock (_sync)
            {
                _retired = true;
                CloseIfDrained();
                return _drained.Task;
            }
        }

        private void CloseIfDrained()
        {
            if (!_retired || _pending != 0 || _closed)
                return;
            _closed = true;
            try { _stream.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Volatile.Write(ref _failed, 1);
            }
            _drained.TrySetResult();
        }
    }

    private sealed class PrinterOutputQueue
    {
        private const int Capacity = 4;
        private static readonly object StartSync = new();
        private static PrinterOutputQueue? _instance;
        private readonly Channel<PrintJob> _jobs = Channel.CreateBounded<PrintJob>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        private PrinterOutputQueue() => _ = Task.Run(WriteLoopAsync);

        public static void EnsureStarted()
        {
            if (_instance is not null)
                return;
            lock (StartSync)
                _instance ??= new PrinterOutputQueue();
        }

        public static bool CanAccept
        {
            get
            {
                PrinterOutputQueue? queue = _instance;
                return queue is not null && (!queue._jobs.Reader.CanCount || queue._jobs.Reader.Count < Capacity);
            }
        }

        public static bool TryEnqueue(FilePrinterSink sink, byte[] payload)
        {
            PrinterOutputQueue? queue = _instance;
            return queue is not null && queue._jobs.Writer.TryWrite(new PrintJob(sink, payload));
        }

        private async Task WriteLoopAsync()
        {
            await foreach (PrintJob job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
                await job.Sink.WriteAsync(job.Payload).ConfigureAwait(false);
        }

        private readonly record struct PrintJob(FilePrinterSink Sink, byte[] Payload);
    }
}
