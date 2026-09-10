// =============================================================================
// ScanInputCoordinator.cs
//
// 中文：
//   输入状态机的所有者（规格 §17）。它把关联、扫描会话、模式、解析与校验
//   串成一条流水线，并**持有暂停开关**。
//
//   状态流转（规格 §5.1）：
//       IDLE → IDENTIFYING → SCANNING → PROCESSING → IDLE
//   外加两个横切状态：PendingError（等 F10 / Esc）与 Paused。
//
//   ★★ 暂停必须在**咨询关联器之前**就返回放行。
//
//     规格 §5.7 说得很清楚：PAUSED 存在的意义，是当捕获、关联、重放这几件事
//     本身坏掉时，把工人救出来。因此它必须**独立于那几件事的正确性**——
//     一个先问关联器再看暂停标志的实现，会把关联器的每一个缺陷都继承进
//     这个唯一用来逃离缺陷的状态里。
//
//     这条独立性是可测的，用例 CO1 就是这么写的：塞一个"一旦被调用就抛异常"
//     的关联器替身，然后确认暂停状态下它一次都没被碰过。
//
//     规格假设 A4 把赌注抬高了：工位是笔记本，没有备用键盘可插。台式机上
//     "键盘失灵"还能插一把新的救急，笔记本上唯一的出路就是用触摸板点那个
//     暂停按钮。
//
//   ★ 进入暂停时必须先把扣留中的按键放出去。
//
//     进入 PAUSED 的那一刻，很可能正有几个钩子事件被扣留着等 Raw Input。
//     直接清空状态走人，那几次按键就永久消失了——一个会吃掉按键的安全阀，
//     在最需要它的那一刻反而加重了故障。所以先 Flush 再切状态。
//
//   ★ SN 模式**不是**旁路，它是流水线内部的恒等变换（规格 §5.7 的对照表）。
//
//     字符照样被吞掉、解码、重新发出。"SN 就是原样透传"是一个非常自然的
//     心理简写，但照着它写代码——比如让 SN 模式跳过流水线——会同时破坏
//     注入事件的标记和 PAUSED 与 SN 的区别，而后者正是规格 §5.7 反复强调
//     不能被合并的东西。
//
// English:
//   Owner of the input state machine (spec §17), stringing correlation, the scan session,
//   mode, parsing and validation into one pipeline — and holding the paused flag.
//
//   States (spec §5.1): IDLE → IDENTIFYING → SCANNING → PROCESSING → IDLE, plus two
//   cross-cutting ones: PendingError (awaiting F10/Esc) and Paused.
//
//   Pausing must return pass-through *before* consulting the correlator. Spec §5.7 is plain
//   about why PAUSED exists: to rescue the operator when capture, correlation and replay are
//   themselves broken. It must therefore be independent of their correctness — an
//   implementation that asked the correlator first and checked the paused flag afterwards
//   would inherit every correlator defect into the one state that exists to escape them.
//
//   That independence is testable, and case CO1 tests it: hand in a correlator substitute
//   that throws if touched, and confirm it is never touched while paused.
//
//   Spec assumption A4 raises the stakes: the workstation is a laptop with no spare keyboard.
//   On a desktop "the keyboard stopped working" can be worked around by plugging in another;
//   on a laptop the only way out is the touchpad and the pause button.
//
//   Entering PAUSED must release withheld keystrokes first. At that moment several hook events
//   are likely withheld awaiting Raw Input, and clearing state outright would lose them
//   permanently — a safety valve that swallows keystrokes makes the failure worse exactly when
//   it is needed. So flush, then switch state.
//
//   SN mode is not a bypass but the identity transform *inside* the pipeline (spec §5.7's
//   table). Characters are still swallowed, decoded and re-emitted. "SN just passes it
//   through" is a very natural shorthand, and coding to it — skipping the pipeline for SN —
//   breaks both the injected-event tagging and the PAUSED/SN distinction at once, the latter
//   being what spec §5.7 insists must never be collapsed.
//
// 包含的成员 / Members in this file:
//   State / IsPaused / PendingError
//   SkuParser / SkuValidator   SKU 模式使用的规则，可在设置变更时替换
//   OnHookEvent                钩子事件，必须同步返回放行或吞掉
//   OnRawInputEvent            Raw Input 事件，揭示来源
//   Tick                       周期推进：关联超时与扫描超时
//   Pause / Resume             安全阀
//   ClearPendingError          清除待决错误（Task 7 的 F10 / Esc 会用到）
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Output;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：钩子回调必须当场做出的动作。
/// English: The action a hook callback must take on the spot.
/// </summary>
public enum HookAction
{
    /// <summary>
    /// 中文：放行，让 Windows 照常把这次按键送给焦点程序。
    /// English: Pass through; let Windows deliver the keystroke normally.
    /// </summary>
    PassThrough,

