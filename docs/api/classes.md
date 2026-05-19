# Classes

## Static Classes

A static class emits a Lua table under the root table. Static methods use dot syntax.

```csharp
public static class Game
{
    public static int Score;

    public static void AddScore(int amount)
    {
        Score += amount;
    }
}
```

```lua
SF__ = SF__ or {}
SF__.Game = SF__.Game or {}
SF__.Game.Score = 0

function SF__.Game.AddScore(amount)
    SF__.Game.Score = (SF__.Game.Score + amount)
end
```

## Instance Classes

Instance methods use colon syntax. Constructors emit `.New(...)` and return `self`.
Constructor chaining with `: this(args)` emits a call to the current type's generated `__Init...` function before the chained constructor body. The chained constructor performs instance field initialization, so the outer constructor does not re-run field initializers.

```csharp
public class Hero
{
    public string Name;
    public int Health;

    public Hero(string name, int health)
    {
        Name = name;
        Health = health;
    }

    public void TakeDamage(int amount)
    {
        Health -= amount;
    }
}
```

```lua
SF__.Hero = SF__.Hero or {}

function SF__.Hero.New(name, health)
    local self = setmetatable({}, { __index = SF__.Hero })
    self.Name = name
    self.Health = health
    return self
end

function SF__.Hero:TakeDamage(amount)
    self.Health = (self.Health - amount)
end
```

```csharp
var h = new Hero("Arthas", 100);
h.TakeDamage(10);
```

```lua
local h = SF__.Hero.New("Arthas", 100)
h:TakeDamage(10)
```

## Fields

Instance fields are `self.Field`. Static fields and `const` fields are `SF__.Type.Field`. Static field initializers run after the type table and methods are registered, so they can safely call current-type helpers such as `.New()`.

## Auto-Properties

Auto-properties lower to fields. Getters and setters are not emitted as methods.

```csharp
public int Health { get; set; }
```

Lowers the same as `public int Health;`.

## Events (field-like)

Field-like events lower to a delegate field. Raise and subscribe use the field directly.

## Method Overloads

Overloaded methods and constructors get simplified parameter-signature suffixes to avoid Lua name collisions. Primitive signatures use short codes such as `i` for integer, `f` for float, `d` for double, `b` for bool, and `s` for string; named types use a lowercase sanitized type name.

```csharp
public string Add(int a, int b) => "ints";
public string Add(string a, string b) => "strings";

var ints = Add(1, 2);
var strings = Add("a", "b");
```

```lua
function SF__.Demo.Add__ii(a, b)
    return "ints"
end

function SF__.Demo.Add__ss(a, b)
    return "strings"
end

local ints = SF__.Demo.Add__ii(1, 2)
local strings = SF__.Demo.Add__ss("a", "b")
```

## Generic Methods

Generic method type arguments lower to hidden leading Lua parameters that carry the runtime type table. This supports type checks against the type parameter and `new T()` for class types with a `new()` constraint.

```csharp
public T AddComponent<T>() where T : Component, new()
{
    var component = new T();
    return component;
}

var transform = AddComponent<Transform>();
```

```lua
function SF__.GameObject:AddComponent(T)
    local component = T.New()
    return component
end

local transform = self:AddComponent(SF__.Transform)
```

## Optional Parameters

Optional parameter defaults lower to a nil guard at the start of the Lua function. This preserves explicit false and zero defaults, and lets omitted Lua arguments use the C# default value.

```csharp
public static int Pick(int first = 1, int second = 2)
{
    return first + second;
}

var value = Pick(second: 5);
```

```lua
function SF__.Demo.Pick(first, second)
    if first == nil then first = 1 end
    if second == nil then second = 2 end
    return (first + second)
end

local value = SF__.Demo.Pick(nil, 5)
```

## Static Constructors

Static constructors lower to top-level Lua statements that run after the type's methods are emitted. That keeps current-type helpers such as `.New()` available before static initialization executes.

## Unsupported

Abstract classes (without implementations), partial classes across files that don't compile together, and `ref`/`out` parameters produce a transpiler error.
