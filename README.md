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

| 阶段 | 任务 | 状态 |
|---|---|---|
| Phase A | 1 解决方案边界 | ✅ |
| Phase A | 2 领域类型、模式、解析、校验 | ✅ |
| Phase A | 3 配置模型与 JSON 持久化 | ✅ |
| Phase A | 6 ScanSession / ScanInputCoordinator / InputEventCorrelator | ✅ |
| Phase A | 7a 输出接口与强制发送流程 | ✅ |
| Phase A | 8a 热键路由规则 | ✅ |
| Phase A | 5a DeviceIdentityMatcher | ✅ |
| | **Phase A 完成 —— 230 个用例，零警告** | ✅ |
| Phase B | 4a 输入观测 spike | ✅ 测量完成，**尚未在现场机复现** |
| Phase B | 4b 输入拦截 spike — **硬性关卡** | ⬜ |
| Phase B | 5b、7b、8b、9–13 | ⬜ |

Task 4b 是硬性关卡。若 Raw Input 与低层键盘钩子的关联被证明不可靠，
**停下来汇报证据，不要用时序启发式把症状掩盖过去**（规格 §4.3）。

Task 4b is a hard gate. If Raw Input/hook correlation proves unreliable, stop and
report the evidence rather than hiding the symptoms behind timing heuristics
(spec §4.3).

### 两个仍然开着的问题 / Two questions still open

这两条都属于 Phase B，且都必须在试点之前有答案。

1. **4a 的数字尚未在现场机器上复现。** 事件时序是机器的性质，不是代码的性质，
   而开发机不是试点机。

2. **钩子看到的码是否完整。** Task 4a 实测发现，**本程序完全没有运行**时，
   这把扫码枪就已经在往 Windows 的路上丢字符了（规格 §22.5）。Phase A 的
   任何测试都答不了这个问题——这里全部的逻辑都假定关联器拿到的是真实发生过
   的事件。硬件若不给，正确的逻辑只会产出一个自信的错误结果，而那正是
   规格 §19.1 最在意的失效形态。

Both belong to Phase B and both need an answer before the pilot. First, 4a's figures have
not been reproduced on pilot hardware: event timing is a property of the machine, not of
the code. Second, whether the hook receives a complete code — Task 4a found this scanner
already losing characters on its way to Windows with Scanner Helper not running at all
(spec §22.5), and no Phase A test can settle it. Everything here assumes the correlator is
handed the events that actually occurred; if the hardware does not deliver them, correct
logic produces a confidently wrong result, which is the failure mode spec §19.1 cares about
most.
