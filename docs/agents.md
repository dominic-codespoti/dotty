# AI Agents Instructions for Dotty

This `agents.md` provides best practices and guidelines for AI agents interacting with the `dotnet-term` codebase.

## 🎯 General Objective
Dotty is a terminal emulator in .NET. AI agents should prioritize performance, memory safety (avoiding allocations in hot paths like rendering and parsing), and cross-platform compatibility.

## 🏗️ Architecture Best Practices
For detailed architectural information, we use progressive disclosure. Please refer to the specific documentation files based on the component you are modifying:

- **System Architecture:** High-level project structure and dependencies.
  ➡️ See [Architecture Docs](./architecture.md) for details on project layering and responsibilities.
- **Rendering:** `src/Dotty.App/Controls/Canvas/`
  ➡️ See [Rendering Docs](./rendering.md) for details on GlyphAtlas, BackgroundSynth, etc.
- **Terminal Parsing:** `src/Dotty.Abstractions/` and `src/Dotty.Terminal/`
  ➡️ See [Parsing Docs](./parsing.md) for control code handling and escaping sequences.
- **Testing:** `tests/`
  ➡️ See [Testing Docs](./testing.md) for fuzzing, repro tests, and terminal emulation benchmarks.

## 🔍 Progressive Disclosure Guide
To respect token limits and optimize context processing:
1. Identify the domain of the task (e.g., UI, Parsing, Native PTY).
2. Follow the specific deep-dive link above to read the necessary context.
3. Keep context scoped: Only read files within the relevant subfolder unless cross-cutting concerns are explicitly triggered.

## 💡 Code Conventions
- Prefer `ref struct` and `Span<T>` for buffer manipulation.
- Avoid boxing and LINQ in rendering/parsing paths.
- Add regression tests in `tests/Dotty.App.Tests/` if addressing a structural bug.

---
*Note: Ensure you read referenced `docs/*.md` files when assigned a specific feature area before touching the codebase.*
