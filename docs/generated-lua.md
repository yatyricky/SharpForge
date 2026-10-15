# Generated Lua

This page describes the Lua shape produced by `sf-transpile`, the small runtime helpers it may emit, and the lowering vocabulary used when designing or reviewing transpiler behavior. Source and tests remain the source of truth.

## Transpiler Architecture

SharpForge uses the **SharpForge Lowering Stack**: C# input starts as an ergonomic authoring surface, passes through explicit supported APIs, canonicalizes into simpler internal shapes, and then emits Lua.

| Layer | Name | Role | Examples |
| --- | --- | --- |
| 4 | Authoring Layer | Map and gameplay code written by project authors. | Game systems, triggers, abilities, project-specific helpers |
| 3 | Library Surface Layer | Reusable C#-shaped APIs that user code can reference without turning SharpForge into a .NET compatibility layer. | `List<T>`, `Dictionary<K,V>`, `Vector3`, `SpellEvent`, interop stubs |
| 2 | Canonicalization Layer | Roslyn-aware lowering that turns rich C# shapes into simpler SharpForge IR shapes before Lua emission. | Static method groups become function references, `async`/`await` becomes coroutine flow, exceptions become typed headers, structs become flattened fields and multi-return values |
| 1 | Lua Emission Layer | The final lowered representation is printed as direct Lua with the smallest practical helper surface. | Root-table functions, tables, nil guards, coroutine helpers only when needed |

Use these terms when discussing lowering behavior:

| Term | Meaning |
| --- | --- |
| Authoring shape | The C# form the user wrote. |
| Surface API | A library or interop API shape SharpForge intentionally supports. |
| Canonical shape | The internal IR representation that equivalent C# forms converge on. |
| Emission shape | The actual Lua text pattern emitted from canonical IR. |
| Runtime helper | A Lua helper emitted only when canonical IR needs it. |

The intent is that the Lua Emission Layer stays small and predictable. Authoring ergonomics belong in the Authoring Layer, reusable concepts belong in the Library Surface Layer, and broad language features should usually canonicalize before the Lua Emission Layer sees them.

SharpForge is strongly typed Lua behind a C# authoring surface. It uses C# syntax and Roslyn analysis, but it is not a .NET compatibility layer.

## Scope

Add SFLib stubs or transpiler support only when a feature needs explicit SharpForge lowering. A feature that users can compose from existing primitives, such as a stack built from `List<T>`, should stay outside the core surface area.

Unsupported C# constructs should produce hard diagnostics. Do not silently emit `nil`, passthrough Lua comments, or best-guess Lua calls for unrecognized constructs.

## Design Priorities

When designing a lowering feature, compare viable strategies in this order:

1. Performance: avoid runtime allocation, indirection, dynamic dispatch, table churn, IIFEs, or repeated work when an equally correct alternative avoids them.
2. Implementation fit: prefer designs that fit the existing IR, emitter, semantic model, and tests.
3. Correctness: prefer explicit, centralized rules and stable generated shapes over clever special cases, especially at representation boundaries.
4. Concision: keep generated Lua compact when doing so preserves full semantics.
5. Readability: prefer stable names, predictable ordering, and source-shaped structure after the earlier concerns are satisfied.

## Canonicalization Notes

When implementing syntax conveniences or target-typed forms, canonicalize into the existing semantic lowering path wherever practical. Shared lowering helpers should be keyed by Roslyn symbols, receiver shape, and supported API semantics rather than by prompt-shaped special cases.

Collection expressions canonicalize into the object-initializer path before any lowering: `[a, b]` with a `StdLib.List<T>` target becomes `new List<T> { a, b }` (constructor plus `Add` calls in an IIFE), `[]` becomes a plain `new List<T>()` / `new Dictionary<K, V>()` construction, and any other target (arrays, interfaces), spread elements, or non-empty dictionary targets are diagnosed rather than guessed.

Index-from-end canonicalizes the same way: `list[^n]` becomes `list[list.Count - n]` on `StdLib.List` receivers before the ordinary get_Item/set_Item paths run; other receivers are diagnosed.

Null-conditional delegate invocation must preserve C# short-circuiting. Lower `handler?.Invoke(args)` with a temporary and nil guard or another lazy form so the receiver is evaluated once and the arguments are evaluated only when the delegate is non-nil.

Method groups passed as delegate arguments must preserve function identity so register/unregister-style matching works: static method groups lower to the direct function reference (`SF__.Type.Method`, including overload suffix names), and instance method groups are rejected with a diagnostic because passing them would require a self-capturing closure whose handle is never the original function.

Optional parameters should use callee-side nil guards, such as `if arg == nil then arg = default end`, so omitted Lua arguments, named-argument gaps, and explicit false or zero defaults share one representation.

Overloaded methods, constructors, and operators should canonicalize to unique Lua names at the semantic boundary using the same signature function for declarations and call sites. Count-only suffixes are not enough for same-arity overloads.

