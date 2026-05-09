---
description: "Repository-wide coding behavior: document user-visible changes and capture reusable implementation principles."
alwaysApply: true
applyTo: "**"
---

# General Coding Behaviour

These instructions apply across SharpForge source, tests, docs, samples, and project files.

## Document User-Facing Changes

When adding or changing user-visible behavior, update the appropriate documentation before considering the task complete.

- CLI contracts and examples belong in `docs/cli.md`.
- GUI behavior belongs in `docs/gui.md`.
- Generated Lua shape, lowering behavior, runtime helpers, and transpiler feature coverage belong in `docs/generated-lua.md` or a focused feature doc such as `docs/collections.md`, `docs/struct.md`, or `docs/lua-interop.md`.
- Add or update `README.md` links when a new doc page or major feature needs to be discoverable from the project overview.

Do not leave feature knowledge only in tests, commit messages, chat, or local plans.

## Capture Reusable Principles

After implementing, debugging, or reviewing a feature, distill durable lessons into the most specific applicable instruction file.

- Use this file for broad repository habits that apply to all source areas.
- Use `lowering-feature-design.instructions.md` for transpiler lowering representation, emitted Lua shape, and helper/runtime design principles.
- Use `planning-hazard-complexity.instructions.md` for cross-feature interaction risks and verification heuristics.
- Use `karpathy-guidelines.instructions.md` for general simplicity, scope control, and execution discipline.

Keep instruction updates short, actionable, and non-duplicative. Prefer a precise rule with examples over a long narrative.
