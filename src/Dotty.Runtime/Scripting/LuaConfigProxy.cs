using System;
using Dotty.Runtime.Config;
using KeraLua;

namespace Dotty.Runtime.Scripting;

/// <summary>Provides a managed view of the current user configuration.</summary>
public sealed class LuaConfigProxy
{
    private DottyUserConfig _config;
    private readonly LuaScriptHost _host;

    internal LuaConfigProxy(LuaScriptHost host)
    {
        _host = host;
        _config = new DottyUserConfig();
    }

    internal DottyUserConfig Config => _config;

    internal void Bind(DottyUserConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>Gets or sets the active theme name.</summary>
    public string theme
    {
        get => _config.Theme;
        set
        {
            _config.Theme = value ?? "DarkPlus";
            _host.MarkConfigDirty();
        }
    }

    internal void ApplyTable(Lua lua, int tableIndex)
    {
        _host.ApplyConfigTable(lua, tableIndex);
    }
}