    /// <summary>
    /// 中文：吞掉。可能是因为确认它来自扫码枪，也可能是因为来源未定、
    ///       先扣留等结论（见 <see cref="CorrelationDecision.Undecided"/>）。
    ///       两种情况下钩子回调要做的事完全一样，所以这里不区分。
    /// English: Swallow it — either because it is confirmed scanner data, or because the
    ///          source is undecided and it is withheld pending an answer (see
    ///          <see cref="CorrelationDecision.Undecided"/>). The hook callback does the same
    ///          thing in both cases, so this does not distinguish them.
    /// </summary>
    Swallow,
}

/// <summary>
/// 中文：输入流水线的状态（规格 §5.1）。
/// English: The input pipeline's state (spec §5.1).
/// </summary>
public enum ScanPipelineState
{
    /// <summary>中文：空闲，等待输入。 English: Idle, awaiting input.</summary>
    Idle,

    /// <summary>
    /// 中文：有按键被扣留，正在等 Raw Input 揭示来源。
    /// English: A keystroke is withheld while its source is resolved.
    /// </summary>
    Identifying,

    /// <summary>中文：正在收集扫码枪的字符。 English: Collecting the scanner's characters.</summary>
    Scanning,

    /// <summary>
    /// 中文：一枪已经收完，正在按当前模式处理（解析、校验）。
    ///
    ///       ★ 这个状态在现实中几乎瞬间就过去了——解析与校验都是纯计算，
    ///         正则还带着 100 毫秒的有限超时。它之所以仍然存在，是因为规格
    ///         §5.1 明确列出了 PROCESSING：状态机的形状本身是规格的一部分，
    ///         把一个"反正很快"的阶段省掉，会让代码与规格对不上，日后读规格
    ///         的人找不到它对应哪一段。
    /// English:
    ///   The scan is complete and is being processed under the current mode — parsing and
    ///   validation.
    ///
    ///   In practice this state passes almost instantly: both steps are pure computation and
    ///   the regex carries a finite 100 ms timeout. It exists because spec §5.1 lists
    ///   PROCESSING explicitly. The shape of the state machine is itself part of the
    ///   specification, and omitting a stage on the grounds that it is fast leaves the code
    ///   and the spec out of correspondence, so a later reader cannot find which code answers
    ///   to which paragraph.
    /// </summary>
    Processing,

    /// <summary>
    /// 中文：有一个错误在等工人决定（F10 / Esc）。
    ///       只有解析与校验失败会到这里；扫描本身没完成不会（规格 §5.4 对
    ///       §10 的区别，见 ScanOutcome）。
    /// English: An error awaits the operator's decision (F10/Esc). Only parse and validation
    ///          failures reach here; a scan that never completed does not — spec §5.4's
    ///          distinction from §10, explained in ScanOutcome.
    /// </summary>
    PendingError,

    /// <summary>
    /// 中文：暂停。流水线被完全旁路，一切输入原样放行（规格 §5.7）。
    /// English: Paused. The pipeline is bypassed entirely and all input passes through
    ///          untouched (spec §5.7).
    /// </summary>
    Paused,
}

/// <summary>
/// 中文：输入状态机的所有者。
/// English: Owner of the input state machine.
/// </summary>
public sealed class ScanInputCoordinator
{
    private readonly IInputEventCorrelator _correlator;
    private readonly ScanSession _session;
    private readonly ModeManager _modeManager;
    private readonly IKeyboardOutputService _output;
    private readonly HotkeyCoordinator _hotkeys;

