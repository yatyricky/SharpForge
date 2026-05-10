---
description: "Use when: designing, implementing, reviewing, or documenting a new SharpForge transpiler lowering feature."
applyTo: "src/Transpiler/**,tests/Transpiler.Tests/**,*.md"
---

# Lowering Feature Design Priorities

When designing a new lowering feature, evaluate options in this priority order. Higher-priority rules win over lower-priority rules when they conflict.

1. Performance first. Prefer lowering that avoids unnecessary runtime allocation, indirection, dynamic dispatch, table churn, or repeated work in generated Lua.
2. Easy to implement. Prefer designs that fit the existing IR, emitter, semantic model, and test style without broad rewrites or speculative abstractions.
3. Less likely to produce bugs. Prefer explicit, centralized rules and stable generated shapes over clever special cases, especially at representation boundaries. Avoids identifier name collisions and fragile transformations that could break when the input code changes.
4. Concise output. Prefer generated Lua that is compact enough to inspect and debug, as long as concision does not hide semantics or introduce fragile transformations.
5. Human-readable output. Prefer stable names, predictable ordering, and source-shaped structure where possible, after the first four priorities are satisfied.

Before coding, compare viable lowering strategies against all five priorities and state the chosen tradeoff. Add focused tests for the boundary cases most likely to violate the selected representation.

For supported local `List<struct>` lowering, follow `docs/struct.md`: emit one parallel field array per leaf field, insert each field separately, and read indexed fields from those arrays. Use table-shaped struct values only at collection boundaries where that struct-of-arrays representation is not applicable, such as unsupported fallback list operations or dictionary value storage.

For flattened struct collection mutations, evaluate the source value or index once, then apply the same logical operation to every field array. Removal and take operations must keep arrays in lockstep; equality scans must call the struct's typed `Equals(T)` shape with spread field arguments.

For bundled `SFLib` lowering, classify symbols by purpose-specific namespace predicates such as `SFLib.Collections`, `SFLib.Interop`, `SFLib.Async`, and `SFLib.Diagnostics`. Avoid broad root-namespace recognition or legacy namespace aliases; unsupported or removed library namespaces should fail normally instead of silently lowering.