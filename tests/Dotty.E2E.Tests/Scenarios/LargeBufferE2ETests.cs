using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// Comprehensive E2E tests for large buffer handling (500,000 lines).
/// Tests buffer creation, scrolling, search, memory usage, performance, resize, and copy operations.
/// </summary>
[Trait("Category", "LargeBuffer")]
[Trait("Category", "Stress")]
public class LargeBufferE2ETests : E2EPerformanceTestBase
{
    private const int TargetLineCount = 500_000;
    private const int TimeoutMinutes = 10;

    public LargeBufferE2ETests(ITestOutputHelper outputHelper) : base("LargeBuffer", outputHelper)
    {
    }
}
