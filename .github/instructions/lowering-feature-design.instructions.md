---
description: "Use when: designing, implementing, or reviewing SharpForge transpiler lowering code."
applyTo: "src/Transpiler/**,tests/Transpiler.Tests/**"
---

# Lowering Feature Guidelines

Keep this instruction file short and general. Detailed feature notes belong in docs and tests, especially [docs/generated-lua.md](../../docs/generated-lua.md).

- Prefer direct, predictable Lua with a small helper surface.
- Canonicalize C# conveniences into existing semantic paths before adding Lua Emission Layer special cases.
- Choose the simplest design that preserves semantics; weigh performance, implementation fit, correctness risk, output size, and readability.
- Add focused tests for observable lowering behavior and boundary cases.
- Emit diagnostics for unsupported constructs instead of guessing.
