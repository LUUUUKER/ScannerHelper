// =============================================================================
// ScanProcessor.cs
//
// 中文：
//   一次扫描进来之后发生的全部事情：按模式处理，产出输出或错误。
//
//   ★ 它取代了 ScanInputCoordinator 里与关联有关的那一半。
//
//     旧的协调器要同时干两件事：把键盘流里的扫码枪字符**认出来**，以及把认出来
//     的内容**处理掉**。前一半在 2026-09-10 被证明做不到
//     （TASK_4B_FINDING_20260910.md），后一半一直是对的。
//
//     串口模式下，「认出来」这件事消失了——一次扫描天然就是一个完整的原始码。
//     于是本类只剩下后一半，而后一半正是这个产品真正的业务：SN 原样发，
//     SKU 解析、校验、再发，失败就等工人决定。
//
//     少掉的东西：关联器、扣留队列、超时推进、按键领域类型、以及围绕它们的
//     全部状态与并发约束。规格 §5.1 的 IDENTIFYING 状态也随之消失
//     （ARCHITECTURE_CHANGE_SERIAL.md §4）。
//
//   ★ PAUSED 的语义变了，理由写在决策 D-26 里。
//
//     旧的 PAUSED 是键盘失灵时的救命稻草：钩子放行一切。串口模式下键盘从未被
//     碰过，那个意思不存在了。新语义是**跳过解析与校验，把原始码原样发出去**。
//
//     为什么不是「暂停 = 什么都不发」：那让工人在暂停期间完全没法录入。而这个
//     安全阀存在的本意从来是「出问题时还能干活」，不是「出问题时停工」——
//     一个会让人停工的安全阀，在最需要它的时候反而加重了故障。SKU 规则配错、
//     解析一直失败时，工人点一下暂停就能继续按原始码录入，不必等人来改配置。
//
// English:
//   Everything that happens once a scan arrives: process it under the current mode, producing
//   output or an error.
//
//   It replaces the correlation half of ScanInputCoordinator. The old coordinator did two jobs —
//   picking the scanner's characters out of the keyboard stream, and processing what it picked —
//   and the first was proven impossible on 2026-09-10 (TASK_4B_FINDING_20260910.md) while the
//   second was always right. On a serial port the picking-out disappears, because a scan is
//   inherently one complete raw code, and what remains is this product's actual business: SN goes
//   out as-is; SKU is parsed, validated and then sent; a failure waits for the operator.
//
//   Gone with it: the correlator, the pending queues, the periodic advance, the keystroke domain
//   types, and every piece of state and concurrency constraint around them — along with spec
//   §5.1's IDENTIFYING state (ARCHITECTURE_CHANGE_SERIAL.md §4).
//
//   PAUSED means something different now (decision D-26). It used to be the lifeline for a dead
//   keyboard, passing everything through the hook; with the keyboard never touched, that meaning
//   is gone. It now skips parsing and validation and emits the raw code unchanged.
//
//   Why not "paused means nothing is sent": that leaves the operator unable to enter anything at
//   all, while this valve has always existed so that work can continue when something is wrong
//   rather than stopping it — a valve that halts work makes the failure worse exactly when it is
//   needed. With a misconfigured SKU rule failing every parse, one click keeps the operator
//   entering raw codes instead of waiting for someone to fix the configuration.
//
// 包含的成员 / Members in this file:
//   State / IsPaused / PendingError  当前状态
//   OnScanReceived                   一次扫描进来
//   Pause / Resume                   安全阀（决策 D-26）
//   ForceSend / Cancel               待决错误的两个出口
//   UpdateSkuRules                   设置变更
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Output;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：把一次扫描处理成输出或错误。
/// English: Turns one scan into output or an error.
/// </summary>
public sealed class ScanProcessor
{
    private readonly ModeManager _modeManager;
    private readonly IKeyboardOutputService _output;

    /// <summary>
    /// 中文：
    ///   构造处理器。全部参数不得为 null。
    ///
    ///   解析器与校验器在构造时就要求非空，而不是等到 SKU 模式才检查：全新安装的
    ///   默认配置本来就能构造出可用的解析器与校验器（见 SkuParserFactory 的用例
    ///   PF10），因此"没有规则"只可能是组装代码的缺陷，不是正常状态。让它在启动
    ///   时就炸，好过让工人切到 SKU 模式扫第一枪时才发现。
    /// English:
    ///   Creates the processor; every argument is required.
    ///
    ///   The parser and validator are required at construction rather than checked when SKU mode
    ///   is entered: a fresh install's defaults already build usable ones (SkuParserFactory's case
    ///   PF10), so "no rule" can only be a composition defect. Failing at startup beats the
    ///   operator discovering it at the first scan after switching to SKU mode.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：任一参数为 null。 English: Any argument is null.
    /// </exception>
    public ScanProcessor(
        ModeManager modeManager,
        ISkuParser skuParser,
        ISkuValidator skuValidator,
        IKeyboardOutputService output)
    {
        ArgumentNullException.ThrowIfNull(modeManager);
        ArgumentNullException.ThrowIfNull(skuParser);
        ArgumentNullException.ThrowIfNull(skuValidator);
        ArgumentNullException.ThrowIfNull(output);

        _modeManager = modeManager;
        _output = output;
        SkuParser = skuParser;
        SkuValidator = skuValidator;
    }

