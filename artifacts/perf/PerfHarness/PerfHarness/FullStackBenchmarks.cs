using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.Terminal.Parser;
using Dotty.Terminal.Adapter;

namespace PerfHarness
{
    [MemoryDiagnoser]
    public class FullStackBenchmarks
    {
        private BasicAnsiParser _parser;
        private TerminalAdapter _adapter;
        private byte[] _payloadHeavySgr;
        private byte[] _payloadScrollHeavy;
        private byte[] _payloadLongLines;
        private byte[] _payloadMassiveText;
        private byte[] _payloadComplexUnicode;
        private byte[] _payloadAltBufferTui;

        [GlobalSetup]
        public void Setup()
        {
            // Reset state
            _adapter = new TerminalAdapter(24, 80);
            _parser = new BasicAnsiParser();
            _parser.Handler = _adapter;

            var sbSgr = new StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sbSgr.Append($"\x1b[38;2;{i % 255};{i % 100};0m\x1b[48;2;10;10;10mTest\x1b[0m ");
            }
            _payloadHeavySgr = Encoding.UTF8.GetBytes(sbSgr.ToString());

            var sbScroll = new StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sbScroll.Append($"Line {i}\r\n");
            }
            _payloadScrollHeavy = Encoding.UTF8.GetBytes(sbScroll.ToString());

            var sbLong = new StringBuilder();
            for (int i = 0; i < 2000; i++)
            {
                for (int j = 0; j < 500; j++)
                {
                    sbLong.Append("A");
                }
                sbLong.Append("\r\n");
            }
            _payloadLongLines = Encoding.UTF8.GetBytes(sbLong.ToString());

            var sbMassiveText = new StringBuilder();
            for (int i = 0; i < 50000; i++)
            {
                sbMassiveText.Append("The quick brown fox jumps over the lazy dog. ");
            }
            _payloadMassiveText = Encoding.UTF8.GetBytes(sbMassiveText.ToString());

            var sbComplexUnicode = new StringBuilder();
            for (int i = 0; i < 10000; i++)
            {
                sbComplexUnicode.Append("👨‍👩‍👧‍👦안녕하세요こんにちは"); // ZWJs + CJK
            }
            _payloadComplexUnicode = Encoding.UTF8.GetBytes(sbComplexUnicode.ToString());

            var sbAltBuffer = new StringBuilder();
            for (int i = 0; i < 5000; i++)
            {
                sbAltBuffer.Append("\x1b[?1049h"); // Enable Alt Buffer
                sbAltBuffer.Append("\x1b[2J\x1b[1;1H"); // Clear and move to home
                sbAltBuffer.Append("TUI Render Frame...");
                sbAltBuffer.Append("\x1b[?1049l"); // Disable Alt Buffer
            }
            _payloadAltBufferTui = Encoding.UTF8.GetBytes(sbAltBuffer.ToString());
        }

        [IterationSetup]
        public void IterationSetup()
        {
            // We want to clear buffer back so we don't just accumulate infinite scrollback in some benchmarks if it affects perf over time
            _adapter.OnClearScrollback();
            _adapter.OnEraseDisplay(2);
            _adapter.OnMoveCursor(1, 1);
        }

        [Benchmark]
        public void ParseHeavySgr()
        {
            _parser.Feed(_payloadHeavySgr);
        }

        [Benchmark]
        public void ParseScrollHeavy()
        {
            _parser.Feed(_payloadScrollHeavy);
        }

        [Benchmark]
        public void ParseLongLinesAutoWrap()
        {
            _parser.Feed(_payloadLongLines);
        }

        [Benchmark]
        public void ParseMassiveText()
        {
            _parser.Feed(_payloadMassiveText);
        }

        [Benchmark]
        public void ParseComplexUnicode()
        {
            _parser.Feed(_payloadComplexUnicode);
        }

        [Benchmark]
        public void ParseAltBufferTui()
        {
            _parser.Feed(_payloadAltBufferTui);
        }
    }
}
