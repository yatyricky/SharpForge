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