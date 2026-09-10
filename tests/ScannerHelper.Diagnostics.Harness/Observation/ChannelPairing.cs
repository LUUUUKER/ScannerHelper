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
//   ★ 未配对事件必须被记账，而且要记清楚是**哪一类按键**。
//
//     一条通道看得到、另一条看不到的事件，本身就是极重要的发现：可能意味着
//     某类按键根本不经过 Raw Input，或者钩子被 Windows 跳过了。若把它们静静
//     丢弃，报告会显得干净漂亮，而最值得注意的现象恰好被抹掉了。
//
//     只给一个总数还不够。"有 40 个事件没配上"无法行动，"这 40 个都是扫描码
//     0x1F 的按下事件、只出现在 Raw Input 一侧"才能行动。所以这里按
//     (通道, 扫描码, 虚拟键码, 方向) 分类计数。
//
//   ★★ 等待中的事件必须有时效上限，否则一次漏事件会污染后面全部配对。
//
//     这是第二轮实测踩出来的。当时报出的时差最小值是**负 14.8 秒**、最大值
//     **24.5 秒**——按键时差不可能是十几秒。原因是某条通道漏掉了一个事件之后，
//     该扫描码的先进先出队列整体错位：后面每一个事件都跟队列里那个早就该配走
//     的老事件配成了对，于是算出十几秒的"时差"。一个漏事件毁掉了后面所有数据。
//
//     更糟的是结果看上去仍然像数据：中位数依然正常，只有尾部离谱。若没注意到
//     那个负数，整份时差分布都会被当成真的拿去定 4b 的等待窗口。
//
//     加上时效上限之后，等太久没等到对家的事件直接判为未配对并计入分类统计，
//     错位不会向后传播。上限取 250 毫秒：远大于实测的通道间时差（p99 约 8 毫秒），
//     又远小于两枪扫描之间的间隔，因此不会误伤正常配对。
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
/// 中文：一类未配对事件的身份。分类计数用它做键。
/// English: The identity of one kind of unpaired event, used as the breakdown's key.
/// </summary>
/// <param name="Channel">
/// 中文：这一类事件只出现在哪条通道上。这是最要紧的一项——
///       "只有 Raw Input 看到"指向钩子被跳过（规格 §19.1 的 LowLevelHooksTimeout），
///       "只有钩子看到"则指向合成事件或某类不经过 Raw Input 的输入。
///       两者的排查方向完全不同。
/// English: Which channel alone saw this kind. The most important field: "Raw Input
///          only" points at the hook being skipped (spec §19.1's LowLevelHooksTimeout),
///          while "hook only" points at synthesized input or something that bypasses Raw
///          Input. The two lead in entirely different directions.
/// </param>
/// <param name="ScanCode">中文：硬件扫描码。 English: The scan code.</param>
/// <param name="VirtualKey">中文：虚拟键码。 English: The virtual key.</param>
/// <param name="IsKeyUp">中文：按下还是弹起。 English: Down or up.</param>
public readonly record struct UnmatchedKind(
    InputChannel Channel,
    ushort ScanCode,
    ushort VirtualKey,
    bool IsKeyUp);

/// <summary>
/// 中文：两条通道的事件配对器。
/// English: Pairs events across the two channels.
/// </summary>
public sealed class ChannelPairing
{
    /// <summary>
    /// 中文：等待配对的最长时间。超过这个岁数还没等到对家的事件，判为未配对丢弃。
    ///
    ///       取 250 毫秒：实测通道间时差 p99 约 8 毫秒，因此这个上限远在正常
    ///       范围之外，不会误伤真配对；同时又远小于两枪扫描之间的间隔，
    ///       所以一个漏事件不会一直等到下一枪来跟人家配对。
    /// English:
    ///   How long an event may wait for its counterpart before being written off as
    ///   unpaired. 250 ms sits far outside the measured inter-channel delta (p99 around
    ///   8 ms), so it cannot sever a genuine pair, and far inside the gap between scans,
    ///   so a lost event cannot survive to pair with the next scan's.
    /// </summary>
    public static readonly TimeSpan MaximumPendingAge = TimeSpan.FromMilliseconds(250);

    private static readonly long MaximumPendingTicks =
        (long)(MaximumPendingAge.TotalSeconds * Stopwatch.Frequency);

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
    /// 中文：未配对事件的分类计数。只给总数无法行动——"40 个没配上"什么也说明
    ///       不了，"这 40 个都是扫描码 0x1F 的按下、只出现在 Raw Input 一侧"
    ///       才能指向具体原因。
    /// English:
    ///   Unpaired events broken down by kind. A bare total is not actionable — "40
    ///   unpaired" says nothing, while "all 40 were scan code 0x1F key-downs seen only
    ///   by Raw Input" points at a cause.
    /// </summary>
    private readonly Dictionary<UnmatchedKind, int> _unmatched = [];

