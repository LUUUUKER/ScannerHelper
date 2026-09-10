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
        ISkuValidator skuValidator)
    {
        ArgumentNullException.ThrowIfNull(correlator);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modeManager);
        ArgumentNullException.ThrowIfNull(skuParser);
        ArgumentNullException.ThrowIfNull(skuValidator);

        _correlator = correlator;
        _session = session;
        _modeManager = modeManager;
        SkuParser = skuParser;
        SkuValidator = skuValidator;
    }

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
                return HookAction.PassThrough;

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
    /// 中文：清除待决错误，回到空闲。Task 7 的 F10 强制发送与 Esc 取消都会
    ///       在处理完之后调用它（规格 §10：两者都要回到出错前的界面状态）。
    /// English: Clears the pending error and returns to Idle. Task 7's Force Send and Cancel
    ///          both call it after handling (spec §10: both return to the pre-error UI state).
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

            ReplayRequested?.Invoke(this, new ReplayRequestedEventArgs(resolution.Event));
        }
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
