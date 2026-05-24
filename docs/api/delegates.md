# Delegates

## Lambdas

Lambdas lower to Lua anonymous functions.

```csharp
Func<int, int, bool> less = (a, b) => a < b;
```

```lua
local less = function(a, b)
    return (a < b)
end
```

## Func\<\>

`Func<T1, ..., TResult>` and `Action<T1, ...>` are valid field, local, and parameter types. The delegate is stored as a Lua function value and called directly.

```csharp
public Func<int, bool> Filter;

Filter = x => x > 0;
bool ok = Filter(5);
```

```lua
self.Filter = function(x)
    return (x > 0)
end
local ok = self.Filter(5)
```

Bound instance method groups passed to delegate parameters lower as receiver-capturing lambdas before emission. For example, `_units.Add` passed to an `Action<unit>` parameter lowers like `u => _units.Add(u)`.

```lua
ExTriggerRegisterNewUnit(function(u)
    self._units:Add(u)
end)
```

Statement-form conditional delegate invocation is supported. SharpForge evaluates the receiver once, stores it in a temporary, and only invokes when that temporary is non-nil.

```csharp
handler?.Invoke(value);
```

```lua
local delegate = handler
if (delegate ~= nil) then
    delegate(value)
end
```

## Operator Overloads

Operator overloads (`operator +`, `operator ==`, etc.) lower to static functions named with a suffix. They are not called implicitly by the transpiler for built-in binary expressions; they must be called explicitly or the transpiler will use the operator directly.

## Unsupported

Multi-cast delegates (`+=` for event subscription outside field-like events), `delegate` declarations, and `event` with custom add/remove accessors produce a transpiler error.
