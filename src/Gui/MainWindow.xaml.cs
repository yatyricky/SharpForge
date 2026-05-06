using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfControl = System.Windows.Controls.Control;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace SharpForge.Gui;

public sealed partial class MainWindow : Window
{
    private const int DwmWindowAttributeSystemBackdropType = 38;
    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmBackdropTypeMica = 2;
    private const int DwmCornerPreferenceRound = 2;
    private const string RunMarkerPrefix = "__SHARPFORGE_RUN_";
    private const string RunMarkerBeginTranspiler = RunMarkerPrefix + "BEGIN_TRANSPILER__";
    private const string RunMarkerEndTranspiler = RunMarkerPrefix + "END_TRANSPILER__";
    private const string RunMarkerBeginBuilder = RunMarkerPrefix + "BEGIN_BUILDER__";
    private const string RunMarkerEndBuilder = RunMarkerPrefix + "END_BUILDER__";
    private const string DefaultRootTable = "SF__";
    private const string DefaultIgnoreClass = "JASS";
    private const string DefaultLibraryFolder = "libs";
    private const string DefaultJassHostClass = "JASS";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex LuaIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly HashSet<WpfControl> _invalidControls = [];
    private readonly System.Text.StringBuilder _logBuffer = new();
    private bool _controlsReady;
    private bool _loadingSettings;
    private bool _updatingRunCommandPreview;

