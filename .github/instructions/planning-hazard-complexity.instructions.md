---
description: "Use when planning or implementing SharpForge features whose behavior can interact with other lowering/runtime features."
applyTo: "src/Transpiler/**,tests/Transpiler.Tests/**,docs/**,*.md"
---

# Hazard Matrix

Every new feature can regress existing ones. Before implementing, check intersections in order:

1. **Representation**: struct flattening, `List<T>`, `Dictionary<K,V>`, arrays, `foreach`
2. **Type system**: overloads, virtual dispatch, inheritance, interfaces, nil/null/defaults
3. **Interop**: JASS handles, `LuaObject`, raw tables, functions, userdata
4. **Surface**: unsupported intersections → diagnostics, not best-effort Lua

Prefer separate representations for separate semantics. Keep helpers small and purpose-built. Add explicit diagnostics for unsupported intersections.

Tests must cover pairwise intersections with structs, collections, overloads, nil/null, and Lua/JASS interop — not only isolated happy paths.
