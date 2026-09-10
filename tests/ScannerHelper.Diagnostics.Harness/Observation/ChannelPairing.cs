// =============================================================================
// ChannelPairing.cs
//
// 中文：
//   把两条通道上属于同一次物理按键的事件配对起来（Task 4a 的第 1、2 个问题）。
//
//   这是整个诊断工具的核心一步，也是最容易做错的一步。
//
//   ★ 配对依据：按 (扫描码, 扩展位, 按下还是弹起) 分组，组内先进先出。
//
//     两条通道看到的是**同一串**物理按键，顺序一致、数量一致。所以对每一种
//     按键身份各维护两个队列，一条通道来了事件就去另一条的队列里找对家：
//     找到就配成一对，找不到就自己排队等着。
//
//     ★★ 身份里刻意**不含虚拟键码**，这是第一轮测量用四百多个"未配对"事件
//       换来的教训。
//
//       实测日志：
//         HOOK  LShift (2A) down     钩子报 VK_LSHIFT = 0xA0
//         RAW   Shift  (2A) down     Raw Input 报 VK_SHIFT = 0x10
//
//       同一次按键，扫描码都是 0x2A，虚拟键码却不同——钩子区分左右修饰键，
//       Raw Input 只给通用码。按虚拟键码分组的结果是所有修饰键在两条队列里
//       各自堆积、永远配不上，第一轮因此报出 423 个"钩子未配对"和 422 个
//       "Raw 未配对"，两个数几乎相等，正是同一批事件被劈成两半的形状。
//
//       扫描码则两条通道一致。扩展位必须一起比：扫描码本身会重复（右 Ctrl
//       与左 Ctrl 的扫描码相同），只有加上它才能区分左右。
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
/// <param name="ScanCode">中文：硬件扫描码，两条通道一致。 English: The scan code, on which both channels agree.</param>
/// <param name="HookVirtualKey">
/// 中文：钩子报告的虚拟键码。
/// English: The virtual key the hook reported.
/// </param>
/// <param name="RawInputVirtualKey">
/// 中文：Raw Input 报告的虚拟键码。与 <paramref name="HookVirtualKey"/> 不同是
///       **正常现象**，修饰键上必然如此——两者都记下来，才能把这件事作为
///       一条发现写进报告，而不是当成配对出错。
/// English: The virtual key Raw Input reported. Differing from
///          <paramref name="HookVirtualKey"/> is normal and inevitable for modifiers.
///          Recording both is what lets the report state it as a finding rather than
///          mistake it for a pairing fault.
/// </param>
/// <param name="IsKeyUp">中文：是否为弹起。 English: Whether this is a key up.</param>
/// <param name="HookTimestamp">
/// 中文：钩子那一侧的时间戳。
///
///       ★ 需要"这次按键什么时候发生"时，一律用这一个，不要用 Raw Input 的。
///         钩子回调是在输入派发路径上被**同步**调用的，而 `WM_INPUT` 要先排队
///         再被消息循环取出，中间隔着调度延迟。字符间隔这类统计若用后者，
///         量到的就掺着消息队列的等待时间——第一轮扫码枪段内间隔 p99 到 48 毫秒，
///         正是这么来的。
/// English: The hook-side timestamp.
///
///          Use this one whenever "when did this keystroke happen" is the question,
///          never the Raw Input one. The hook callback is invoked synchronously on the
///          input dispatch path, whereas WM_INPUT queues and waits for a message loop.
///          Interval statistics built on the latter measure queue latency too — which
///          is how the first run produced a 48 ms p99 for a scanner's within-burst
///          interval.
/// </param>
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
    ushort ScanCode,
    ushort HookVirtualKey,
    ushort RawInputVirtualKey,
    bool IsKeyUp,
    long HookTimestamp,
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
    private readonly Dictionary<(ushort ScanCode, bool IsExtended, bool IsKeyUp), PendingEvents>
        _pending = [];

    /// <summary>
    /// 中文：观测到的「同一次按键、两条通道虚拟键码不同」的组合，
    ///       键是钩子报的码，值是 Raw Input 报的码。
    ///
    ///       这不是错误统计，而是一条**发现**。它直接决定关联器的实现：
    ///       只要这张表非空，就证明虚拟键码不能用来跨通道对应事件。
    /// English:
    ///   The observed combinations where the two channels reported different virtual
    ///   keys for one keystroke, mapping the hook's code to Raw Input's.
    ///
    ///   Not an error count but a finding. It settles the correlator's implementation:
    ///   a non-empty table proves virtual keys cannot match events across channels.
    /// </summary>
    private readonly Dictionary<ushort, ushort> _virtualKeyDisagreements = [];

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
    /// 中文：两条通道虚拟键码不一致的组合：(钩子的码, Raw Input 的码)。
    ///       非空即证明关联器必须按扫描码而不是虚拟键码来对应事件。
    /// English: Combinations where the channels disagreed on the virtual key, as
    ///          (hook code, Raw Input code). Non-empty proves the correlator must match
    ///          on scan code rather than virtual key.
    /// </summary>
    public IReadOnlyDictionary<ushort, ushort> VirtualKeyDisagreements => _virtualKeyDisagreements;

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

        // 步骤 2 / Step 2 —— 按扫描码而非虚拟键码，理由见文件头
        // Keyed on scan code, not virtual key; see the file header
        var identity = observedEvent.PairingIdentity;
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

        // 两条通道对同一次按键给出了不同的虚拟键码——记下来，这是一条发现。
        // The channels reported different virtual keys for one keystroke — record it,
        // because it is a finding.
        if (hookEvent.VirtualKey != rawInputEvent.VirtualKey)
        {
            _virtualKeyDisagreements[hookEvent.VirtualKey] = rawInputEvent.VirtualKey;
        }

        var pair = new PairedObservation(
            ScanCode: observedEvent.ScanCode,
            HookVirtualKey: hookEvent.VirtualKey,
            RawInputVirtualKey: rawInputEvent.VirtualKey,
            IsKeyUp: observedEvent.IsKeyUp,
            HookTimestamp: hookEvent.Timestamp,
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
        _virtualKeyDisagreements.Clear();
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
