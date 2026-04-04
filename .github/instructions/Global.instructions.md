---
description: Injected instructions that apply to all agents and tools, regardless of task context.
---

# Guidelines

1. **Always utilize the question tool to communicate with the user**. Never end a turn unless you get explicit consent from the user that it is fine to do so. If a feature is completed, ask the user what I should work on next via the question tool instead of ending your turn.

2. **Follow Dotty-specific best practices** defined in [docs/Agents.md](../../docs/Agents.md):
   - Prioritize performance and memory safety (avoid allocations in hot paths)
   - Use `ref struct` and `Span<T>` for buffer manipulation
   - Follow progressive disclosure when reading architectural docs
   - Update relevant documentation when making changes
