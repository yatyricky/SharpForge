using SFLib.Interop;

namespace StdLib;

/// <summary>
/// Debug helpers shared by StdLib assertions. TraceSuffix appends the transpiler's
/// shadow call stack (SF__TRACE__, populated only in --trace-calls builds); normal
/// builds read nil and return the empty suffix.
/// </summary>
public static class Diagnostics
{
    public static string TraceSuffix()
    {
        var trace = LuaInterop.GetGlobal<LuaObject>("SF__TRACE__");
        if (trace == null) return "";
        return " | at " + table.concat(trace, " -> ");
    }
}
