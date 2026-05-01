A single page GUI app that basically runs Builder, JassGen, Transpiler, and Warcraft3 commands.
User data is saved as `gui-settings.json` next to the app executable.

The UI has top-level tabs:

--- Map Builder tab ---

--- Project Info ---
Warcraft installation path  : [C:\Users\UserName\Games\Warcraft III] 【Select】
Map                         : [tkok.w3x]▼ 【Open】【Folder】 // supports .w3x files and folder-format maps
--- Transpiler ---
CSharp Project path         : [C:\Users\UserName\workspace\tkok\src\csharp] 【Select】 // remembers with Map
【Init】【Check】
Output lua bundle path      : [C:\Users\UserName\workspace\tkok\src\csharp\sharpforge.lua] 【Select】 // remembers with Map, when CSharp Project path is defined the first time, this field is initialized as <CsprojPath>/sharpforge.lua
Define Preprocessor symbols : [] // text input, split by ; or ,
Root table                  : [SF__] // regex check with standard lua identifier
Ignore class                : [JASS] // text input, split by ; or ,
Library folder              : [libs]
// Transpiler optional fields are collapsed by default.
--- Builder ---
Main Lua file               : [path] // defaults to Transpiler.Output_lua_bundle_path when it's set the first time.
Output                      : [path] // defaults to Map path
Include                     : [paths] // 
// Builder optional fields are collapsed by default.
--- Run ---
Commands textarea           : [exact command lines] // editable, word-wrapped, regenerated on field changes
【Run】 // &"{Warcraft installation path}\_retail_\x86_64\Warcraft III.exe" -launch -window -loadfile {Full path of map}
【Replay】

--- JassGen tab ---
JASS source folder          : [path] 【Select】
Output folder               : [path] 【Select】
Host class                  : [JASS]
【Generate】

--- Shared Log ---
// The Log panel is shared across tabs.

Note:
【Select】 pulls up system file selector or accepts user input in the prior input field.
Selecting a new map or folder-map clears Transpiler and Builder fields, then initializes defaults from the selected map.
Each field has a visible `ⓘ` help marker with a tooltip.
Required fields are checked before actions run and marked with the framework validation indicator.
The first launch window is tall enough to show the Run button and log area, with excess height assigned to the command textarea. Each tab viewport scrolls when content is taller than the window.
Run shows the exact command lines it will execute. Manual edits are used by Run until another field change regenerates the preview.
