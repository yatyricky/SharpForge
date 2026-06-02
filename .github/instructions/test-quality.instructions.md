---
description: "Use when writing, reviewing, or modifying transpiler tests."
applyTo: "tests/Transpiler.Tests/**"
---

# Test Quality

## Verify actual output, not assumptions

When writing a test that asserts generated Lua output, you MUST run the transpiler on the test source and inspect the actual output before writing assertions. Do not assume what the output looks like based on reading the transpiler code — run it.

```bash
# Write source to a temp file, run transpiler, inspect output
mkdir -p /tmp/sf-test-xxx
cat > /tmp/sf-test-xxx/Test.cs << 'EOF'
<source code>
EOF
dotnet run --project src/Transpiler/Transpiler.csproj --no-restore -- /tmp/sf-test-xxx -o /tmp/sf-test-xxx/out.lua --ignore-namespace SFLib.Interop
cat /tmp/sf-test-xxx/out.lua
```

## Test both positive and negative cases

When testing a feature that conditionally generates code (e.g., struct equality only when used), test BOTH:
- The case where the code IS generated (e.g., struct uses `LuaInterop.Eq`)
- The case where the code is NOT generated (e.g., struct without equality usage)

## Use the correct assertion

- `Assert.Equal(expected, lua)` — for full output comparison when the output is small and stable
- `Assert.Contains(substring, lua)` — for checking specific functions or patterns exist
- `Assert.DoesNotContain(substring, lua)` — for verifying code is NOT emitted
- `Assert.Matches(regex, lua)` — for patterns with generated names (numeric suffixes)

## Test on real code when possible

For features that affect the user's project (like struct equality emission), verify against the actual project output at `C:\Users\yatyr\workspace\lua-maps\LuaProject\Main.lua` in addition to isolated tests.

## Every bug fix must have a regression test

When fixing a bug, you MUST add a test that reproduces the bug and verifies the fix. The test should:
1. Fail before the fix (reproduces the bug)
2. Pass after the fix (verifies correctness)
3. Remain in the test suite permanently (prevents regression)

Do not ship a bug fix without a corresponding test. This is non-negotiable.