    public MainWindow()
    {
        InitializeComponent();
        _controlsReady = true;
        NavigationList.SelectedIndex = 0;
        _settings = AppSettings.Load();
        LoadSettingsIntoFields();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        TryApplyFluentWindowAttributes();
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        MapBuilderView.Visibility = NavigationList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        JassGenView.Visibility = NavigationList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = NavigationList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSettingsIntoFields()
    {
        _loadingSettings = true;
        try
        {
            WarcraftPathBox.Text = _settings.WarcraftPath;
            MapPathBox.Items.Clear();
            foreach (var map in _settings.Maps.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                MapPathBox.Items.Add(map);
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastMapPath))
            {
                MapPathBox.Text = _settings.LastMapPath;
                LoadMapSettings();
            }
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateRunCommandPreview();
    }

    private void LoadMapSettings()
    {
        var map = MapPathBox.Text.Trim();
        if (map.Length == 0 || !_settings.Maps.TryGetValue(map, out var settings))
        {
            ResetMapBuilderFields();
            return;
        }

        _loadingSettings = true;
        try
        {
            CSharpPathBox.Text = settings.CSharpPath;
            TranspileOutputBox.Text = settings.TranspileOutput;
            DefinesBox.Text = settings.Defines;
            RootTableBox.Text = string.IsNullOrWhiteSpace(settings.RootTable) ? DefaultRootTable : settings.RootTable;
            IgnoreClassBox.Text = string.IsNullOrWhiteSpace(settings.IgnoreClass) ? DefaultIgnoreClass : settings.IgnoreClass;
            LibraryFolderBox.Text = string.IsNullOrWhiteSpace(settings.LibraryFolder) ? DefaultLibraryFolder : settings.LibraryFolder;
            EnableTranspilerBox.IsChecked = settings.RunTranspiler;
            MainLuaBox.Text = settings.MainLua;
            BuilderOutputBox.Text = settings.BuilderOutput;
            IncludeBox.Text = settings.Include;
            EnableBuilderBox.IsChecked = settings.RunBuilder;
            JassInputBox.Text = settings.JassInput;
            JassOutputBox.Text = settings.JassOutput;
            JassHostClassBox.Text = string.IsNullOrWhiteSpace(settings.JassHostClass) ? DefaultJassHostClass : settings.JassHostClass;
            FillMissingDefaults();
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateRunCommandPreview();
    }

    private void ResetMapBuilderFields()
    {
        _loadingSettings = true;
        try
        {
            CSharpPathBox.Clear();
            TranspileOutputBox.Clear();
            DefinesBox.Clear();
            RootTableBox.Text = DefaultRootTable;
            IgnoreClassBox.Text = DefaultIgnoreClass;
            LibraryFolderBox.Text = DefaultLibraryFolder;
            EnableTranspilerBox.IsChecked = true;
            MainLuaBox.Clear();
            BuilderOutputBox.Clear();
            IncludeBox.Clear();
            EnableBuilderBox.IsChecked = true;
            FillDefaultsFromMap();
        }
        finally
        {
            _loadingSettings = false;
        }

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
        if (string.IsNullOrWhiteSpace(BuilderOutputBox.Text) && !string.IsNullOrWhiteSpace(MapPathBox.Text))
        {
            BuilderOutputBox.Text = MapPathBox.Text.Trim();
        }
    }

    private void FillDefaultsFromCSharpPath()
    {
        var csharpPath = CSharpPathBox.Text.Trim();
        if (csharpPath.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TranspileOutputBox.Text))
        {
            TranspileOutputBox.Text = Path.Combine(csharpPath, "sharpforge.lua");
        }
    }

    private void FillDefaultsFromTranspileOutput()
    {
        if (string.IsNullOrWhiteSpace(MainLuaBox.Text) && !string.IsNullOrWhiteSpace(TranspileOutputBox.Text))
        {
            MainLuaBox.Text = TranspileOutputBox.Text.Trim();
        }
    }

    private void SaveSettingsFromFields()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.WarcraftPath = WarcraftPathBox.Text.Trim();
        var map = MapPathBox.Text.Trim();
        if (map.Length > 0)
        {
            _settings.LastMapPath = map;
            _settings.Maps[map] = new MapSettings
            {
                CSharpPath = CSharpPathBox.Text.Trim(),
                TranspileOutput = TranspileOutputBox.Text.Trim(),
                Defines = DefinesBox.Text.Trim(),
                RootTable = RootTableBox.Text.Trim(),
                IgnoreClass = IgnoreClassBox.Text.Trim(),
                LibraryFolder = LibraryFolderBox.Text.Trim(),
                RunTranspiler = EnableTranspilerBox.IsChecked == true,
                MainLua = MainLuaBox.Text.Trim(),
                BuilderOutput = BuilderOutputBox.Text.Trim(),
                Include = IncludeBox.Text.Trim(),
                RunBuilder = EnableBuilderBox.IsChecked == true,
                JassInput = JassInputBox.Text.Trim(),
                JassOutput = JassOutputBox.Text.Trim(),
                JassHostClass = JassHostClassBox.Text.Trim(),
            };
            if (!MapPathBox.Items.Cast<string>().Contains(map, StringComparer.OrdinalIgnoreCase))
            {
                MapPathBox.Items.Add(map);
            }
        }
        _settings.Save();
    }

    private async void RunTranspileInit(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, CSharpPathBox, "CSharp project path is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        _logBuffer.Clear();
        await RunTranspilerStepAsync([CSharpPathBox.Text.Trim(), "--init"]);
        ShowLogDialog();
    }

    private async void RunTranspileCheck(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateTranspilerInputs())
        {
            return;
        }

