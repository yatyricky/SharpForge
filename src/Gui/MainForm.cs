using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpForge.Gui;

public sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex LuaIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 12000, InitialDelay = 250, ReshowDelay = 100, ShowAlways = true };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _mapBuilderTab = new("Map Builder");
    private readonly TabPage _jassGenTab = new("JassGen");
    private readonly TextBox _warcraftPath = TextBox();
    private readonly ComboBox _mapPath = new() { DropDownStyle = ComboBoxStyle.DropDown, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly TextBox _csharpPath = TextBox();
    private readonly TextBox _transpileOutput = TextBox();
    private readonly TextBox _defines = TextBox();
    private readonly TextBox _rootTable = TextBox("SF__");
    private readonly TextBox _ignoreClass = TextBox("JASS");
    private readonly TextBox _libraryFolder = TextBox("libs");
    private readonly TextBox _mainLua = TextBox();
    private readonly TextBox _builderOutput = TextBox();
    private readonly TextBox _include = TextBox();
    private readonly TextBox _jassInput = TextBox();
    private readonly TextBox _jassOutput = TextBox();
    private readonly TextBox _jassHostClass = TextBox("JASS");
    private readonly TextBox _runCommands = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        AcceptsReturn = true,
        Height = 282,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        Dock = DockStyle.Fill,
    };

    public MainForm()
    {
        Text = "SharpForge";
        MinimumSize = new Size(880, 720);
        ClientSize = new Size(1040, 1180);
        StartPosition = FormStartPosition.CenterScreen;
        _errors.ContainerControl = this;

        _settings = AppSettings.Load();
        BuildLayout();
        LoadSettingsIntoFields();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        Controls.Add(root);

        root.Controls.Add(_tabs, 0, 0);
        _tabs.TabPages.Add(_mapBuilderTab);
        _tabs.TabPages.Add(_jassGenTab);

        var form = CreateForm(_mapBuilderTab);

        AddHeader(form, "Project Info");
        AddRow(form, "Warcraft installation path", _warcraftPath, Button("Select", SelectWarcraftPath), "Folder containing _retail_\\x86_64\\Warcraft III.exe.");
        AddRow(form, "Map", _mapPath, MapButtons(), "The .w3x file or folder-map that Builder updates and Warcraft launches.");

        AddHeader(form, "Transpiler");
        AddRow(form, "CSharp project path", _csharpPath, Button("Select", SelectCSharpPath), "Folder containing the C# sources to transpile. Settings are remembered per map.");
        AddButtonRow(form, Button("Init", RunTranspileInit), Button("Check", RunTranspileCheck), Button("Transpile", RunTranspile));
        AddRow(form, "Output lua bundle path", _transpileOutput, Button("Select", SelectTranspileOutput), "Lua file written by sf-transpile. Defaults to <CSharp project path>\\sharpforge.lua.");
        var transpilerOptions = AddCollapsibleSection(form, "Transpiler Options", expanded: false);
        AddRow(transpilerOptions, "Define preprocessor symbols", _defines, null, "Optional symbols passed as repeated --define values. Split with semicolons or commas.");
        AddRow(transpilerOptions, "Root table", _rootTable, null, "Top-level Lua table for generated C# code. Must be a valid Lua identifier.");
        AddRow(transpilerOptions, "Ignore class", _ignoreClass, null, "Class names treated as external and not emitted, split with semicolons or commas.");
        AddRow(transpilerOptions, "Library folder", _libraryFolder, null, "Folder names compiled for symbols but skipped during Lua lowering, split with semicolons or commas.");

        AddHeader(form, "Builder");
        AddRow(form, "Main Lua file", _mainLua, Button("Select", SelectMainLua), "Entry Lua file for sf-build. Defaults to the transpiler output when first available.");
        AddRow(form, "Output", _builderOutput, Button("Select", SelectBuilderOutput), "Build target. Defaults to the selected map path.");
        var builderOptions = AddCollapsibleSection(form, "Builder Options", expanded: false);
        AddRow(builderOptions, "Include", _include, Button("Select", SelectIncludes), "Extra Lua files for dynamic dependencies. Split with semicolons.");
        AddButtonRow(form, Button("Build", RunBuild));

        AddHeader(form, "Run");
        AddRow(form, "Commands", _runCommands, null, "Commands executed by Run, one per line. You can edit this box; any field change regenerates it.");
        AddButtonRow(form, Button("Run", RunWarcraft), Button("Replay", RunWarcraftReplay));

        var jassGen = CreateForm(_jassGenTab);
        AddHeader(jassGen, "JassGen");
        AddRow(jassGen, "JASS source folder", _jassInput, Button("Select", SelectJassInput), "Folder containing common.j and/or blizzard.j sources for sf-jassgen.");
        AddRow(jassGen, "Output folder", _jassOutput, Button("Select", SelectJassOutput), "Folder where generated C# JASS binding stubs are written.");
        AddRow(jassGen, "Host class", _jassHostClass, null, "Static partial class name that hosts generated JASS natives and globals.");
        AddButtonRow(jassGen, Button("Generate", RunJassGen));

        var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        logGroup.Controls.Add(_log);
        root.Controls.Add(logGroup, 0, 1);

        _mapPath.SelectedIndexChanged += (_, _) => LoadMapSettings();
        _mapPath.Leave += (_, _) => LoadMapSettings();
        foreach (var box in TextBoxes())
        {
            box.Leave += (_, _) => SaveSettingsFromFields();
            box.TextChanged += (_, _) =>
            {
                _errors.SetError(box, string.Empty);
                UpdateRunCommandPreview();
            };
        }
        _mapPath.TextChanged += (_, _) => FillDefaultsFromMap();
        _mapPath.TextChanged += (_, _) =>
        {
            _errors.SetError(_mapPath, string.Empty);
            UpdateRunCommandPreview();
        };
        _csharpPath.TextChanged += (_, _) => FillDefaultsFromCSharpPath();
        _transpileOutput.TextChanged += (_, _) => FillDefaultsFromTranspileOutput();
        _mapPath.Leave += (_, _) => SaveSettingsFromFields();
    }

    private static TableLayoutPanel CreateForm(Control parent)
    {
        var formViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        parent.Controls.Add(formViewport);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        formViewport.Controls.Add(form);
        return form;
    }

    private void LoadSettingsIntoFields()
    {
        _warcraftPath.Text = _settings.WarcraftPath;
        foreach (var map in _settings.Maps.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            _mapPath.Items.Add(map);
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastMapPath))
        {
            _mapPath.Text = _settings.LastMapPath;
            LoadMapSettings();
        }
        UpdateRunCommandPreview();
    }

    private void LoadMapSettings()
    {
        var map = _mapPath.Text.Trim();
        if (map.Length == 0 || !_settings.Maps.TryGetValue(map, out var settings))
        {
            ResetMapBuilderFields();
            return;
        }

        _csharpPath.Text = settings.CSharpPath;
        _transpileOutput.Text = settings.TranspileOutput;
        _defines.Text = settings.Defines;
        _rootTable.Text = string.IsNullOrWhiteSpace(settings.RootTable) ? "SF__" : settings.RootTable;
        _ignoreClass.Text = string.IsNullOrWhiteSpace(settings.IgnoreClass) ? "JASS" : settings.IgnoreClass;
        _libraryFolder.Text = string.IsNullOrWhiteSpace(settings.LibraryFolder) ? "libs" : settings.LibraryFolder;
        _mainLua.Text = settings.MainLua;
        _builderOutput.Text = settings.BuilderOutput;
        _include.Text = settings.Include;
        _jassInput.Text = settings.JassInput;
        _jassOutput.Text = settings.JassOutput;
        _jassHostClass.Text = string.IsNullOrWhiteSpace(settings.JassHostClass) ? "JASS" : settings.JassHostClass;
        FillMissingDefaults();
    }

    private void ResetMapBuilderFields()
    {
        _csharpPath.Clear();
        _transpileOutput.Clear();
        _defines.Clear();
        _rootTable.Text = "SF__";
        _ignoreClass.Text = "JASS";
        _libraryFolder.Text = "libs";
        _mainLua.Clear();
        _builderOutput.Clear();
        _include.Clear();
        FillDefaultsFromMap();
        UpdateRunCommandPreview();
    }

    private void FillMissingDefaults()
    {
        FillDefaultsFromMap();
        FillDefaultsFromCSharpPath();
        FillDefaultsFromTranspileOutput();
    }

    private void FillDefaultsFromMap()
    {
        if (string.IsNullOrWhiteSpace(_builderOutput.Text) && !string.IsNullOrWhiteSpace(_mapPath.Text))
        {
            _builderOutput.Text = _mapPath.Text.Trim();
        }
    }

    private void FillDefaultsFromCSharpPath()
    {
        var csharpPath = _csharpPath.Text.Trim();
        if (csharpPath.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_transpileOutput.Text))
        {
            _transpileOutput.Text = Path.Combine(csharpPath, "sharpforge.lua");
        }
    }

    private void FillDefaultsFromTranspileOutput()
    {
        if (string.IsNullOrWhiteSpace(_mainLua.Text) && !string.IsNullOrWhiteSpace(_transpileOutput.Text))
        {
            _mainLua.Text = _transpileOutput.Text.Trim();
        }
    }

    private void SaveSettingsFromFields()
    {
        _settings.WarcraftPath = _warcraftPath.Text.Trim();
        var map = _mapPath.Text.Trim();
        if (map.Length > 0)
        {
            _settings.LastMapPath = map;
            _settings.Maps[map] = new MapSettings
            {
                CSharpPath = _csharpPath.Text.Trim(),
                TranspileOutput = _transpileOutput.Text.Trim(),
                Defines = _defines.Text.Trim(),
                RootTable = _rootTable.Text.Trim(),
                IgnoreClass = _ignoreClass.Text.Trim(),
                LibraryFolder = _libraryFolder.Text.Trim(),
                MainLua = _mainLua.Text.Trim(),
                BuilderOutput = _builderOutput.Text.Trim(),
                Include = _include.Text.Trim(),
                JassInput = _jassInput.Text.Trim(),
                JassOutput = _jassOutput.Text.Trim(),
                JassHostClass = _jassHostClass.Text.Trim(),
            };
            if (!_mapPath.Items.Cast<string>().Contains(map, StringComparer.OrdinalIgnoreCase))
            {
                _mapPath.Items.Add(map);
            }
        }
        _settings.Save();
    }

    private async void RunTranspileInit(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, _csharpPath, "CSharp project path is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        await RunToolAsync("sf-transpile", _csharpPath.Text.Trim(), "--init");
    }

    private async void RunTranspileCheck(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateTranspilerInputs())
        {
            return;
        }

        var args = BuildTranspileArguments(checkOnly: true);
        await RunToolAsync("sf-transpile", args);
    }

    private async void RunTranspile(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateTranspilerInputs())
        {
            return;
        }

        var args = BuildTranspileArguments(checkOnly: false);
        await RunToolAsync("sf-transpile", args);
    }

    private async void RunBuild(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        var mainLua = _mainLua.Text.Trim();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, _mainLua, "Main Lua file is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        var args = new List<string> { mainLua };
        AddOptional(args, "-o", _builderOutput.Text.Trim());
        AddOptional(args, "--include", _include.Text.Trim());
        await RunToolAsync("sf-build", args.ToArray());
    }

    private async void RunJassGen(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        var input = _jassInput.Text.Trim();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, _jassInput, "JASS source folder is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        var args = new List<string> { input };
        AddOptional(args, "-o", _jassOutput.Text.Trim());
        AddOptional(args, "--host-class", _jassHostClass.Text.Trim());
        await RunToolAsync("sf-jassgen", args.ToArray());
    }

    private async void RunWarcraft(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateRunInputs())
        {
            return;
        }

        var commands = _runCommands.Lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (commands.Length == 0)
        {
            ShowInputError("Run commands are required.");
            return;
        }

        for (var index = 0; index < commands.Length; index++)
        {
            if (!TryParseCommandLine(commands[index], out var executable, out var args, out var error))
            {
                ShowInputError(error);
                return;
            }

            if (index == commands.Length - 1)
            {
                StartDetached(executable, args);
                return;
            }

            var exitCode = await RunProcessAsync(executable, args);
            if (exitCode != 0)
            {
                Log($"Run stopped because command {index + 1} failed.");
                return;
            }
        }
    }

    private void RunWarcraftReplay(object? sender, EventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var exe = WarcraftExePath();
        var messages = new List<string>();
        AddRequiredTextError(messages, _warcraftPath, "Warcraft installation path is required.");
        if (!File.Exists(exe))
        {
            AddValidationError(messages, _warcraftPath, "Warcraft III.exe was not found under the configured installation path.");
        }
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        StartDetached(exe, ["-launch", "-window"]);
    }

    private string[] BuildTranspileArguments(bool checkOnly)
    {
        var args = new List<string> { _csharpPath.Text.Trim() };
        if (checkOnly)
        {
            args.Add("--check");
        }
        else
        {
            AddOptional(args, "-o", _transpileOutput.Text.Trim());
        }
        AddOptional(args, "--root-table", _rootTable.Text.Trim());
        AddSplitOptions(args, "--define", _defines.Text);
        AddSplitOptions(args, "--ignore-class", _ignoreClass.Text);
        AddSplitOptions(args, "--library-folder", _libraryFolder.Text);
        return args.ToArray();
    }

    private string[] BuildBuildArguments(string mainLua, string map)
    {
        var args = new List<string> { mainLua };
        AddOptional(args, "-o", map);
        AddOptional(args, "--include", _include.Text.Trim());
        return args.ToArray();
    }

    private bool ValidateRunInputs()
    {
        var exe = WarcraftExePath();
        var map = _mapPath.Text.Trim();
        ClearValidationErrors();
        var messages = new List<string>();

        AddRequiredTextError(messages, _warcraftPath, "Warcraft installation path is required.");
        if (!File.Exists(exe))
        {
            AddValidationError(messages, _warcraftPath, "Warcraft III.exe was not found under the configured installation path.");
        }
        AddRequiredTextError(messages, _mapPath, "Map path is required.");
        if (!File.Exists(map) && !Directory.Exists(map))
        {
            AddValidationError(messages, _mapPath, "Map file or folder was not found.");
        }
        AddTranspilerValidationErrors(messages);

        return ShowValidationErrors(messages);
    }

    private void UpdateRunCommandPreview()
    {
        var mainLua = RunMainLuaPath();
        var map = _mapPath.Text.Trim();
        var lines = new[]
        {
            FormatCommand(ResolveTool("sf-transpile"), BuildTranspileArguments(checkOnly: false)),
            FormatCommand(ResolveTool("sf-build"), BuildBuildArguments(mainLua, map)),
            FormatCommand(WarcraftExePath(), ["-launch", "-window", "-loadfile", map]),
        };
        _runCommands.Text = string.Join(Environment.NewLine, lines);
    }

    private string RunMainLuaPath()
    {
        var mainLua = _mainLua.Text.Trim();
        if (mainLua.Length > 0)
        {
            return mainLua;
        }

        var transpileOutput = _transpileOutput.Text.Trim();
        if (transpileOutput.Length > 0)
        {
            return transpileOutput;
        }

        var csharpPath = _csharpPath.Text.Trim();
        return csharpPath.Length == 0 ? string.Empty : Path.Combine(csharpPath, "sharpforge.lua");
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> args)
        => string.Join(' ', new[] { executable }.Concat(args).Select(Quote));

    private bool ValidateTranspilerInputs()
    {
        ClearValidationErrors();
        var messages = new List<string>();
        AddTranspilerValidationErrors(messages);
        return ShowValidationErrors(messages);
    }

    private void AddTranspilerValidationErrors(List<string> messages)
    {
        AddRequiredTextError(messages, _csharpPath, "CSharp project path is required.");
        if (!LuaIdentifier.IsMatch(_rootTable.Text.Trim()))
        {
            AddValidationError(messages, _rootTable, "Root table must be a valid Lua identifier.");
        }
    }

    private void ClearValidationErrors()
    {
        _errors.Clear();
    }

    private void AddRequiredTextError(List<string> messages, Control control, string message)
    {
        if (control.Text.Trim().Length == 0)
        {
            AddValidationError(messages, control, message);
        }
    }

    private void AddValidationError(List<string> messages, Control control, string message)
    {
        if (_errors.GetError(control).Length == 0)
        {
            _errors.SetError(control, message);
        }
        messages.Add(message);
    }

    private bool ShowValidationErrors(List<string> messages)
    {
        if (messages.Count == 0)
        {
            return true;
        }

        FocusFirstInvalidControl();
        ShowInputError(string.Join(Environment.NewLine, messages.Distinct(StringComparer.Ordinal)));
        return false;
    }

    private void FocusFirstInvalidControl()
    {
        foreach (var control in RequiredControls())
        {
            if (_errors.GetError(control).Length == 0)
            {
                continue;
            }
            if (control == _jassInput)
            {
                _tabs.SelectedTab = _jassGenTab;
            }
            else
            {
                _tabs.SelectedTab = _mapBuilderTab;
            }
            control.Focus();
            return;
        }
    }

    private IEnumerable<Control> RequiredControls()
    {
        yield return _warcraftPath;
        yield return _mapPath;
        yield return _csharpPath;
        yield return _rootTable;
        yield return _mainLua;
        yield return _jassInput;
    }

    private async Task<int> RunToolAsync(string toolName, params string[] args)
    {
        var exe = ResolveTool(toolName);
        if (!File.Exists(exe))
        {
            ShowInputError($"Could not find {toolName}. Build the solution first.");
            return 1;
        }

        return await RunProcessAsync(exe, args);
    }

    private async Task<int> RunProcessAsync(string exe, IReadOnlyList<string> args)
    {
        Log($"> {Quote(exe)} {string.Join(' ', args.Select(Quote))}");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args.Where(arg => !string.IsNullOrWhiteSpace(arg)))
        {
            start.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            Log(ex.Message);
            return 1;
        }
        if (process is null)
        {
            Log("Failed to start process.");
            return 1;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Log(await stdout);
            Log(await stderr);
            Log($"Exit code: {process.ExitCode}");
            return process.ExitCode;
        }
    }

    private static string ResolveTool(string toolName)
    {
        var exeName = toolName + ".exe";
        var sideBySide = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        var repo = FindRepoRoot();
        if (repo is null)
        {
            return sideBySide;
        }

        var project = toolName switch
        {
            "sf-transpile" => "Transpiler",
            "sf-build" => "Builder",
            "sf-jassgen" => "JassGen",
            _ => toolName,
        };
        return Path.Combine(repo, "src", project, "bin", "Debug", "net10.0", "win-x64", exeName);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SharpForge.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private string WarcraftExePath()
        => Path.Combine(_warcraftPath.Text.Trim(), "_retail_", "x86_64", "Warcraft III.exe");

    private void SelectWarcraftPath(object? sender, EventArgs e)
    {
        if (SelectFolder(_warcraftPath.Text, out var path))
        {
            _warcraftPath.Text = path;
            SaveSettingsFromFields();
        }
    }

    private void SelectMapPath(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Warcraft maps (*.w3x)|*.w3x|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _mapPath.Text = dialog.FileName;
        ResetMapBuilderFields();
        SaveSettingsFromFields();
    }

    private void SelectMapFolder(object? sender, EventArgs e)
    {
        if (SelectFolder(_mapPath.Text, out var path))
        {
            _mapPath.Text = path;
            ResetMapBuilderFields();
            SaveSettingsFromFields();
        }
    }

    private void SelectCSharpPath(object? sender, EventArgs e)
    {
        if (SelectFolder(_csharpPath.Text, out var path))
        {
            _csharpPath.Text = path;
            FillDefaultsFromCSharpPath();
            FillDefaultsFromTranspileOutput();
            SaveSettingsFromFields();
        }
    }

    private void SelectTranspileOutput(object? sender, EventArgs e)
    {
        SelectSavePath(_transpileOutput, "Lua files (*.lua)|*.lua|All files (*.*)|*.*");
        FillDefaultsFromTranspileOutput();
    }
    private void SelectMainLua(object? sender, EventArgs e) => SelectOpenPath(_mainLua, "Lua files (*.lua)|*.lua|All files (*.*)|*.*");
    private void SelectBuilderOutput(object? sender, EventArgs e) => SelectSavePath(_builderOutput, "Warcraft maps (*.w3x)|*.w3x|Lua files (*.lua)|*.lua|All files (*.*)|*.*");
    private void SelectJassInput(object? sender, EventArgs e) { if (SelectFolder(_jassInput.Text, out var path)) { _jassInput.Text = path; SaveSettingsFromFields(); } }
    private void SelectJassOutput(object? sender, EventArgs e) { if (SelectFolder(_jassOutput.Text, out var path)) { _jassOutput.Text = path; SaveSettingsFromFields(); } }

    private void SelectIncludes(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Lua files (*.lua)|*.lua|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _include.Text = string.Join(';', dialog.FileNames);
            SaveSettingsFromFields();
        }
    }

    private void SelectOpenPath(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, FileName = target.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
            SaveSettingsFromFields();
        }
    }

    private void SelectSavePath(TextBox target, string filter)
    {
        using var dialog = new SaveFileDialog { Filter = filter, FileName = target.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
            SaveSettingsFromFields();
        }
    }

    private bool SelectFolder(string initialPath, out string selectedPath)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(initialPath) ? initialPath : string.Empty };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            selectedPath = dialog.SelectedPath;
            return true;
        }
        selectedPath = string.Empty;
        return false;
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        button.Click += handler;
        return button;
    }

    private FlowLayoutPanel MapButtons()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        panel.Controls.Add(Button("Open", SelectMapPath));
        panel.Controls.Add(Button("Folder", SelectMapFolder));
        return panel;
    }

    private static TextBox TextBox(string text = "")
        => new() { Text = text, Anchor = AnchorStyles.Left | AnchorStyles.Right };

    private static void AddHeader(TableLayoutPanel table, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4),
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, table.RowCount);
        table.SetColumnSpan(label, 3);
        table.RowCount++;
    }

    private TableLayoutPanel AddCollapsibleSection(TableLayoutPanel table, string text, bool expanded)
    {
        var section = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(16, 0, 0, 0),
            Visible = expanded,
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var toggle = new Button
        {
            Text = CollapsibleTitle(text, expanded),
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 0, 4),
        };
        toggle.Click += (_, _) =>
        {
            section.Visible = !section.Visible;
            toggle.Text = CollapsibleTitle(text, section.Visible);
        };

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var toggleRow = table.RowCount;
        table.Controls.Add(toggle, 0, toggleRow);
        table.SetColumnSpan(toggle, 3);
        table.RowCount++;

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var sectionRow = table.RowCount;
        table.Controls.Add(section, 0, sectionRow);
        table.SetColumnSpan(section, 3);
        table.RowCount++;

        return section;
    }

    private static string CollapsibleTitle(string text, bool expanded)
        => (expanded ? "- " : "+ ") + text;

    private void AddRow(TableLayoutPanel table, string labelText, Control editor, Control? action, string helpText)
    {
        var label = new Label { Text = labelText + " ⓘ", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 4) };
        _toolTip.SetToolTip(label, helpText);
        _toolTip.SetToolTip(editor, helpText);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var row = table.RowCount;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(editor, 1, row);
        if (action is not null)
        {
            _toolTip.SetToolTip(action, helpText);
            table.Controls.Add(action, 2, row);
        }
        table.RowCount++;
    }

    private static void AddButtonRow(TableLayoutPanel table, params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill };
        panel.Controls.AddRange(buttons);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var row = table.RowCount;
        table.Controls.Add(panel, 1, row);
        table.SetColumnSpan(panel, 2);
        table.RowCount++;
    }

    private IEnumerable<TextBox> TextBoxes()
    {
        yield return _warcraftPath;
        yield return _csharpPath;
        yield return _transpileOutput;
        yield return _defines;
        yield return _rootTable;
        yield return _ignoreClass;
        yield return _libraryFolder;
        yield return _mainLua;
        yield return _builderOutput;
        yield return _include;
        yield return _jassInput;
        yield return _jassOutput;
        yield return _jassHostClass;
    }

    private void Log(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _log.AppendText(text.TrimEnd() + Environment.NewLine);
    }

    private void ShowInputError(string message)
    {
        Log(message);
        MessageBox.Show(this, message, "SharpForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void AddOptional(List<string> args, string option, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add(option);
            args.Add(value);
        }
    }

    private static void AddSplitOptions(List<string> args, string option, string value)
    {
        foreach (var item in Split(value))
        {
            args.Add(option);
            args.Add(item);
        }
    }

    private static string[] Split(string value)
        => value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Quote(string value)
        => value.Length == 0 || value.Any(char.IsWhiteSpace) ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"' : value;

    private static bool TryParseCommandLine(string commandLine, out string executable, out string[] args, out string error)
    {
        executable = string.Empty;
        args = [];
        error = string.Empty;

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var ch = commandLine[index];
            if (ch == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"');
                index++;
                continue;
            }
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (inQuotes)
        {
            error = "Run command has an unmatched quote.";
            return false;
        }
        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }
        if (parts.Count == 0)
        {
            error = "Run command is empty.";
            return false;
        }

        executable = parts[0];
        args = parts.Skip(1).ToArray();
        return true;
    }

    private void StartDetached(string exe, IReadOnlyList<string> args)
    {
        Log($"> {Quote(exe)} {string.Join(' ', args.Select(Quote))}");
        var start = new ProcessStartInfo(exe) { UseShellExecute = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        Process.Start(start);
    }

    private sealed class AppSettings
    {
        public string WarcraftPath { get; set; } = string.Empty;
        public string LastMapPath { get; set; } = string.Empty;
        public Dictionary<string, MapSettings> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static AppSettings Load()
        {
            var path = SettingsPath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        private static string SettingsPath()
            => Path.Combine(AppContext.BaseDirectory, "gui-settings.json");
    }

    private sealed class MapSettings
    {
        public string CSharpPath { get; set; } = string.Empty;
        public string TranspileOutput { get; set; } = string.Empty;
        public string Defines { get; set; } = string.Empty;
        public string RootTable { get; set; } = "SF__";
        public string IgnoreClass { get; set; } = "JASS";
        public string LibraryFolder { get; set; } = "libs";
        public string MainLua { get; set; } = string.Empty;
        public string BuilderOutput { get; set; } = string.Empty;
        public string Include { get; set; } = string.Empty;
        public string JassInput { get; set; } = string.Empty;
        public string JassOutput { get; set; } = string.Empty;
        public string JassHostClass { get; set; } = "JASS";
    }
}
