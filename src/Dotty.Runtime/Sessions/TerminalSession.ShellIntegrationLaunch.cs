using System;
using System.Collections.Generic;
using Dotty.Runtime.Config;

namespace Dotty.Runtime.Sessions;

public partial class TerminalSession
{
    /// <summary>
    /// Computes the shell-integration rewrite for a launch. Reads the config at call time so a hot
    /// reload applies to sessions created afterwards. Never throws; failure means "launch unchanged".
    /// The result is not stored, so duplicated tabs/panes re-resolve and re-inject independently.
    /// </summary>
    private static ShellLaunch PrepareShellIntegration(
        string? shell, bool shellIsExecutable, IReadOnlyList<string>? command)
    {
        try
        {
            string mode = UserConfigService.Current.ShellIntegration;
            if (!ShellIntegrationLauncher.IsDetectMode(mode))
                return ShellLaunch.None;
            return ShellIntegrationLauncher.Prepare(
                shell, shellIsExecutable, command, mode, ShellIntegrationLocator.Find(),
                Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
        }
        catch (Exception)
        {
            return ShellLaunch.None;
        }
    }
}
