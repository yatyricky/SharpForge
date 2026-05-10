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

Instance fields are `self.Field`. Static fields are `SF__.Type.Field`. Field initializers run at declaration.

## Auto-Properties

Auto-properties lower to fields. Getters and setters are not emitted as methods.

```csharp
public int Health { get; set; }
```

Lowers the same as `public int Health;`.

## Events (field-like)

Field-like events lower to a delegate field. Raise and subscribe use the field directly.

## Method Overloads

Overloaded methods get name suffixes based on their parameter count and types to avoid Lua name collisions. The exact suffix is determined by the transpiler.

## Static Constructors

Static constructors are emitted as a regular static function called immediately after type registration.

## Unsupported

Abstract classes (without implementations), partial classes across files that don't compile together, and `ref`/`out` parameters produce a transpiler error.
