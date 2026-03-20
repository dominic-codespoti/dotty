# AI Agents Instructions for Dotty

This `agents.md` provides best practices and guidelines for AI agents interacting with the `dotnet-term` codebase.

## 🎯 General Objective
Dotty is a terminal emulator in .NET. AI agents should prioritize performance, memory safety (avoiding allocations in hot paths like rendering and parsing), and cross-platform compatibility.

## 🏗️ Architecture Best Practices
For detailed architectural information, we use progressive disclosure. Please refer to the specific documentation files based on the component you are modifying:

- **System Architecture:** High-level project structure and dependencies.
  ➡️ See [Architecture Docs](./docs/architecture.md) for details on project layering and responsibilities.
- **Rendering:** `src/Dotty.App/Controls/Canvas/`
  ➡️ See [Rendering Docs](./docs/rendering.md) for details on GlyphAtlas, BackgroundSynth, etc.
- **Terminal Parsing:** `src/Dotty.Abstractions/` and `src/Dotty.Terminal/`
  ➡️ See [Parsing Docs](./docs/parsing.md) for control code handling and escaping sequences.
- **Testing:** `tests/`
  ➡️ See [Testing Docs](./docs/testing.md) for fuzzing, repro tests, and terminal emulation benchmarks.

## 💡 Code Conventions
- Prefer `ref struct` and `Span<T>` for buffer manipulation.
- Avoid boxing and LINQ in rendering/parsing paths.
- Add regression tests in `tests/Dotty.App.Tests/` if addressing a structural bug.

## 📝 Documentation Maintenance
As Dotty evolves, it is your responsibility to keep the system knowledge base current. If you implement a new architectural pattern, add a new service, change deployment steps, or discover a new pattern/bug:
- You **MUST** update the relevant file in the `docs/` folder.
- If you add a completely new category of documentation, you **MUST** update this `AGENTS.md` file to add the new doc to the **Knowledge Base Routing** list above.
- If you establish a new universal rule to prevent a category of bugs, you **MUST** add it to the **Strict Project Guardrails** section below.
