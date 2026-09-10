// =============================================================================
// ChannelPairing.cs
//
// 中文：
//   把两条通道上属于同一次物理按键的事件配对起来（Task 4a 的第 1、2 个问题）。
//
//   这是整个诊断工具的核心一步，也是最容易做错的一步。
//
//   ★ 配对依据：按 (虚拟键码, 按下还是弹起) 分组，组内先进先出。
//
//     两条通道看到的是**同一串**物理按键，顺序一致、数量一致。所以对每一种
//     按键身份各维护两个队列，一条通道来了事件就去另一条的队列里找对家：
//     找到就配成一对，找不到就自己排队等着。
//
//     不按时间就近配对，是因为那正好会把要测的东西当成前提：若假定"时间最近
//     的两个事件属于同一次按键"，测出来的时差必然很小——测量方法本身保证了
//     结论。按顺序配对不含这个假设，无论时差是 50 微秒还是 50 毫秒，配出来的
//     都是真正的同一次按键。
//
//   ★ 规格 §4.2 那条约束是这个方法成立的前提：扫描进行中，工人不会同时打字。
//     一次扫描是一串原子的按键，两条通道各看一遍，顺序不会交错。若这条前提
//     在现场不成立，本配对方法会给出错位的结果——而错位之后的时差是纯噪声，
//     却长得和真数据一模一样。所以 4a 同时也在检验这条前提本身。
//
//   ★ 未配对事件必须被记账，不能悄悄丢掉。
//
//     一条通道看得到、另一条看不到的事件，本身就是极重要的发现：可能意味着
//     某类按键根本不经过 Raw Input，或者钩子漏掉了什么。若把它们静静丢弃，
//     报告会显得干净漂亮，而最值得注意的现象恰好被抹掉了。
//
// English:
//   Pairs up the events the two channels saw for one physical keystroke (4a's first
//   and second questions). The core step of the whole tool, and the easiest to get
//   wrong.
//
//   Pairing is by (virtual key, down-or-up) with first-in-first-out within each group.
//   Both channels see the *same* sequence of physical keystrokes, in the same order and
//   the same number. So each key identity keeps one queue per channel: an arriving
//   event either finds its counterpart waiting in the other queue and forms a pair, or
//   joins its own queue.
//
//   Pairing by nearest timestamp is deliberately avoided, because it would assume the
//   very thing being measured. "The two closest events belong to the same keystroke"
//   guarantees a small measured delta by construction — the method would produce the
//   conclusion. Order-based pairing carries no such assumption and pairs the genuine
//   same keystroke whether the delta is 50 µs or 50 ms.
//
//   Spec §4.2's constraint is the precondition that makes this work: the operator does
//   not type during an active scan. A scan is an atomic run of keystrokes that each
//   channel sees once, without interleaving. If that precondition fails on site, this
//   method yields shifted pairings — and a delta computed from shifted pairs is pure
//   noise that looks exactly like real data. So 4a is also testing the precondition
//   itself.
//
//   Unpaired events must be accounted for rather than dropped. An event one channel saw
//   and the other did not is itself a highly significant finding: perhaps some class of
//   key never reaches Raw Input, or the hook missed something. Discarding them quietly
//   would produce a clean-looking report with the most notable phenomenon erased.
//
// 包含的成员 / Members in this file:
//   PairedObservation  一次配对的结果
//   ChannelPairing     配对器
// =============================================================================

using System.Diagnostics;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：同一次物理按键在两条通道上的一对观测。
/// English: One physical keystroke as observed on both channels.
/// </summary>
/// <param name="VirtualKey">中文：虚拟键码。 English: The virtual key code.</param>
/// <param name="IsKeyUp">中文：是否为弹起。 English: Whether this is a key up.</param>
/// <param name="DeviceHandle">
/// 中文：产生该按键的设备句柄，来自 Raw Input 那一侧。
/// English: The device that produced it, from the Raw Input side.
/// </param>
/// <param name="DeltaMicroseconds">
/// 中文：Raw Input 时间戳减去钩子时间戳，单位微秒。
///       **正数表示钩子先到**，负数表示 Raw Input 先到。
///       正数意味着钩子回调必须做决定时设备身份还不可用——那么规格 §5.3 的
///       扣留-重放就是唯一可行的设计。
/// English: The Raw Input timestamp minus the hook timestamp, in microseconds.
///          A positive value means the hook came first; negative means Raw Input did.
///          Positive means device identity is unavailable when the callback must
///          decide, which makes spec §5.3's withhold-and-replay the only viable design.
/// </param>
public readonly record struct PairedObservation(
    ushort VirtualKey,
    bool IsKeyUp,
    nint DeviceHandle,
    double DeltaMicroseconds);

/// <summary>
/// 中文：两条通道的事件配对器。
/// English: Pairs events across the two channels.
/// </summary>
public sealed class ChannelPairing
{
    /// <summary>
    /// 中文：
    ///   每种按键身份各自等待配对的事件。键是 (虚拟键码, 是否弹起)，
    ///   值是两条通道各自的等待队列。
    /// English:
    ///   Events awaiting a counterpart, keyed by (virtual key, is-key-up), with one
    ///   waiting queue per channel.
    /// </summary>
    private readonly Dictionary<(ushort VirtualKey, bool IsKeyUp), PendingEvents> _pending = [];

