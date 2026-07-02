# Profiler 工具

*中文 | [English](PROFILER_TOOLS.md)*

本文档介绍 `Editor/Tools/Builtins/ProfilerFunctions.cs`(工具分类 `Profiler`)新增的 13 个 Profiler 相关工具,是引入这些工具的 PR 的配套说明——完整工具列表中它的位置见主 [README_CN.md](README_CN.md#内置工具)。

## 为什么需要这些工具

在此之前,唯一和性能相关的工具是 `get_performance_snapshot` 和 `analyze_scene_complexity`(分类 `Performance`)——两者都是轻量的单次快照。此前没有办法:

- 启动/停止真正的 Profiler 采集会话,并持续读取实时计数器
- 独立于单次快照,精确获取 CPU/GPU 帧计时
- 查看某个具体资源或 GameObject 的运行时内存占用
- 拍摄并对比内存快照,发现内存增长趋势
- 驱动 Frame Debugger,查看这一帧实际提交给 GPU 的内容

这 13 个工具补齐了这个空缺。它们刻意做成了小而专一、扁平化的工具(一个工具只做一件事),而不是像 `manage_profiler` 那样的大型分发工具,以匹配本仓库现有的扁平工具风格。

## 工具参考

### 会话控制

| 工具 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `profiler_start` | *(无)* | 确认信息 + recorder 数量 | 启用 `UnityEngine.Profiling.Profiler`,并启动 9 个常驻的 `ProfilerRecorder`(5 个 Render 类、4 个 Memory 类——见[默认计数器](#默认计数器))。幂等——重复调用时安全,不会出错。 |
| `profiler_stop` | *(无)* | 确认信息 | 释放全部常驻 recorder 并关闭 `Profiler`。 |
| `profiler_status` | *(无)* | 多行状态信息 | 会话是否在跑、活跃 recorder 数量,以及每个 recorder 的 `running`/`valid` 标志。 |

### 帧计时 / 计数器

| 工具 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `get_frame_timing` | `sample_frames`(int,可选,默认 1,限制在 1–30) | CPU/GPU 平均帧时间(ms)+ 估算 FPS | 使用 `UnityEngine.FrameTimingManager`。**不需要**先调用 `profiler_start`。如果暂时没有可用的计时样本,会回退到 `Time.unscaledDeltaTime`。 |
| `get_counters` | `names`(string,可选,逗号分隔) | 每个计数器一行:`last`、`current`、`unit` | 读取 `profiler_start` 启动的常驻 recorder;如果尚未启动,会自动启动(并给出提示)。Render 类计数器的限制见[已知限制](#已知限制)。 |

#### 默认计数器

```
Render/Draw Calls Count, Render/Batches Count, Render/SetPass Calls Count,
Render/Triangles Count, Render/Vertices Count,
Memory/GC Allocated In Frame, Memory/GC Used Memory,
Memory/Gfx Used Memory, Memory/Total Used Memory
```

### 对象内存

| 工具 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `get_object_memory` | `target`(string,必填) | 类型 + `Profiler.GetRuntimeMemorySizeLong` 的可读字节数 | 以 `Assets/` 开头的 `target` 通过 `AssetDatabase.LoadMainAssetAtPath` 解析;否则通过 `GameObject.Find` 解析(层级路径,例如 `Canvas/Panel/Icon`)。如果目标是 GameObject,还会汇总其全部子组件的内存(`Total (incl. all child components)`)。 |
| `get_top_memory_objects` | `type_name`(string,可选,默认 `Texture2D`)、`top_n`(int,可选,默认 20,限制在 1–100) | 按内存降序排列的该类型 Top N 对象,每个对象带细节(贴图宽高+格式、网格顶点数、音频时长)和 `hideFlags` | `get_object_memory` 的反向查询:用 `Resources.FindObjectsOfTypeAll` 枚举该类型**全部**已加载对象,按 `GetRuntimeMemorySizeLong` 排序。传 `type_name='All'` 得到分类型总量汇总(Texture2D/RenderTexture/Mesh/AudioClip/Material/AnimationClip/Shader/Sprite)。其他任何继承 `UnityEngine.Object` 的类型名会通过反射在已加载程序集中解析。设计定位是 `memory_compare_snapshots` 发现增长之后的下一步:回答"到底是**哪些对象**在占内存"。注意:在 Editor 里也会枚举到编辑器自身的对象——flags 列非空(如 `HideAndDontSave`)通常代表编辑器内部对象或运行时创建的对象。 |

### 内存快照

| 工具 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `memory_take_snapshot` | `name`(string,可选) | 保存的文件路径 + Total Allocated / Mono Used | 写入一个只含聚合数字的小型 JSON 文件(`Profiler.GetTotalAllocatedMemoryLong`、`GetTotalReservedMemoryLong`、`GetMonoUsedSizeLong`、`GetMonoHeapSizeLong`,加上 3 个 Memory 类计数器),存到 `MemoryCaptures/mcp-snapshots/`。**这里刻意不是真实的 Unity `.snap` 文件**——原因见[已知限制](#已知限制)。 |
| `memory_list_snapshots` | *(无)* | 文件名列表 | 列出快照目录下的 `*.json` 文件。 |
| `memory_compare_snapshots` | `path_a`、`path_b`(string,必填) | 每个字段的增量(`before -> after (Δ)`) | 接受带或不带 `.json` 后缀的文件名。在拼接进快照目录路径之前,先用 `Path.GetFileName` 做清洗(会把任何路径穿越尝试压缩成一个裸文件名,后续因找不到文件而被拒绝)。 |

### Frame Debugger

| 工具 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `frame_debugger_enable` | *(无)* | 确认信息 | 打开一个 Frame Debugger 窗口(反射方式)并驱动其内部采集循环。见[实现细节](#实现细节)——单独调用 `FrameDebuggerUtility.SetEnabled` **不会**在没有存活窗口实例的情况下填充事件数据。 |
| `frame_debugger_disable` | *(无)* | 确认信息 | 关闭**当前所有**已打开的 Frame Debugger 窗口,包括开发者手动打开的——不仅是这个工具自己打开的那一个。 |
| `frame_debugger_get_events` | `max_events`(int,可选,默认 50,限制在 1–500) | 每个事件的名称 + 关联对象 | Best-effort:返回事件名称(渲染 pass / draw call 标签)和关联的 `UnityEngine.Object`(如果有)——不是完整的逐 draw call shader 参数导出(那需要通过反射构造内部的 `FrameDebuggerEventData` 结构体,v1 不在范围内)。 |

## 实现细节

有两个不太直观、值得向审阅者特别说明的点,都是通过对着真实运行的 Editor 测试发现的,而不是从 API 文档推测出来的:

1. **`ProfilerRecorder` 必须跨帧存活。** 在单次同步调用里构造一个 `ProfilerRecorder`、读取、再释放,读到的值是 `0`——recorder 需要跨越至少一个真实的帧边界持续运行。`profiler_start` 把一个 `Dictionary<string, ProfilerRecorder>` 作为静态字段常驻保留(在 `AssemblyReloadEvents.beforeAssemblyReload` 时释放,避免原生句柄在域重载时泄漏),而不是每次调用时现场构造 recorder。

2. **单独启用 `FrameDebuggerUtility` 不会驱动采集。** `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` 只是一个数据容器;真正驱动采集循环的是 `UnityEditor.FrameDebuggerWindow` 的 `Update()`/`OnGUI()`。`frame_debugger_enable` 反射调用 `FrameDebuggerWindow.OpenWindow()`(public static)获取/创建一个窗口实例,再反射调用该实例上的 `EnableFrameDebugger()`(non-public 实例方法)并调用 `Repaint()`。`frame_debugger_disable` 通过 `Resources.FindObjectsOfTypeAll` 找到所有已打开的实例,对每一个调用 `DisableFrameDebugger()` + `Close()`。

## 已知限制

- **Render 类计数器在 Unity Editor 里持续读到 `0`**,即便确认渲染正在发生。这一点通过同时运行 `frame_debugger_get_events` 和 `get_counters` 得到验证——某次测试里 Frame Debugger 显示了 78–95 个真实渲染事件,而 `Render/Draw Calls Count` 等指标一直是 `0`。切换 `ProfilerDriver.profileEditor = true` 也不改变这个结果。Memory 类计数器(`GC Used Memory`、`Gfx Used Memory`、`Total Used Memory`)不受影响,能可靠读到真实数值。**未验证**这个限制是否同样存在于 Standalone Player 构建——如果有人测试这个 PR 时能确认这一点,会是很有用的补充信息。
- **`memory_take_snapshot` 不是真实的 Unity Memory Profiler 快照。** `com.unity.memoryprofiler` 的公开脚本 API 几乎为空(反射其全部程序集只找到 3 个跟捕获/对比无关的公开类型),旧的 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot` 在 Unity 6 已被完全移除,`ProfilerDriver.RequestMemorySnapshot()` 在测试中也没能可靠落盘生成文件——而且中等规模项目的真实快照文件可能达到数 GB,不适合作为能被 AI agent 频繁调用的工具动作。所以这个工具改成用一套自定义 JSON 格式记录一小组聚合数字,与任何真实的 `.snap` 文件分开存放。
- **`frame_debugger_disable` 会关闭所有已打开的 Frame Debugger 窗口**,不仅是这个工具自己打开的那一个——如果最终用户手动开着一个窗口,这一点值得让调用方(LLM)知会用户。

## 兼容性

已针对两个 Unity 版本核实:

- **Unity 6000.3.13f1**——通过真实的 Unity Editor MCP 调用现场验证(见[测试报告](#测试报告))。
- **Unity 2022.3.62f1c1**(测试时能找到的、安装完整可用的最早 Unity 2022 LTS 版本)——通过一个独立的离线反射检测工具验证(不需要启动 Unity Editor):用 `Assembly.LoadFrom` 加载 Editor 的 `Managed/UnityEngine/` 目录下的 `UnityEditor.CoreModule.dll` / `UnityEngine.CoreModule.dll`,确认这个文件依赖的每一个反射目标和公开 API 表面(共 34 项检查——类型、方法、属性,包括 `FrameDebuggerInternal` 嵌套命名空间、`ProfilerRecorder(string, string, int)` 构造函数重载等)都和 6000.3.13f1 上的结果完全一致。**34/34 全部通过**——Unity 2022.3 不需要任何版本分支处理。

本项目 `package.json` 已经声明 `"unity": "2022.3"` 为最低版本要求,所以这次检测专门针对这个版本下限。

## 测试报告

全部 13 个工具经过了两轮独立测试:

### 1. 开发期测试(逐工具,通过反射)

实现过程中,每个工具都是单独针对一个真实运行的 Unity Editor,通过 `execute_code` 反射调用 `ProfilerFunctions` 的静态方法来编译验证的(当时这些工具还没作为具名 MCP 工具注册进当前会话——详见下方的[MCP 工具发现的一个坑点](#mcp-工具发现的一个坑点))。覆盖范围包括:

- `profiler_start` → `profiler_status` → `profiler_stop` 完整生命周期,确认 recorder 数量(9)以及 stop 后干净归零(0 个活跃 recorder)。
- `get_frame_timing` 在 Edit Mode(发现一个真实的坑:Editor 空闲、没有持续重绘时,`FrameTimingManager` 可能返回几百毫秒的陈旧样本)和 Play Mode(稳定合理,~20ms CPU / ~2-3ms GPU)下分别采样 1–15 帧。
- `get_counters` 默认计数器集合,以及带逗号加空格边界情况的 `names` 过滤(`"Draw Calls Count, Triangles Count"`)——发现并修复了一个真实 bug:过滤逻辑在匹配前没有去除空格。
- `get_object_memory` 分别针对真实项目里的一个贴图资源、一个真实场景 GameObject(核对 `Total (incl. all child components)` 的汇总逻辑)、以及一个不存在的路径(干净报错,不抛异常)。
- `memory_take_snapshot` → `memory_list_snapshots` → `memory_compare_snapshots`,在两次快照之间分配约 400MB 的 `Texture2D`,确认增量方向正确——发现并修复了一个真实 bug:`ReadCounterOrZero` 辅助方法读的是 `ProfilerRecorder.LastValue`(Memory 类 recorder 的这个字段恒为 `0`),而不是 `CurrentValue`。同时发现并修复了一个真实的路径穿越 bug:`memory_compare_snapshots` 的 `path_a`/`path_b` 在加清洗守卫之前直接未经处理传给 `Path.Combine`(`../secret` 能逃出快照目录)。
- `frame_debugger_enable` → `frame_debugger_get_events` → `frame_debugger_disable`,在 `disable` 前后用 `capture_game_view` 截图确认渲染能干净恢复。正是在这一步发现了需要驱动 `FrameDebuggerWindow`(见[实现细节](#实现细节))——第一次实现尝试正确地报告了"卡住"而不是硬凑一个悄悄不工作的工具,这也是找到并验证这套双类型反射方案的过程。
- `get_top_memory_objects` 四种模式全部针对 Play Mode 下的真实项目测试:`'All'` 分类型汇总(8 个类型,总量合理);`Texture2D` 排行(正确区分出带 `hideFlags` 的编辑器内部对象——`GizmoIconAtlas HideAndDontSave`——和真实项目内容,如 4 个 8MB 的 TMP 字体图集、每个 2MB 且带图集名的 `sactx-*` sprite atlas 页);`RenderTexture`(URP 相机附件和 GameView RT,带尺寸/格式);错误类型名(干净返回已知类型建议列表,不抛异常);内置列表外的反射兜底类型(`Font` → 通过程序集扫描解析,列出 2 个 16MB 的中日韩字体)。

### 2. 真实端到端 MCP 测试(合入后,通过真正具名的 MCP 工具调用)

把这些工具注册进 MCP server 之后(见下方说明),全部 13 个工具改用**真正的一等 MCP 工具调用**(不是反射)重新完整测试了一遍,针对一个真实运行、处于 Play Mode 的 Unity Editor 连续执行:

```
profiler_start → profiler_status(9个recorder,运行中) → get_frame_timing(5)
  (Avg CPU 25.85ms / Avg GPU 2.77ms / ~38.7 FPS)
→ get_counters()(Render:0,符合文档说明;Memory:真实非零值)
→ get_counters(names="Draw Calls Count, Triangles Count")(确认空格 trim 修复生效)
→ get_object_memory(资源路径) → get_object_memory(GameObject路径) → get_object_memory(错误路径,干净报错)
→ memory_take_snapshot("before") → [分配60张1024x1024贴图] → memory_take_snapshot("after")
→ memory_list_snapshots() → memory_compare_snapshots(before, after)
  (Gfx Used Memory: +287.80MB,和约240MB原始贴图数据+开销的量级吻合)
→ memory_compare_snapshots(有效值, "../../../../etc/hosts") → 干净返回"Snapshot not found",路径穿越被挡
→ frame_debugger_enable → frame_debugger_get_events(10)(78个真实事件,包含"WorldSea1"等对象名)
→ capture_game_view(干净,无异常) → frame_debugger_disable → capture_game_view(和之前一致,渲染完好)
→ profiler_stop → profiler_status(0个recorder,干净收尾)
→ get_top_memory_objects("All")(分类型汇总:2125个Texture2D/310.76MB、28个RenderTexture/126.38MB...)
→ get_top_memory_objects("Texture2D", 10)(带宽高/格式/hideFlags的排行列表)
```

每一步都符合预期。这一轮没有发现回归问题。

### 3. 真实项目实战验证

作为最后一步检查,用这套工具分析了一个真实的在研手游项目(不是玩具测试场景)——在约 2 分钟的 Play Mode 里采样帧计时和内存、跨多个时间窗口对比快照,并把 `get_counters` 的 Render 计数器盲点和 `frame_debugger_get_events` 的真实事件数量做交叉核对,确认这不是那个项目特有的 bug。这次分析发现了确实有价值的信号(一个持续攀升、值得长期观察的 `Gfx Used Memory` 增长趋势,以及两条不相关的控制台警告),仅靠这套工具就做到了,可以合理证明这些工具确实能达到设计目的,而不只是单个工具本身正确。

#### MCP 工具发现的一个坑点

有一个操作层面的坑点值得记录下来,方便本地测试这个 PR 的人:新增一个 `[ToolProvider]` 类之后,即便 `request_recompile` 确认 0 编译错误、经历了多次域重载,运行中的 MCP server 的工具清单也**没有**自动被已连接的 AI 客户端拿到。需要两步:①在 Funplay 的 **MCP Server → Tool Exposure** 面板里,手动勾选新工具并点 **Save**(面板"`full` 默认暴露所有已注册工具"这条行为在这里没生效,因为暴露列表之前某个时间点已经被自定义过、偏离了默认状态)——这会重启正在运行的 server;②AI 客户端需要重新连接(这次是重启了 Claude Code 会话)才能对着修好的 server 重新做一次 `tools/list` 握手。这不是这个 PR 代码本身的 bug,但确实是"为什么我的 AI 助手看不到新工具"的一个真实陷阱,值得写进文档,给之后扩展这个包的人提个醒。
