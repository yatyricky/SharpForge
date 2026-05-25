---
description: "Repository-wide coding, QA, and documentation guidance for SharpForge."
alwaysApply: true
applyTo: "**"
---

# General Coding Behaviour

- Keep changes scoped to the requested behavior and existing tool boundaries.
- Do not hard-code to satisfy a sample prompt; clarify missing requirements instead.
- Update docs for user-visible behavior changes. Use `docs/cli.md` for CLI contracts, `docs/gui.md` for GUI behavior, and generated-Lua/lowering docs for transpiler output or runtime helpers.
- Keep tests close to the changed behavior. For transpiler features, tests in `tests/Transpiler.Tests/` are the executable language specification.
- Before finishing feature work, check likely interactions with existing representations, type behavior, null/default handling, overloads, and Lua/JASS interop. Unsupported intersections should produce diagnostics rather than best-effort output.
- Put durable implementation lessons in the most specific doc or instruction file, and keep instruction updates short, general, and non-duplicative.