    /// <summary>
    /// 中文：已配对的观测。
    /// English: The observations paired so far.
    /// </summary>
    public List<PairedObservation> Pairs { get; } = [];

    /// <summary>
    /// 中文：钩子看到了、Raw Input 始终没看到的事件数。含已超时判废的和仍在等的。
    ///       见文件头——这个数字不为零本身就是一条重要发现，必须写进报告。
    /// English: Events the hook saw and Raw Input never did, counting both those written
    ///          off as stale and those still waiting. As the header says, a non-zero
    ///          value is itself a significant finding and belongs in the report.
    /// </summary>
    public int UnpairedHookCount
        => _pending.Values.Sum(pending => pending.Hook.Count)
           + _unmatched.Where(entry => entry.Key.Channel == InputChannel.Hook)
               .Sum(entry => entry.Value);

    /// <summary>
    /// 中文：Raw Input 看到了、钩子始终没看到的事件数。
    /// English: Events Raw Input saw and the hook never did.
    /// </summary>
    public int UnpairedRawInputCount
        => _pending.Values.Sum(pending => pending.RawInput.Count)
           + _unmatched.Where(entry => entry.Key.Channel == InputChannel.RawInput)
               .Sum(entry => entry.Value);

    /// <summary>
    /// 中文：未配对事件的分类明细，按出现次数从多到少。
    /// English: The breakdown of unpaired events, most frequent first.
    /// </summary>
    public IReadOnlyList<(UnmatchedKind Kind, int Count)> UnmatchedBreakdown
        => _unmatched
            .Select(entry => (entry.Key, entry.Value))
            .OrderByDescending(entry => entry.Value)
            .ToArray();

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

        // 步骤 2.5 —— 先把等太久的事件清掉，再找对家。
        // 不清的话，一次漏事件会让队列永久错位，后面每个事件都跟十几秒前的
        // 老事件配对，算出荒谬的时差（第二轮实测出现过负 14.8 秒）。
        // Sweep stale entries before looking for a counterpart. Without this one lost
        // event shifts the queue permanently and every later event pairs with one from
        // seconds ago — the second run produced a delta of minus 14.8 seconds.
        DiscardStale(pending.Hook, observedEvent.Timestamp);
        DiscardStale(pending.RawInput, observedEvent.Timestamp);

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
        _unmatched.Clear();
        Pairs.Clear();
        HookFirstCount = 0;
        RawInputFirstCount = 0;
    }

    /// <summary>
    /// 中文：
    ///   把队头等得过久的事件判为未配对并清出去。
    ///   输入：queue 某条通道的等待队列；now 当前事件的时间戳。
    ///   输出：无。副作用：被清掉的事件计入分类统计。
    ///
    ///   只需检查队头：队列内时间戳递增，队头最老，队头不过期则后面都不过期。
    ///
    ///   ★ 清掉而不是留着，是为了**阻断错位向后传播**。留着的话，队头那个
    ///     永远等不到对家的事件会把后面每一个事件都错配一位，而错配算出来的
    ///     时差是纯噪声——偏偏看起来和真数据一模一样，只有尾部离谱到十几秒
    ///     才会露馅。
    /// English:
    ///   Writes off queue-head events that have waited too long, counting them in the
    ///   breakdown.
    ///
    ///   Only the head needs checking: timestamps increase along the queue, so if the
    ///   oldest has not expired none has.
    ///
    ///   Discarding rather than keeping is what stops the misalignment propagating. Kept,
    ///   the head event that will never find its counterpart shifts every later event by
    ///   one, and a delta computed from a shifted pair is pure noise that looks exactly
    ///   like real data — betrayed only when the tail reaches tens of seconds.
    /// </summary>
    private void DiscardStale(Queue<ObservedInputEvent> queue, long now)
    {
        while (queue.Count > 0 && now - queue.Peek().Timestamp > MaximumPendingTicks)
        {
            var abandoned = queue.Dequeue();
            var kind = new UnmatchedKind(
                abandoned.Channel, abandoned.ScanCode, abandoned.VirtualKey, abandoned.IsKeyUp);

            _unmatched[kind] = _unmatched.GetValueOrDefault(kind) + 1;
        }
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
