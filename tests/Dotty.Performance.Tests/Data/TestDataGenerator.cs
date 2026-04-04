using System.Text;

namespace Dotty.Performance.Tests.Data;

/// <summary>
/// Generates realistic test data for terminal emulator benchmarks
/// </summary>
public static class TestDataGenerator
{
    private static readonly Random Random = new(42); // Seeded for reproducibility
    
    // Sample text content for realistic workloads
    private static readonly string[] CodeSamples = new[]
    {
        "using System;",
        "namespace Dotty.Terminal {",
        "    public class Program {",
        "        public static void Main(string[] args) {",
        "            Console.WriteLine(\"Hello, World!\");",
        "        }",
        "    }",
        "}",
    };

    private static readonly string[] LogSamples = new[]
    {
        "[INFO] 2024-01-15 10:23:45 Application started",
        "[DEBUG] 2024-01-15 10:23:46 Initializing components...",
        "[WARN]  2024-01-15 10:23:47 Deprecated API usage detected",
        "[ERROR] 2024-01-15 10:23:48 Failed to connect to database",
        "[INFO]  2024-01-15 10:23:49 Retrying connection (attempt 1/3)",
    };

    private static readonly string[] ShellPrompts = new[]
    {
        "$ ",
        "> ",
        "# ",
        "user@host:~$ ",
        "admin@server:/var/log# ",
    };

    private static readonly string[] FileListings = new[]
    {
        "drwxr-xr-x  5 user group  4096 Jan 15 10:00 .",
        "drwxr-xr-x 12 user group  4096 Jan 14 09:30 ..",
        "-rw-r--r--  1 user group  2341 Jan 15 08:20 Program.cs",
        "-rw-r--r--  1 user group  8901 Jan 14 16:45 README.md",
        "-rwxr-xr-x  1 user group  1567 Jan 15 09:10 build.sh",
    };

    // ANSI sequences for styling
    private static readonly string[] BasicColors = new[]
    {
        "\u001b[30m", // Black
        "\u001b[31m", // Red
        "\u001b[32m", // Green
        "\u001b[33m", // Yellow
        "\u001b[34m", // Blue
        "\u001b[35m", // Magenta
        "\u001b[36m", // Cyan
        "\u001b[37m", // White
    };

    private static readonly string[] BackgroundColors = new[]
    {
        "\u001b[40m",
        "\u001b[41m",
        "\u001b[42m",
        "\u001b[43m",
        "\u001b[44m",
        "\u001b[45m",
        "\u001b[46m",
        "\u001b[47m",
    };

    private static readonly string[] Attributes = new[]
    {
        "\u001b[1m",  // Bold
        "\u001b[2m",  // Dim
        "\u001b[3m",  // Italic
        "\u001b[4m",  // Underline
        "\u001b[5m",  // Blink
        "\u001b[7m",  // Reverse
        "\u001b[8m",  // Hidden
        "\u001b[9m",  // Strikethrough
    };

