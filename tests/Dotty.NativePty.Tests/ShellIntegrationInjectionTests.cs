using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Drives the injection assets under <c>shell-integration/inject</c> with the exact argv/environment
/// <c>ShellIntegrationLauncher</c> produces (hand-built here: this project does not reference
/// Dotty.Runtime). Bash runs for real on Unix when installed; zsh/fish/PowerShell only get content
/// checks because they cannot be exercised everywhere (needs-runtime-verification).
/// </summary>
public class ShellIntegrationInjectionTests
{
    private static string RepoRoot
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "shell-integration", "dotty.bash")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("repository root not found above " + AppContext.BaseDirectory);
        }
    }

    private static string IntegrationDirectory => Path.Combine(RepoRoot, "shell-integration");

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine([IntegrationDirectory, .. relative]));

    private static string? FindBash() =>
        new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" }.FirstOrDefault(File.Exists);

    private static string RunInjectedBash(string bash, string home, string workingDirectory, string stdin,
        bool inject = true, IDictionary<string, string>? extraEnvironment = null)
    {
        var psi = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        if (inject)
        {
            psi.ArgumentList.Add("--rcfile");
            psi.ArgumentList.Add(Path.Combine(IntegrationDirectory, "inject", "bash", "dotty-rcfile.bash"));
            psi.Environment["DOTTY_SHELL_INTEGRATION"] = "1";
            psi.Environment["DOTTY_SHELL_INTEGRATION_DIR"] = IntegrationDirectory;
        }
        psi.ArgumentList.Add("-i");
        psi.Environment["HOME"] = home;
        psi.Environment["TERM"] = "xterm";
        psi.Environment.Remove("PROMPT_COMMAND");
        if (extraEnvironment != null)
            foreach (var pair in extraEnvironment) psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi)!;
        var stdout = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        _ = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        if (!process.WaitForExit(20000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("injected bash did not exit");
        }
        copy.Wait(5000);
        return Encoding.Latin1.GetString(stdout.ToArray());
    }

    private static List<string> Payloads(string output, string prefix)
    {
        var payloads = new List<string>();
        int index = 0;
        while ((index = output.IndexOf("\u001b]" + prefix, index, StringComparison.Ordinal)) >= 0)
        {
            int start = index + 2;
            int end = output.IndexOf("\u001b\\", start, StringComparison.Ordinal);
            if (end < 0) break;
            payloads.Add(output[start..end]);
            index = end + 2;
        }
        return payloads;
    }

    private static string TempHome(string rc)
    {
        string home = Path.Combine(Path.GetTempPath(), "dotty-inject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        if (rc.Length > 0) File.WriteAllText(Path.Combine(home, ".bashrc"), rc);
        return home;
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_RcfileLoadsUserRcThenDottyScriptAndKeepsUserHooks()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string home = TempHome("FOO=bar\nPROMPT_COMMAND='HOOKRAN=1'\n");
        try
        {
            string output = RunInjectedBash(bash, home, home,
                "true\nfalse\necho \"R:$FOO:$HOOKRAN:$DOTTY_SHELL_INTEGRATION\"\nexit\n");

            output.Should().Contain("R:bar:1:1");
            Payloads(output, "133;A").Should().NotBeEmpty();
            Payloads(output, "133;D;0").Should().NotBeEmpty();
            Payloads(output, "133;D;1").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_RcfileWithoutUserBashrcStillStartsAndPrompts()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string home = TempHome("");
        try
        {
            string output = RunInjectedBash(bash, home, home, "echo STARTED\nexit\n");

            output.Should().Contain("STARTED");
            Payloads(output, "133;A").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_RcfileSurvivesFailingUserBashrc()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string home = TempHome("false\nnonexistent_command_dotty_test\nreturn 3\n");
        try
        {
            string output = RunInjectedBash(bash, home, home, "echo STARTED\nexit\n");

            output.Should().Contain("STARTED");
            Payloads(output, "133;A").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_InjectedOsc7IsByteExactForHostileDirectoryNames()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string home = TempHome("");
        try
        {
            string[] names = ["sp ace;semi", "\u00fcn\u00ef", "100%25", "%1b", "q'uote", "new\nline", "\U0001F600x"];
            foreach (string name in names)
            {
                string dir = Path.Combine(home, name);
                Directory.CreateDirectory(dir);
                string output = RunInjectedBash(bash, home, home, "cd -- \"$DOTTY_TEST_DIR\"\ntrue\nexit\n",
                    extraEnvironment: new Dictionary<string, string> { ["DOTTY_TEST_DIR"] = dir });

                string last = Payloads(output, "7;").Last();
                last.Should().MatchRegex("^7;file://[A-Za-z0-9/._~%-]*$");
                last.Should().Be("7;file://" + PercentEncode(dir));
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [ConditionalFacts.UnixOnlyFact]
    public void Bash_WithoutInjectionEmitsNoShellIntegrationMarks()
    {
        string? bash = FindBash();
        if (bash == null) return;
        string home = TempHome("");
        try
        {
            string output = RunInjectedBash(bash, home, home, "echo hi\nexit\n", inject: false);

            Payloads(output, "133;").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
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

    [Fact]
    public void BashRcfile_SourcesDistroRcUserRcThenDottyScriptInThatOrder()
    {
        string rc = Read("inject", "bash", "dotty-rcfile.bash");
        int etc = rc.IndexOf("/etc/bash.bashrc", StringComparison.Ordinal);
        int user = rc.IndexOf("$HOME/.bashrc", StringComparison.Ordinal);
        int dotty = rc.IndexOf("dotty.bash", StringComparison.Ordinal);
        etc.Should().BeGreaterThan(-1);
        user.Should().BeGreaterThan(etc);
        dotty.Should().BeGreaterThan(user);
        rc.Should().NotContain("eval");
    }

    // needs-runtime-verification: zsh is not installed in the authoring environment.
    [Fact]
    public void ZshShim_RestoresZdotdirSourcesUserZshenvAndDefersDottyScriptToFirstPrompt()
    {
        string zshenv = Read("inject", "zsh", ".zshenv");
        int restore = zshenv.IndexOf("DOTTY_ZSH_ZDOTDIR", StringComparison.Ordinal);
        int userEnv = zshenv.IndexOf("/.zshenv", StringComparison.Ordinal);
        int hook = zshenv.IndexOf("precmd_functions", StringComparison.Ordinal);
        restore.Should().BeGreaterThan(-1);
        userEnv.Should().BeGreaterThan(restore);
        hook.Should().BeGreaterThan(userEnv);
        zshenv.Should().Contain("unset ZDOTDIR");
        zshenv.Should().Contain("dotty.zsh");
        zshenv.Should().Contain("_dotty_zsh_precmd");
        zshenv.Should().NotContain("eval");
    }

    // needs-runtime-verification: fish is not installed in the authoring environment.
    [Fact]
    public void FishVendorConf_RemovesItsDirectoryFromXdgDataDirsAndSourcesScriptWhenInteractive()
    {
        string fish = Read("inject", "xdg", "fish", "vendor_conf.d", "dotty-shell-integration.fish");
        fish.Should().Contain("XDG_DATA_DIRS");
        fish.Should().Contain("inject/xdg");
        fish.Should().Contain("status is-interactive");
        fish.Should().Contain("dotty.fish");
        fish.Should().NotContain("eval");
    }

    [Fact]
    public void DottyCsproj_PackagesShellIntegrationRecursivelyIncludingDotfiles()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Dotty", "Dotty.csproj"));
        csproj.Should().Contain("shell-integration/**/*");
        csproj.Should().Contain("%(RecursiveDir)");
        csproj.Should().Contain("CopyToPublishDirectory");
        File.Exists(Path.Combine(IntegrationDirectory, "inject", "zsh", ".zshenv")).Should().BeTrue();
    }

    [Fact]
    public void ReleaseWorkflow_AssertsInjectionAssetsInArchives()
    {
        string release = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));
        release.Should().Contain("inject/zsh/.zshenv");
        release.Should().Contain("inject/bash/dotty-rcfile.bash");
        release.Should().Contain("Dotty.ps1");
    }
}