    /// <summary>
    /// 中文：
    ///   扫描后是否自动补一个回车，**默认关闭**（决策 D-10、规格 §10）。
    ///
    ///   ★ 默认关闭意味着网页收到的回车数是 0，而不是"少了一个"。扫码枪的终止符
    ///     是串口的帧边界，根本不会变成按键。规格 §10 特意点明这一点，因为条码
    ///     录入的网页表单**很常见地**靠回车提交或跳到下一个字段——而这个页面到底
    ///     靠不靠，在拿到真实页面之前无从知道。
    ///
    ///   它对 SN 输出、SKU 输出、强制发送、以及暂停期间的原样输出**一视同仁**。
    /// English:
    ///   Whether to append an Enter after a scan; off by default (decision D-10, spec §10).
    ///
    ///   Off means the page receives zero Enters, not one fewer: the scanner's terminator is a
    ///   serial frame boundary and never becomes a keystroke at all. Spec §10 makes the point
    ///   because barcode entry in web forms very commonly relies on Enter to submit or advance,
    ///   and whether this page does cannot be known before the real page is in hand.
    ///
    ///   It applies equally to SN output, SKU output, Force Send, and the raw output emitted
    ///   while paused.
    /// </summary>
    public bool AppendEnterAfterScan { get; set; }

    /// <summary>
    /// 中文：当前状态。
    /// English: The current state.
    /// </summary>
    public ScanPipelineState State { get; private set; } = ScanPipelineState.Idle;

    /// <summary>
    /// 中文：是否处于暂停。规格 §5.7 要求它**绝不持久化**，程序每次启动都是未暂停。
    /// English: Whether paused. Spec §5.7 requires this never be persisted; every launch starts
    ///          un-paused.
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
    /// 中文：一枪处理完毕。
    /// English: A scan finished processing.
    /// </summary>
    public event EventHandler<ScanProcessedEventArgs>? ScanProcessed;