    /// <summary>
    /// Generate plain text content of specified size
    /// </summary>
    public static byte[] GeneratePlainText(int charCount)
    {
        var sb = new StringBuilder(charCount);
        int remaining = charCount;
        
        while (remaining > 0)
        {
            string line = CodeSamples[Random.Next(CodeSamples.Length)];
            if (line.Length > remaining)
            {
                line = line.Substring(0, remaining);
            }
            sb.Append(line);
            if (remaining > line.Length)
            {
                sb.Append('\n');
                remaining--;
            }
            remaining -= line.Length;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate text with basic ANSI color codes
    /// </summary>
    public static byte[] GenerateBasicAnsiText(int charCount, double ansiDensity = 0.1)
    {
        var sb = new StringBuilder(charCount * 2);
        int remaining = charCount;
        
        while (remaining > 0)
        {
            // Add ANSI code with probability based on density
            if (Random.NextDouble() < ansiDensity)
            {
                sb.Append(BasicColors[Random.Next(BasicColors.Length)]);
            }

            string word = GetRandomWord();
            if (word.Length > remaining)
            {
                word = word.Substring(0, remaining);
            }
            
            sb.Append(word);
            sb.Append(' ');
            remaining -= word.Length + 1;

            // Occasional newline
            if (Random.Next(10) == 0 && remaining > 10)
            {
                sb.Append('\n');
                // Add reset occasionally
                if (Random.Next(3) == 0)
                {
                    sb.Append("\u001b[0m");
                }
            }
        }

        sb.Append("\u001b[0m"); // Reset at end
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate text with 256-color ANSI codes (CSI sequences)
    /// </summary>
    public static byte[] GenerateExtendedAnsiText(int charCount)
    {
        var sb = new StringBuilder(charCount * 2);
        int remaining = charCount;
        
        while (remaining > 0)
        {
            // Add 256-color foreground
            if (Random.NextDouble() < 0.1)
            {
                int color = Random.Next(256);
                sb.Append($"\u001b[38;5;{color}m");
            }

            // Add 256-color background
            if (Random.NextDouble() < 0.05)
            {
                int color = Random.Next(256);
                sb.Append($"\u001b[48;5;{color}m");
            }

            string word = GetRandomWord();
            if (word.Length > remaining)
            {
                word = word.Substring(0, remaining);
            }
            
            sb.Append(word);
            sb.Append(' ');
            remaining -= word.Length + 1;

            if (Random.Next(15) == 0 && remaining > 10)
            {
                sb.Append('\n');
            }
        }

        sb.Append("\u001b[0m");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate text with TrueColor (24-bit) ANSI codes
    /// </summary>
    public static byte[] GenerateTrueColorAnsiText(int charCount)
    {
        var sb = new StringBuilder(charCount * 3);
        int remaining = charCount;
        
        while (remaining > 0)
        {
            // Add TrueColor foreground
            if (Random.NextDouble() < 0.08)
            {
                int r = Random.Next(256);
                int g = Random.Next(256);
                int b = Random.Next(256);
                sb.Append($"\u001b[38;2;{r};{g};{b}m");
            }

            string word = GetRandomWord();
            if (word.Length > remaining)
            {
                word = word.Substring(0, remaining);
            }
            
            sb.Append(word);
            sb.Append(' ');
            remaining -= word.Length + 1;
        }

        sb.Append("\u001b[0m");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate complex ANSI sequences including cursor movements, clears, etc.
    /// </summary>
    public static byte[] GenerateComplexAnsi(int charCount)
    {
        var sb = new StringBuilder(charCount * 2);
        int remaining = charCount;
        
        while (remaining > 0)
        {
            int choice = Random.Next(10);
            string sequence = choice switch
            {
                0 => "\u001b[H",              // Cursor home
                1 => "\u001b[2J",             // Clear screen
                2 => $"\u001b[{Random.Next(1, 25)};{Random.Next(1, 80)}H", // Cursor position
                3 => $"\u001b[{Random.Next(1, 10)}A", // Cursor up
                4 => $"\u001b[{Random.Next(1, 10)}B", // Cursor down
                5 => $"\u001b[{Random.Next(1, 10)}C", // Cursor forward
                6 => $"\u001b[{Random.Next(1, 10)}D", // Cursor back
                7 => "\u001b[K",              // Clear line
                8 => $"\u001b[{Random.Next(0, 8)}m",  // SGR attribute
                _ => GetRandomWord()
            };

            if (sequence.Length > remaining)
            {
                sequence = sequence.Substring(0, remaining);
            }

            sb.Append(sequence);
            remaining -= sequence.Length;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate realistic log output with ANSI colors
    /// </summary>
    public static byte[] GenerateLogOutput(int lineCount)
    {
        var sb = new StringBuilder(lineCount * 100);
        
        for (int i = 0; i < lineCount; i++)
        {
            var logLine = LogSamples[i % LogSamples.Length];
            
            // Add color based on log level
            string colorCode = logLine.Contains("[ERROR]") ? "\u001b[31m" :
                              logLine.Contains("[WARN]") ? "\u001b[33m" :
                              logLine.Contains("[DEBUG]") ? "\u001b[36m" :
                              "\u001b[32m";
            
            sb.Append(colorCode);
            sb.Append(logLine);
            sb.Append("\u001b[0m\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate realistic shell session output
    /// </summary>
    public static byte[] GenerateShellSession(int commandCount)
    {
        var sb = new StringBuilder(commandCount * 200);
        
        for (int i = 0; i < commandCount; i++)
        {
            // Prompt
            sb.Append(ShellPrompts[i % ShellPrompts.Length]);
            
            // Command (colored)
            sb.Append("\u001b[1m");
            sb.Append(GetRandomCommand());
            sb.Append("\u001b[0m\n");
            
            // Output
            int outputLines = Random.Next(1, 10);
            for (int j = 0; j < outputLines; j++)
            {
                if (Random.Next(2) == 0)
                {
                    sb.Append(FileListings[j % FileListings.Length]);
                }
                else
                {
                    sb.Append(GetRandomWord()).Append(' ').Append(GetRandomWord());
                }
                sb.Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate content simulating heavy scrolling workload
    /// </summary>
    public static byte[] GenerateScrollingWorkload(int lines)
    {
        var sb = new StringBuilder(lines * 80);
        
        for (int i = 0; i < lines; i++)
        {
            sb.Append($"[{i:D6}] ");
            sb.Append(string.Join(" ", Enumerable.Range(0, 10).Select(_ => GetRandomWord())));
            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate a buffer full screen redraw workload
    /// </summary>
    public static byte[] GenerateFullScreenRedraw(int rows, int cols)
    {
        var sb = new StringBuilder(rows * cols * 2);
        
        // Clear screen and home cursor
        sb.Append("\u001b[2J\u001b[H");
        
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                if (Random.NextDouble() < 0.1)
                {
                    // Add some color changes
                    sb.Append($"\u001b[{Random.Next(30, 38)}m");
                }
                
                char c = (char)('A' + Random.Next(26));
                sb.Append(c);
            }
            
            if (row < rows - 1)
            {
                sb.Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate progressive update workload (screen updates over time)
    /// </summary>
    public static List<byte[]> GenerateProgressiveUpdates(int updateCount, int charsPerUpdate)
    {
        var updates = new List<byte[]>();
        var sb = new StringBuilder();
        
        for (int i = 0; i < updateCount; i++)
        {
            sb.Clear();
            
            // Random cursor movement
            if (Random.Next(5) == 0)
            {
                sb.Append($"\u001b[{Random.Next(1, 25)};{Random.Next(1, 80)}H");
            }
            
            // Add some text
            for (int j = 0; j < charsPerUpdate; j++)
            {
                sb.Append(GetRandomWord());
                sb.Append(' ');
                
                if (Random.Next(20) == 0)
                {
                    sb.Append('\n');
                }
            }
            
            updates.Add(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        return updates;
    }

    /// <summary>
    /// Generate mouse event sequences
    /// </summary>
    public static byte[] GenerateMouseEvents(int eventCount)
    {
        var sb = new StringBuilder(eventCount * 20);
        
        for (int i = 0; i < eventCount; i++)
        {
            int button = Random.Next(4);
            int x = Random.Next(1, 200);
            int y = Random.Next(1, 100);
            int cb = button + 32;
            int cx = x + 32;
            int cy = y + 32;
            sb.Append($"\u001b[M{(char)cb}{(char)cx}{(char)cy}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate OSC (Operating System Command) sequences
    /// </summary>
    public static byte[] GenerateOscSequences(int count)
    {
        var sb = new StringBuilder(count * 100);
        
        for (int i = 0; i < count; i++)
        {
            int oscCode = Random.Next(4);
            string sequence = oscCode switch
            {
                0 => $"\u001b]0;Window Title {i}\u0007",      // Set window title
                1 => $"\u001b]2;Icon Name {i}\u0007",          // Set icon name
                2 => $"\u001b]8;;https://example.com/{i}\u0007", // Hyperlink
                _ => $"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes($"clipboard{i}"))}\u0007" // Clipboard
            };
            
            sb.Append(sequence);
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Predefined test data sizes for consistent benchmarking
    /// </summary>
    public static class Sizes
    {
        public const int Tiny = 100;           // 100 characters
        public const int Small = 1_000;      // 1 KB of text
        public const int Medium = 10_000;    // 10 KB of text
        public const int Large = 100_000;    // 100 KB of text
        public const int XLarge = 1_000_000; // 1 MB of text
    }

    // Private helper methods
    private static string GetRandomWord()
    {
        string[] words = new[]
        {
            "terminal", "emulator", "performance", "benchmark", 
            "rendering", "parsing", "ansi", "sequence", 
            "cursor", "scroll", "buffer", "memory",
            "throughput", "latency", "optimization", "dotnet",
            "async", "await", "span", "memory",
            "console", "output", "input", "command",
            "shell", "bash", "zsh", "fish"
        };
        
        return words[Random.Next(words.Length)];
    }

    private static string GetRandomCommand()
    {
        string[] commands = new[]
        {
            "ls -la", "cat file.txt", "grep -r pattern .",
            "git status", "docker ps", "kubectl get pods",
            "dotnet build", "npm install", "cargo build",
            "python script.py", "vim file.txt", "ssh server"
        };
        
        return commands[Random.Next(commands.Length)];
    }
}
