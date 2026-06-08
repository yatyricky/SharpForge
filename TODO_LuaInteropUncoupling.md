# TODO: Lua Interop 解耦 — Profile 文件系统

## 问题

`LuaAttribute.Class` 硬编码 `class()` 模式，SharpForge 与 lua-maps 的 `Lib/class.lua` 高度耦合。WC3 生态中一个工程可能混用多套 OOP 写法。

## 方案：Lua Profile 文件

项目中放置 `.lua` profile 文件，描述一种 OOP 系统的模板：

```lua
-- profiles/class.lua（默认，内置）
return {
    class = '{Type} = {Type} or class("{Name}"{BaseRef})',
    ctor = 'function {Type}.New({Params})\n    local self = {Type}.new({CtorArgs})\n    {Type}.__Init(self{CtorArgs})\n    return self\nend',
    init = 'function {Type}.__Init(self{Params})\n    {Body}\nend',
    method = 'function {Type}:{Name}({Params})\n    {Body}\nend',
    static_method = 'function {Type}.{Name}({Params})\n    {Body}\nend',
    base_init = '{Base}.__Init(self{Args})',
    base_call = '{Base}.{Name}(self{Args})',
    setmetatable = '{Type}.__sf_base = {Base}',
}
```

```lua
-- profiles/raw.lua（纯 setmetatable）
return {
    class = '{Type} = {Type} or setmetatable({{}}, {{ __index = {Base} }})',
    ctor = 'function {Type}.New({Params})\n    local self = setmetatable({{}}, {{ __index = {Type} }})\n    {Body}\n    return self\nend',
    init = nil,
    method = 'function {Type}:{Name}({Params})\n    {Body}\nend',
    static_method = 'function {Type}.{Name}({Params})\n    {Body}\nend',
    base_init = '{Base}.New({Args})',
    base_call = '{Base}.{Name}({Args})',
    setmetatable = '{Type}.__sf_base = {Base}',
}
```

## 属性引用

```csharp
[Lua(Class = "MyClass", Profile = "profiles/class.lua")]  // 指定 profile
[Lua(Class = "MyClass")]  // 默认 class.lua，零迁移成本
[Lua(Class = "OldStyle", Profile = "profiles/raw.lua")]  // 同项目不同 OOP
```

## 模板变量

| 变量 | 含义 |
|------|------|
| `{Type}` | 类型全名（如 `SF__.MyClass`） |
| `{Name}` | 类型短名 |
| `{Base}` | 基类引用 |
| `{BaseRef}` | 基类引用带分隔符（如 `, BuffBase`） |
| `{Params}` | 参数列表 |
| `{CtorArgs}` | 构造函数参数（不含 self） |
| `{Args}` | 方法调用参数 |
| `{Body}` | 方法体 |

## 关键设计决策

1. Profile 用 Lua 语法 — WC3 开发者熟悉
2. `init` 可选 — nil 时 ctor 包含全部初始化
3. 模板变量 `{}` — 与 C# 字符串插值一致
4. 内置默认不变 — `[Lua(Class = "X")]` 不写 Profile 等于用 class.lua

## 实现步骤

1. `LuaProfile` 类：解析 `.lua` profile 文件
2. `IRLowering`：`GetLuaClass` 使用 profile 模板替代硬编码
3. `LuaEmitter`：`EmitLuaClassDeclaration`、`EmitConstructor` 使用 profile 模板
4. `TranspileOptions`：新增 `ProfileDirectory` 选项
5. 内置默认 profile（当前 class.lua 行为）
6. 现有属性（`Module`、`Name`、`StaticMethod`、`Method`、`TableLiteral`、`PackStruct`、`Require`）不变