    /// <summary>
    /// 中文：
    ///   处理一次扫描。
    ///   步骤：
    ///     1. 空码直接忽略；
    ///     2. 暂停时原样发出（决策 D-26），不解析不校验；
    ///     3. SN 模式原样发出（规格 §3）；
    ///     4. SKU 模式解析，失败进入待决错误（规格 §10）；
    ///     5. 校验，失败同样进入待决错误；
    ///     6. 发出解析结果。
    ///
    ///   ★ 步骤 1 不是防御性代码。串口上一个孤零零的终止符（例如 CRLF 被拆在
    ///     两次读取里）会切出一个空帧，那不是一枪扫描。把它当成扫描处理，
    ///     SKU 模式下会报一个工人完全无法理解的解析失败。
    ///
    ///   ★ 步骤 2 在解析之前，这个顺序是本方法唯一重要的地方。暂停存在的意义
    ///     就是绕开解析与校验，先咨询它们再看暂停标志，等于把它们的每一个缺陷
    ///     都继承进这个唯一用来逃离缺陷的状态。
    /// English:
    ///   Processes one scan: ignore an empty code; while paused emit it unchanged (decision D-26)
    ///   without parsing or validating; in SN mode emit it unchanged (spec §3); in SKU mode parse,
    ///   validate, and emit, each failure entering the pending-error state (spec §10).
    ///
    ///   Ignoring an empty code is not defensive coding: a lone terminator on the wire — a CRLF
    ///   split across two reads, say — cuts an empty frame, and that is not a scan. Treating it as
    ///   one would report a parse failure the operator cannot possibly make sense of.
    ///
    ///   Checking paused before parsing is the one ordering that matters here. PAUSED exists to
    ///   bypass parsing and validation, and consulting them first would inherit their every defect
    ///   into the one state meant to escape them.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：rawCode 为 null。 English: rawCode is null.
    /// </exception>
    public void OnScanReceived(string rawCode)
    {
        ArgumentNullException.ThrowIfNull(rawCode);

        // 步骤 1 / Step 1
        if (rawCode.Length == 0)
        {
            return;
        }

        // 步骤 2 —— 暂停：原样发出，绝不解析（决策 D-26）
        // Step 2 — paused: emit unchanged, never parse (decision D-26)
        if (IsPaused)
        {
            Emit(rawCode);
            RaiseProcessed(new ScanOutcome.EmitRawWhilePaused(rawCode));
            return;
        }

        // 步骤 2.5 —— 命令条码：切模式，不发出去（见 ScanCommand）
        //
        // ★ 位置是有讲究的：在**暂停判断之后**，在其余一切之前。
        //
        //   在暂停之后，是因为暂停的承诺没有例外——见 ScanCommand 的文件头。
        //
        //   在其余一切之前，是因为一张命令条码永远不该被当成货品去解析、校验、
        //   或者发进业务软件。放到解析后面去判断，等于让 #SH:SKU# 先去撞一遍
        //   SKU 规则：撞不上就报一个莫名其妙的解析失败，撞上了更糟——一张纸
        //   变成了一件货。
        //
        // Step 2.5 — command barcodes: switch the mode, emit nothing (see ScanCommand).
        //
        // The position matters: after the paused check, before everything else. After paused,
        // because that promise has no exceptions (see ScanCommand's header). Before everything
        // else, because a command sheet must never be parsed, validated or sent as if it were
        // goods: deciding after parsing would run #SH:SKU# through the SKU rule first, which either
        // reports a baffling parse failure or — worse — succeeds, turning a sheet of paper into an
        // item.
        var command = ScanCommand.Recognize(rawCode);

        if (command != ScanCommandKind.None)
        {
            State = ScanPipelineState.Idle;

            if (command == ScanCommandKind.Unknown)
            {
                RaiseProcessed(new ScanOutcome.UnknownCommand(rawCode));
                return;
            }

            var target = command == ScanCommandKind.SwitchToSn ? ScanMode.Sn : ScanMode.Sku;
            _modeManager.SetMode(target);
            RaiseProcessed(new ScanOutcome.ModeCommand(target, rawCode));
            return;
        }

        State = ScanPipelineState.Processing;

        // 步骤 3 —— SN 是流水线内部的恒等变换，不是旁路（规格 §5.7）
        // Step 3 — SN is the identity transform inside the pipeline, not a bypass (spec §5.7)
        if (_modeManager.CurrentMode == ScanMode.Sn)
        {
            State = ScanPipelineState.Idle;
            Emit(rawCode);
            RaiseProcessed(new ScanOutcome.Emit(rawCode, rawCode));
            return;
        }

        // 步骤 4 / Step 4
        //
        // 解析只调用一次，结果存下来复用。看起来是小事，其实不是：正则解析带有限
        // 超时（规格 §8.2），而超时是**不确定的**——同一个输入调两次，完全可能一次
        // 成功、一次超时。调两次意味着"判断走哪条分支"用的是第一次的结果，而"报告
        // 失败原因"用的是第二次的，两者可以不一致。那种缺陷极其罕见、无法复现，
        // 而且只在最坏的规则上发作。
        //
        // Parse once and reuse the result. Regex parsing carries a finite timeout (spec §8.2) and
        // a timeout is nondeterministic — the same input can succeed on one call and time out on
        // the next. Calling twice would branch on the first result while reporting the second's
        // reason, and the two can disagree: a defect that is vanishingly rare, irreproducible, and
        // fires only on the worst-behaved rules.
        var parseResult = SkuParser.Parse(rawCode);
        if (parseResult is not ParseResult.Success parsed)
        {
            EnterPendingError(rawCode);
            RaiseProcessed(new ScanOutcome.ParseFailed((ParseResult.Failure)parseResult));
            return;
        }

        // 步骤 5 / Step 5
        if (SkuValidator.Validate(parsed.Sku) is ValidationResult.Invalid invalid)
        {
            EnterPendingError(rawCode);
            RaiseProcessed(new ScanOutcome.ValidationFailed(rawCode, parsed.Sku, invalid));
            return;
        }

        // 步骤 6 / Step 6
        State = ScanPipelineState.Idle;
        Emit(parsed.Sku);
        RaiseProcessed(new ScanOutcome.Emit(parsed.Sku, rawCode));
    }

