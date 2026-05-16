---
description: "Repository-wide coding behavior. Priority 1: document user-visible changes. Priority 2: capture reusable implementation principles."
alwaysApply: true
applyTo: "**"
---

# General Coding Behaviour

These instructions apply across SharpForge source, tests, docs, and project files.

## Document User-Facing Changes

When adding or changing user-visible behavior, update the appropriate documentation before considering the task complete. When the implementation changes, update any doc that describes that behavior — stale docs are bugs.

- CLI contracts and examples belong in `docs/cli.md`.
- GUI behavior belongs in `docs/gui.md`.
- Generated Lua shape, lowering behavior, runtime helpers, and transpiler feature coverage belong in `docs/generated-lua.md` or a focused feature doc such as `docs/collections.md`, `docs/struct.md`, or `docs/lua-interop.md`.
- Add or update `README.md` links when a new doc page or major feature needs to be discoverable from the project overview.

Do not leave feature knowledge only in tests, commit messages, chat, or local plans.

When documenting a runtime helper (e.g., `ArrayNew__`, `StrConcat__`), include the emitted Lua implementation so readers understand what runs at runtime, not just that the helper exists.

When implementing or expanding a feature that has a docs/api/ page, add a `// ref: docs/api/FEATURE.md` comment on the line immediately above the primary lowering or emission entry-point method for that feature. Use `#fragment` only when one page covers two distinct subsystems (e.g., `docs/api/collections.md#list` vs `#dictionary`). One anchor per feature cluster is sufficient; do not annotate sub-helpers.

## Capture Reusable Principles

After implementing, debugging, or reviewing a feature, distill durable lessons into the most specific applicable instruction file.

- Use this file for broad repository habits that apply to all source areas.
- Use `lowering-feature-design.instructions.md` for transpiler lowering representation, emitted Lua shape, and helper/runtime design principles.
- Use `planning-hazard-complexity.instructions.md` for cross-feature interaction risks and verification heuristics.
- Use `karpathy-guidelines.instructions.md` for general simplicity, scope control, and execution discipline.

Keep instruction updates concise (one to two sentences per rule), actionable, and non-duplicative. Prefer a precise rule with examples over a long narrative.

## Never hard-code to fulfill a prompt

The prompt is expected to contain samples to describe the desired behavior. If the prompt is missing details, ask for clarification instead of guessing or hard-coding a specific behavior. The implementation should be driven by the prompt's requirements, not by assumptions about what the user might want.
