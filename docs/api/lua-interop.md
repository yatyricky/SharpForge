# Lua Interop

Use `SFLib.Interop` to call Lua code from C# and to annotate types that map to existing Lua objects.

## LuaObject

`LuaObject` is a stub type for raw Lua values. Use it when interfacing with external Lua APIs.

```csharp
using SFLib.Interop;

LuaObject table = LuaInterop.CreateTable();
```

## LuaInterop Methods

| C# call | Lua emitted |
| --- | --- |
| `LuaInterop.CreateTable()` | `{}` |
| `LuaInterop.Require("mod")` | `require("mod")` |
| `LuaInterop.Require<T>("mod")` | `require("mod")` |
| `LuaInterop.Get<T>(target, "name")` | `target.name` |
| `LuaInterop.GetGlobal<T>("name")` | `name` (global) |
| `LuaInterop.Set(target, "name", value)` | `target.name = value` |
| `LuaInterop.SetGlobal("name", value)` | `name = value` |
| `LuaInterop.Call(target, "fn", args)` | `target.fn(args)` |
| `LuaInterop.Call<T>(target, "fn", args)` | `target.fn(args)` |
| `LuaInterop.CallMethod(target, "fn", args)` | `target:fn(args)` |
| `LuaInterop.CallMethod<T>(target, "fn", args)` | `target:fn(args)` |
| `LuaInterop.CallGlobal("fn", args)` | `fn(args)` |
| `LuaInterop.CallGlobal<T>("fn", args)` | `fn(args)` |

## [Lua] Attribute

The `[Lua]` attribute from `SFLib.Interop` annotates types and members to control how they are emitted.

```csharp
[Lua(Class = "SomeExternalClass", Require = "external_module")]
public class ExternalWrapper : LuaObject
{
    [Lua(Method = "doThing")]
    public void DoThing(int x) { }
}
```

The transpiler uses the attribute values to emit the correct Lua call instead of the default `SF__.` path.

### [Lua] Properties

| Property | Meaning |
| --- | --- |
| `Name` | Override the emitted Lua name for a member |
| `StaticMethod` | Call as a static method (dot syntax) with this name |
| `Method` | Call as an instance method (colon syntax) with this name |
| `Module` | Module name for `require` |
| `Class` | Lua class name to use instead of the SharpForge-generated one |
| `Require` | `require("...")` expression to emit at module top |
| `TableLiteral` | Emit object creation as a table literal `{}` |
