using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dotty.Runtime.Config;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ShellIntegrationLauncherTests
{
    private const string Dir = "/opt/dotty/shell-integration";

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values) map[key] = value;
        return key => map.TryGetValue(key, out string? v) ? v : null;
    }

    private static ShellLaunch Prepare(
        string? shell,
        string? mode = "detect",
        IReadOnlyList<string>? userCommand = null,
        string? dir = Dir,
        Func<string, string?>? getenv = null,
        bool isWindows = false,
        bool shellIsExecutable = false,
        Func<string, bool>? fileExists = null) =>
        ShellIntegrationLauncher.Prepare(shell, shellIsExecutable, userCommand, mode, dir,
            getenv ?? Env(), isWindows, fileExists ?? (_ => false));

    [Theory]
    [InlineData("/bin/bash", ShellKind.Bash)]
    [InlineData("bash", ShellKind.Bash)]
    [InlineData("/usr/local/bin/ZSH", ShellKind.Zsh)]
    [InlineData("/usr/bin/fish", ShellKind.Fish)]
    [InlineData("pwsh", ShellKind.PowerShell)]
    [InlineData("pwsh.exe", ShellKind.PowerShell)]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellKind.PowerShell)]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.EXE", ShellKind.PowerShell)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Bash)]
    [InlineData("/bin/sh", ShellKind.Unknown)]
    [InlineData("dash", ShellKind.Unknown)]
    [InlineData("/usr/bin/nu", ShellKind.Unknown)]
    [InlineData("cmd.exe", ShellKind.Unknown)]
    [InlineData("bash-5", ShellKind.Unknown)]
    [InlineData("", ShellKind.Unknown)]
    [InlineData(null, ShellKind.Unknown)]
    public void Classify_UsesFileNameStem(string? shell, ShellKind expected) =>
        Assert.Equal(expected, ShellIntegrationLauncher.Classify(shell));

    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("bash")]
    public void Bash_UsesRcfileWrapper(string shell)
    {
        ShellLaunch launch = Prepare(shell);

        Assert.Equal(
            new[] { shell, "--rcfile", Path.Combine(Dir, "inject", "bash", "dotty-rcfile.bash"), "-i" },
            launch.Command);
        Assert.Equal("1", launch.Environment["DOTTY_SHELL_INTEGRATION"]);
        Assert.Equal(Dir, launch.Environment["DOTTY_SHELL_INTEGRATION_DIR"]);
    }

    [Fact]
    public void Zsh_SetsZdotdirAndKeepsUserZdotdirOnlyWhenSet()
    {
        ShellLaunch plain = Prepare("/bin/zsh");
        Assert.Null(plain.Command);
        Assert.Equal(Path.Combine(Dir, "inject", "zsh"), plain.Environment["ZDOTDIR"]);
        Assert.False(plain.Environment.ContainsKey("DOTTY_ZSH_ZDOTDIR"));

        ShellLaunch custom = Prepare("/bin/zsh", getenv: Env(("ZDOTDIR", "/home/u/zsh")));
        Assert.Equal("/home/u/zsh", custom.Environment["DOTTY_ZSH_ZDOTDIR"]);

        ShellLaunch empty = Prepare("/bin/zsh", getenv: Env(("ZDOTDIR", "")));
        Assert.False(empty.Environment.ContainsKey("DOTTY_ZSH_ZDOTDIR"));
    }

    [Fact]
    public void Fish_PrependsXdgDataDirsWithFallbackDefault()
    {
        string inject = Path.Combine(Dir, "inject", "xdg");

        ShellLaunch fallback = Prepare("/usr/bin/fish");
        Assert.Null(fallback.Command);
        Assert.Equal(inject + ":/usr/local/share:/usr/share", fallback.Environment["XDG_DATA_DIRS"]);

        ShellLaunch existing = Prepare("fish", getenv: Env(("XDG_DATA_DIRS", "/a:/b")));
        Assert.Equal(inject + ":/a:/b", existing.Environment["XDG_DATA_DIRS"]);
    }

    [Fact]
    public void Fish_WithColonInDirectoryIsLeftUntouched() =>
        Assert.True(Prepare("fish", dir: "/odd:dir/shell-integration").IsNone);

    [Fact]
    public void PowerShell_UsesGuardedInvokeExpressionAndDoublesSingleQuotes()
    {
        ShellLaunch launch = Prepare("pwsh", dir: "/opt/O'Brien/shell-integration", isWindows: false);

        Assert.NotNull(launch.Command);
        Assert.Equal(new[] { "pwsh", "-NoLogo", "-NoExit", "-Command" }, System.Linq.Enumerable.Take(launch.Command!, 4));
        string script = Path.Combine("/opt/O'Brien/shell-integration", "Dotty.ps1").Replace("'", "''");
        Assert.Equal(
            "try { Invoke-Expression ([System.IO.File]::ReadAllText('" + script + "')) } catch { }",
            launch.Command[4]);
        Assert.Contains("O''Brien", launch.Command[4]);
    }

    [Fact]
    public void PowerShell_DoublesTypographicSingleQuotesToo()
    {
        ShellLaunch launch = Prepare("pwsh", dir: "/opt/Jo\u2019s \u2018dir\u2019/shell-integration");

        Assert.NotNull(launch.Command);
        Assert.Contains("Jo\u2019\u2019s \u2018\u2018dir\u2019\u2019", launch.Command![4]);
    }

    [Fact]
    public void WindowsPathWithSpaces_IsAcceptedWhenItIsAnExistingFile()
    {
        const string exe = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        ShellLaunch launch = Prepare(exe, isWindows: true, shellIsExecutable: true, fileExists: p => p == exe);

        Assert.NotNull(launch.Command);
        Assert.Equal(exe, launch.Command![0]);

        ShellLaunch viaExists = Prepare(exe, isWindows: true, fileExists: p => p == exe);
        Assert.NotNull(viaExists.Command);
    }

    [Fact]
    public void ZshAndFish_AreNotInjectedOnWindows()
    {
        Assert.True(Prepare("C:\\msys\\zsh.exe", isWindows: true).IsNone);
        Assert.True(Prepare("fish.exe", isWindows: true).IsNone);
    }

    [Fact]
    public void ExplicitUserCommand_IsNeverRewritten() =>
        Assert.True(Prepare("/bin/bash", userCommand: new[] { "/bin/bash", "-c", "true" }).IsNone);

    [Theory]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("detec")]
    [InlineData("")]
    [InlineData(null)]
    public void NonDetectModes_DisableInjection(string? mode) =>
        Assert.True(Prepare("/bin/bash", mode: mode).IsNone);

    [Theory]
    [InlineData("detect")]
    [InlineData("DETECT")]
    [InlineData(" Detect ")]
    public void DetectMode_IsCaseAndWhitespaceInsensitive(string mode) =>
        Assert.False(Prepare("/bin/bash", mode: mode).IsNone);

    [Theory]
    [InlineData("0")]
    [InlineData("none")]
    [InlineData("false")]
    [InlineData("OFF")]
    public void ProcessEnvironmentOptOut_DisablesInjection(string value) =>
        Assert.True(Prepare("/bin/bash", getenv: Env(("DOTTY_SHELL_INTEGRATION", value))).IsNone);

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("dash")]
    [InlineData("nu")]
    [InlineData("cmd.exe")]
    [InlineData("/usr/bin/env bash")]
    [InlineData("bash --login")]
    [InlineData(null)]
    [InlineData("   ")]
    public void UnknownOrArgumentBearingShells_AreUntouched(string? shell) =>
        Assert.True(Prepare(shell).IsNone);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingIntegrationDirectory_IsUntouched(string? dir) =>
        Assert.True(Prepare("/bin/bash", dir: dir).IsNone);

    [Fact]
    public void ThrowingGetenv_NeverEscapes() =>
        Assert.True(Prepare("/bin/bash", getenv: _ => throw new InvalidOperationException()).IsNone);

    [Fact]
    public void Locator_PrefersOverrideThenBesideThenFhs()
    {
        static bool Exists(string p) => p.Replace('\\', '/') is
            "/o/dotty.bash" or "/app/bin/shell-integration/dotty.bash" or "/app/share/dotty/shell-integration/dotty.bash";

        Assert.Equal("/o", ShellIntegrationLocator.Resolve("/o", "/app/bin", Exists, out string? d1));
        Assert.Null(d1);
        Assert.Null(ShellIntegrationLocator.Resolve("/missing", "/app/bin", Exists, out string? d2));
        Assert.NotNull(d2);
        Assert.Equal(Path.Combine("/app/bin", "shell-integration"),
            ShellIntegrationLocator.Resolve(null, "/app/bin", Exists, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine("/app/bin", "..", "share", "dotty", "shell-integration")),
            ShellIntegrationLocator.Resolve(null, "/app/bin", p => p.Replace('\\', '/').EndsWith("/share/dotty/shell-integration/dotty.bash"),
                out _));
        Assert.Null(ShellIntegrationLocator.Resolve(null, "/nowhere", _ => false, out string? d3));
        Assert.NotNull(d3);
    }

    [Fact]
    public void Config_DefaultsToNoneAndRoundTripsJson()
    {
        Assert.Equal("none", new DottyUserConfig().ShellIntegration);

        DottyUserConfig loaded = JsonSerializer.Deserialize<DottyUserConfig>("{\"shellIntegration\":\"detect\"}")!;
        Assert.Equal("detect", loaded.ShellIntegration);
    }
}
