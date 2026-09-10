# Scanner Helper

面向仓库工位的 Windows 桌面工具。扫码枪以 **HID 键盘**模式工作时，同一次扫描
可以按当前模式输出完整 SN，或从中解析出 SKU 后再输出——工人不必扫两次，
仓库现有的业务软件也不需要任何改造。

A Windows desktop utility for warehouse workstations using USB barcode scanners in
HID Keyboard mode. One scan is emitted either as the full SN or as an SKU parsed out
of it, depending on the current mode, so the operator never scans twice and the
existing warehouse application needs no modification.

---

## 文档 / Documentation

规格与实施计划是事实来源。改动行为之前先读它们，不要在实现里另行解释业务规则。

The spec and the implementation plan are the source of truth. Read them before changing
behavior; do not reinterpret business rules in the implementation.

| 文件 | 内容 |
|---|---|
| [`docs/SCANNER_HELPER_SPEC.md`](docs/SCANNER_HELPER_SPEC.md) | 产品与技术规格，含验收标准 |
| [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) | 分任务实施计划与推荐顺序 |
| [`docs/design/palette.html`](docs/design/palette.html) | 状态配色参考，含色觉障碍与低质量显示器模拟 |

测试计划按任务分册。每一册都记录了**已定的设计决策**及其理由，以及每条守卫是
如何通过"故意制造违规"验证过的——决策的编号（D-1…D-24）跨册连续。

The test plans are split by task. Each records the design decisions it settled and why,
and how every load-bearing guard was verified by deliberately injecting the violation it
exists to catch. Decision numbers (D-1…D-24) run continuously across all three.

| 文件 | 内容 |
|---|---|
| [`docs/TEST_PLAN_PHASE_A.md`](docs/TEST_PLAN_PHASE_A.md) | Task 1–3：解析、校验、配置持久化。D-1…D-10 |
| [`docs/TEST_PLAN_TASK_6.md`](docs/TEST_PLAN_TASK_6.md) | Task 6：扫描会话、通道关联、输入协调器。D-11…D-18 |
| [`docs/TEST_PLAN_PHASE_A_REMAINDER.md`](docs/TEST_PLAN_PHASE_A_REMAINDER.md) | Task 7a、8a、5a：输出、热键、设备匹配。D-19…D-24 |

Task 4a 的测量报告也在 `docs/` 下，文件名形如 `TASK_4A_MEASUREMENTS_*.md`。
它是后续每一个架构论断的证据基础（规格 §4.3），其中第 5 节记录了一个到现在
仍未解决的问题——见下方「当前进度」。

Task 4a's measurement reports live under `docs/` as `TASK_4A_MEASUREMENTS_*.md`. They are
the evidence base for every later architectural argument (spec §4.3), and their section 5
records a question that is still open — see Status below.

---

## 仓库结构 / Repository layout

```text
src/
  ScannerHelper.Core/     net8.0          纯逻辑，零 Windows 依赖，可在 macOS 上构建与测试
  ScannerHelper.Win32/    net8.0-windows  全部原生互操作
  ScannerHelper.App/      net8.0-windows  WPF 界面（Phase B，尚未创建）
tests/
  ScannerHelper.Core.Tests/            net8.0          单元测试
  ScannerHelper.Diagnostics.Harness/   net8.0-windows  人工诊断工具（Task 4a 观测工具）
```

依赖方向一律向内：`Core` 定义接口，`Win32` 实现接口，`App` 负责组装。
`Core` 的目标框架是 `net8.0` 而非 `net8.0-windows`——这不是风格问题：一旦原生代码
漏进 `Core`，构建会立即失败，分层违规无法悄悄通过。守卫见 `ArchitectureTests`。

Dependencies point inward: `Core` defines interfaces, `Win32` implements them, `App`
composes. `Core` targets `net8.0` rather than `net8.0-windows`, which is not
stylistic — the build breaks immediately if native code leaks in, so the layering
violation cannot pass silently. Guarded by `ArchitectureTests`.

---

## 构建与测试 / Build and test

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

**在 Windows 上**（完整解决方案，含原生项目）：

```bash
dotnet build ScannerHelper.sln
dotnet test  ScannerHelper.sln
```

**在 macOS / Linux 上**，用跨平台筛选器，它只包含 `net8.0` 的两个项目：

```bash
dotnet build ScannerHelper.CrossPlatform.slnf
dotnet test  ScannerHelper.CrossPlatform.slnf
```

