// =============================================================================
// IInputEventCorrelator.cs
//
// 中文：
//   两条输入通道的关联契约（规格 §17）。
//
//   ★ 为什么这里有一个接口，而规格的目录结构里只列了实现类。
//
//     规格 §17 说得很明白：关联器"被刻意隔离出来，以便在 spike 揭示可靠性
//     问题时可以整体替换"。接口正是这句话的落实——它让替换成为一次实现的
//     更换，而不是一次跨越多个文件的手术。
//
//     还有一个当下就用得上的理由：规格 §5.7 要求 PAUSED **独立于关联、缓冲、
//     重放三者的正确性**，因为暂停存在的意义就是当那三件事坏掉时救人。
//     这条独立性要被测出来，唯一的办法是给协调器塞一个"一旦被调用就抛异常"
//     的关联器替身，然后确认暂停状态下它一次都没被碰过。没有接口，这条
//     测试写不出来（用例 CO1）。
//
//   ★ 实现必须保持为**对时间戳事件序列的纯函数**（规格 §17）。
//
//     它消费事件、返回判断。不发送输入，不写日志，不碰 UI，不直接读时钟
//     （时间一律经 ISystemClock）。这不是洁癖：关联器是全产品风险最高的
//     组件，而它能被穷尽测试的唯一前提，就是它的全部行为都由输入序列决定。
//     一旦它开始自己做事，那些分支就只能靠真实硬件去撞了。
//
// English:
//   The contract for correlating the two input channels (spec §17).
//
//   Why an interface exists when the spec's folder listing names only the implementation:
//   spec §17 states that the correlator is deliberately isolated so the implementation
//   can be replaced if the spike reveals reliability issues, and an interface is what
//   makes that a swap of one implementation rather than surgery across several files.
//
//   There is also an immediate reason. Spec §5.7 requires PAUSED to be independent of the
//   correctness of correlation, buffering and replay, because pausing exists to rescue the
//   operator when those are broken. The only way to *test* that independence is to hand
//   the coordinator a correlator substitute that throws if touched, and confirm it is
//   never touched while paused. Without an interface that test cannot be written (case
//   CO1).
//
//   Implementations must remain pure functions over a sequence of timestamped events
//   (spec §17): consume events, return decisions; send no input, write no log, touch no
//   UI, and read no clock except through ISystemClock. This is not fastidiousness. The
//   correlator is the highest-risk component in the product, and the sole precondition for
//   testing it exhaustively is that its behavior is determined entirely by the input
//   sequence. The moment it starts doing things on its own, those branches can only be hit
//   with real hardware.
//
// 包含的类型 / Types in this file:
//   IInputEventCorrelator
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：把低层钩子事件与 Raw Input 事件对应起来，判断每一次按键的来源。
/// English: Matches low-level hook events to Raw Input events to decide where each
///          keystroke came from.
/// </summary>
public interface IInputEventCorrelator
{
    /// <summary>
    /// 中文：
    ///   当前绑定的扫码枪设备标识，null 表示尚未绑定。
    ///
    ///   ★ 未绑定时的行为是**安全的那一侧**：没有任何设备会被判定为扫码枪，
    ///     因此没有任何按键会被吞掉，键盘完全正常。首次运行、重新绑定期间
    ///     都处于这个状态（规格 §6），此时把按键吞掉才是灾难。
    ///
    ///   可写，因为绑定会在运行中改变——重新绑定、断开重连（规格 §6）。
    /// English:
    ///   The bound scanner's device identifier, or null when nothing is bound.
    ///
    ///   Unbound behavior errs to the safe side: no device is judged to be the scanner, so
    ///   nothing is swallowed and the keyboard works normally. First run and rebinding both
    ///   sit in this state (spec §6), and swallowing keystrokes there would be the disaster.
    ///
    ///   Settable, because the binding changes at runtime through rebinding and reconnection.
    /// </summary>
    long? BoundScannerDeviceId { get; set; }

    /// <summary>
    /// 中文：
    ///   接纳一个低层钩子事件，并给出**当场**的判断。
    ///   输入：hookEvent 钩子观测到的按键。
    ///   输出：当场判断，外加本次调用顺带解决的、之前被扣留的事件。
    ///
    ///   ★ 调用方必须**同步**处理返回值。钩子回调不能阻塞等待——超过
    ///     LowLevelHooksTimeout（默认 300 毫秒），Windows 会跳过回调、通常还把
    ///     钩子摘掉且不发通知（规格 §19.1）。
    ///
    ///   返回 <see cref="CorrelationDecision.Undecided"/> 意味着**吞掉并等待**，
    ///   而不是"什么都不做"，详见 <see cref="CorrelationDecision"/>。
    /// English:
    ///   Accepts one low-level hook event and returns the on-the-spot verdict, plus any
    ///   previously withheld events this call incidentally resolved.
    ///
    ///   The caller must handle the result synchronously: a hook callback cannot block, and
    ///   exceeding LowLevelHooksTimeout has Windows skip it and usually remove the hook
    ///   without notification (spec §19.1).
    ///
    ///   <see cref="CorrelationDecision.Undecided"/> means swallow and wait, not "do
    ///   nothing"; see <see cref="CorrelationDecision"/>.
    /// </summary>
    HookCorrelationOutcome AcceptHookEvent(in KeyEvent hookEvent);

    /// <summary>
    /// 中文:
    ///   接纳一个 Raw Input 事件。
    ///   输入：rawInputEvent 带设备身份的按键。
    ///   输出：因为这条身份信息而得出结论的、之前被扣留的事件；可能为空。
    ///
    ///   这条通道没有"当场判断"可给：事件到达时按键早已在送往焦点程序的路上，
    ///   拦不住了。它唯一的作用是揭示来源。
    /// English:
    ///   Accepts one Raw Input event and returns any previously withheld events its device
    ///   identity resolved; possibly none.
    ///
    ///   This channel has no on-the-spot verdict to give: by the time an event arrives the
    ///   keystroke is already on its way to the focused application and cannot be stopped.
    ///   Its only role is to reveal the source.
    /// </summary>
    IReadOnlyList<ResolvedKeyEvent> AcceptRawInputEvent(in RawInputEvent rawInputEvent);

    /// <summary>
    /// 中文：
    ///   推进时间，结掉等待过久的事件。
    ///   输入：无（时间经 ISystemClock 读取）。
    ///   输出：因超时而得出结论的事件；可能为空。
    ///
    ///   ★ 调用方必须**周期性**调用它，不能只在有事件时调用。
    ///
    ///     一个被扣留的事件，若对应的 Raw Input 始终不来、之后又恰好没有新按键，
    ///     它就会一直卡在那里——而"卡在那里"意味着工人的某一次按键被永久吞掉了。
    ///     规格 §19 明写：绝不无限期吞掉普通键盘输入。定时推进是这条要求的实现。
    /// English:
    ///   Advances time and settles over-aged events, returning any it resolved.
    ///
    ///   The caller must call this periodically, not only when events arrive. A withheld
    ///   event whose Raw Input counterpart never comes, followed by no further keystrokes,
    ///   would otherwise sit there forever — and sitting there means one of the operator's
    ///   keystrokes was swallowed permanently. Spec §19 states that normal keyboard input
    ///   must never be swallowed indefinitely; the periodic advance is how that is honored.
    /// </summary>
    IReadOnlyList<ResolvedKeyEvent> Advance();
}
