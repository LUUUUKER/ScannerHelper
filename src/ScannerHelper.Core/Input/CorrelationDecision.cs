// =============================================================================
// CorrelationDecision.cs
//
// 中文：
//   关联器对一次按键给出的判断（规格 §17 规定了这四个取值）。
//
//   ★★ 四个取值的含义，以及**调用方各自该做什么**：
//
//     PassThrough  不要拦截，让 Windows 照常把这次按键送给焦点程序。
//                  用于：已确知不是扫码枪（Raw Input 先到的情形），
//                        以及本程序自己合成的事件（规格 §5.6）。
//
//     Swallow      吞掉，业务软件永远不会看到它。这是扫码枪的数据，
//                  交给扫描会话去攒。
//
//     Undecided    ★ 这个取值最容易被误解，务必看清：
//                  **它不是"什么都不做"，而是"先吞掉，我稍后告诉你结果"。**
//
//                  钩子回调必须当场返回放行或吞掉，不能阻塞等待——等待超过
//                  LowLevelHooksTimeout（默认 300 毫秒），Windows 会跳过回调、
//                  通常还把钩子摘掉且不发任何通知（规格 §19.1）。
//
//                  而 Task 4a 实测：钩子 100% 先于 WM_INPUT 到达，最小时差
//                  110 微秒，一次例外都没有。所以决策时设备身份**从来**不在手上。
//                  规格 §5.3 的扣留-重放因此不是可选方案，是唯一方案：
//                  先全吞掉，等 Raw Input 说明来源，若是普通键盘再补发回去。
//
//     Replay       这个事件之前被扣留（吞掉）了，现在确认它来自普通键盘，
//                  必须用 SendInput 把它补发出去。
//
//                  ★ 收到 Replay 却不补发 = 工人的按键凭空消失。规格 §19
//                    明写"绝不无限期吞掉普通键盘输入"，而笔记本工位没有
//                    备用键盘可插（规格假设 A4），键盘失灵就是彻底失灵。
//
//   ★ 为什么"已确知不是扫码枪"给 PassThrough 而不是 Replay。
//
//     两者结果看似都是"这次按键最终到达业务软件"，代价却差得远。
//     PassThrough 是让 Windows 原样送达，中文输入法的组字过程完全不受影响；
//     Replay 要先吞掉再合成一个事件补发，而合成事件正是最容易打断 IME 组字的
//     东西——规格 §4.3 把 IME 单列为验收项就是为此。
//
//     所以只要能在钩子回调那一刻就确定不是扫码枪（Raw Input 恰好先到，
//     实测约占 0.6%），就走 PassThrough，省掉一次扣留-重放。
//
// English:
//   The correlator's verdict on one keystroke. Spec §17 fixes these four values.
//
//   PassThrough — do not intercept; let Windows deliver the keystroke normally. Used when
//   the source is already known not to be the scanner (Raw Input happened to arrive
//   first) and for this application's own synthesized events (spec §5.6).
//
//   Swallow — consume it; the business application never sees it. This is scanner data,
//   handed to the scan session.
//
//   Undecided — the value most easily misread. It does not mean "do nothing"; it means
//   "swallow for now, I will tell you the answer shortly". The hook callback must return
//   pass-or-swallow synchronously and cannot block: waiting past LowLevelHooksTimeout
//   (300 ms by default) has Windows skip the callback and usually remove the hook without
//   notification (spec §19.1). Task 4a measured the hook arriving before WM_INPUT in
//   100% of cases with a minimum delta of 110 µs and no exception, so device identity is
//   never in hand at decision time. Spec §5.3's withhold-then-replay is therefore not one
//   option among several but the only one.
//
//   Replay — this event was previously withheld, has turned out to come from an ordinary
//   keyboard, and must be re-emitted through SendInput. Receiving Replay and not
//   re-emitting means the operator's keystroke vanishes. Spec §19 states plainly that
//   normal keyboard input must never be swallowed indefinitely, and a laptop workstation
//   has no spare keyboard to plug in (spec assumption A4): a dead keyboard is simply dead.
//
//   Why "known not to be the scanner" yields PassThrough rather than Replay: both end
//   with the keystroke reaching the application, but the cost differs sharply.
//   PassThrough lets Windows deliver it untouched and leaves IME composition entirely
//   alone, whereas Replay swallows it and synthesizes a replacement — and synthesized
//   events are the thing most likely to break IME composition, which is why spec §4.3
//   lists IME as its own acceptance item. So whenever the source can be settled at
//   callback time (Raw Input arriving first, measured at roughly 0.6%), PassThrough saves
//   a withhold-and-replay round trip.
//
// 包含的类型 / Types in this file:
//   CorrelationDecision
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：关联器对一次按键的判断。各取值下调用方该做什么，见文件头。
/// English: The correlator's verdict on one keystroke. What the caller must do for each
///          value is set out in the file header.
/// </summary>
public enum CorrelationDecision
{
    /// <summary>
    /// 中文：**先吞掉，稍后给出结果。** 不是"什么都不做"。
    ///       后续会以 <see cref="Swallow"/> 或 <see cref="Replay"/> 的形式给出结论。
    /// English: **Swallow for now; the answer follows.** Not "do nothing". The conclusion
    ///          arrives later as <see cref="Swallow"/> or <see cref="Replay"/>.
    /// </summary>
    Undecided,

    /// <summary>
    /// 中文：不拦截，让 Windows 照常送达焦点程序。
    /// English: Do not intercept; let Windows deliver it normally.
    /// </summary>
    PassThrough,

    /// <summary>
    /// 中文：吞掉。这是扫码枪的数据，业务软件不该看到它。
    /// English: Swallow it. This is scanner data and the business application must not
    ///          see it.
    /// </summary>
    Swallow,

    /// <summary>
    /// 中文：之前被扣留的事件确认来自普通键盘，必须补发出去。
    ///       收到它却不补发，等于让工人的按键凭空消失（规格 §19）。
    /// English: A previously withheld event has turned out to come from an ordinary
    ///          keyboard and must be re-emitted. Receiving this and not re-emitting makes
    ///          the operator's keystroke vanish (spec §19).
    /// </summary>
    Replay,
}
