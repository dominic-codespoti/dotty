# AI Agents Instructions for Dotty

This `Agents.md` provides best practices and guidelines for AI agents interacting with the `dotnet-term` codebase.

## 🎯 General Objective

Dotty is a terminal emulator in .NET. AI agents should prioritize performance, memory safety (avoiding allocations in hot paths like rendering and parsing), and cross-platform compatibility.

## 🏗️ Architecture Best Practices

For detailed architectural information, we use progressive disclosure. Please refer to the specific documentation files based on the component you are modifying:

- **System Architecture:** High-level project structure and dependencies.
  ➡️ See [Architecture Docs](./Architecture.md) for details on project layering and responsibilities.
- **Rendering:** `src/Dotty.App/Controls/Canvas/`
  ➡️ See [Rendering Docs](./Rendering.md) for details on GlyphAtlas, BackgroundSynth, etc.
- **Terminal Parsing:** `src/Dotty.Abstractions/` and `src/Dotty.Terminal/`
  ➡️ See [Parsing Docs](./Parsing.md) for control code handling and escaping sequences.
- **Native PTY Integration:** `src/Dotty.NativePty/`
  ➡️ See [Native PTY Docs](./NativePty.md) for POSIX APIs and UNIX process isolation.
- **Competitor Analysis:**
  ➡️ See [Comparison Report](./ComparisonReport.md) for a technical breakdown of how Dotty compares against tools like Ghostty and Wezterm.
- **Testing:** `tests/`
  ➡️ See [Testing Docs](./Testing.md) for fuzzing, repro tests, and terminal emulation benchmarks.

## 🔍 Progressive Disclosure Guide

To respect token limits and optimize context processing:

1. Identify the domain of the task (e.g., UI, Parsing, Native PTY).
2. Follow the specific deep-dive link above to read the necessary context.
3. Keep context scoped: Only read files within the relevant subfolder unless cross-cutting concerns are explicitly triggered.

## 💡 Code Conventions

- Prefer `ref struct` and `Span<T>` for buffer manipulation.
- Avoid boxing and LINQ in rendering/parsing paths.
- Add regression tests in `tests/Dotty.App.Tests/` if addressing a structural bug.

## 📝 Documentation Maintenance

As Dotty evolves, it is your responsibility to keep the system knowledge base current. If you implement a new architectural pattern, add a new service, change deployment steps, or discover a new pattern/bug:

- You **MUST** update the relevant file in the `docs/` folder.
- If you add a completely new category of documentation, you **MUST** update this `Agents.md` file to add the new doc to the **Architecture Best Practices** list above.
- If you establish a new universal rule to prevent a category of bugs, you **MUST** add it to the **Strict Project Guardrails** section below.

## 🚧 Strict Project Guardrails

*This section is reserved for critical rules that prevent common bugs or enforce architectural constraints. When you discover a recurring bug pattern, add a rule here.*

---

*Note: Ensure you read referenced `docs/*.md` files when assigned a specific feature area before touching the codebase.*