    /// <summary>
    /// 中文：
    ///   构造协调器。
    ///   输入：correlator 关联器；session 扫描会话；modeManager 模式持有者；
    ///         skuParser、skuValidator SKU 模式使用的规则。
    ///         全部不得为 null。
    ///
    ///   解析器与校验器在构造时就要求非空，而不是等到 SKU 模式才检查：
    ///   全新安装的默认配置本来就能构造出可用的解析器与校验器
    ///   （见 SkuParserFactory 的用例 PF10），因此"没有规则"只可能是组装
    ///   代码的缺陷，不是正常状态。让它在启动时就炸，好过让工人切到 SKU 模式
    ///   扫第一枪时才发现。
    /// English:
    ///   Creates the coordinator; every argument is required.
    ///
    ///   The parser and validator are required at construction rather than checked when SKU
    ///   mode is entered: a fresh install's defaults already build usable ones (see
    ///   SkuParserFactory's case PF10), so "no rule" can only be a composition defect rather
    ///   than a normal state. Failing at startup beats the operator discovering it at the
    ///   first scan after switching to SKU mode.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：任一参数为 null。 English: Any argument is null.
    /// </exception>
    public ScanInputCoordinator(
        IInputEventCorrelator correlator,
        ScanSession session,
        ModeManager modeManager,
        ISkuParser skuParser,
        ISkuValidator skuValidator,
        IKeyboardOutputService output,
        HotkeyCoordinator hotkeys)
    {
        ArgumentNullException.ThrowIfNull(correlator);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modeManager);
        ArgumentNullException.ThrowIfNull(skuParser);
        ArgumentNullException.ThrowIfNull(skuValidator);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _correlator = correlator;
        _session = session;
        _modeManager = modeManager;
        _output = output;
        _hotkeys = hotkeys;
        SkuParser = skuParser;
        SkuValidator = skuValidator;
    }

    /// <summary>
    /// 中文：
    ///   扫描后是否自动补一个回车，**默认关闭**（决策 D-10、规格 §10）。
    ///
    ///   ★ 默认关闭意味着网页收到的回车数是 **0**，而不是"少了一个"。
    ///
    ///     整段原始扫描都被吞掉，扫码枪自带的那个终止回车也不例外。所以
    ///     关掉这个开关时，网页从一次扫描里得到的回车是零。规格 §10 特意
    ///     点明了这一点，因为条码录入的网页表单**很常见地**靠回车提交或
    ///     跳到下一个字段——而这个页面到底靠不靠，在拿到真实页面之前无从
    ///     知道。
    ///
    ///   做成设置项而不是常量，代价是一个布尔和一个分支，换来的是这个答案
    ///   能在试点当天现场切换，而不必改代码、重新构建、重新部署。规格把它
    ///   称作"针对一个未知数的、刻意廉价的对冲"。
    ///
    ///   它对 SN 输出、SKU 输出、以及 F10 强制发送**一视同仁**。
    /// English:
    ///   Whether to append an Enter after a scan; off by default (decision D-10, spec §10).
    ///
    ///   Off means the page receives zero Enters from a scan, not one fewer. The entire raw
    ///   scan is swallowed, the scanner's own terminating Enter included. Spec §10 makes the
    ///   point explicitly because barcode entry in web forms very commonly relies on Enter to
    ///   submit or to advance to the next field — and whether this page does cannot be known
    ///   before the real page is in hand.
    ///
    ///   Making it a setting rather than a constant costs one boolean and one branch, and buys
    ///   an answer that can be switched on the pilot floor in seconds instead of requiring a
    ///   code change, a rebuild and a redeployment. The spec calls it a deliberately cheap
    ///   hedge against an unknown.
    ///
    ///   It applies equally to SN output, SKU output and F10 Force Send.
    /// </summary>
    public bool AppendEnterAfterScan { get; set; }

    /// <summary>
    /// 中文：当前状态。
    /// English: The current state.
    /// </summary>
    public ScanPipelineState State { get; private set; } = ScanPipelineState.Idle;

    /// <summary>
    /// 中文：是否处于暂停。规格 §5.7 要求它**绝不持久化**，程序每次启动都是
    ///       未暂停状态——守卫见测试 S12、S13。
    /// English: Whether paused. Spec §5.7 requires it never be persisted; every launch starts
    ///          un-paused, guarded by tests S12 and S13.
    /// </summary>
    public bool IsPaused => State == ScanPipelineState.Paused;

    /// <summary>
    /// 中文：正在等待工人决定的错误，null 表示没有。
    /// English: The error awaiting the operator's decision, or null.
    /// </summary>
    public PendingScanError? PendingError { get; private set; }

    /// <summary>
    /// 中文：SKU 模式使用的解析规则。设置变更时替换（规格 §13.4）。
    /// English: The parsing rule used in SKU mode, replaced when settings change (spec §13.4).
    /// </summary>
    public ISkuParser SkuParser { get; private set; }

    /// <summary>
    /// 中文：SKU 模式使用的校验规则。
    /// English: The validation rule used in SKU mode.
    /// </summary>
    public ISkuValidator SkuValidator { get; private set; }

    /// <summary>
    /// 中文：请求补发一次被扣留的按键。订阅方必须真的发出去，否则工人的按键
    ///       凭空消失（规格 §19）。
    /// English: A withheld keystroke needs re-emitting. Subscribers must actually emit it, or
    ///          the operator's keystroke vanishes (spec §19).
    /// </summary>
    public event EventHandler<ReplayRequestedEventArgs>? ReplayRequested;

    /// <summary>
    /// 中文：一枪处理完毕。
    /// English: A scan finished processing.
    /// </summary>
    public event EventHandler<ScanProcessedEventArgs>? ScanProcessed;

    /// <summary>
    /// 中文：
    ///   处理一个低层钩子事件，返回钩子回调该做的动作。
    ///
    ///   ★ 第一件事就是查暂停，而且是在碰关联器**之前**。
    ///
    ///     规格 §5.7 要求 PAUSED 独立于关联、缓冲、重放的正确性——它存在的
    ///     意义就是当那几件事坏掉时救人。先问关联器再看暂停标志，等于把
    ///     关联器的每一个缺陷都继承进这个唯一用来逃离缺陷的状态。
    ///
    ///     用例 CO1 用一个"一旦被调用就抛异常"的关联器替身钉住这一点。
    /// English:
    ///   Handles one low-level hook event and returns what the callback should do.
    ///
    ///   The first thing checked is the paused flag, before the correlator is touched at all.
    ///   Spec §5.7 requires PAUSED to be independent of correlation, buffering and replay,
    ///   since it exists to rescue the operator when those break; consulting the correlator
    ///   first would inherit its every defect into the one state meant to escape them. Case
    ///   CO1 pins this with a correlator substitute that throws if touched.
    /// </summary>
    public HookAction OnHookEvent(in KeyEvent keyEvent)
    {
        // 步骤 1 —— 暂停时一切放行，绝不咨询关联器（规格 §5.7）
        // Step 1 — while paused everything passes through, without consulting the correlator
        if (IsPaused)
        {
            return HookAction.PassThrough;
        }

        // 步骤 2 / Step 2
        var outcome = _correlator.AcceptHookEvent(keyEvent);
        DispatchResolved(outcome.Resolved);

        // 步骤 3 / Step 3
        switch (outcome.Decision)
        {
            case CorrelationDecision.PassThrough:
                // 来源已确知不是扫码枪，此刻就能判断它是不是热键。
                // 是热键就吞掉（规格 §7：处理过的热键不转发），否则放行。
                // The source is already known not to be the scanner, so whether it is a hotkey
                // can be decided now: swallow if it is (spec §7 does not forward an acted-upon
                // hotkey), otherwise pass through.
                return RouteHotkey(keyEvent, isFromBoundScanner: false) == HotkeyAction.None
                    ? HookAction.PassThrough
                    : HookAction.Swallow;

            case CorrelationDecision.Swallow:
                FeedSession(keyEvent);
                return HookAction.Swallow;

            case CorrelationDecision.Undecided:
                // 来源未定 —— 先吞掉等结论。这不是"什么都不做"，
                // 详见 CorrelationDecision 的说明。
                // Source undecided — swallow and await the answer. Not "do nothing";
                // see CorrelationDecision.
                if (State is ScanPipelineState.Idle)
                {
                    State = ScanPipelineState.Identifying;
                }

                return HookAction.Swallow;

            default:
                // Replay 不会作为"刚接纳的这个事件"的判断出现——它只可能出现在
                // Resolved 列表里，那是之前被扣留的事件。走到这里说明关联器
                // 违反了契约。
                // Replay never appears as the verdict for the event just accepted; it can only
                // appear in Resolved, for previously withheld events. Reaching here means the
                // correlator broke its contract.
                throw new InvalidOperationException(
                    $"关联器对刚接纳的钩子事件返回了 {outcome.Decision}，这违反了契约。"
                    + $" The correlator returned {outcome.Decision} as the verdict for the"
                    + " event just accepted, which breaks its contract.");
        }
    }

    /// <summary>
    /// 中文：
    ///   处理一个 Raw Input 事件。
    ///   暂停时直接忽略：流水线被完全旁路，没有任何事件被扣留，也就没有什么
    ///   需要它来揭示的（规格 §5.7）。
    /// English:
    ///   Handles one Raw Input event. Ignored while paused: the pipeline is bypassed
    ///   entirely, nothing is withheld, and so there is nothing for it to resolve (spec §5.7).
    /// </summary>
    public void OnRawInputEvent(in RawInputEvent rawInputEvent)
    {
        if (IsPaused)
        {
            return;
        }

        DispatchResolved(_correlator.AcceptRawInputEvent(rawInputEvent));
    }

    /// <summary>
    /// 中文：
    ///   周期性推进：关联超时与扫描超时。
    ///
    ///   ★ 必须被周期性调用，不能只在有事件时调用。两个超时都是"某件事不再
    ///     发生"的判定，而"不再发生"本身不会产生任何事件来触发检查。
    ///
    ///     漏掉它的后果各不相同，但都不轻：
    ///       关联超时不检查 → 被扣留的按键永远等下去，工人那一下永久消失
    ///                        （规格 §19 明写不得无限期吞掉键盘输入）。
    ///       扫描超时不检查 → 断掉的半枪一直挂在进行中，直到下一枪的第一个
    ///                        字符到来，然后接成一个"前半截是上一次"的码
    ///                        （见 ScanSession 文件头）。
    /// English:
    ///   The periodic advance for both timeouts. It must be called periodically rather than
    ///   only on events: both timeouts judge that something has *stopped* happening, and
    ///   stopping produces no event to trigger a check.
    ///
    ///   Omitting it fails differently in each case and neither is minor. Without the
    ///   correlation timeout a withheld keystroke waits forever and the operator's keypress is
    ///   permanently lost (spec §19 forbids swallowing keyboard input indefinitely). Without
    ///   the scan timeout a broken half-scan stays in progress until the next scan's first
    ///   character arrives and is joined to it, producing a code that is part previous scan
    ///   (see ScanSession's header).
    /// </summary>
    public void Tick()
    {
        if (IsPaused)
        {
            return;
        }

        DispatchResolved(_correlator.Advance());

        if (_session.CheckTimeout() is { } timedOut)
        {
            ProcessScanResult(timedOut);
        }
    }

    /// <summary>
    /// 中文：
    ///   进入暂停（规格 §5.7）。
    ///   步骤：
    ///     1. 先把关联器里扣留着的按键全部放出去；
    ///     2. 丢弃当前扫描会话；
    ///     3. 切到暂停状态。
    ///
    ///   ★ 步骤 1 不能省，也不能放到步骤 3 之后。
    ///
    ///     进入暂停的那一刻很可能正有按键被扣留着等 Raw Input。不放出去就
    ///     直接切状态，那几次按键会永久消失——而 PAUSED 恰恰是"键盘失灵"时
    ///     的救命稻草。一个会吃掉按键的安全阀，在最需要它的那一刻反而加重
    ///     了故障。
    ///
    ///   ★ 暂停**不改变当前模式**（规格 §5.7：暂停与 SN/SKU 正交）。
    ///     本方法完全不碰 ModeManager，恢复时模式自然还是原来那个。
    ///
    ///     被放出去的那些按键也不会改变模式，即便其中恰好有一个 F8。
    ///     Flush 结掉的事件来源都没能判定（DeviceId 为 null），而 DispatchResolved
    ///     只对来源确知的事件做热键路由——理由见那里的说明。于是"点一下暂停，
    ///     模式跟着变了"这种意外不可能发生，而这正是规格 §5.7 要求的正交性。
    /// English:
    ///   Enters PAUSED (spec §5.7).
    ///   Steps: (1) release every keystroke the correlator is withholding; (2) discard the
    ///   current scan session; (3) switch state.
    ///
    ///   Step 1 cannot be skipped or moved after step 3. At the moment of pausing, keystrokes
    ///   are likely withheld awaiting Raw Input, and switching state without releasing them
    ///   loses them permanently — while PAUSED is the lifeline for "the keyboard stopped
    ///   working". A safety valve that swallows keystrokes makes the failure worse exactly
    ///   when it is needed.
    ///
    ///   Pausing does not change the mode (spec §5.7: PAUSED is orthogonal to SN/SKU). This
    ///   method never touches ModeManager, so resuming naturally restores what was in force.
    ///
    ///   Nor can the released keystrokes change it, even if one of them happens to be F8: every
    ///   event Flush settles has an undetermined source (a null DeviceId), and DispatchResolved
    ///   routes hotkeys only for events whose source is known — see the reasoning there. "Click
    ///   pause and the mode changes with it" is therefore impossible, which is the orthogonality
    ///   spec §5.7 requires.
    /// </summary>
    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        // 步骤 1 / Step 1
        DispatchResolved(_correlator.Flush());

        // 步骤 2 / Step 2
        _session.Reset();

        // 步骤 3 / Step 3
        State = ScanPipelineState.Paused;
    }

    /// <summary>
    /// 中文：
    ///   离开暂停，回到空闲（规格 §5.7）。
    ///   模式保持不变——本方法同样完全不碰 ModeManager。
    ///
    ///   待决错误也一并清除：暂停期间工人已经绕过本程序直接操作了业务软件，
    ///   那个错误的上下文早已不存在，留着它只会让恢复之后弹出一个莫名其妙
    ///   的旧错误。
    /// English:
    ///   Leaves PAUSED for Idle (spec §5.7), with the mode unchanged — this method likewise
    ///   never touches ModeManager.
    ///
    ///   Any pending error is cleared too: while paused the operator worked the business
    ///   application directly, so that error's context is long gone and keeping it would only
    ///   surface a bewildering stale error on resume.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        PendingError = null;
        State = ScanPipelineState.Idle;
    }

    /// <summary>
    /// 中文：更换 SKU 模式使用的规则（设置变更时调用，规格 §13.4）。
    /// English: Replaces the SKU rules when settings change (spec §13.4).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：任一参数为 null。 English: Either argument is null.
    /// </exception>
    public void UpdateSkuRules(ISkuParser skuParser, ISkuValidator skuValidator)
    {
        ArgumentNullException.ThrowIfNull(skuParser);
        ArgumentNullException.ThrowIfNull(skuValidator);

        SkuParser = skuParser;
        SkuValidator = skuValidator;
    }

    /// <summary>
    /// 中文：
    ///   强制发送（F10）：把出错那一枪的**原始码**原样发出去（规格 §10）。
    ///   输入：无。
    ///   输出：确实有待决错误并已发送返回 true；没有待决错误返回 false。
    ///
    ///   ★ 发的是**原始码**，不是解析出来的候选 SKU。
    ///
    ///     规格 §10 写得很直白："Force Send 始终发送原始扫描码，而不是部分
    ///     解析出来的候选值。" 道理在于工人按 F10 的处境：他看到错误提示，
    ///     并且判断"这个码本身是对的，是规则还没配好"。这时他想送出去的是
    ///     他扫到的那个东西。发一个解析了一半的候选值，等于用一条他不信任的
    ///     规则的中间产物去覆盖他的判断。
    ///
    ///   ★ 返回布尔而不是抛异常，是为了 Task 8 的热键路由。
    ///
    ///     规格 §7 的表格规定：F10 在有待决错误时**吞掉**，没有待决错误时
    ///     **透传**。路由层需要知道"这次到底管没管用"才能决定吞还是放，
    ///     而"没有待决错误时按 F10"是完全正常的操作，不是异常。
    ///
    ///   模式保持不变（规格 §10）：本方法完全不碰 ModeManager。
    /// English:
    ///   Force Send (F10): emits the failed scan's raw code exactly (spec §10). Returns true
    ///   when a pending error existed and was sent, false when there was none.
    ///
    ///   It emits the raw code, not the parsed candidate. Spec §10 is blunt: "Force Send always
    ///   emits the raw scanned code, not a partially parsed candidate." The reason lies in the
    ///   operator's situation when they press F10 — they have read the error and judged that
    ///   the code itself is right and the rule is not yet configured. What they want sent is
    ///   what they scanned. Emitting a half-parsed candidate would override that judgement with
    ///   an intermediate product of the very rule they do not trust.
    ///
    ///   Returning a boolean rather than throwing serves Task 8's hotkey routing: spec §7's
    ///   table has F10 swallowed while an error is pending and passed through otherwise, so the
    ///   routing layer needs to know whether it did anything — and pressing F10 with no pending
    ///   error is entirely normal rather than exceptional.
    ///
    ///   The mode is preserved (spec §10): this method never touches ModeManager.
    /// </summary>
    public bool ForceSend()
    {
        if (PendingError is not { } pendingError)
        {
            return false;
        }

        Emit(pendingError.RawCode);
        ClearPendingError();
        return true;
    }

    /// <summary>
    /// 中文：
    ///   取消（Esc）：什么都不发，丢弃这一枪（规格 §10）。
    ///   输入：无。
    ///   输出：确实有待决错误并已取消返回 true；没有待决错误返回 false。
    ///
    ///   返回布尔的理由与 ForceSend 相同，而且在 Esc 上更要紧：规格 §7 特意
    ///   说明 Esc **不能无条件吞掉**——它在网页里太常用了，无条件吞会让工人
    ///   发现"网页的取消键坏了"，却完全想不到是本程序所为。
    ///
    ///   模式保持不变（规格 §10）。
    /// English:
    ///   Cancel (Esc): emits nothing and discards the scan (spec §10). Returns true when a
    ///   pending error existed and was cancelled, false when there was none.
    ///
    ///   The boolean matters more here than for F10: spec §7 specifically forbids swallowing
    ///   Esc unconditionally, being far too common in web use — doing so would leave the
    ///   operator with a "broken" cancel key and no reason to suspect this tool.
    ///
    ///   The mode is preserved (spec §10).
    /// </summary>
    public bool Cancel()
    {
        if (PendingError is null)
        {
            return false;
        }

        ClearPendingError();
        return true;
    }

    /// <summary>
    /// 中文：清除待决错误，回到空闲（规格 §10：强制发送与取消都要回到出错前
    ///       的界面状态）。
    /// English: Clears the pending error and returns to Idle (spec §10: both Force Send and
    ///          Cancel return to the pre-error UI state).
    /// </summary>
    public void ClearPendingError()
    {
        if (PendingError is null)
        {
            return;
        }

        PendingError = null;
        State = ScanPipelineState.Idle;
    }

    /// <summary>
    /// 中文：
    ///   把内容送进业务软件。
    ///   步骤：
    ///     1. 原样发送文本；
    ///     2. AppendEnterAfterScan 打开时，另外发一次回车。
    ///
    ///   ★ 步骤 2 用独立的 EmitEnter，而不是在文本末尾拼一个 '\r'。
    ///
    ///     以 Unicode 字符发出的 U+000D 是一个**字符**，而网页表单的提交行为
    ///     通常挂在 Enter 的**按键事件**上。拼成字符发出去，很可能文本框里
    ///     多了个看不见的字符而表单根本没提交——那个设置开了等于没开，
    ///     现场还极难判断是设置没生效还是网页不认。详见 IKeyboardOutputService。
    ///
    ///   本方法是 SN 输出、SKU 输出、F10 强制发送共同的出口，因此那条设置
    ///   对三者一视同仁（规格 §10 明确要求），不需要在三处各写一遍。
    /// English:
    ///   Delivers content to the business application: emit the text as given, then emit an
    ///   Enter if AppendEnterAfterScan is on.
    ///
    ///   The Enter goes through EmitEnter rather than being appended to the text as '\r'.
    ///   U+000D sent as a Unicode character is a *character*, while a web form's submit
    ///   behavior normally hangs off the Enter *key event*; appending it tends to leave an
    ///   invisible character in the field with the form not submitting — the setting enabled
    ///   yet ineffective, and very hard on site to tell from a page that does not accept the
    ///   input. See IKeyboardOutputService.
    ///
    ///   This is the single exit shared by SN output, SKU output and Force Send, which is what
    ///   makes the setting apply equally to all three as spec §10 requires, without repeating
    ///   the rule in three places.
    /// </summary>
    private void Emit(string text)
    {
        _output.EmitText(text);

        if (AppendEnterAfterScan)
        {
            _output.EmitEnter();
        }
    }

    /// <summary>
    /// 中文：
    ///   分发关联器给出的结论。
    ///   Swallow → 是扫码枪的数据，喂给扫描会话。
    ///   Replay  → 是普通键盘，请求补发。
    /// English:
    ///   Dispatches the correlator's conclusions: Swallow feeds the scan session, Replay
    ///   requests re-emission.
    /// </summary>
    private void DispatchResolved(IReadOnlyList<ResolvedKeyEvent> resolved)
    {
        for (var index = 0; index < resolved.Count; index++)
        {
            var resolution = resolved[index];

            if (resolution.Decision == CorrelationDecision.Swallow)
            {
                FeedSession(resolution.Event);
                continue;
            }

            // ★ 只有来源**确实判定出来**的事件才允许被当作热键。
            //
            //   DeviceId 为 null 表示这个事件是按超时规则结掉的（决策 D-13）——
            //   对应的 Raw Input 始终没来，我们并不知道是谁按的，只是按"宁可
            //   重放也不吞掉"的原则放行。
            //
            //   这种事件绝不能触发热键，两个方向的后果完全不对称：
            //     误重放一个扫码枪字符 → 业务软件里多一个字符，看得见、能改。
            //     误切换模式           → 接下来几十枪全部以错误的模式发出去，
            //                            而工人正低头看货，毫无提示。
            //   后者正是规格 §7「扫码枪发出的 F8 绝不能切换模式」要防的东西，
            //   而"不知道是不是扫码枪"与"知道是扫码枪"在这条规则面前应当同等对待。
            //
            // Only events whose source was actually determined may act as a hotkey.
            //
            // A null DeviceId means the event was settled by the expiry rule (decision D-13):
            // its Raw Input counterpart never arrived, we do not know who pressed it, and it is
            // released only under "replay rather than swallow".
            //
            // Such an event must never fire a hotkey, the two directions being entirely
            // asymmetric: wrongly replaying a scanner character puts one visible, correctable
            // character into the business application, whereas wrongly toggling the mode sends
            // the next few dozen scans out in the wrong mode with no indication at all, while
            // the operator is looking at goods. The latter is exactly what spec §7's "scanner
            // F8 must never toggle" exists to prevent — and "we do not know whether it was the
            // scanner" deserves the same treatment as "we know it was".
            if (resolution.DeviceId is not null
                && RouteHotkey(resolution.Event, isFromBoundScanner: false) != HotkeyAction.None)
            {
                continue;
            }

            ReplayRequested?.Invoke(this, new ReplayRequestedEventArgs(resolution.Event));
        }
    }

    /// <summary>
    /// 中文：
    ///   判断一次按键是不是热键，是就当场执行对应的动作。
    ///   输入：keyEvent 按键；isFromBoundScanner 是否来自绑定的扫码枪。
    ///   输出：执行的动作。不是 None 就意味着这个按键被消费掉了，
    ///         不应再转发或重放（规格 §7）。
    ///
    ///   PauseResume 在这里只可能意味着"暂停"。规格 §5.7 规定暂停期间不拦截
    ///   任何热键，所以这个键在已暂停状态下根本走不到这里——它只能单向。
    ///   恢复必须靠鼠标点界面上的按钮，详见 HotkeyAction.PauseResume。
    /// English:
    ///   Decides whether a keystroke is a hotkey and performs its action if so, returning what
    ///   was done. Anything other than None means the key was consumed and must not be forwarded
    ///   or replayed (spec §7).
    ///
    ///   PauseResume can only ever mean "pause" here. Spec §5.7 intercepts no hotkey while
    ///   paused, so this key cannot reach here in the paused state — it is one-way, and resuming
    ///   requires the mouse-clickable control. See HotkeyAction.PauseResume.
    /// </summary>
    private HotkeyAction RouteHotkey(in KeyEvent keyEvent, bool isFromBoundScanner)
    {
        var action = _hotkeys.Route(
            keyEvent,
            new HotkeyContext(isFromBoundScanner, IsPaused, PendingError is not null));

        switch (action)
        {
            case HotkeyAction.ToggleMode:
                _modeManager.Toggle();
                break;

            case HotkeyAction.ForceSend:
                ForceSend();
                break;

            case HotkeyAction.Cancel:
                Cancel();
                break;

            case HotkeyAction.PauseResume:
                Pause();
                break;
        }

        return action;
    }

    private void FeedSession(in KeyEvent keyEvent)
    {
        if (State is not ScanPipelineState.PendingError)
        {
            State = ScanPipelineState.Scanning;
        }

        if (_session.Accept(keyEvent) is { } result)
        {
            ProcessScanResult(result);
        }
    }

    /// <summary>
    /// 中文：
    ///   处理一枪的结局：按当前模式产出输出，或转成错误。
    ///   步骤：
    ///     1. 扫描本身没完成 → 报错并回到空闲，**不进入待决错误**（规格 §5.4）；
    ///     2. SN 模式 → 原样输出（规格 §3）；
    ///     3. SKU 模式 → 解析，失败则进入待决错误（规格 §10）；
    ///     4. 校验，失败则进入待决错误；
    ///     5. 输出解析出来的 SKU。
    ///
    ///   ★ 步骤 1 与步骤 3、4 的区别是"手里那段内容完不完整"，而不是"错得
    ///     严不严重"。超时的半截码不是完整条码，发出去等于主动写错误数据；
    ///     解析失败时手里是一个完整的原始码，工人完全可能知道"这个码是对的、
    ///     规则还没配好"，强制发送是合理的选择（详见 ScanOutcome 文件头）。
    /// English:
    ///   Turns a scan result into output or an error.
    ///   Steps: (1) an incomplete scan reports an error and returns to Idle without entering
    ///   the pending-error state (spec §5.4); (2) SN mode emits the raw code (spec §3);
    ///   (3) SKU mode parses, entering the pending-error state on failure (spec §10);
    ///   (4) validates, likewise; (5) emits the parsed SKU.
    ///
    ///   The difference between step 1 and steps 3–4 is whether what is in hand is complete,
    ///   not how severe the error is. Half a code was never a complete barcode and emitting it
    ///   would volunteer bad data, whereas a parse failure holds a complete raw code the
    ///   operator may well know to be right with the rule not yet configured, making Force Send
    ///   a reasonable choice (see ScanOutcome's header).
    /// </summary>
    private void ProcessScanResult(ScanResult result)
    {
        State = ScanPipelineState.Processing;

        // 步骤 1 / Step 1
        if (result is ScanResult.Failed failed)
        {
            State = ScanPipelineState.Idle;
            RaiseProcessed(new ScanOutcome.ScanFailed(failed));
            return;
        }

        var rawCode = ((ScanResult.Completed)result).RawCode;

        // 步骤 2 / Step 2 —— SN 是流水线内部的恒等变换，不是旁路（规格 §5.7）
        // Step 2 — SN is the identity transform inside the pipeline, not a bypass (spec §5.7)
        if (_modeManager.CurrentMode == ScanMode.Sn)
        {
            State = ScanPipelineState.Idle;
            Emit(rawCode);
            RaiseProcessed(new ScanOutcome.Emit(rawCode, rawCode));
            return;
        }

        // 步骤 3 / Step 3
        //
        // 解析只调用一次，结果存下来复用。看起来是小事，其实不是：正则解析
        // 带有限超时（规格 §8.2），而超时是**不确定的**——同一个输入调两次，
        // 完全可能一次成功、一次超时。调两次意味着"判断走哪条分支"用的是
        // 第一次的结果，而"报告失败原因"用的是第二次的，两者可以不一致。
        // 那种缺陷极其罕见、无法复现，而且只在最坏的规则上发作。
        //
        // Parse once and reuse the result. This looks like a triviality and is not: regex
        // parsing carries a finite timeout (spec §8.2) and a timeout is *nondeterministic* —
        // the same input can succeed on one call and time out on the next. Calling twice would
        // branch on the first result while reporting the second's reason, and the two can
        // disagree. Such a defect is vanishingly rare, irreproducible, and fires only on the
        // worst-behaved rules.
        var parseResult = SkuParser.Parse(rawCode);
        if (parseResult is not ParseResult.Success parsed)
        {
            EnterPendingError(rawCode);
            RaiseProcessed(new ScanOutcome.ParseFailed((ParseResult.Failure)parseResult));
            return;
        }

        // 步骤 4 / Step 4
        if (SkuValidator.Validate(parsed.Sku) is ValidationResult.Invalid invalid)
        {
            EnterPendingError(rawCode);
            RaiseProcessed(new ScanOutcome.ValidationFailed(rawCode, parsed.Sku, invalid));
            return;
        }

        // 步骤 5 / Step 5
        State = ScanPipelineState.Idle;
        Emit(parsed.Sku);
        RaiseProcessed(new ScanOutcome.Emit(parsed.Sku, rawCode));
    }

    /// <summary>
    /// 中文：进入待决错误，记下完整的原始码与当时的模式。
    ///       F10 强制发送的是**原始码**而不是部分解析出来的候选值（规格 §10）。
    /// English: Enters the pending-error state, recording the complete raw code and the mode in
    ///          force. Force Send emits the raw code, not a partially parsed candidate
    ///          (spec §10).
    /// </summary>
    private void EnterPendingError(string rawCode)
    {
        PendingError = new PendingScanError(rawCode, _modeManager.CurrentMode);
        State = ScanPipelineState.PendingError;
    }

    private void RaiseProcessed(ScanOutcome outcome)
        => ScanProcessed?.Invoke(this, new ScanProcessedEventArgs(outcome));
}