A method name reused across an inheritance chain with different parameter lists (method hiding) must also carry signature suffixes on every involved declaration: a plain name on both the base and derived tables would make the derived entry shadow the base entry in the metatable chain, so base-bound calls would execute the derived body with mismatched arguments. Families whose signatures are all identical (pure overrides) stay plain — they rely on one shared name for metatable dispatch — and the signature comparison uses constructed base members so `virtual Foo(T)` overridden as `Foo(Concrete)` stays plain. Static methods never need this: they are always type-qualified, so no metatable lookup can shadow them.

Generic lowering should add hidden runtime type arguments only on ordinary SharpForge method call emission after special API lowerings have consumed source-shaped arguments. Hidden arguments must not leak into Lua interop, Regex, or other purpose-built API lowerings.

Type syntax for runtime checks such as `is`, `as`, and patterns should fall back from `SemanticModel.GetTypeInfo(...).Type` to `GetSymbolInfo(...).Symbol as ITypeSymbol`, so generic type parameters lower to hidden runtime type parameters instead of unsupported identifier expressions.

Constructor initializer arguments for `base(...)` and `this(...)` should be lowered only after the constructor's own parameters have been declared in the Lua name map.

Expressions with ordered side effects, such as class object initializer assignments, should lower to an immediately invoked function with a temporary, statement sequence, and final return rather than trying to encode side effects into a single expression.

Metadata values that behave like runtime objects should be represented by explicit metadata fields on the runtime representation rather than compile-time folding or origin-tracking side tables.

## Flattened Struct Notes

When a representation guard decides whether a struct local can be flattened, keep it in sync with the expression-lowering paths that consume that representation. For example, if argument lowering expands struct parameters, the local-use scan must treat calls to struct-typed parameters as supported uses.

When struct instance receivers are flattened, apply the same receiver expansion to accessor methods such as computed property getters. Otherwise chained property reads can emit Lua colon calls on multi-return struct values.

Flattened struct assignment should resolve the Roslyn symbol before choosing a receiver: check flattened locals first, then flattened members. Class fields and auto-properties use backing members such as `self.position__x`; explicit receivers such as `trs.position` lower to `trs.position__x`; a same-named local must never default to `self`.

Object initializer assignments to flattened struct members must write the same `member__field` slots as plain assignments and `__Init` defaults. Lower them through the shared struct value machinery instead of emitting a table assignment, so initializer writes stay consistent with flattened member reads.

Register flattened struct member metadata before lowering method and property bodies across the compilation. Cross-file accesses such as `gameObject.transform.position.x` must flatten by symbol regardless of declaration order.

When a method or property returns a flattenable struct and is itself used as a struct argument, forward the call as the flattened value instead of projecting `.Field` from it. Multi-return struct calls are not Lua tables.

Any return position for a flattenable struct, including expression-bodied methods, operators, and property getters, should use the struct multi-return path instead of generic expression lowering. Generic struct expression lowering may intentionally produce a table for non-return contexts.

When a flattened struct target or local is assigned from a conditional expression, declare the flattened temporaries in the surrounding scope, then spill the selected branch inside statement-level `if` branches before assigning the target. Expression-level helpers consume Lua multi-return values, and `do` blocks hide local declarations.

## Generic Types And Structs

Generic types lower to one shared Lua table per generic type definition; there is no per-instantiation specialization of the type itself. The struct decision is share-vs-specialize (the CLR strategy for reference vs value types):

- A method whose parameter type is a **type parameter** that an instantiation substitutes with a flattenable struct gets a **specialized copy per instantiation**: `Method__<StructName>` (namespace-qualified struct name with `.` replaced by `_`), appended after any overload signature suffix (`Method__<sig>__<Struct>`), with the flattened parameter signature. The specialized body lowers with a type-parameter-to-struct substitution so field access, delegate invocation, call arguments, and returns reuse the ordinary flattening paths. Call sites resolve through Roslyn's substituted symbol and emit the same specialized name, so the ABI is identical on both sides.
- Methods without direct type-parameter parameters stay shared; non-struct instantiations call the shared copies with single values. The shared copy of a specializable method remains emitted for those instantiations.
- PackStruct containers are excluded: their contract is table representation for struct elements, which specialization would break.
- Diagnostics instead of guessing: passing a struct to a **generic method** type parameter, passing a struct to a **generic constructor**, and referencing a specializable method **as a value** (method-group argument, identifier, or generic name) all report errors.

Known gaps: transitive open-generic instantiation closure (demand-driven discovery covers direct call sites), generic-method specialization, virtual/override interaction with specialized names, and member-access-as-value (`obj.Method` without invocation), which still resolves to the shared name.

## Root Table Contract

Every transpiled namespace and type lives under one configurable root table. The default is `SF__`.

Module assembly is two-phase: all type declarations (namespace tables, type tables, metatables, methods, LuaClass `class()` calls) are emitted first; static field initializers and static constructors run afterwards in a single `-- static initialization` section, in the original type order, before the entry point call. A static initializer may therefore reference any type's tables and methods. Relative order between initializers is preserved, and field initializers still precede their own type's static constructor.