    /// <summary>
    /// 中文：
    ///   进入暂停（规格 §5.7，语义见决策 D-26）。
    ///
    ///   ★ 暂停**不改变当前模式**（规格 §5.7：暂停与 SN/SKU 正交）。本方法完全不碰
    ///     ModeManager，恢复时模式自然还是原来那个。
    ///
    ///   ★ 进入暂停会清掉待决错误：暂停的意思是"现在按原始码干活"，而待决错误
    ///     等的是"要不要按原始码发出去"——同一个问题已经被暂停这个动作回答了，
    ///     再让一个错误挂在那里等，工人会以为还有什么没处理完。
    /// English:
    ///   Enters PAUSED (spec §5.7; semantics in decision D-26).
    ///
    ///   Pausing does not change the mode — spec §5.7 makes PAUSED orthogonal to SN/SKU — and this
    ///   method never touches ModeManager, so resuming restores what was in force.
    ///
    ///   It clears any pending error: pausing means "work with raw codes from now on", which is
    ///   the very question a pending error is waiting on. Leaving one hanging would suggest to the
    ///   operator that something is still unresolved when it is not.
    /// </summary>
    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        PendingError = null;
        State = ScanPipelineState.Paused;
    }

    /// <summary>
    /// 中文：离开暂停，回到空闲。
    /// English: Leaves PAUSED, returning to Idle.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        State = ScanPipelineState.Idle;
    }

    /// <summary>
    /// 中文：
    ///   替换 SKU 规则（规格 §13.4）。
    ///   待决错误**保留**：工人正在决定的那一枪与规则变更无关，替他做主取消掉，
    ///   等于把他手里那个码悄悄丢了。
    /// English:
    ///   Replaces the SKU rules (spec §13.4). Any pending error survives: the scan the operator is
    ///   deciding about has nothing to do with a rule change, and cancelling it on their behalf
    ///   quietly discards the code in their hand.
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
    ///   强制发送待决错误里的**原始码**（规格 §10）。
    ///   输出：真的发出去了返回 true；没有待决错误返回 false。
    ///
    ///   发的是原始码而不是部分解析出来的候选值：解析失败时我们对这个码的理解
    ///   本来就是错的，拿一个错误理解的产物去发，比原样发出去更危险。
    /// English:
    ///   Force-sends the pending error's raw code (spec §10), returning false when there is none.
    ///
    ///   The raw code rather than a partially parsed candidate: when parsing failed our
    ///   understanding of the code was wrong to begin with, and emitting the product of a wrong
    ///   understanding is more dangerous than emitting it untouched.
    /// </summary>
    public bool ForceSend()
    {
        if (PendingError is not { } pendingError)
        {
            return false;
        }

        Emit(pendingError.RawCode);
        ClearPendingError();
        RaiseProcessed(new ScanOutcome.ForceSent(pendingError.RawCode));
        return true;
    }

    /// <summary>
    /// 中文：取消待决错误，什么都不发。输出：真的取消了返回 true。
    /// English: Cancels the pending error, emitting nothing. Returns whether there was one.
    /// </summary>
    public bool Cancel()
    {
        if (PendingError is not { } pendingError)
        {
            return false;
        }

        ClearPendingError();
        RaiseProcessed(new ScanOutcome.Cancelled(pendingError.RawCode));
        return true;
    }

    /// <summary>
    /// 中文：清掉待决错误并回到空闲。
    /// English: Clears the pending error and returns to Idle.
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
    ///
    ///   ★ 补回车用独立的 EmitEnter，而不是在文本末尾拼一个 '\r'。以 Unicode 字符
    ///     发出的 U+000D 是一个**字符**，而网页表单的提交行为通常挂在 Enter 的
    ///     **按键事件**上。拼成字符发出去，很可能文本框里多了个看不见的字符而表单
    ///     根本没提交——那个设置开了等于没开，现场还极难判断是设置没生效还是网页
    ///     不认。详见 IKeyboardOutputService。
    ///
    ///   本方法是 SN 输出、SKU 输出、强制发送、暂停期间原样输出共同的出口，
    ///   因此那条设置对四者一视同仁，不需要在四处各写一遍。
    /// English:
    ///   Delivers content to the business application.
    ///
    ///   The Enter goes through EmitEnter rather than being appended as '\r': U+000D sent as a
    ///   Unicode character is a character, while a web form's submit behavior normally hangs off
    ///   the Enter key event. Appending it tends to leave an invisible character in the field with
    ///   the form not submitting — the setting enabled yet ineffective, and very hard on site to
    ///   tell from a page that does not accept the input. See IKeyboardOutputService.
    ///
    ///   This is the single exit shared by SN output, SKU output, Force Send and the raw output
    ///   emitted while paused, which is what makes the setting apply equally to all four without
    ///   repeating the rule in four places.
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
    /// 中文：进入待决错误，记下完整的原始码与当时的模式。
    /// English: Enters the pending-error state, recording the complete raw code and the mode in
    ///          force.
    /// </summary>
    private void EnterPendingError(string rawCode)
    {
        PendingError = new PendingScanError(rawCode, _modeManager.CurrentMode);
        State = ScanPipelineState.PendingError;
    }

    private void RaiseProcessed(ScanOutcome outcome)
        => ScanProcessed?.Invoke(this, new ScanProcessedEventArgs(outcome));
}
