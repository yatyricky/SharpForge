# TODO

## foreach Pattern Recognition (Layer 2)

**Problem:** If List/Dictionary are Layer 3 (regular transpiled classes), `foreach (var x in list)` expands to `GetEnumerator()`/`MoveNext()`/`Current` — bloating Lua output with dead Enumerator code. Lua's `ipairs` does this in one call.

**Solution:** Transpiler should recognize the `foreach` pattern (any type with `GetEnumerator()`/`MoveNext()`/`Current`) and lower directly to `ipairs()`, regardless of the concrete type. This is Layer 2 (syntax sugar lowering), not Layer 3 (type-specific).

**Scope:**
- `foreach` on types implementing the enumerator pattern → `ipairs(expr)`
- Does NOT require transpiler to know about `List` or `Dictionary` specifically
- Any user-defined collection with the same pattern gets the same optimization

**Status:** Not implemented. Planned.