    /// <summary>
    /// 中文：已配对的观测。
    /// English: The observations paired so far.
    /// </summary>
    public List<PairedObservation> Pairs { get; } = [];

    /// <summary>
    /// 中文：钩子看到了、Raw Input 始终没看到的事件数。见文件头——这个数字
    ///       不为零本身就是一条重要发现，必须写进报告。
    /// English: Events the hook saw and Raw Input never did. As the header says, a
    ///          non-zero value is itself a significant finding and belongs in the report.
    /// </summary>
    public int UnpairedHookCount => _pending.Values.Sum(pending => pending.Hook.Count);

    /// <summary>
    /// 中文：Raw Input 看到了、钩子始终没看到的事件数。
    /// English: Events Raw Input saw and the hook never did.
    /// </summary>
    public int UnpairedRawInputCount => _pending.Values.Sum(pending => pending.RawInput.Count);

    /// <summary>
    /// 中文：钩子先到的次数。
    /// English: How often the hook arrived first.
    /// </summary>
    public int HookFirstCount { get; private set; }

    /// <summary>
    /// 中文：Raw Input 先到的次数。
    /// English: How often Raw Input arrived first.
    /// </summary>
    public int RawInputFirstCount { get; private set; }

    /// <summary>
    /// 中文：
    ///   接纳一个观测事件，可能促成一次配对。
    ///   输入：observedEvent 来自任一通道的事件。
    ///   输出：配成一对时返回该对，否则返回 null。
    ///   步骤：
    ///     1. 合成的事件直接忽略——它们只出现在钩子通道，Raw Input 那边根本
    ///        没有对应项，留着只会污染未配对计数；
    ///     2. 取出这种按键身份的两个等待队列；
    ///     3. 对方队列里有等待者就出队配对，并记下谁先到；
    ///     4. 否则把自己排进本方队列。
    ///
    ///   步骤 1 值得说明：LLKHF_INJECTED 标记的事件是软件用 SendInput 合成的，
    ///   不经过物理设备，因此 Raw Input 永远看不到它们。若不排除，它们会全部
    ///   堆积成"钩子未配对"，把一个本该为零、一旦不为零就该警觉的指标变成噪声。
    ///
    /// English:
    ///   Accepts one observed event, possibly completing a pair, which is returned;
    ///   otherwise null.
    ///   Steps: (1) ignore synthesized events; (2) fetch this key identity's two
    ///   queues; (3) if the other channel has one waiting, dequeue it, form the pair
    ///   and record which came first; (4) otherwise queue this one.
    ///
    ///   Step 1 deserves a note: events flagged LLKHF_INJECTED were synthesized by
    ///   software via SendInput, never travelled through a physical device, and so are
    ///   invisible to Raw Input. Left in, they would all pile up as "unpaired hook"
    ///   events, turning a metric that should be zero — and alarming when it is not —
    ///   into noise.
    /// </summary>
    public PairedObservation? Accept(in ObservedInputEvent observedEvent)
    {
        // 步骤 1 / Step 1
        if (observedEvent.IsInjected)
        {
            return null;
        }

        // 步骤 2 / Step 2
        var identity = (observedEvent.VirtualKey, observedEvent.IsKeyUp);
        if (!_pending.TryGetValue(identity, out var pending))
        {
            pending = new PendingEvents();
            _pending[identity] = pending;
        }

        var ownQueue = observedEvent.Channel == InputChannel.Hook ? pending.Hook : pending.RawInput;
        var otherQueue = observedEvent.Channel == InputChannel.Hook ? pending.RawInput : pending.Hook;

        // 步骤 4 / Step 4（先判断对方是否有等待者，没有就排队）
        if (otherQueue.Count == 0)
        {
            ownQueue.Enqueue(observedEvent);
            return null;
        }

        // 步骤 3 / Step 3
        var counterpart = otherQueue.Dequeue();

        var hookEvent = observedEvent.Channel == InputChannel.Hook ? observedEvent : counterpart;
        var rawInputEvent = observedEvent.Channel == InputChannel.RawInput ? observedEvent : counterpart;

        var deltaTicks = rawInputEvent.Timestamp - hookEvent.Timestamp;
        var deltaMicroseconds = deltaTicks * 1_000_000.0 / Stopwatch.Frequency;

        if (deltaTicks > 0)
        {
            HookFirstCount++;
        }
        else if (deltaTicks < 0)
        {
            RawInputFirstCount++;
        }

        var pair = new PairedObservation(
            VirtualKey: observedEvent.VirtualKey,
            IsKeyUp: observedEvent.IsKeyUp,
            DeviceHandle: rawInputEvent.DeviceHandle,
            DeltaMicroseconds: deltaMicroseconds);

        Pairs.Add(pair);
        return pair;
    }

    /// <summary>
    /// 中文：清空全部配对状态与统计。
    /// English: Clears all pairing state and statistics.
    /// </summary>
    public void Reset()
    {
        _pending.Clear();
        Pairs.Clear();
        HookFirstCount = 0;
        RawInputFirstCount = 0;
    }

    /// <summary>
    /// 中文：一种按键身份下，两条通道各自等待配对的事件。
    /// English: The per-channel queues of events awaiting a counterpart for one key
    ///          identity.
    /// </summary>
    private sealed class PendingEvents
    {
        public Queue<ObservedInputEvent> Hook { get; } = new();

        public Queue<ObservedInputEvent> RawInput { get; } = new();
    }
}
