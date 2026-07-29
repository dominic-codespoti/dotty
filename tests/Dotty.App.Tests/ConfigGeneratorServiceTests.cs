using Xunit;
using Dotty.App.Services;

namespace Dotty.App.Tests;

public sealed class ConfigGeneratorServiceTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dotty-config-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureConfigExists_GeneratesFlatConfigAndEditorProject()
    {
        var root = NewRoot();
        try
        {
            Assert.True(ConfigGeneratorService.EnsureConfigExists(root));
            Assert.True(File.Exists(Path.Combine(root, "Config.cs")));
            var project = Path.Combine(root, "Dotty.UserConfig.csproj");
            Assert.True(File.Exists(project));
            var projectText = File.ReadAllText(project);
            Assert.Contains("<Compile Include=\"Config.cs\" />", projectText);
            Assert.Contains("PackageReference Include=\"Dotty.Abstractions\"", projectText);
            Assert.False(Directory.Exists(Path.Combine(root, "Dotty.UserConfig")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void FlatConfig_IsSelectedWithoutTouchingLegacyTree()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var flat = Path.Combine(root, "Config.cs");
            File.WriteAllText(flat, "flat");

            var nested = Path.Combine(root, "Dotty.UserConfig", "Config.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "legacy");

            Assert.Equal(flat, ConfigGeneratorService.GetExistingConfigPath(root));
            Assert.Equal("legacy", File.ReadAllText(nested));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void LegacyConfig_IsCopiedAndPreserved()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "Dotty.UserConfig", "Config.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "legacy config");

            var flat = Path.Combine(root, "Config.cs");
            Assert.Equal(flat, ConfigGeneratorService.GetExistingConfigPath(root));
            Assert.Equal("legacy config", File.ReadAllText(flat));
            Assert.Equal("legacy config", File.ReadAllText(nested));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void LegacyConfig_CopyFailureLeavesLegacySelected()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "Dotty.UserConfig", "Config.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "legacy config");
            Directory.CreateDirectory(Path.Combine(root, "Config.cs"));

            Assert.Equal(nested, ConfigGeneratorService.GetExistingConfigPath(root));
            Assert.Equal("legacy config", File.ReadAllText(nested));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ForceRegeneration_ReplacesFlatAndPreservesLegacy()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "Dotty.UserConfig", "Config.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "legacy config");
            ConfigGeneratorService.GetExistingConfigPath(root);
            var flat = Path.Combine(root, "Config.cs");
            File.WriteAllText(flat, "custom config");

            Assert.True(ConfigGeneratorService.EnsureConfigExists(root, force: true));
            Assert.Contains("MyDottyConfig", File.ReadAllText(flat));
            Assert.Equal("legacy config", File.ReadAllText(nested));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