筛选器存在的理由只有一个：验收标准 20 要求"`ScannerHelper.Core` 能在非 Windows
机器上构建、且全部单元测试通过，以此证明分层边界成立"。自从
`ScannerHelper.Win32` 和诊断工具进入解决方案，`dotnet build ScannerHelper.sln`
在 macOS 上必然失败——那是**目标框架不兼容**，不是分层出了问题，但两者的
报错长得一样。筛选器把这两种情况分开：筛选器一旦变红，就真的是 `Core` 沾上了
Windows 依赖。

The filter exists for exactly one reason: acceptance criterion 20 requires
`ScannerHelper.Core` to build and pass its full test suite on a non-Windows machine,
proving the layering boundary holds. Since `ScannerHelper.Win32` and the harness
joined the solution, `dotnet build ScannerHelper.sln` necessarily fails on macOS —
that is target-framework incompatibility, not a layering problem, and the two look
alike in the output. The filter separates them: if the filter goes red, `Core` really
has acquired a Windows dependency.

警告一律当作错误（见 `Directory.Build.props`）。

---

## 当前进度 / Status

> **2026-09-10：输入架构已更换。** Task 4b 的硬性关卡失败——实测证明**按键一旦被
> `WH_KEYBOARD_LL` 吞掉，Windows 就不再为它产生 `WM_INPUT`**，所以「先扣留、等
> Raw Input 揭示来源、再决定」不可能实现：扣留这个动作本身销毁了做决定所需要的证据。
>
> 扫码枪改为工作在 **USB 虚拟串口**模式，它不再打字，业务软件收不到它发出的任何东西。
> 21 枪实测零误差（357 字节 = 21 × 17）。
>
> - 失败证据：[`docs/TASK_4B_FINDING_20260910.md`](docs/TASK_4B_FINDING_20260910.md)
> - 新架构与逐条影响：[`docs/ARCHITECTURE_CHANGE_SERIAL.md`](docs/ARCHITECTURE_CHANGE_SERIAL.md)

| 阶段 | 任务 | 状态 |
|---|---|---|
| 基础 | 1 解决方案边界 | ✅ |
| 基础 | 2 领域类型、模式、解析、校验 | ✅ |
| 基础 | 3 配置模型与 JSON 持久化 | ✅ |
| 基础 | 7a 输出契约与强制发送流程 | ✅ |
| 基础 | 8a 热键路由规则 | ✅ |
| 基础 | 5a DeviceIdentityMatcher | ✅ |
| spike | 4a 输入观测 | ✅ 测量完成，结论有效 |
| spike | 4b 输入拦截 — **硬性关卡** | ❌ **失败**，架构因此更换 |
| spike | 虚拟串口验证 | ✅ 21 枪零误差 |
| 新架构 | `IScannerInputSource` / `ScanProcessor` | ✅ |
| 新架构 | `SerialScannerReader` / `SerialScannerInputSource` | ✅ |
| 新架构 | 端到端：串口 → 处理 → 输出 | ✅ 实机验证 |
| 新架构 | 端口 ↔ USB 设备身份、热插拔 | ⬜ |
| 新架构 | 热键（`RegisterHotKey` 优先） | ⬜ |
| 新架构 | 正式界面、本地化、设置界面、诊断、试点 | ⬜ |
| | **当前 196 个用例，零警告** | ✅ |

退役的任务：6（`ScanSession` / `ScanInputCoordinator` / `InputEventCorrelator`）
随旧架构一起删除。它的业务那一半——模式、解析、校验、输出、待决错误——保留在
`ScanProcessor` 里并重新钉了测试（SP1~SP17）。

用例数从 248 降到 196，不是覆盖变差了，是**被覆盖的东西没有了**：关联器、扣留队列、
按键级拼装、以及「一枪收到一半就断了」这个概念，在串口模式下都不存在。

### 仍然开着的问题 / Still open

1. **有人把扫码枪切回键盘模式。** 那之后串口好好地开着、不报任何错、也没有数据，
   而扫码枪开始直接往业务软件里打字。这是新架构下 §19.1 那类「看起来在工作」的
   失效，必须靠「多久没收到数据」检测，界面不得显示一个没有验证过的「正常」。

2. **端口号会变。** 换一个 USB 口 `COM3` 可能变成 `COM7`，所以绑定要认 VID/PID
   或序列号，不能记住端口号。

3. **上线多了一步。** 每把新枪都要扫一次配置条码切到虚拟串口模式，配置码要和程序
   一起发。

Both the architecture change and its evidence are documented in `docs/`. The retired half of
the old input pipeline is gone from the code but preserved in git history; the case for the
change rests on the two documents linked above, not on keeping unusable code compiling.
