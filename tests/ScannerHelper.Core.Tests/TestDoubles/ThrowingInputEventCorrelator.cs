// =============================================================================
// ThrowingInputEventCorrelator.cs
//
// 中文：
//   一个**一旦被碰就抛异常**的关联器替身。
//
//   ★ 它存在的唯一理由，是把规格 §5.7 的一句话变成可执行的断言：
//
//       「PAUSED 必须独立于关联、缓冲、重放三者的正确性。」
//
//     这句话不是风格建议。PAUSED 存在的意义就是当那三件事本身坏掉时，
//     把工人救出来。一个先问关联器、再看暂停标志的协调器，会把关联器的
//     每一个缺陷都继承进这个唯一用来逃离缺陷的状态——它在别的时候都工作
//     得好好的，偏偏在最需要它的那一刻跟着一起坏。
//
//     这种依赖关系用普通的替身测不出来：一个"什么都不做"的假关联器，
//     无论协调器有没有调用它，测试都是绿的。只有让它一被调用就炸，
//     "没有调用过"这件事才成为一个可以断言的事实。
//
//   规格假设 A4 把赌注抬高了：工位是笔记本，没有备用键盘可插。台式机上
//   "键盘失灵"还能插一把新的救急；笔记本上唯一的出路就是用触摸板点那个
//   暂停按钮。所以这条独立性不是设计洁癖，是最后一道退路。
//
// English:
//   A correlator substitute that throws the moment it is touched.
//
//   It exists to turn one sentence of spec §5.7 into an executable assertion: PAUSED must be
//   independent of the correctness of correlation, buffering and replay.
//
//   That is not a stylistic preference. PAUSED exists to rescue the operator when those three
//   are themselves broken, and a coordinator that consulted the correlator before checking the
//   paused flag would inherit its every defect into the one state meant to escape them —
//   working perfectly at all other times and failing precisely when it is needed.
//
//   An ordinary substitute cannot detect that dependency: a do-nothing fake leaves the test
//   green whether or not the coordinator called it. Only by throwing on contact does "it was
//   never called" become an assertable fact.
//
//   Spec assumption A4 raises the stakes: the workstation is a laptop with no spare keyboard.
//   On a desktop "the keyboard stopped working" can be worked around by plugging in another;
//   on a laptop the only way out is the touchpad and the pause button. This independence is
//   not fastidiousness but the last line of retreat.
//
// 包含的类型 / Types in this file:
//   ThrowingInputEventCorrelator
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;

namespace ScannerHelper.Core.Tests.TestDoubles;

/// <summary>
/// 中文：一被调用就抛异常的关联器替身。
/// English: A correlator substitute that throws on any call.
/// </summary>
public sealed class ThrowingInputEventCorrelator : IInputEventCorrelator
{
    private const string Message =
        "暂停状态下不得咨询关联器。规格 §5.7 要求 PAUSED 独立于关联、缓冲、重放"
        + "三者的正确性——它存在的意义就是当那几件事坏掉时救人。"
        + " The correlator must not be consulted while paused. Spec §5.7 requires PAUSED to be"
        + " independent of the correctness of correlation, buffering and replay, because it"
        + " exists to rescue the operator when those are broken.";

    /// <summary>
    /// 中文：读写它本身不算"咨询关联器"，因此不抛——协调器可能在暂停/恢复时
    ///       合理地更新绑定。只有真正参与判断的方法才抛。
    /// English: Reading or writing this is not "consulting the correlator" and does not throw:
    ///          the coordinator may legitimately update the binding around pausing. Only the
    ///          methods that actually make decisions throw.
    /// </summary>
    public long? BoundScannerDeviceId { get; set; }

    /// <inheritdoc />
    public HookCorrelationOutcome AcceptHookEvent(in KeyEvent hookEvent)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public IReadOnlyList<ResolvedKeyEvent> AcceptRawInputEvent(in RawInputEvent rawInputEvent)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public IReadOnlyList<ResolvedKeyEvent> Advance()
        => throw new InvalidOperationException(Message);

    /// <summary>
    /// 中文：
    ///   Flush **不抛**，这是刻意的。
    ///
    ///   进入暂停时协调器必须先把扣留中的按键放出去，否则那几次按键会永久
    ///   消失——一个会吃掉按键的安全阀，在最需要它的那一刻反而加重故障。
    ///   所以"暂停时调用 Flush"是**正确行为**，替身不该把它当成违规。
    ///
    ///   本类要抓的是"暂停之后还在拿关联器做判断"，不是"暂停的过程中收了尾"。
    /// English:
    ///   Flush deliberately does not throw.
    ///
    ///   On entering PAUSED the coordinator must release withheld keystrokes first, or they
    ///   are lost permanently — a safety valve that swallows keystrokes makes the failure worse
    ///   exactly when it is needed. Calling Flush while pausing is therefore correct behavior
    ///   and the substitute must not treat it as a violation.
    ///
    ///   What this class catches is decisions still being routed through the correlator *after*
    ///   pausing, not the tidying-up that pausing itself performs.
    /// </summary>
    public IReadOnlyList<ResolvedKeyEvent> Flush() => [];
}
