# Transpiler Lowering Design Notes

This page keeps detailed lowering design knowledge out of always-loaded agent instructions. It is a reference for feature work, reviews, and bug investigations. Source and tests remain the source of truth.

## Architecture

SharpForge treats C# input as a layered authoring surface that gets progressively simplified before Lua emission:

| Layer | Role | Examples |
| --- | --- | --- |
| 4. User code | Map and gameplay code written by project authors. | Game systems, triggers, abilities, project-specific helpers |
| 3. Library surface | Reusable C#-shaped APIs that user code can reference without turning SharpForge into a .NET compatibility layer. | `List<T>`, `Dictionary<K,V>`, `Vector3`, `SpellEvent`, interop stubs |
| 2. Normalization | Broad C# syntax conveniences are rewritten into simpler, Lua-oriented forms before final Lua emission. | Method groups become lambdas, `async`/`await` becomes coroutine flow, `action?.Invoke()` becomes an explicit nil-guarded call |
| 1. Lua emission | The final lowered representation is emitted as direct Lua with the smallest practical helper surface. | Root-table functions, tables, nil guards, coroutine helpers only when needed |

SharpForge is strongly typed Lua behind a C# authoring surface. It uses C# syntax and Roslyn analysis, but it is not a .NET compatibility layer.

## Scope

Add SFLib stubs or transpiler support only when a feature needs explicit transpiler-specific lowering. A feature that users can compose from existing primitives, such as a stack built from `List<T>`, should stay outside the core surface area.

Unsupported C# constructs should produce hard diagnostics. Do not silently emit `nil`, passthrough Lua comments, or best-guess Lua calls for unrecognized constructs.

## Design Priorities

When designing a lowering feature, compare viable strategies in this order:

1. Performance: avoid runtime allocation, indirection, dynamic dispatch, table churn, IIFEs, or repeated work when an equally correct alternative avoids them.
2. Implementation fit: prefer designs that fit the existing IR, emitter, semantic model, and tests.
3. Correctness: prefer explicit, centralized rules and stable generated shapes over clever special cases, especially at representation boundaries.
4. Concision: keep generated Lua compact when doing so preserves full semantics.
5. Readability: prefer stable names, predictable ordering, and source-shaped structure after the earlier concerns are satisfied.

## Normalization Notes

When implementing syntax conveniences or target-typed forms, normalize into the existing semantic lowering path wherever practical. Shared lowering helpers should be keyed by Roslyn symbols, receiver shape, and supported API semantics rather than by prompt-shaped special cases.

Null-conditional delegate invocation must preserve C# short-circuiting. Lower `handler?.Invoke(args)` with a temporary and nil guard or another lazy form so the receiver is evaluated once and the arguments are evaluated only when the delegate is non-nil.

Optional parameters should use callee-side nil guards, such as `if arg == nil then arg = default end`, so omitted Lua arguments, named-argument gaps, and explicit false or zero defaults share one representation.

Overloaded methods, constructors, and operators should be normalized to unique Lua names at the semantic boundary using the same signature function for declarations and call sites. Count-only suffixes are not enough for same-arity overloads.

Generic lowering should add hidden runtime type arguments only on ordinary SharpForge method call emission after special API lowerings have consumed source-shaped arguments. Hidden arguments must not leak into Lua interop, Regex, or other purpose-built API lowerings.

Type syntax for runtime checks such as `is`, `as`, and patterns should fall back from `SemanticModel.GetTypeInfo(...).Type` to `GetSymbolInfo(...).Symbol as ITypeSymbol`, so generic type parameters lower to hidden runtime type parameters instead of unsupported identifier expressions.

Constructor initializer arguments for `base(...)` and `this(...)` should be lowered only after the constructor's own parameters have been declared in the Lua name map.

Expressions with ordered side effects, such as class object initializer assignments, should lower to an immediately invoked function with a temporary, statement sequence, and final return rather than trying to encode side effects into a single expression.

Metadata values that behave like runtime objects should be represented by explicit metadata fields on the runtime representation rather than compile-time folding or origin-tracking side tables.

## Flattened Struct Notes

When a representation guard decides whether a struct local can be flattened, keep it in sync with the expression-lowering paths that consume that representation. For example, if argument lowering expands struct parameters, the local-use scan must treat calls to struct-typed parameters as supported uses.

When struct instance receivers are flattened, apply the same receiver expansion to accessor methods such as computed property getters. Otherwise chained property reads can emit Lua colon calls on multi-return struct values.

Flattened struct assignment should resolve the Roslyn symbol before choosing a receiver: check flattened locals first, then flattened members. Class fields and auto-properties use backing members such as `self.position__x`; explicit receivers such as `trs.position` lower to `trs.position__x`; a same-named local must never default to `self`.

Register flattened struct member metadata before lowering method and property bodies across the compilation. Cross-file accesses such as `gameObject.transform.position.x` must flatten by symbol regardless of declaration order.

When a method or property returns a flattenable struct and is itself used as a struct argument, forward the call as the flattened value instead of projecting `.Field` from it. Multi-return struct calls are not Lua tables.

Any return position for a flattenable struct, including expression-bodied methods, operators, and property getters, should use the struct multi-return path instead of generic expression lowering. Generic struct expression lowering may intentionally produce a table for non-return contexts.

When a flattened struct target or local is assigned from a conditional expression, declare the flattened temporaries in the surrounding scope, then spill the selected branch inside statement-level `if` branches before assigning the target. Expression-level helpers consume Lua multi-return values, and `do` blocks hide local declarations.