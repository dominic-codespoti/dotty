# Test Suite

Located in `tests/Dotty.App.Tests/` and related directories, the test suite thoroughly verifies boundary edge-cases with headless state simulation to assert safety:

* **Buffer Correctness**: Extensive assertions over matrix mutation instructions using tests like `BasicAnsiParserTests.cs` and `SgrColorTests.cs`.
* **Visual State Handling**: Rendering permutations are tested by visual mapping bounds assertions in `AsciiArtRenderTests.cs` and `PermutationScrollRenderTests.cs`.
* **Advanced Fuzzing**: Advanced testing pipelines simulate intense and misaligned UNIX inputs (`StressFuzzReproTests.cs`, `NeovimReplayTests.cs`) to prevent crashes regarding `Span<T>` boundaries, test memory overlaps, and guarantee continuous thread safety.
