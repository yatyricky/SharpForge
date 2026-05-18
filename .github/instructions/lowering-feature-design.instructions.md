---
description: "Use when: designing, implementing, reviewing, or documenting a new SharpForge transpiler lowering feature."
applyTo: "src/Transpiler/**,tests/Transpiler.Tests/**,*.md"
---

# Lowering Feature Design Priorities

When designing a new lowering feature, evaluate options in this priority order. Higher-priority rules win over lower-priority rules when they conflict. Example: if performance and human-readable output conflict, prioritize performance unless the readability loss makes the output actively difficult to debug.

1. Performance first. Prefer lowering that avoids runtime allocation, indirection, dynamic dispatch, table churn, or repeated work in generated Lua when an equally correct alternative does not incur those costs.
2. Easy to implement. Prefer designs that fit the existing IR, emitter, semantic model, and test style without broad rewrites or speculative abstractions.
3. Less likely to produce bugs. Prefer explicit, centralized rules and stable generated shapes over clever special cases, especially at representation boundaries. Avoids identifier name collisions and fragile transformations that could break when the input code changes.
4. Concise output. Prefer generated Lua where each construct maps to the fewest Lua lines that preserve the full semantics, as long as concision does not hide semantics or introduce fragile transformations.
5. Human-readable output. Prefer stable names, predictable ordering, and source-shaped structure where possible, after the first four priorities are satisfied.

Before coding, compare viable lowering strategies against all five priorities and state the chosen tradeoff. Add focused tests for the boundary cases most likely to violate the selected representation.

When implementing syntax sugar or target-typed forms, first normalize into the existing semantic lowering path wherever practical. Avoid prompt-shaped helpers such as method-group-specific collection branches; shared lowering helpers should be keyed by Roslyn symbols, receiver shape, and supported API semantics.

When a representation guard decides whether a struct local can be flattened, keep it in sync with the expression lowerer paths that consume that representation; for example, if argument lowering expands struct parameters, the local-use scan must treat calls to struct-typed parameters as supported uses.

When struct instance receivers are flattened, apply the same receiver expansion to accessor methods such as computed property getters; otherwise chained property reads can accidentally emit Lua colon calls on multi-return struct values.

When a method or property returns a flattenable struct and is itself used as a struct argument, forward the call as the flattened value instead of projecting `.Field` from it; multi-return struct calls are not Lua tables.

When lowering any return position for a flattenable struct, including expression-bodied methods, operators, and property getters, use the struct multi-return path instead of generic expression lowering; generic struct expression lowering may intentionally produce a table for non-return contexts.

When lowering optional parameters, prefer callee-side nil guards (`if arg == nil then arg = default end`) over call-site default substitution so omitted Lua arguments, named-argument gaps, and explicit false/zero defaults share one representation.

When lowering overloaded methods, constructors, or operators, normalize them to unique Lua names at the semantic boundary using the same signature function for declarations and call sites; count-only suffixes are insufficient for same-arity overloads.

When generic lowering needs hidden runtime type arguments, add them only on ordinary SharpForge method call emission after special API lowerings have consumed source-shaped arguments; do not leak hidden arguments into Lua interop, Regex, collection, or other purpose-built API lowerings.

## Core Identity

SharpForge is strong-typed Lua covered by a C# skin. It takes advantage of C# syntax and Roslyn analysis; it is **not** a .NET compatibility layer and does not aim to reproduce .NET semantics in Lua.

## MVP Scope

Only add SFLib stubs and transpiler support for features that require explicit transpiler-specific lowering. Anything a user can compose from existing primitives (e.g., a Stack built from `List<T>`) is out of scope. Do not grow the surface area for convenience.

If asked to add a feature that violates this principle, do not implement it. Instead, explain why it is out of scope and show how the user can compose the same behavior from existing primitives.

## No Fallback Emission

If a C# construct is not explicitly handled by the transpiler, emit a hard error diagnostic. Never silently emit `nil`, a passthrough Lua comment, or a best-guess Lua call for an unrecognised construct. The user must fix or replace the unrecognised code; the transpiler must not guess.
