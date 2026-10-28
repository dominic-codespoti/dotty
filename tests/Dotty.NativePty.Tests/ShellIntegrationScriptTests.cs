using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Hardening checks for the opt-in scripts in <c>shell-integration/</c>: idempotence guards,
/// byte-exact OSC 7 percent-encoding, and silent no-op behaviour. Bash is exercised for real
/// (Unix only, skipped when bash is absent); the other shells get content checks.
/// </summary>
public class ShellIntegrationScriptTests
{
    private static string ScriptDirectory
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "shell-integration");
                if (File.Exists(Path.Combine(candidate, "dotty.bash")))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("shell-integration directory not found above " + AppContext.BaseDirectory);
        }
    }

    private static string ReadScript(string name) => File.ReadAllText(Path.Combine(ScriptDirectory, name));

    private static string? FindBash() =>
        new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" }.FirstOrDefault(File.Exists);

    private static byte[] RunInteractiveBash(string bash, string scriptBody, string workingDirectory, string? promptCommand = null)
    {
        string file = Path.Combine(Path.GetTempPath(), "dotty-int-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(file, scriptBody);
        try
        {
            var psi = new ProcessStartInfo(bash)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            psi.ArgumentList.Add("--norc");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(file);
            psi.Environment["TERM"] = "xterm";
            if (promptCommand != null) psi.Environment["PROMPT_COMMAND"] = promptCommand;
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = new MemoryStream();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("bash did not exit; the script may recurse.");
            }
            copy.Wait(5000);
            return stdout.ToArray();
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static List<string> OscPayloads(byte[] output, string prefix)
    {
        string text = Encoding.Latin1.GetString(output);
        var payloads = new List<string>();
        int index = 0;
        while ((index = text.IndexOf("\u001b]" + prefix, index, StringComparison.Ordinal)) >= 0)
        {
            int start = index + 2;
            int end = text.IndexOf("\u001b\\", start, StringComparison.Ordinal);
            if (end < 0) break;
            payloads.Add(text[start..end]);
            index = end + 2;
        }
        return payloads;
    }

    private static string PercentEncode(string path)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '~' or '-'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_SourcingTwiceWithExportedPromptCommandDoesNotRecurse()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string script = ScriptDirectory + "/dotty.bash";
        string body = $"source '{script}'\nsource '{script}'\n_dotty_bash_prompt\necho DONE\n";

        byte[] output = RunInteractiveBash(bash, body, Path.GetTempPath(), promptCommand: "_dotty_bash_prompt");

        Encoding.Latin1.GetString(output).Should().Contain("DONE");
        OscPayloads(output, "133;A").Should().ContainSingle();
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_Osc7PayloadIsByteExactPercentEncoding()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string root = Path.Combine(Path.GetTempPath(), "dotty-osc7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] names =
            [
                "a\u001b]52;c;ZXZpbA==\u0007b",
                "100%25",
                "new\nline",
                "sp ace;semi",
                "\u00fcn\u00ef",
                "\U0001F600x",
                "%1b",
            ];
            foreach (string name in names)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                string script = ScriptDirectory + "/dotty.bash";
                string body = $"source '{script}'\nsource '{script}'\ncd -- \"$DOTTY_TEST_DIR\"\n_dotty_bash_file_uri\n";
                string file = Path.Combine(root, "run.sh");
                File.WriteAllText(file, body);

                var psi = new ProcessStartInfo(bash)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--norc");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(file);
                psi.Environment["TERM"] = "xterm";
                psi.Environment["DOTTY_TEST_DIR"] = dir;
                using var process = Process.Start(psi)!;
                process.StandardInput.Close();
                var stdout = new MemoryStream();
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                _ = process.StandardError.ReadToEndAsync();
                process.WaitForExit(15000).Should().BeTrue();
                copy.Wait(5000);

                List<string> payloads = OscPayloads(stdout.ToArray(), "7;");
                payloads.Should().NotBeEmpty();
                payloads[^1].Should().Be("7;file://" + PercentEncode(dir));
                payloads[^1].Should().MatchRegex("^7;file://[A-Za-z0-9/._~%-]*$");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scripts_GuardAgainstRepeatedSourcing()
    {
        ReadScript("dotty.bash").Should().Contain("_dotty_bash_installed");
        ReadScript("dotty.zsh").Should().Contain("_dotty_zsh_installed");
        ReadScript("dotty.fish").Should().Contain("__dotty_installed");
        ReadScript("Dotty.ps1").Should().Contain("DottyShellIntegrationInstalled");
    }

    [Fact]
    public void BashAndZsh_MaskPrintfByteToAvoidSignExtension()
    {
        ReadScript("dotty.bash").Should().Contain("code & 255");
        ReadScript("dotty.zsh").Should().Contain("code & 255");
    }

    [Fact]
    public void Fish_CapturesStatusBeforeAnyOtherCommand()
    {
        string fish = ReadScript("dotty.fish");
        int capture = fish.IndexOf("set -l result $status", StringComparison.Ordinal);
        int test = fish.IndexOf("test \"$__dotty_command_active\"", StringComparison.Ordinal);
        capture.Should().BeGreaterThan(0);
        test.Should().BeGreaterThan(capture);
        fish.Should().Contain("--on-event fish_prompt");
    }

    [Fact]
    public void PowerShell_UsesGuardsAndNoUriForOsc7()
    {
        string ps = ReadScript("Dotty.ps1");
        ps.Should().NotContain("[Uri]");
        ps.Should().Contain("FileSystem");
        ps.Should().Contain("[version]'2.2'");
        ps.Should().Contain("ConsoleHost");
        ps.Should().Contain("FullLanguage");
    }
}
