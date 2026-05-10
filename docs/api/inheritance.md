# Inheritance

## Single Inheritance

The derived type's Lua table sets `__index` to the base type table. The constructor calls the base constructor via an emitted `Init` function.

```csharp
public class Unit
{
    public int Health;

    public Unit(int health)
    {
        Health = health;
    }

    public virtual void TakeDamage(int amount)
    {
        Health -= amount;
    }
}

public class Hero : Unit
{
    public string Name;

    public Hero(string name, int health) : base(health)
    {
        Name = name;
    }

    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount * 2);
    }
}
```

```lua
SF__.Unit = SF__.Unit or {}

function SF__.Unit.New(health)
    local self = setmetatable({}, { __index = SF__.Unit })
    SF__.Unit.Init(self, health)
    return self
end

function SF__.Unit.Init(self, health)
    self.Health = health
end

function SF__.Unit:TakeDamage(amount)
    self.Health = (self.Health - amount)
end

SF__.Hero = setmetatable(SF__.Hero or {}, { __index = SF__.Unit })

function SF__.Hero.New(name, health)
    local self = setmetatable({}, { __index = SF__.Hero })
    SF__.Hero.Init(self, name, health)
    return self
end

function SF__.Hero.Init(self, name, health)
    SF__.Unit.Init(self, health)
    self.Name = name
end

function SF__.Hero:TakeDamage(amount)
    SF__.Unit.TakeDamage(self, (amount * 2))
end
```

## virtual / override

`virtual` and `override` are tracked by the transpiler for correctness but produce no extra Lua. Method lookup follows the metatable chain.

## base()

`base(args)` in a constructor lowers to a call to the base type's `Init` function. `base.Method(args)` lowers to an explicit static call `SF__.Base.Method(self, args)`.

## Interfaces

An interface lowers to an empty Lua table. Implementing a class stores interface type metadata for `is`/`as` checks. See [casting.md](casting.md).

## Multiple Inheritance

Not supported. A class may implement multiple interfaces but inherit from at most one class.
