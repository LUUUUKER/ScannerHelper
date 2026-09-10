// =============================================================================
// ScanOutcome.cs
//
// 中文：
//   一枪走完整条流水线之后的结局，以及协调器的两种对外通知。
//
//   ★★ 四个分支里，最重要的区别是**哪些进入待决错误、哪些不进**。
//
//     这不是实现细节，是规格里两条不同规定的直接后果：
//
//         规格 §5.4 对扫描超时写得很明确：丢弃这次不完整的扫描、不产生任何
//         业务输入、回到 IDLE、显示一个可见的扫描错误。
//         **没有 F10 这条路。** 因为手里那半截根本不是完整的条码，把它发出去
//         等于主动往仓库系统里写错误数据。
//
//       ParseFailed / ValidationFailed
//         规格 §10 要求进入错误状态并等待工人决定：F10 强制发送原始码，
//         或 Esc 取消。这里手里是一个**完整的**原始条码，只是解析或校验
//         不通过——工人完全可能知道"这个码就是对的，规则还没配好"，
//         强制发送是合理的选择。
//
//     两者的差别是"手里那段内容完不完整"，而不是"错得严不严重"。混为一谈
//     的后果是：要么让工人无法强制发送一个本来正确的码（规则没配好时整条
//     产线卡住），要么让半截码可以被发出去（静默的错误数据，规格 §19.1）。
//
// English:
//   How a scan ended after running the whole pipeline, plus the coordinator's two outward
//   notifications.
//
//   The most important distinction among the four cases is which ones enter the pending-error
//   state and which do not — not an implementation detail but the direct consequence of two
//   different rules in the spec.
//
//   ScanFailed (timeout, empty, too long): spec §5.4 is explicit for a scan timeout —
//   discard the incomplete scan, produce no business input, return to IDLE, show a visible
//   error. There is no F10 path, because what is in hand was never a complete barcode and
//   emitting it would volunteer bad data into the warehouse system.
//
//   ParseFailed / ValidationFailed: spec §10 requires entering an error state and awaiting the
//   operator's decision — F10 to force-send the raw code, Esc to cancel. Here a *complete*
//   raw barcode is in hand and only the rule disagreed with it, and the operator may well know
//   the code is right and the rule is not yet configured, making Force Send a reasonable choice.
//
//   The difference is whether what is in hand is complete, not how severe the error is.
//   Conflating them either denies the operator a force-send for a perfectly good code — halting
//   the line whenever a rule is misconfigured — or lets half a code be emitted, which is spec
//   §19.1's silently wrong data.
//
// 包含的类型 / Types in this file:
//   ScanOutcome                   抽象基类型，构造函数私有，分支集合封闭
//   ScanOutcome.Emit              可以发送
//   ScanOutcome.ParseFailed       解析失败，进入待决错误
//   ScanOutcome.ValidationFailed  校验失败，进入待决错误
//   PendingScanError              待决错误的内容
//   ScanProcessedEventArgs        一枪处理完毕
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：一枪走完流水线之后的结局。
/// English: How a scan ended after the pipeline ran.
/// </summary>
public abstract record ScanOutcome
{
    private ScanOutcome()
    {
    }

    /// <summary>
    /// 中文：可以发送。
    /// English: Ready to emit.
    /// </summary>
    /// <param name="Text">
    /// 中文：要发送的内容。SN 模式下等于原始码，SKU 模式下是解析出来的 SKU。
    ///       规格 §10 要求原样发送——不修剪、不补齐、不改大小写。
    /// English: What to emit: the raw code in SN mode, the parsed SKU in SKU mode. Spec §10
    ///          requires it exactly as produced — no trimming, padding or case conversion.
    /// </param>
    /// <param name="RawCode">
    /// 中文：这一枪扫到的完整原始码。即便在 SKU 模式下也带着，供诊断日志记录
    ///       （规格 §15）——只记 SKU 的话，事后无法判断是条码错了还是规则错了。
    /// English: The complete raw code, carried even in SKU mode for the diagnostic log
    ///          (spec §15): with only the SKU recorded, nobody can later tell whether the
    ///          barcode or the rule was at fault.
    /// </param>
    public sealed record Emit(string Text, string RawCode) : ScanOutcome;

