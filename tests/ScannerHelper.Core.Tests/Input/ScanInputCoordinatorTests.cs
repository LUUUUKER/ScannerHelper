// =============================================================================
// ScanInputCoordinatorTests.cs
//
// 中文：
//   输入协调器的行为测试（对应 TEST_PLAN_TASK_6.md 的 CO1~CO15）。
//
//   本文件里分量最重的三条：
//     CO1  暂停必须在**咨询关联器之前**就放行。用一个"一碰就抛"的关联器
//          替身来钉——只有这样，"没有调用过"才成为可断言的事实。
//     CO2  进入暂停时必须先把扣留中的按键放出去。一个会吃掉按键的安全阀，
//          在最需要它的那一刻反而加重故障。
//     CO5  SN 模式是流水线内部的恒等变换，不是旁路（规格 §5.7 的对照表）。
//
//   本组测试用真实的 InputEventCorrelator 与 ScanSession，只有 CO1、CO2 用
//   替身——测的是协调器怎么把它们串起来，用真件才测得到真正的接缝。
//
// English:
//   Behavior tests for the input coordinator (CO1–CO15 in TEST_PLAN_TASK_6.md).
//
//   Three carry the most weight. CO1: pausing must pass through *before* the correlator is
//   consulted, pinned with a substitute that throws on contact — the only way "it was never
//   called" becomes assertable. CO2: entering PAUSED must release withheld keystrokes first,
//   since a safety valve that swallows them makes the failure worse exactly when it is needed.
//   CO5: SN mode is the identity transform inside the pipeline, not a bypass (spec §5.7's
//   table).
//
//   These use the real InputEventCorrelator and ScanSession except in CO1 and CO2. What is
//   under test is how the coordinator joins them, and only real parts exercise the real seams.
//
// 包含的测试 / Tests in this file:
//   Paused_passes_everything_through_without_consulting_the_correlator  CO1
//   Pausing_releases_withheld_keystrokes                                CO2
//   Pausing_buffers_nothing_and_emits_nothing                           CO2
//   Pausing_and_resuming_leave_the_mode_untouched                       CO3
//   Sn_mode_emits_the_raw_string_unchanged                              CO5
//   Sku_mode_parses_then_validates                                      CO6
//   Parse_failure_enters_the_pending_error_state                        CO7
//   Validation_failure_carries_every_reason                             CO8
//   Scan_failure_does_not_enter_the_pending_error_state                 CO12
//   Full_cycle_walks_the_documented_states                              CO11
//   Coordinator_exposes_no_platform_dependency                          CO14
//   Null_dependencies_are_rejected                                      CO15
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Tests.TestDoubles;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Input;

public class ScanInputCoordinatorTests
{
    private const long ScannerDeviceId = 1001;
    private const long KeyboardDeviceId = 2002;

    private readonly TestSystemClock _clock = new();
    private readonly ModeManager _modeManager = new();

    private readonly RecordingKeyboardOutputService _output = new();
    private readonly List<ScanOutcome> _outcomes = [];
    private readonly List<KeyEvent> _replayed = [];

