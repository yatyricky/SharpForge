# sf-transpile 新增 Jass bindings 版本选择 + GUI 下拉/卡片显示 + jassgen 开发工具提示

## 背景与根因(已确认)
- `TranspilePipeline.RunAsync:22` 每次转译无条件把 sf-transpile 嵌入的全部 `assets/libs/**` 资源解压到目标工程 `libs/`;现在 assets/libs 同时含 Jass-2.0.4 与 Jass-3.0.0(用户已自行更新,未入 git),两个版本一起落盘导致 CS0101 `handle` 重复定义。
- sf-transpile 无版本概念、无 jass 参数;GUI 的 JassInput/JassOutput 只喂给 sf-jassgen(仅 Generate 按钮触发,不在一键流程内)。
- GUI 为 WPF(sf-gui):MainWindow.xaml(262 行)+ MainWindow.xaml.cs(1539 行);Transpiler 卡片现有两行字段;Edit 为动态构建的模态弹窗(`ShowSettingsDialog`,目前只支持 TextBox);设置 POCO(`AppSettings`/`MapSettings`)内嵌在 MainWindow.xaml.cs。

## A. Transpiler:版本化部署

### 1. `--jass-version <VERSION>` CLI 选项(src/Transpiler/Cli/RootCommandFactory.cs + TranspileOptions.cs)
- `TranspileOptions` 新增 `string JassVersion`(默认空 = 自动选最新版本)。
- 版本比较用 `System.Version`(解析失败回退序号字符串比较),默认取嵌入基线中最高版本(当前为 3.0.0)。
- 指定版本不存在 → 报错并列出可用版本,exit 2。

### 2. `--list-jass` 查询选项(RootCommandFactory)
- 设置该选项时仅枚举嵌入资源 `SharpForge.Transpiler.Assets.libs/Jass-*` 顶级目录,每行输出版本号后退出 0(不跑 pipeline)。作为 GUI 下拉的单一事实来源。

### 3. LibraryAssetCopier 版本过滤 + 自愈清理(src/Transpiler/Pipeline/LibraryAssetCopier.cs)
- 从嵌入资源枚举可用 `Jass-*` 基线版本(供上面校验/列举复用)。
- 只部署所选版本的 `Jass-{v}/**`;其他 Jass 版本资源跳过;`SFLib`、`StdLib`、csproj 模板行为不变。
- **自愈清理**:部署后删除 `<input>/libs/` 下其他"已知基线版本"的 `Jass-*` 目录(仅限嵌入资源里存在的版本名,绝不动 `Jass-MyCustom` 之类未知目录),verbose 下输出日志。这会自动清掉 Zulaman-WE 里残留的 Jass-2.0.4。
- 方法签名传入所选版本与 verbose 日志开关。

## B. GUI(卡片第三行 + Edit 下拉 + 开发工具提示)

### 4. Transpiler 卡片第三行(MainWindow.xaml 67-110 区)
- 新增只读字段行:标签 "JASS bindings",值显示所选版本(空时显示解析到的默认,如 3.0.0)。

### 5. Edit 弹窗支持下拉(MainWindow.xaml.cs 1199-1316)
- `DialogField` 框架扩展一种 Choice 类型:渲染 ComboBox(可编辑,便于列表查询失败时手输),保存时把 SelectedItem/Text 写回 `JassVersionBox`。
- `EditTranspiler` 增加字段 "JASS bindings version",数据源调用新写的静默查询方法 `QueryToolAsync("sf-transpile", "--list-jass")`(捕获 stdout 返回字符串的 RunProcessAsync 变体;失败时下拉仍可手输)。
- 当前选中项 = JassVersionBox.Text(空则选列表第一项=最新)。

### 6. 设置与命令拼装(MainWindow.xaml.cs)
- `MapSettings` 新增 `JassVersion`(默认空;旧 gui-settings.json 缺字段自动落默认,无需迁移)。
- `SaveSettingsFromFields`/`LoadMapSettings`/`ResetMapBuilderFields` 四处接线(仿 JassHostClass 模式)。
- `BuildTranspileArguments`(382-398):JassVersion 非空时追加 `--jass-version <v>`。

### 7. JASS Bindings 页开发工具提示(MainWindow.xaml 198-238 区)
- 页头 Border 内加一条 MutedTextBrush 样式提示文字:"开发工具:生产环境无需使用,也无需设置(生产环境由 Transpiler 的 JASS bindings 版本选项接管)"。措辞实现时可微调,保留"开发工具/生产环境无需使用、无需设置"语义。

## C. 测试与文档(项目规则:bug 修复必须有回归测试;用户可见行为变化必须更新文档)

### 8. 回归测试(tests/Transpiler.Tests/PipelineTests.cs 等)
- 复现原 bug 的用例:预置 `libs/Jass-2.0.4`(陈旧版本),以 `--jass-version 3.0.0` 跑 pipeline → exit 0、`libs/Jass-3.0.0/Natives.g.cs` 存在、`libs/Jass-2.0.4` 已被清理、SFLib 仍部署(修复前该场景两版本并存必然失败)。
- 默认版本用例:空版本 + 全新目录 → 只部署最新(3.0.0),不出现 2.0.4。
- 非法版本 → 报错列出可用版本。
- 更新现有 `Pipeline_copies_bundled_library_stubs_before_compiling`(166-167 行断言路径改为默认版本)。
- 按 test-quality 规则:写断言前实际运行转译器核对输出。

### 9. 文档
- `docs/cli.md`:新增 `--jass-version`、`--list-jass` 说明;改写 38 行"Every run copies bundled C# stubs"为版本化部署 + 陈旧基线清理行为;115 行示例补充 3.0.0。
- `docs/gui.md`:Transpiler 卡片第三行、Edit 下拉、JASS Bindings 页开发工具提示。

## D. 验证与发布
- `dotnet test tests/Transpiler.Tests` 全绿。
- 运行 `publish-all.ps1` 刷新 `publish/win-x64`(EmbeddedResource 通配自动带入你的 Jass-3.0.0)。
- 端到端验证:对 `C:\Users\nef\workspace\Zulaman-WE\csproj` 跑修复后的 `sf-transpile --jass-version 3.0.0`(等价于 GUI 行为),确认自动清掉残留的 `libs\Jass-2.0.4`、转译成功生成 sharpforge.lua——直接治好你当前报错的项目。

## 备注
- `assets/libs/Jass-3.0.0` 目前未入 git(untracked),是否提交由你决定,本次不代做 git 操作。
- `docs/cli.md:25` 提到的 `--library-folder` 选项代码中不存在(文档超前),本次不动。