    /// <summary>
    /// 中文：扫描本身没能完成（超时 / 空扫描 / 过长）。
    ///       **不进入待决错误**，不提供 F10——手里那半截不是完整条码（规格 §5.4）。
    /// English: The scan itself did not complete (timeout, empty, too long). Does not enter
    ///          the pending-error state and offers no F10: what is in hand is not a complete
    ///          barcode (spec §5.4).
    /// </summary>
    /// <summary>
    /// 中文：
    ///   暂停期间原样发出的一枪（决策 D-26）。
    ///
    ///   ★ 它与 <see cref="Emit"/> 分开，而不是复用「输出内容等于原始码」那一种。
    ///     两者发出去的字节可能完全一样，含义却相反：Emit 表示"按当前规则处理过，
    ///     结论就是这个"，而这一种表示"**没有**按规则处理，因为现在处于暂停"。
    ///
    ///     合并的代价是界面再也分不清"SN 模式正常发出"和"暂停中绕过了规则"——
    ///     而工人最需要看清的恰恰是后者：暂停是一个非常态，界面必须一直提醒它
    ///     还开着，否则会有人在暂停状态下干一整天而不自知。
    /// English:
    ///   A scan emitted unchanged while paused (decision D-26).
    ///
    ///   Kept separate from <see cref="Emit"/> rather than reusing "output equals the raw code":
    ///   the bytes may be identical while the meanings are opposite. Emit says "processed under
    ///   the current rules, and this is the result"; this says "not processed at all, because we
    ///   are paused".
    ///
    ///   Merging them would leave the UI unable to distinguish a normal SN emission from a paused
    ///   bypass — and the bypass is precisely what the operator must see, PAUSED being an
    ///   exceptional state that the UI has to keep announcing, or somebody works a whole shift in
    ///   it without noticing.
    /// </summary>
    public sealed record EmitRawWhilePaused(string RawCode) : ScanOutcome;

    /// <summary>
    /// 中文：SKU 解析失败。**进入待决错误**，等待 F10 或 Esc（规格 §10）。
    /// English: SKU parsing failed. Enters the pending-error state to await F10 or Esc
    ///          (spec §10).
    /// </summary>
    /// <summary>
    /// 中文：
    ///   工人按下强制发送，原始码被发了出去（规格 §10）。
    ///
    ///   ★ 它必须是一条独立的、看得见的结局，不能只是"待决错误消失了"。
    ///
    ///     强制发送是整个产品里唯一一个**明知有问题还是发出去**的动作：解析或校验
    ///     已经说这个码不对，工人判断它其实是对的，于是绕过规则。那正是最需要留痕
    ///     的一次输出——事后追查"这条错数据是怎么进系统的"，答案往往就在这里。
    ///
    ///     不记录的后果不是少了一行日志，而是那次输出**在记录里根本不存在**：
    ///     列表上前一枪失败、什么都没发，下一枪一切正常，中间那次绕过规则的发送
    ///     无迹可寻。规格 §19.1 说的"静默的错误数据"，具体形态就是这个。
    /// English:
    ///   The operator pressed Force Send and the raw code was emitted (spec §10).
    ///
    ///   It must be a visible outcome of its own rather than merely "the pending error went away".
    ///   Force Send is the one action in this product that emits something already known to be
    ///   questionable: parsing or validation said the code was wrong, the operator judged
    ///   otherwise, and the rules were bypassed. That is the single output most in need of a
    ///   record — "how did this bad row get into the system" is usually answered here.
    ///
    ///   Without it the emission does not merely lack a log line; it does not exist in the record
    ///   at all: the list shows a failed scan that emitted nothing, then a normal one, with the
    ///   rule-bypassing emission between them leaving no trace. That is the concrete shape of spec
    ///   §19.1's silently wrong data.
    /// </summary>
    public sealed record ForceSent(string RawCode) : ScanOutcome;