Foreach is protocol-driven, matching each collection's runtime shape: `IIpairs<T>` collections lower to `for _, item in (Type.IpairsNext)(collection)`, and `IPairs<K,V>` dictionaries to `for key, value in (Type.PairsNext)(collection)` — the deconstructing form `foreach (var (key, value) in dict)` maps the protocol's value list directly onto the loop variables (discards become `_`), while the plain form `foreach (var p in dict)` binds hidden `__key/__value` temps and packs the named tuple into `local p = {key = ..., value = ...}` so `p.key`/`p.value` read naturally. Collection initializers (`new List<T> { a, b }`, `new Dictionary<K,V> { {k, v} }`) lower to `Add` calls and are orthogonal to iteration.

```lua
SF__ = SF__ or {}
SF__.Game = SF__.Game or {}
SF__.Game.Hero = SF__.Game.Hero or {}
```

Override the root table with:

```powershell
sf-transpile .\CSProject -o .\out.lua --root-table MyMod_
```

SharpForge writes generated C# types under this one root table so it does not collide with hand-written Lua modules or `war3map.lua` globals.

## Lowering Shape

Common C# constructs lower to direct Lua patterns:

| C# concept | Lua shape |
| --- | --- |
| Namespace | nested root-table fields |
| Static method | `function SF__.Type.Method(...)` |
| Instance method | `function SF__.Type:Method(...)` |
| Constructor | `SF__.Type.New(...)` with `setmetatable` |
| `this(...)` constructor initializer | current type `__Init...` call on `self` before the constructor body |
| Object creation | `SF__.Type.New(...)` |
| Object initializer assignment | temporary object plus assignments, then returned temporary |
| `float` literal | emitted as its source-faithful decimal (`0.65f` → `0.65`, not `0.6499...`) |
| `double` literal | emitted with full round-trip precision |
| `string.Empty` | empty string literal (`""`) |
| Struct field-only local | flattened locals named `<local>__<field>` |
| Struct object return | Lua multi-return in field declaration order |
| Instance field access | `self.Field` |
| Static field initializer | `SF__.Type.Field = ...` |
| `const` field | static type-table field (`SF__.Type.Field = ...`) |
| String interpolation | nil-safe `SF__.StrConcat__(...)` calls; supported format clauses use `string.format(...)` |
| `try` / `catch` / `finally` | Lua `pcall` scaffolding with typed SharpForge exception headers |
| `is` / `as` | emitted type metadata helpers when needed |
| `??=` | nil-check assignment; expression form returns the existing or assigned value |

## Lua Interop Classes

Classes that inherit (directly or transitively) from `LuaObject` are treated as **external Lua types** by default — they are not transpiled and are expected to exist in the Lua runtime.

If you define a **user class** that inherits from `LuaObject` and want it to be transpiled, you must add the `[Lua(Class = "ClassName")]` attribute:

```csharp
using SFLib.Interop;

[Lua(Class = "MyBuff")]
public class MyBuff : LuaObject
{
    public float duration;
    public MyBuff(float duration) { this.duration = duration; }
    public void Activate() { }
}
```

This generates the class definition in Lua with proper inheritance (`setmetatable` / `__sf_base`).

Types with `[Lua(Module = "path")]` are also valid — they bind to an existing Lua module via `require`. Types with `[Lua(TableLiteral = true)]` are emitted as plain Lua tables.

Only **direct** subclasses of `LuaObject` are treated as external types. Indirect subclasses (e.g., inheriting from `BuffBase` which inherits from `LuaObject`) are transpiled normally.
| `List<T>`, `Dictionary<K,V>` | Library Surface Layer APIs backed by explicit Lua/interoperability behavior |

C# line comments, block comments, and XML doc comments on lowered types, members, fields, and statements are emitted as Lua `--` comments near the corresponding generated code.

## Exceptions

SharpForge exceptions are represented as Lua error strings. The first 13 characters are a compile-time type header in the form `SF__E########`, where the hash-like suffix is derived from the C# exception type symbol. User-defined classes that inherit from `System.Exception` receive their own headers.

`throw new SomeException("boom")` lowers to an `error(...)` call whose message begins with that header. `catch` clauses compare the header, so multiple catches and catches of user-defined base exception types can dispatch without constructing a .NET exception object at runtime. A `catch (Exception)` clause catches any SharpForge exception header; unmatched Lua errors are rethrown after `finally` runs.

## Minimal Runtime Bias

SharpForge emits only helpers needed by the lowered code. Library APIs such as `List<T>` and `Dictionary<K,V>` belong above the Lua Emission Layer, backed by explicit library or interop code rather than a broad translated runtime.

That bias keeps output small and predictable: use platform functions directly, model only what the generated Lua needs, and avoid a broad compatibility runtime.

## Build Integration

`sf-transpile` only emits Lua. It does not mutate `.w3x` files.

`sf-build` owns Lua dependency bundling and map injection. The separation keeps the C# compiler path independent from MPQ/archive work.

## API Reference

See [docs/api/](api/) for complete documentation of every supported language construct and how it lowers to Lua.