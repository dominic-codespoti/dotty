using System;

namespace Dotty.Terminal.Adapter
{
    // Simple DebugLog implementation that writes to files when environment
    // variables are set. This is intentionally lightweight and robust: all
    // operations swallow exceptions to avoid interfering with production code.
    public static class DebugLog
    {
        private static bool EnvSet(string name)
        {
            try { return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(name)); } catch { return false; }
        }

        public static bool VerboseEnabled => EnvSet("DOTTY_DEBUG_VERBOSE");
        public static bool InputLogEnabled => EnvSet("DOTTY_DEBUG_INPUT_LOG");

        public static void MarkRender()
        {
            try
            {
                if (EnvSet("DOTTY_DEBUG_RENDER_LOG"))
                {
                    FileAppend("/tmp/dotty_render_trace.log", $"[MARK_RENDER] {DateTime.UtcNow:o}\n");
                }
            }
            catch { }
        }

        public static void Log(string s)
        {
            try
            {
                if (VerboseEnabled) FileAppend("/tmp/dotty_verbose.log", s + "\n");
            }
            catch { }
        }

        public static void FileAppend(string path, string s)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(path, s);
            }
            catch { }
        }

        public static void FileWrite(string path, string s)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, s);
            }
            catch { }
        }

        public static void LogInput(string s)
        {
            try
            {
                if (InputLogEnabled) FileAppend("/tmp/dotty_input.log", s + "\n");
            }
            catch { }
        }
    }
}