    private ScanInputCoordinator CreateCoordinator(
        IInputEventCorrelator? correlator = null,
        ISkuParser? parser = null,
        ISkuValidator? validator = null)
    {
        var coordinator = new ScanInputCoordinator(
            correlator ?? new InputEventCorrelator(_clock) { BoundScannerDeviceId = ScannerDeviceId },
            new ScanSession(_clock),
            _modeManager,
            parser ?? SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition, StartPosition = 5, Length = 8,
            }),
            validator ?? SkuValidatorFactory.Create(new SkuValidationSettings()),
            _output);

        coordinator.ScanProcessed += (_, args) => _outcomes.Add(args.Outcome);
        coordinator.ReplayRequested += (_, args) => _replayed.Add(args.KeyEvent);

        return coordinator;
    }

    private KeyEvent HookEvent(char? character, ushort scanCode = 0x20, bool isKeyUp = false)
        => new(_clock.MonotonicNow, scanCode, IsExtended: false, isKeyUp,
            VirtualKey: 0x44, character, IsInjected: false);

    private RawInputEvent RawEvent(ushort scanCode = 0x20, long deviceId = ScannerDeviceId)
        => new(_clock.MonotonicNow, scanCode, IsExtended: false, IsKeyUp: false, deviceId);

    /// <summary>
    /// 中文：模拟扫码枪送出一整枪：每个字符先走钩子、再走 Raw Input，
    ///       中间推进 1 毫秒（贴近实测的约 1.1 毫秒节奏）。
    /// English: Simulates a full scan from the scanner: each character through the hook then
    ///          Raw Input, 1 ms apart (close to the measured ~1.1 ms cadence).
    /// </summary>
    private void Scan(ScanInputCoordinator coordinator, string content)
    {
        ushort scanCode = 0x20;

        foreach (var character in content + '\r')
        {
            coordinator.OnHookEvent(HookEvent(character, scanCode));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            coordinator.OnRawInputEvent(RawEvent(scanCode));
            _clock.Advance(TimeSpan.FromMilliseconds(1));

            // 每个字符换一个扫描码，避免同一身份的队列互相干扰——真实扫描里
            // 相邻字符本来也是不同的键。
            // Vary the scan code per character so identical identities do not queue behind
            // each other; in a real scan consecutive characters are different keys anyway.
            scanCode = (ushort)(scanCode == 0x2F ? 0x20 : scanCode + 1);
        }
    }

    /// <summary>
    /// 中文：
    ///   CO1 —— 暂停时一切放行，且**绝不咨询关联器**。
    ///
    ///   ★ "绝不咨询"这半句才是全部重点，而且它是可测的。
    ///
    ///     规格 §5.7：PAUSED 必须独立于关联、缓冲、重放三者的正确性——它存在
    ///     的意义就是当那几件事本身坏掉时把工人救出来。一个先问关联器、再看
    ///     暂停标志的协调器，会把关联器的每一个缺陷都继承进这个唯一用来逃离
    ///     缺陷的状态：平时工作得好好的，偏偏在最需要它的那一刻跟着一起坏。
    ///
    ///     用一个"什么都不做"的假关联器测不出这件事——无论协调器有没有调用它，
    ///     测试都是绿的。只有让它一被调用就炸，"没有调用过"才成为可断言的事实。
    ///
    ///   规格假设 A4 把赌注抬高了：工位是笔记本，没有备用键盘可插。台式机上
    ///   键盘失灵还能插一把新的救急，笔记本上唯一的出路就是用触摸板点暂停。
    /// English:
    ///   CO1 — while paused everything passes through and the correlator is never consulted.
    ///
    ///   The second half is the entire point and it is testable. Spec §5.7: PAUSED must be
    ///   independent of the correctness of correlation, buffering and replay, because it exists
    ///   to rescue the operator when those are broken. A coordinator consulting the correlator
    ///   before checking the paused flag inherits its every defect into the one state meant to
    ///   escape them — fine at all other times, broken precisely when needed.
    ///
    ///   A do-nothing fake cannot detect this: the test stays green whether or not the
    ///   coordinator calls it. Only throwing on contact makes "never called" assertable.
    ///
    ///   Assumption A4 raises the stakes: a laptop has no spare keyboard, so the touchpad and
    ///   the pause button are the only way out.
    /// </summary>
    [Fact]
    public void Paused_passes_everything_through_without_consulting_the_correlator()
    {
        var coordinator = CreateCoordinator(new ThrowingInputEventCorrelator());
        coordinator.Pause();

        Assert.True(coordinator.IsPaused);

        // 一旦协调器在暂停状态下碰了关联器，这里会抛异常
        // Touching the correlator while paused throws here
        Assert.Equal(HookAction.PassThrough, coordinator.OnHookEvent(HookEvent('D')));
        Assert.Equal(HookAction.PassThrough, coordinator.OnHookEvent(HookEvent('\r')));

        coordinator.OnRawInputEvent(RawEvent());
        coordinator.Tick();

        Assert.Empty(_outcomes);
    }

    /// <summary>
    /// 中文：
    ///   CO2 —— 进入暂停时，扣留中的按键必须被放出去。
    ///
    ///   ★ 进入暂停的那一刻，很可能正有钩子事件被扣留着等 Raw Input——实测
    ///     钩子恒先到，所以只要工人正在打字，队列里几乎总有东西。若直接切
    ///     状态走人，那几次按键就永久消失了。
    ///
    ///     而 PAUSED 恰恰是"键盘失灵"时的救命稻草。一个会吃掉按键的安全阀，
    ///     在最需要它的那一刻反而加重了故障——工人点下暂停，期待键盘恢复，
    ///     结果刚才按的那几下不见了。
    /// English:
    ///   CO2 — entering PAUSED must release withheld keystrokes.
    ///
    ///   At that moment hook events are likely withheld awaiting Raw Input: measurements show
    ///   the hook always arrives first, so while anyone is typing the queue is rarely empty.
    ///   Switching state without releasing them loses those keystrokes permanently.
    ///
    ///   And PAUSED is the lifeline for "the keyboard stopped working". A safety valve that
    ///   swallows keystrokes makes the failure worse exactly when it is needed: the operator
    ///   clicks pause expecting the keyboard back, and the last few presses have vanished.
    /// </summary>
    [Fact]
    public void Pausing_releases_withheld_keystrokes()
    {
        var coordinator = CreateCoordinator();

        // 两次按键被扣留，等着 Raw Input 揭示来源
        // Two keystrokes withheld, awaiting Raw Input
        Assert.Equal(HookAction.Swallow, coordinator.OnHookEvent(HookEvent('A', 0x1E)));
        Assert.Equal(HookAction.Swallow, coordinator.OnHookEvent(HookEvent('B', 0x30)));
        Assert.Empty(_replayed);

        coordinator.Pause();

        Assert.True(_replayed.Count == 2,
            "进入暂停时必须把扣留中的按键放出去，否则工人那几下按键永久消失。"
            + "PAUSED 是键盘失灵时的救命稻草，一个会吃掉按键的安全阀在最需要它的"
            + $"那一刻反而加重故障。实际放出 {_replayed.Count} 个。");

        Assert.Equal('A', _replayed[0].Character);
        Assert.Equal('B', _replayed[1].Character);
    }

    /// <summary>
    /// 中文：CO2 续 —— 暂停期间不缓冲、不输出。
    ///       流水线被完全旁路，扫码枪的按键原样进业务软件（规格 §5.7）。
    /// English: CO2 continued — nothing is buffered or emitted while paused. The pipeline is
    ///          bypassed entirely and the scanner's keystrokes reach the business application
    ///          untouched (spec §5.7).
    /// </summary>
    [Fact]
    public void Pausing_buffers_nothing_and_emits_nothing()
    {
        var coordinator = CreateCoordinator();
        coordinator.Pause();
        _replayed.Clear();

        Scan(coordinator, "DGKJRDC5679F5NF");
        coordinator.Tick();

        Assert.Empty(_outcomes);
        Assert.Empty(_replayed);
        Assert.Equal(ScanPipelineState.Paused, coordinator.State);
    }

    /// <summary>
    /// 中文：
    ///   CO3 —— 暂停与恢复都不改变当前模式（规格 §5.7：暂停与 SN/SKU 正交）。
    ///
    ///   ★ 这条不是形式要求。工人点暂停是为了脱困，不是为了换模式；若恢复
    ///     之后模式变了，他扫的下一枪就会以他不知道的模式送出去——而那正是
    ///     规格 §3 坚持"启动恒为 SN"要避免的那类错误数据。
    /// English:
    ///   CO3 — neither pausing nor resuming changes the mode (spec §5.7: PAUSED is orthogonal
    ///   to SN/SKU).
    ///
    ///   Not a formality. The operator pauses to get out of trouble, not to change mode, and a
    ///   mode that shifted across a pause would send their next scan in a mode they do not know
    ///   about — the same class of wrong data spec §3's "always start in SN" exists to prevent.
    /// </summary>
    [Fact]
    public void Pausing_and_resuming_leave_the_mode_untouched()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        coordinator.Pause();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);

        coordinator.Resume();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：
    ///   CO5 —— SN 模式原样输出完整原始码（规格 §3）。
    ///
    ///   ★ 看起来平凡，但值得单独钉住：SN 模式是流水线**内部**的恒等变换，
    ///     不是旁路（规格 §5.7 的对照表）。字符照样被吞掉、解码、重新发出。
    ///
    ///     "SN 就是原样透传"是一个非常自然的心理简写，而照着它写代码——
    ///     比如让 SN 模式跳过流水线直接放行——会同时破坏注入事件的标记
    ///     和 PAUSED 与 SN 的区别。后者正是规格 §5.7 反复强调不能被合并的东西：
    ///     SN 依赖捕获、解码、重发全部正常，PAUSED 一个都不依赖。
    /// English:
    ///   CO5 — SN mode emits the complete raw code unchanged (spec §3).
    ///
    ///   It looks trivial and is worth pinning: SN is the identity transform *inside* the
    ///   pipeline, not a bypass (spec §5.7's table). Characters are still swallowed, decoded
    ///   and re-emitted.
    ///
    ///   "SN just passes it through" is a very natural shorthand, and coding to it — letting SN
    ///   skip the pipeline — breaks both the injected-event tagging and the PAUSED/SN
    ///   distinction, the latter being what spec §5.7 insists must never be collapsed: SN
    ///   depends on capture, decoding and re-emission all working, while PAUSED depends on none
    ///   of them.
    /// </summary>
    [Fact]
    public void Sn_mode_emits_the_raw_string_unchanged()
    {
        var coordinator = CreateCoordinator();
        Assert.Equal(ScanMode.Sn, _modeManager.CurrentMode);

        Scan(coordinator, "DGKJRDC5679F5NF");

        var emit = Assert.IsType<ScanOutcome.Emit>(Assert.Single(_outcomes));
        Assert.Equal("DGKJRDC5679F5NF", emit.Text);
        Assert.Equal("DGKJRDC5679F5NF", emit.RawCode);
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：CO6 —— SKU 模式下先解析再校验，输出解析出来的 SKU。
    ///       用规格 §8.1 的示例规则：起点 5、长度 8。
    /// English: CO6 — SKU mode parses then validates and emits the parsed SKU, using spec
    ///          §8.1's example rule of start 5, length 8.
    /// </summary>
    [Fact]
    public void Sku_mode_parses_then_validates()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABCD12345678XYZ");

        var emit = Assert.IsType<ScanOutcome.Emit>(Assert.Single(_outcomes));
        Assert.Equal("12345678", emit.Text);

        // 原始码即便在 SKU 模式下也要带着：只记 SKU 的话，事后无法判断是条码
        // 错了还是规则错了（规格 §15）。
        // The raw code travels even in SKU mode: with only the SKU recorded, nobody can later
        // tell whether the barcode or the rule was at fault (spec §15).
        Assert.Equal("ABCD12345678XYZ", emit.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   CO7 —— 解析失败进入待决错误，不自动输出，并保留完整原始码（规格 §10）。
    ///
    ///   保留的是**原始码**而不是部分解析出来的候选值：规格 §10 明写
    ///   "Force Send 始终发送原始扫描码"。工人完全可能知道"这个码是对的、
    ///   规则还没配好"，这时强制发送原码是合理的选择。
    /// English:
    ///   CO7 — a parse failure enters the pending-error state, emits nothing automatically, and
    ///   retains the complete raw code (spec §10).
    ///
    ///   The raw code, not a partially parsed candidate: spec §10 states that Force Send always
    ///   emits the raw scanned code. The operator may well know the code is right and the rule
    ///   is not yet configured, making a forced send of the raw code the reasonable choice.
    /// </summary>
    [Fact]
    public void Parse_failure_enters_the_pending_error_state()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        // 规则要求从第 5 位取 8 个字符，而这枪只有 3 个字符
        // The rule takes 8 characters from position 5; this scan has 3
        Scan(coordinator, "ABC");

        Assert.IsType<ScanOutcome.ParseFailed>(Assert.Single(_outcomes));
        Assert.Equal(ScanPipelineState.PendingError, coordinator.State);

        Assert.NotNull(coordinator.PendingError);
        Assert.Equal("ABC", coordinator.PendingError.RawCode);
        Assert.Equal(ScanMode.Sku, coordinator.PendingError.Mode);
    }

    /// <summary>
    /// 中文：CO8 —— 校验失败同样进入待决错误，并携带**全部**失败原因（决策 D-6）。
    ///       工人要一次看到"长度不对**且**含非法字符"，而不是修好一个再重扫一次
    ///       才发现下一个。
    /// English: CO8 — a validation failure likewise enters the pending-error state and carries
    ///          every reason (decision D-6). The operator must see "too short *and* contains an
    ///          illegal character" at once rather than fixing one and rescanning to meet the next.
    /// </summary>
    [Fact]
    public void Validation_failure_carries_every_reason()
    {
        var coordinator = CreateCoordinator(
            parser: SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition, StartPosition = 1, Length = 3,
            }),
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 8,
                CharacterSet = CharacterSetPreset.Numbers,
            }));

        _modeManager.SetMode(ScanMode.Sku);

        // 取出 "12A"：既短于 8，又含非数字
        // Extracts "12A": both shorter than 8 and containing a non-digit
        Scan(coordinator, "12ABCDEF");

        var failed = Assert.IsType<ScanOutcome.ValidationFailed>(Assert.Single(_outcomes));

        Assert.Equal("12A", failed.Sku);
        Assert.Equal("12ABCDEF", failed.RawCode);
        Assert.Equal(2, failed.Failure.Failures.Count);
        Assert.Equal(ScanPipelineState.PendingError, coordinator.State);
    }

    /// <summary>
    /// 中文：
    ///   CO12 —— 扫描本身没完成时**不进入待决错误**，回到空闲。
    ///
    ///   ★ 这是规格 §5.4 与 §10 的区别，不是实现细节。
    ///
    ///     §5.4 对扫描超时写得很明确：丢弃这次不完整的扫描、不产生任何业务
    ///     输入、回到 IDLE、显示一个可见的扫描错误。**没有 F10 这条路**——
    ///     手里那半截根本不是完整的条码，把它发出去等于主动往仓库系统里写
    ///     错误数据。
    ///
    ///     两者的差别是"手里那段内容完不完整"，而不是"错得严不严重"。混为
    ///     一谈的后果是：要么让工人无法强制发送一个本来正确的码（规则没配好
    ///     时整条产线卡住），要么让半截码可以被发出去（规格 §19.1 的静默
    ///     错误数据）。
    /// English:
    ///   CO12 — an incomplete scan does not enter the pending-error state and returns to Idle.
    ///
    ///   This is spec §5.4's distinction from §10, not an implementation detail. §5.4 is
    ///   explicit for a timeout: discard the incomplete scan, produce no business input, return
    ///   to IDLE, show a visible error. No F10 path, because half a code was never a complete
    ///   barcode and emitting it would volunteer bad data.
    ///
    ///   The difference is whether what is in hand is complete, not how severe the error is.
    ///   Conflating them either denies a force-send for a perfectly good code — halting the line
    ///   whenever a rule is misconfigured — or lets half a code be emitted, spec §19.1's
    ///   silently wrong data.
    /// </summary>
    [Fact]
    public void Scan_failure_does_not_enter_the_pending_error_state()
    {
        var coordinator = CreateCoordinator();

        // 扫到一半就断了 / the scan breaks off part-way
        foreach (var character in "DGKJRD")
        {
            coordinator.OnHookEvent(HookEvent(character));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            coordinator.OnRawInputEvent(RawEvent());
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        _clock.Advance(TimeSpan.FromMilliseconds(300));
        coordinator.Tick();

        var failed = Assert.IsType<ScanOutcome.ScanFailed>(Assert.Single(_outcomes));
        Assert.Equal(ScanFailureReason.InactivityTimeout, failed.Failure.Reason);

        Assert.True(coordinator.PendingError is null,
            "扫描本身没完成时不得进入待决错误：手里那半截不是完整条码，"
            + "提供 F10 等于允许把错误数据主动写进仓库系统（规格 §5.4）。");
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：CO11 —— 完整走一遍状态流转，并确认下一枪不受上一枪影响。
    /// English: CO11 — the documented states are walked end to end, and the next scan is
    ///          unaffected by the previous one.
    /// </summary>
    [Fact]
    public void Full_cycle_walks_the_documented_states()
    {
        var coordinator = CreateCoordinator();
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);

        // 第一个钩子事件被扣留 —— 进入 IDENTIFYING
        // The first hook event is withheld — IDENTIFYING
        coordinator.OnHookEvent(HookEvent('D'));
        Assert.Equal(ScanPipelineState.Identifying, coordinator.State);

        // Raw Input 确认是扫码枪 —— 进入 SCANNING
        // Raw Input confirms the scanner — SCANNING
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        coordinator.OnRawInputEvent(RawEvent());
        Assert.Equal(ScanPipelineState.Scanning, coordinator.State);

        // 收尾之后回到 IDLE / back to IDLE once finished
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        coordinator.OnHookEvent(HookEvent('\r', 0x1C));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        coordinator.OnRawInputEvent(RawEvent(0x1C));

        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
        var emit = Assert.IsType<ScanOutcome.Emit>(Assert.Single(_outcomes));
        Assert.Equal("D", emit.Text);
    }

    /// <summary>
    /// 中文：CO14 —— 协调器不依赖任何平台类型（规格 §17：不得含 WPF 界面代码）。
    ///       它的依赖全部是 Core 自己定义的抽象。
    /// English: CO14 — the coordinator depends on no platform type (spec §17: no WPF code in
    ///          it). Its dependencies are all abstractions Core defines itself.
    /// </summary>
    [Fact]
    public void Coordinator_exposes_no_platform_dependency()
    {
        var parameterTypes = typeof(ScanInputCoordinator)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.All(parameterTypes, parameterType =>
            Assert.True(
                parameterType.Assembly == typeof(ScanInputCoordinator).Assembly,
                $"协调器只应依赖 Core 自己的抽象，发现构造参数类型 "
                + $"{parameterType.FullName}（来自 {parameterType.Assembly.GetName().Name}）。"));
    }

    /// <summary>
    /// 中文：
    ///   CO15 —— 缺少依赖在构造时即被拒绝。
    ///
    ///   解析器与校验器同样必填。全新安装的默认配置本来就能构造出可用的两者
    ///   （见 SkuParserFactory 的用例 PF10），因此"没有规则"只可能是组装代码
    ///   的缺陷，不是正常状态。让它在启动时就炸，好过让工人切到 SKU 模式扫
    ///   第一枪时才发现——那时现场看到的只是"扫码没反应"。
    /// English:
    ///   CO15 — missing dependencies are rejected at construction, the parser and validator
    ///   included. A fresh install's defaults already build usable ones (SkuParserFactory's case
    ///   PF10), so "no rule" can only be a composition defect. Failing at startup beats the
    ///   operator discovering it at the first scan after switching to SKU mode, where the site
    ///   sees only "scanning does nothing".
    /// </summary>
    [Fact]
    public void Null_dependencies_are_rejected()
    {
        var correlator = new InputEventCorrelator(_clock);
        var session = new ScanSession(_clock);
        var parser = SkuParserFactory.Create(new SkuParsingSettings());
        var validator = SkuValidatorFactory.Create(new SkuValidationSettings());

        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            null!, session, _modeManager, parser, validator, _output));

        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            correlator, null!, _modeManager, parser, validator, _output));

        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            correlator, session, null!, parser, validator, _output));

        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            correlator, session, _modeManager, null!, validator, _output));

        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            correlator, session, _modeManager, parser, null!, _output));

        // 输出服务同样必填。缺了它，一枪扫完之后没有任何东西能到达业务软件，
        // 而界面照样报告成功——规格 §19.1 说的正是这种"看起来在工作"的失效。
        // The output service is required too. Without it nothing reaches the business
        // application after a scan while the UI still reports success — spec §19.1's
        // "looks like it is working" failure exactly.
        Assert.Throws<ArgumentNullException>(() => new ScanInputCoordinator(
            correlator, session, _modeManager, parser, validator, null!));
    }

    /// <summary>
    /// 中文：来自其他设备的按键被补发，不进扫描缓冲区。
    ///       这是"扫码枪的数据进流水线、人的按键还给业务软件"这条根本分工
    ///       在协调器一层的体现。
    /// English: Keystrokes from another device are replayed rather than buffered — the
    ///          coordinator-level expression of the basic division: scanner data into the
    ///          pipeline, human keystrokes back to the business application.
    /// </summary>
    [Fact]
    public void Other_device_keystrokes_are_replayed_not_buffered()
    {
        var coordinator = CreateCoordinator();

        var typed = HookEvent('A', 0x1E);
        Assert.Equal(HookAction.Swallow, coordinator.OnHookEvent(typed));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        coordinator.OnRawInputEvent(RawEvent(0x1E, KeyboardDeviceId));

        var replayed = Assert.Single(_replayed);
        Assert.Equal('A', replayed.Character);
        Assert.Empty(_outcomes);
    }
}
