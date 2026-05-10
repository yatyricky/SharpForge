---
description: "Use when planning or implementing SharpForge features whose behavior can interact with other lowering/runtime features."
applyTo: "src/Transpiler/**,tests/Transpiler.Tests/**,docs/**,*.md"
---

# Planning Hazard Complexity

As SharpForge grows, feature risk grows faster than the feature count. Every new lowering or runtime feature can interact with existing runtime and lowering features. Treat all potential interactions between the new feature and existing runtime/lowering features as part of the design.

Before implementing a non-trivial feature, add a short hazard matrix to the plan. Evaluate interactions in this order (higher-risk categories first):

**Representation layer** (check first — most likely to break generated correctness):
- What changes in the core happy path?
- What happens with structs and struct flattening?
- What happens inside `List<T>`, `Dictionary<K,V>`, arrays, and `foreach`?

**Type system layer** (check second):
- What happens with overloads, virtual/override dispatch, inheritance, and interfaces?
- What happens with `nil`, nullable values, default values, and sentinels?

**Interop layer** (check third):
- What happens when Lua values are JASS handles, `LuaObject` wrappers, raw tables, functions, or userdata?

**Surface layer** (check last):
- What unsupported intersections should become diagnostics instead of best-effort Lua?
- Which docs must state behavior, limits, and escape hatches?

Prefer designs that reduce cross-feature coupling:

- Use separate representations when two cases have genuinely different semantics.
- Add explicit diagnostics for unsupported intersections.
- Keep runtime helpers small and purpose-built instead of expanding toward broad .NET compatibility.
- Avoid hash-based or identity-based shortcuts for value-shaped data unless equality, iteration, serialization, float behavior, object/handle behavior, and docs are all accounted for.

Tests should cover the riskiest pairwise intersections, not only isolated happy paths. For each substantial feature, include focused tests for at least the relevant combinations with structs, collections, overloads, nil/null, iteration/versioning, and Lua/JASS interop.

User docs should describe supported behavior and limits. Design reasoning, rejected alternatives, and planning heuristics belong in instruction files or repo memory, not in user-facing docs.