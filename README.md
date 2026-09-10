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

这三份文件是事实来源。改动行为之前先读它们，不要在实现里另行解释业务规则。

These three files are the source of truth. Read them before changing behavior; do not
reinterpret business rules in the implementation.

| 文件 | 内容 |
|---|---|
| [`docs/SCANNER_HELPER_SPEC.md`](docs/SCANNER_HELPER_SPEC.md) | 产品与技术规格，含验收标准 |
| [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) | 分任务实施计划与推荐顺序 |
| [`docs/TEST_PLAN_PHASE_A.md`](docs/TEST_PLAN_PHASE_A.md) | Task 1–3 的用例清单与九条已定设计决策 |
| [`docs/design/palette.html`](docs/design/palette.html) | 状态配色参考，含色觉障碍与低质量显示器模拟 |

---

## 仓库结构 / Repository layout

```text
src/
  ScannerHelper.Core/     net8.0          纯逻辑，零 Windows 依赖，可在 macOS 上构建与测试
  ScannerHelper.Win32/    net8.0-windows  全部原生互操作（Phase B，尚未创建）
  ScannerHelper.App/      net8.0-windows  WPF 界面（Phase B，尚未创建）
tests/
  ScannerHelper.Core.Tests/            net8.0          单元测试
  ScannerHelper.Diagnostics.Harness/   net8.0-windows  人工诊断工具（Phase B，尚未创建）
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

```bash
dotnet build ScannerHelper.sln
dotnet test  ScannerHelper.sln
```

Phase A 的全部内容在 macOS、Linux、Windows 上都应当构建通过、测试全绿。
若在非 Windows 机器上失败，说明分层边界被破坏了。

Everything in Phase A must build and test green on macOS, Linux and Windows alike.
A failure on a non-Windows machine means the layering boundary has been breached.

警告一律当作错误（见 `Directory.Build.props`）。

---

## 当前进度 / Status

| 阶段 | 任务 | 状态 |
|---|---|---|
| Phase A | 1 解决方案边界 | ✅ |
| Phase A | 2 领域类型、模式、解析、校验 | ✅ |
| Phase A | 3 配置模型与 JSON 持久化 | ✅ |
| Phase A | 6 ScanSession / ScanInputCoordinator / InputEventCorrelator | ⬜ |
| Phase A | 7a 输出接口与强制发送流程 | ⬜ |
| Phase A | 8a 热键路由规则 | ⬜ |
| Phase A | 5a DeviceIdentityMatcher | ⬜ |
| Phase B | 4a 输入观测 spike（需真实硬件） | ⬜ |
| Phase B | 4b 输入拦截 spike — **硬性关卡** | ⬜ |
| Phase B | 5b、7b、8b、9–13 | ⬜ |

Task 4b 是硬性关卡。若 Raw Input 与低层键盘钩子的关联被证明不可靠，
**停下来汇报证据，不要用时序启发式把症状掩盖过去**（规格 §4.3）。

Task 4b is a hard gate. If Raw Input/hook correlation proves unreliable, stop and
report the evidence rather than hiding the symptoms behind timing heuristics
(spec §4.3).