        _logBuffer.Clear();
        await RunTranspilerStepAsync(BuildTranspileArguments(checkOnly: true));
        ShowLogDialog();
    }

    private async void RunTranspile(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateTranspilerInputs())
        {
            return;
        }

        _logBuffer.Clear();
        await RunTranspilerStepAsync(BuildTranspileArguments(checkOnly: false));
        ShowLogDialog();
    }

    private async void RunBuild(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, MainLuaBox, "Main Lua file is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        _logBuffer.Clear();
        await RunBuilderStepAsync();
        ShowLogDialog();
    }

    private async void RunJassGen(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, JassInputBox, "JASS source folder is required.");
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        var args = new List<string> { JassInputBox.Text.Trim() };
        AddOptional(args, "-o", JassOutputBox.Text.Trim());
        AddOptional(args, "--host-class", JassHostClassBox.Text.Trim());
        _logBuffer.Clear();
        await RunToolAsync("sf-jassgen", args.ToArray());
        ShowLogDialog();
    }

    private async void RunWarcraft(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        if (!ValidateRunCommands())
        {
            return;
        }

        _logBuffer.Clear();
        await RunPreviewCommandsAsync();
        ShowLogDialog();
    }

    private void RunWarcraftReplay(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromFields();
        ClearValidationErrors();
        var exe = WarcraftExePath();
        var messages = new List<string>();
        AddRequiredTextError(messages, WarcraftPathBox, "Warcraft installation path is required.");
        if (!File.Exists(exe))
        {
            AddValidationError(messages, WarcraftPathBox, "Warcraft III.exe was not found under the configured installation path.");
        }
        if (!ShowValidationErrors(messages))
        {
            return;
        }

        StartDetached(exe, ["-launch", "-window"]);
    }

    private string[] BuildTranspileArguments(bool checkOnly)
    {
        var args = new List<string> { CSharpPathBox.Text.Trim() };
        if (checkOnly)
        {
            args.Add("--check");
        }
        else
        {
            AddOptional(args, "-o", TranspileOutputBox.Text.Trim());
        }
        AddOptional(args, "--root-table", RootTableBox.Text.Trim());
        AddSplitOptions(args, "--define", DefinesBox.Text);
        AddSplitOptions(args, "--ignore-class", IgnoreClassBox.Text);
        AddSplitOptions(args, "--library-folder", LibraryFolderBox.Text);
        return args.ToArray();
    }

    private string[] BuildBuildArguments(string mainLua, string output)
    {
        var args = new List<string> { mainLua };
        AddOptional(args, "-o", output);
        AddOptional(args, "--include", IncludeBox.Text.Trim());
        return args.ToArray();
    }

    private bool ValidateRunCommands()
    {
        ClearValidationErrors();
        var messages = new List<string>();
        AddRequiredTextError(messages, RunCommandsBox, "At least one Run command is required.");

        return ShowValidationErrors(messages);
    }

    private Task<int> RunTranspilerStepAsync(string[] args)
        => RunCardStepAsync(
            "sf-transpile",
            args,
            TranspilerBusyOverlay,
            TranspilerProgress,
            TranspilerStatusText,
            "Executing transpiler...");

    private Task<int> RunBuilderStepAsync()
    {
        var args = new List<string> { MainLuaBox.Text.Trim() };
        AddOptional(args, "-o", BuilderOutputBox.Text.Trim());
        AddOptional(args, "--include", IncludeBox.Text.Trim());

        return RunCardStepAsync(
            "sf-build",
            args.ToArray(),
            BuilderBusyOverlay,
            BuilderProgress,
            BuilderStatusText,
            "Executing builder...");
    }

    private async Task<int> RunCardStepAsync(string toolName, string[] args, UIElement overlay, UIElement progress, TextBlock statusText, string runningText)
    {
        progress.Visibility = Visibility.Visible;
        statusText.Text = runningText;
        statusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        overlay.Visibility = Visibility.Visible;

        var exitCode = await RunToolAsync(toolName, args);
        if (exitCode == 0)
        {
            progress.Visibility = Visibility.Collapsed;
            statusText.Text = "✓ Done";
            statusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            await Task.Delay(700);
        }

        overlay.Visibility = Visibility.Collapsed;
        return exitCode;
    }

    private async Task<int> RunPreviewCommandsAsync()
    {
        var shellLines = new List<string>();
        foreach (var line in RunCommandLines())
        {
            if (TryParseCommandLine(line, out var executable, out var args, out _) && IsWarcraftCommand(executable))
            {
                var exitCode = await RunShellLinesAsync(shellLines);
                if (exitCode != 0)
                {
                    return exitCode;
                }

                shellLines.Clear();
                StartDetached(executable, args);
                continue;
            }

            shellLines.Add(ToShellRunLine(line));
        }

        return await RunShellLinesAsync(shellLines);
    }

    private async Task<int> RunShellLinesAsync(IReadOnlyList<string> shellLines)
    {
        if (shellLines.Count == 0)
        {
            return 0;
        }

        return await RunShellChainAsync(string.Join(" && ", shellLines));
    }

    private async Task<int> RunShellChainAsync(string command)
    {
        var exe = CommandShellPath();
        var args = new[] { "/D", "/C", command };
        Log($"> {Quote(exe)} {string.Join(' ', args.Select(Quote))}");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
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
            var stdout = ReadRunShellOutputAsync(process.StandardOutput);
            var stderr = ReadProcessOutputAsync(process.StandardError);
            await process.WaitForExitAsync();
            await stdout;
            await stderr;
            ResetRunCardOverlays();
            Log($"Exit code: {process.ExitCode}");
            return process.ExitCode;
        }
    }

    private async Task ReadRunShellOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (ApplyRunMarker(line.Trim()))
            {
                continue;
            }

            Log(line);
        }
    }

    private async Task ReadProcessOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Log(line);
        }
    }

    private bool ApplyRunMarker(string line)
    {
        switch (line)
        {
            case RunMarkerBeginTranspiler:
                ShowRunCardOverlay(TranspilerBusyOverlay, TranspilerProgress, TranspilerStatusText, "Executing transpiler...");
                return true;
            case RunMarkerEndTranspiler:
                HideRunCardOverlay(TranspilerBusyOverlay);
                return true;
            case RunMarkerBeginBuilder:
                ShowRunCardOverlay(BuilderBusyOverlay, BuilderProgress, BuilderStatusText, "Executing builder...");
                return true;
            case RunMarkerEndBuilder:
                HideRunCardOverlay(BuilderBusyOverlay);
                return true;
            default:
                return false;
        }
    }

    private void ShowRunCardOverlay(UIElement overlay, UIElement progress, TextBlock statusText, string runningText)
    {
        progress.Visibility = Visibility.Visible;
        statusText.Text = runningText;
        statusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        overlay.Visibility = Visibility.Visible;
    }

    private static void HideRunCardOverlay(UIElement overlay)
    {
        overlay.Visibility = Visibility.Collapsed;
    }

    private void ResetRunCardOverlays()
    {
        HideRunCardOverlay(TranspilerBusyOverlay);
        HideRunCardOverlay(BuilderBusyOverlay);
    }

    private IEnumerable<string> RunCommandLines()
        => RunCommandsBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    private bool IsWarcraftCommand(string executable)
        => string.Equals(NormalizePath(executable), NormalizePath(WarcraftExePath()), StringComparison.OrdinalIgnoreCase);

    private string ToShellRunLine(string line)
    {
        return TryParseCommandLine(line, out var executable, out _, out _)
            ? WrapToolRunLine(line, executable)
            : line;
    }

    private string WrapToolRunLine(string line, string executable)
    {
        if (IsToolCommand(executable, "sf-transpile"))
        {
            return $"echo {RunMarkerBeginTranspiler} && call {line} && echo {RunMarkerEndTranspiler}";
        }

        if (IsToolCommand(executable, "sf-build"))
        {
            return $"echo {RunMarkerBeginBuilder} && call {line} && echo {RunMarkerEndBuilder}";
        }

        return line;
    }

    private static bool IsToolCommand(string executable, string toolName)
    {
        var exeName = toolName + ".exe";
        return string.Equals(Path.GetFileName(executable), exeName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(executable), toolName, StringComparison.OrdinalIgnoreCase);
    }

    private static string CommandShellPath()
        => Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
    }

    private void UpdateRunCommandPreview()
    {
        if (!_controlsReady || _loadingSettings || _updatingRunCommandPreview)
        {
            return;
        }

        _updatingRunCommandPreview = true;
        try
        {
            var mainLua = RunMainLuaPath();
            var output = BuildOutputPath();
            var lines = new List<string>();

            if (EnableTranspilerBox.IsChecked == true)
            {
                lines.Add(FormatCommand(ResolveTool("sf-transpile"), BuildTranspileArguments(checkOnly: false)));
            }

            if (EnableBuilderBox.IsChecked == true)
            {
                lines.Add(FormatCommand(ResolveTool("sf-build"), BuildBuildArguments(mainLua, output)));
            }

            var launchMap = LaunchMapPath();
            if (!string.IsNullOrWhiteSpace(launchMap))
            {
                lines.Add(FormatCommand(WarcraftExePath(), ["-launch", "-window", "-loadfile", launchMap]));
            }

            RunCommandsBox.Text = string.Join(Environment.NewLine, lines);
        }
        finally
        {
            _updatingRunCommandPreview = false;
        }
    }

    private string BuildOutputPath()
    {
        var output = BuilderOutputBox.Text.Trim();
        return output.Length == 0 ? MapPathBox.Text.Trim() : output;
    }

    private string LaunchMapPath()
    {
        var map = MapPathBox.Text.Trim();
        if (EnableBuilderBox.IsChecked != true)
        {
            return map;
        }

        var output = BuildOutputPath();
        if (output.Length == 0)
        {
            return map;
        }

        if (IsW3xFileTarget(output))
        {
            return GetArchiveCopyPath(output);
        }

        return IsW3xMapPath(output) ? output : map;
    }

    private static bool IsW3xFileTarget(string path)
        => IsW3xMapPath(path) && !Directory.Exists(path);

    private static bool IsW3xMapPath(string path)
        => string.Equals(Path.GetExtension(path), ".w3x", StringComparison.OrdinalIgnoreCase);

    private static string GetArchiveCopyPath(string mapFile)
    {
        var directory = Path.GetDirectoryName(mapFile);
        var fileName = Path.GetFileNameWithoutExtension(mapFile);
        var extension = Path.GetExtension(mapFile);
        var copyName = fileName + ".sf-build" + extension;
        return string.IsNullOrEmpty(directory) ? copyName : Path.Combine(directory, copyName);
    }

    private string RunMainLuaPath()
    {
        var mainLua = MainLuaBox.Text.Trim();
        if (mainLua.Length > 0)
        {
            return mainLua;
        }

        var transpileOutput = TranspileOutputBox.Text.Trim();
        if (transpileOutput.Length > 0)
        {
            return transpileOutput;
        }

        var csharpPath = CSharpPathBox.Text.Trim();
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
        AddRequiredTextError(messages, CSharpPathBox, "CSharp project path is required.");
        if (!LuaIdentifier.IsMatch(RootTableBox.Text.Trim()))
        {
            AddValidationError(messages, RootTableBox, "Root table must be a valid Lua identifier.");
        }
    }

    private void ClearValidationErrors()
    {
        foreach (var control in _invalidControls)
        {
            control.ClearValue(BorderBrushProperty);
            control.ClearValue(ToolTipProperty);
        }
        _invalidControls.Clear();
    }

    private void AddRequiredTextError(List<string> messages, WpfControl control, string message)
    {
        var value = control switch
        {
            WpfTextBox textBox => textBox.Text,
            WpfComboBox comboBox => comboBox.Text,
            _ => string.Empty,
        };
        if (value.Trim().Length == 0)
        {
            AddValidationError(messages, control, message);
        }
    }

    private void AddValidationError(List<string> messages, WpfControl control, string message)
    {
        if (!_invalidControls.Contains(control))
        {
            control.SetResourceReference(BorderBrushProperty, "DangerBrush");
            control.ToolTip = message;
            _invalidControls.Add(control);
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
            if (!_invalidControls.Contains(control))
            {
                continue;
            }
            NavigationList.SelectedIndex = control == JassInputBox ? 1 : control == WarcraftPathBox ? 2 : 0;
            control.Focus();
            return;
        }
    }

    private IEnumerable<WpfControl> RequiredControls()
    {
        yield return WarcraftPathBox;
        yield return MapPathBox;
        yield return CSharpPathBox;
        yield return RootTableBox;
        yield return MainLuaBox;
        yield return JassInputBox;
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
        => Path.Combine(WarcraftPathBox.Text.Trim(), "_retail_", "x86_64", "Warcraft III.exe");

    private void SelectWarcraftPath(object sender, RoutedEventArgs e)
    {
        if (SelectFolder(WarcraftPathBox.Text, out var path))
        {
            WarcraftPathBox.Text = path;
            SaveSettingsFromFields();
        }
    }

    private void SelectMapPath(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog { Filter = "Warcraft maps (*.w3x)|*.w3x|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        MapPathBox.Text = dialog.FileName;
        ResetMapBuilderFields();
        SaveSettingsFromFields();
    }

    private void SelectMapFolder(object sender, RoutedEventArgs e)
    {
        if (SelectFolder(MapPathBox.Text, out var path))
        {
            MapPathBox.Text = path;
            ResetMapBuilderFields();
            SaveSettingsFromFields();
        }
    }

    private void SelectCSharpPath(object sender, RoutedEventArgs e)
    {
        if (SelectFolder(CSharpPathBox.Text, out var path))
        {
            CSharpPathBox.Text = path;
            FillDefaultsFromCSharpPath();
            FillDefaultsFromTranspileOutput();
            SaveSettingsFromFields();
        }
    }

    private void SelectTranspileOutput(object sender, RoutedEventArgs e)
    {
        SelectSavePath(TranspileOutputBox, "Lua files (*.lua)|*.lua|All files (*.*)|*.*");
        FillDefaultsFromTranspileOutput();
    }

    private void SelectMainLua(object sender, RoutedEventArgs e) => SelectOpenPath(MainLuaBox, "Lua files (*.lua)|*.lua|All files (*.*)|*.*");

    private void SelectBuilderOutput(object sender, RoutedEventArgs e) => SelectSavePath(BuilderOutputBox, "Warcraft maps (*.w3x)|*.w3x|Lua files (*.lua)|*.lua|All files (*.*)|*.*");

    private void SelectJassInput(object sender, RoutedEventArgs e)
    {
        if (SelectFolder(JassInputBox.Text, out var path))
        {
            JassInputBox.Text = path;
            SaveSettingsFromFields();
        }
    }

    private void SelectJassOutput(object sender, RoutedEventArgs e)
    {
        if (SelectFolder(JassOutputBox.Text, out var path))
        {
            JassOutputBox.Text = path;
            SaveSettingsFromFields();
        }
    }

    private void SelectIncludes(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Filter = "Lua files (*.lua)|*.lua|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            IncludeBox.Text = string.Join(';', dialog.FileNames);
            SaveSettingsFromFields();
        }
    }

    private void SelectOpenPath(WpfTextBox target, string filter)
    {
        var dialog = new WpfOpenFileDialog { Filter = filter, FileName = target.Text };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
            SaveSettingsFromFields();
        }
    }

    private void SelectSavePath(WpfTextBox target, string filter)
    {
        var dialog = new WpfSaveFileDialog { Filter = filter, FileName = target.Text };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
            SaveSettingsFromFields();
        }
    }

    private bool SelectFolder(string initialPath, out string selectedPath)
    {
        var dialog = new WpfOpenFolderDialog
        {
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
        };
        if (dialog.ShowDialog(this) == true)
        {
            selectedPath = dialog.FolderName;
            return true;
        }
        selectedPath = string.Empty;
        return false;
    }

    private void OpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var path = Path.GetDirectoryName(AppSettings.SettingsPath())!;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AnyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        if (sender is WpfControl control)
        {
            ClearValidationError(control);
        }
        UpdateRunCommandPreview();
    }

    private void CSharpPathChanged(object sender, TextChangedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        ClearValidationError(CSharpPathBox);
        FillDefaultsFromCSharpPath();
        UpdateRunCommandPreview();
    }

    private void TranspileOutputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        ClearValidationError(TranspileOutputBox);
        FillDefaultsFromTranspileOutput();
        UpdateRunCommandPreview();
    }

    private void RunCommandsChanged(object sender, TextChangedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        if (!_updatingRunCommandPreview)
        {
            ClearValidationError(RunCommandsBox);
        }
    }

    private void PipelineOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        SaveSettingsFromFields();
        UpdateRunCommandPreview();
    }

    private void MapPathChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlsReady && !_loadingSettings)
        {
            LoadMapSettings();
        }
    }

    private void MapPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        LoadMapSettings();
        SaveSettingsFromFields();
    }

    private void MapPathKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        ClearValidationError(MapPathBox);
        FillDefaultsFromMap();
        UpdateRunCommandPreview();
    }

    private void AnyFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady)
        {
            return;
        }

        SaveSettingsFromFields();
    }

    private void ClearValidationError(WpfControl control)
    {
        if (!_invalidControls.Remove(control))
        {
            return;
        }

        control.ClearValue(BorderBrushProperty);
        control.ClearValue(ToolTipProperty);
    }

    private void Log(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var line = text.TrimEnd() + Environment.NewLine;
        _logBuffer.Append(line);
    }

    private void ShowLogDialog()
    {
        var textBox = new WpfTextBox
        {
            Text = _logBuffer.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MinWidth = 720,
            MinHeight = 420,
        };

        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 88,
        };

        var panel = new DockPanel { Margin = new Thickness(16) };
        closeButton.Click += (_, _) => Window.GetWindow(closeButton)?.Close();
        DockPanel.SetDock(closeButton, Dock.Bottom);
        panel.Children.Add(closeButton);
        panel.Children.Add(textBox);

        var dialog = new Window
        {
            Title = "SharpForge Log",
            Owner = this,
            Content = panel,
            Width = 780,
            Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.ShowDialog();
    }

    private void EditTranspiler(object sender, RoutedEventArgs e)
    {
        if (ShowSettingsDialog(
            "Transpiler Settings",
            [
                DialogField.Folder("C# project path", CSharpPathBox),
                DialogField.SaveFile("Output Lua", TranspileOutputBox, "Lua files (*.lua)|*.lua|All files (*.*)|*.*"),
                DialogField.Text("Defines", DefinesBox),
                DialogField.Text("Root table", RootTableBox),
                DialogField.Text("Ignore class", IgnoreClassBox),
                DialogField.Text("Library folder", LibraryFolderBox),
            ]))
        {
            FillDefaultsFromCSharpPath();
            FillDefaultsFromTranspileOutput();
            SaveSettingsFromFields();
            UpdateRunCommandPreview();
        }
    }

    private void EditBuilder(object sender, RoutedEventArgs e)
    {
        if (ShowSettingsDialog(
            "Builder Settings",
            [
                DialogField.OpenFile("Main Lua file", MainLuaBox, "Lua files (*.lua)|*.lua|All files (*.*)|*.*"),
                DialogField.SaveFile("Output", BuilderOutputBox, "Warcraft maps (*.w3x)|*.w3x|Lua files (*.lua)|*.lua|All files (*.*)|*.*"),
                DialogField.MultiOpenFile("Includes", IncludeBox, "Lua files (*.lua)|*.lua|All files (*.*)|*.*"),
            ]))
        {
            SaveSettingsFromFields();
            UpdateRunCommandPreview();
        }
    }

    private void EditJassGen(object sender, RoutedEventArgs e)
    {
        if (ShowSettingsDialog(
            "JASS Bindings Settings",
            [
                DialogField.Folder("JASS source folder", JassInputBox),
                DialogField.Folder("Output folder", JassOutputBox),
                DialogField.Text("Host class", JassHostClassBox),
            ]))
        {
            SaveSettingsFromFields();
        }
    }

    private bool ShowSettingsDialog(string title, IReadOnlyList<DialogField> fields)
    {
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var editors = new List<(DialogField Field, WpfTextBox Editor)>();
        for (var index = 0; index < fields.Count; index++)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var field = fields[index];
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 12) };
            var editor = new WpfTextBox { Text = field.Target.Text, Margin = new Thickness(0, 0, 8, 12), MinWidth = 420 };
            Grid.SetRow(label, index);
            Grid.SetRow(editor, index);
            Grid.SetColumn(editor, 1);
            form.Children.Add(label);
            form.Children.Add(editor);
            editors.Add((field, editor));

            if (field.BrowseKind != BrowseKind.None)
            {
                var browse = new Button { Content = "Browse", Margin = new Thickness(0, 0, 0, 12), MinWidth = 88 };
                browse.Click += (_, _) => BrowseDialogField(field, editor);
                Grid.SetRow(browse, index);
                Grid.SetColumn(browse, 2);
                form.Children.Add(browse);
            }
        }

        var saveButton = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), MinWidth = 88, Margin = new Thickness(0, 0, 0, 0) };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(saveButton);
        actions.Children.Add(cancelButton);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(form);

        var saved = false;
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Content = root,
            Width = 760,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        saveButton.Click += (_, _) =>
        {
            foreach (var (field, editor) in editors)
            {
                field.Target.Text = editor.Text.Trim();
            }
            saved = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.ShowDialog();
        return saved;
    }

    private void BrowseDialogField(DialogField field, WpfTextBox editor)
    {
        switch (field.BrowseKind)
        {
            case BrowseKind.Folder:
                if (SelectFolder(editor.Text, out var folder))
                {
                    editor.Text = folder;
                }
                break;
            case BrowseKind.OpenFile:
                SelectOpenPath(editor, field.Filter);
                break;
            case BrowseKind.SaveFile:
                SelectSavePath(editor, field.Filter);
                break;
            case BrowseKind.MultiOpenFile:
                var dialog = new WpfOpenFileDialog { Filter = field.Filter, Multiselect = true };
                if (dialog.ShowDialog(this) == true)
                {
                    editor.Text = string.Join(';', dialog.FileNames);
                }
                break;
        }
    }

    private void ShowInputError(string message)
    {
        Log(message);
        WpfMessageBox.Show(this, message, "SharpForge", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void TryApplyFluentWindowAttributes()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var mica = DwmBackdropTypeMica;
            var lightMode = 0;
            var rounded = DwmCornerPreferenceRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowAttributeSystemBackdropType, ref mica, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmWindowAttributeUseImmersiveDarkMode, ref lightMode, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmWindowAttributeWindowCornerPreference, ref rounded, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private enum BrowseKind
    {
        None,
        Folder,
        OpenFile,
        SaveFile,
        MultiOpenFile,
    }

    private sealed record DialogField(string Label, WpfTextBox Target, BrowseKind BrowseKind, string Filter)
    {
        public static DialogField Text(string label, WpfTextBox target)
            => new(label, target, BrowseKind.None, string.Empty);

        public static DialogField Folder(string label, WpfTextBox target)
            => new(label, target, BrowseKind.Folder, string.Empty);

        public static DialogField OpenFile(string label, WpfTextBox target, string filter)
            => new(label, target, BrowseKind.OpenFile, filter);

        public static DialogField SaveFile(string label, WpfTextBox target, string filter)
            => new(label, target, BrowseKind.SaveFile, filter);

        public static DialogField MultiOpenFile(string label, WpfTextBox target, string filter)
            => new(label, target, BrowseKind.MultiOpenFile, filter);
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

        public static string SettingsPath()
            => Path.Combine(AppContext.BaseDirectory, "gui-settings.json");
    }

    private sealed class MapSettings
    {
        public string CSharpPath { get; set; } = string.Empty;
        public string TranspileOutput { get; set; } = string.Empty;
        public string Defines { get; set; } = string.Empty;
        public string RootTable { get; set; } = DefaultRootTable;
        public string IgnoreClass { get; set; } = DefaultIgnoreClass;
        public string LibraryFolder { get; set; } = DefaultLibraryFolder;
        public bool RunTranspiler { get; set; } = true;
        public string MainLua { get; set; } = string.Empty;
        public string BuilderOutput { get; set; } = string.Empty;
        public string Include { get; set; } = string.Empty;
        public bool RunBuilder { get; set; } = true;
        public string JassInput { get; set; } = string.Empty;
        public string JassOutput { get; set; } = string.Empty;
        public string JassHostClass { get; set; } = DefaultJassHostClass;
    }
}