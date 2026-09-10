// =============================================================================
// CorrelationOutcome.cs
//
// 中文：
//   关联器返回的两种结果形状。
//
//   ★ 为什么每次调用都可能带回"之前扣留的事件现在有结论了"。
//
//     扣留是异步的：钩子事件先到，被扣下；结论要等对应的 Raw Input 事件到达，
//     或者等到超时。这两件事都发生在**后续某一次调用**里，因此任何一次调用
//     都可能顺带解决掉之前积压的事件。
//
//     把这些结论作为返回值交出去，而不是让关联器自己发事件，是为了守住
//     规格 §17 的要求：关联器必须是一个**对时间戳事件序列的纯函数**——
//     它消费事件、返回判断，不发事件、不碰输出、不碰时钟（时钟经 ISystemClock）。
//     这是它能用合成序列穷尽测试的全部前提，而它是全产品风险最高的组件。
//
//   ★ 为什么钩子事件与 Raw Input 事件的返回形状不同。
//
//     钩子事件有一个**必须当场回答**的问题：这一下放行还是吞掉？回调不能等。
//     Raw Input 事件没有这个问题——它到达时按键早已在路上，拦不住了，
//     它唯一的作用是告诉我们之前那些扣留的事件属于谁。
//
//     所以钩子那一侧的返回值多一个 Decision 字段，Raw Input 那一侧只有结论列表。
//     强行统一成一个类型，就得给 Raw Input 塞一个没有意义的 Decision 值，
//     而"没有意义的取值"迟早会被某处代码当真。
//
// English:
//   The two result shapes the correlator returns.
//
//   Why any call may bring back "previously withheld events now have an answer":
//   withholding is asynchronous. A hook event arrives and is withheld; its answer waits
//   for the matching Raw Input event or for expiry, both of which happen during some
//   *later* call. Any call may therefore incidentally resolve an earlier backlog.
//
//   Handing those conclusions back as return values, rather than having the correlator
//   raise events, is what preserves spec §17's requirement that it remain a pure function
//   over a sequence of timestamped events: it consumes events and returns decisions, and
//   never raises, emits, or reads a clock except through ISystemClock. That is the entire
//   precondition for testing it exhaustively with synthetic sequences — and it is the
//   highest-risk component in the product.
//
//   Why the two shapes differ: a hook event carries a question that must be answered on
//   the spot — pass or swallow — because the callback cannot wait. A Raw Input event
//   carries no such question: by the time it arrives the keystroke is already on its way
//   and cannot be stopped, and its sole purpose is to say who the withheld events belonged
//   to. So the hook side returns an extra Decision while the Raw Input side returns only
//   a list. Forcing them into one type would mean giving the Raw Input side a meaningless
//   Decision value, and a meaningless value is eventually taken seriously by some caller.
//
// 包含的类型 / Types in this file:
//   ResolvedKeyEvent        一个之前被扣留、现在有了结论的事件
//   HookCorrelationOutcome  钩子事件的返回形状
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：一个之前被扣留、现在有了结论的按键事件。
/// English: A previously withheld keystroke that now has an answer.
/// </summary>
/// <param name="Event">
/// 中文：当初被扣留的那个事件，原样带回。判定为 <see cref="CorrelationDecision.Replay"/>
///       时，调用方要按它补发——所以扫描码、扩展位、方向都必须原封不动。
/// English: The withheld event, returned unchanged. On
///          <see cref="CorrelationDecision.Replay"/> the caller re-emits from it, so its
///          scan code, extended flag and direction must survive untouched.
/// </param>
/// <param name="Decision">
/// 中文：结论。只可能是 <see cref="CorrelationDecision.Swallow"/>（是扫码枪的数据）
///       或 <see cref="CorrelationDecision.Replay"/>（是普通键盘，必须补发）。
///       不会是 Undecided——那是"还没有结论"，而本类型的存在就意味着有结论了。
/// English: The answer, always either <see cref="CorrelationDecision.Swallow"/> (scanner
///          data) or <see cref="CorrelationDecision.Replay"/> (an ordinary keyboard, so
///          re-emit). Never Undecided, which means "no answer yet" while this type's
///          existence means there is one.
/// </param>
/// <param name="DeviceId">
/// 中文：判定来源的设备，null 表示**没能判定**——对应的 Raw Input 事件始终没有到来，
///       事件是按超时规则处理的（决策 D-13）。
///
///       null 不是可以忽略的细节：它意味着关联在这一次失败了。若它频繁出现，
///       说明关联机制本身有问题，必须被看见而不是被当作正常情况（规格 §19）。
/// English: The device the answer came from, or null when there was no answer — the
///          matching Raw Input event never arrived and the event was resolved by the
///          expiry rule (decision D-13).
///
///          Null is not an ignorable detail: it means correlation failed for that event.
///          Frequent nulls mean the mechanism itself is faulty and must be seen rather
///          than treated as routine (spec §19).
/// </param>
public readonly record struct ResolvedKeyEvent(
    KeyEvent Event,
    CorrelationDecision Decision,
    long? DeviceId);

/// <summary>
/// 中文：接纳一个钩子事件之后的返回结果。
/// English: The result of accepting one hook event.
/// </summary>
/// <param name="Decision">
/// 中文：**针对刚刚接纳的那个事件**的当场判断。钩子回调据此决定放行还是吞掉。
///       取 <see cref="CorrelationDecision.Undecided"/> 时应当吞掉并等待后续结论。
/// English: The on-the-spot verdict for the event just accepted, which the hook callback
///          uses to pass or swallow. <see cref="CorrelationDecision.Undecided"/> means
///          swallow and await the answer.
/// </param>
/// <param name="Resolved">
/// 中文：本次调用顺带解决掉的、**之前**被扣留的事件。通常为空。
///       不为空的情形：本次调用触发了超时清理，把等待过久的事件按决策 D-13 结掉了。
/// English: Previously withheld events that this call incidentally resolved. Usually
///          empty; non-empty when the call triggered an expiry sweep that settled
///          over-aged events under decision D-13.
/// </param>
public readonly record struct HookCorrelationOutcome(
    CorrelationDecision Decision,
    IReadOnlyList<ResolvedKeyEvent> Resolved);