    /// <summary>
    /// 中文：
    ///   工人按下取消，那一枪被丢弃，什么都没发（规格 §10）。
    ///
    ///   ★ 「什么都没发」同样值得记一行。取消意味着一枪数据没有进系统，而工人
    ///     多半会重扫一次——但如果他没有，那件货就漏了。事后看列表能看出
    ///     "这里有一枪被丢掉了"，比看不出来强得多。
    /// English:
    ///   The operator pressed Cancel; the scan was discarded and nothing was emitted (spec §10).
    ///
    ///   "Nothing was emitted" deserves a line too. A cancellation means one scan did not reach the
    ///   system, and the operator will usually rescan — but if they do not, that item is simply
    ///   missed. Seeing "a scan was discarded here" afterwards beats not seeing it.
    /// </summary>
    public sealed record Cancelled(string RawCode) : ScanOutcome;

    public sealed record ParseFailed(ParseResult.Failure Failure) : ScanOutcome;

    /// <summary>
    /// 中文：SKU 校验失败。**进入待决错误**，等待 F10 或 Esc（规格 §10）。
    /// English: SKU validation failed. Enters the pending-error state to await F10 or Esc
    ///          (spec §10).
    /// </summary>
    /// <param name="RawCode">
    /// 中文：完整原始码。F10 强制发送的正是它，而不是解析出来的候选 SKU
    ///       （规格 §10："Force Send 始终发送原始扫描码"）。
    /// English: The complete raw code. Force Send emits this, not the parsed candidate
    ///          (spec §10: "Force Send always emits the raw scanned code").
    /// </param>
    /// <param name="Sku">
    /// 中文：解析出来的候选 SKU。带上它是为了让错误界面能说清"从这个码里取出了
    ///       这一段，但它没通过校验"——只说"校验失败"，工人无从判断是取错了
    ///       还是取对了但内容不合规。
    /// English: The parsed candidate. Carried so the error screen can say "this was extracted
    ///          and it failed validation"; a bare "validation failed" leaves the operator
    ///          unable to tell a wrong extraction from a correct one with unacceptable content.
    /// </param>
    /// <param name="Failure">
    /// 中文：全部失败原因（决策 D-6：组合校验器收集所有失败，不短路）。
    /// English: Every failure (decision D-6: the composite collects them all rather than
    ///          short-circuiting).
    /// </param>
    public sealed record ValidationFailed(
        string RawCode, string Sku, ValidationResult.Invalid Failure) : ScanOutcome;
}

/// <summary>
/// 中文：一个正在等待工人决定的错误（规格 §10）。
/// English: An error awaiting the operator's decision (spec §10).
/// </summary>
/// <param name="RawCode">
/// 中文：完整的原始扫描码。F10 强制发送的就是它——规格 §10 明确要求发送
///       **原始码**而不是部分解析出来的候选值。
/// English: The complete raw scanned code, which is what Force Send emits — spec §10
///          requires the raw code rather than a partially parsed candidate.
/// </param>
/// <param name="Mode">
/// 中文：出错时所处的模式。规格 §10 要求强制发送与取消都**保持当前模式不变**，
///       记下它是为了让诊断日志能还原当时的上下文（规格 §15）。
/// English: The mode in force when the error occurred. Spec §10 requires both Force Send and
///          Cancel to preserve the current mode; recording it lets the diagnostic log
///          reconstruct the context (spec §15).
/// </param>
public sealed record PendingScanError(string RawCode, ScanMode Mode);

/// <summary>
/// 中文：一枪处理完毕。
/// English: A scan finished processing.
/// </summary>
public sealed class ScanProcessedEventArgs : EventArgs
{
    /// <summary>
    /// 中文：构造处理完毕事件。
    /// English: Creates the event.
    /// </summary>
    public ScanProcessedEventArgs(ScanOutcome outcome) => Outcome = outcome;

    /// <summary>
    /// 中文：这一枪的结局。
    /// English: How the scan ended.
    /// </summary>
    public ScanOutcome Outcome { get; }
}
