// =============================================================================
// InputEventCorrelator.cs
//
// 中文：
//   把低层钩子事件与 Raw Input 事件对应起来，判断每一次按键来自哪台设备。
//   规格 §17 称它是全产品风险最高的组件。
//
//   ★ 它解决的问题：两条通道各缺一半。
//
//     钩子        能在按键送达业务软件之前拦下来，但不知道是谁按的
//     Raw Input   知道是谁按的，但事件到手时按键早已在路上，拦不住
//
//     把两条流上属于同一次物理按键的事件配起来，才能同时拿到"是谁"和
//     "拦不拦"。
//
//   ★ 配对依据：(扫描码, 扩展位, 方向)，组内先进先出。**绝不含虚拟键码。**
//
//     Task 4a 实测（docs/TASK_4A_MEASUREMENTS_*.md 第 2 节）：同一次按下左 Shift，
//     钩子报 VK_LSHIFT (0xA0)，Raw Input 报 VK_SHIFT (0x10)，而扫描码都是 0x2A。
//     按虚拟键码配对，所有修饰键永远配不上——落到产品上就是"扫含大写字母的
//     条码时识别不出来源"，而仓库条码几乎都含大写字母。
//
//     这条约束已经被提到类型层面：KeyIdentity 结构上就装不下虚拟键码（决策 D-11）。
//
//     不按"时间最近"配对，是因为那会把要测的东西当成前提：若假定时间最近的
//     两个事件属于同一次按键，测出来的时差必然很小，方法本身保证了结论。
//     按顺序配对不含这个假设。
//
//   ★★ 待配对事件必须过期（决策 D-12）。
//
//     这条是拿一次真实事故换来的。诊断工具第一版没有过期规则，某条通道漏掉
//     一个事件之后，该扫描码的队列整体错位，后面每个事件都跟十几秒前的老事件
//     配成对，报出**负 14.8 秒**的时差。
//
//     最危险的地方不是它错了，而是它**看起来没错**：中位数依然正常，只有
//     尾部离谱。若没人去看最小值，整份分布会被当成真数据拿去定参数。
//
//     落到产品上，同样的错位意味着：一次漏事件之后，**后续每一次按键的来源
//     判定都是错的**，而且会一直错下去，没有任何东西指回起因。过期规则把
//     影响限制在那一个事件上。
//
//   ★ 判不出来的时候，重放而不是吞掉（决策 D-13）。
//
//     规格 §19 明写"绝不无限期吞掉普通键盘输入"。两个方向的后果不对称：
//     泄漏一个扫码字符，它会显眼地落在输入框里，工人看得见、能改；
//     吞掉一次按键则是隐形的，而"键盘失灵"正是规格 §5.7 要救的那个灾难——
//     笔记本工位没有备用键盘可插（假设 A4），失灵就是彻底失灵。
//
//     但这条路径必须被计数并暴露出去。它若频繁发生，说明关联机制本身有问题，
//     必须看得见（规格 §19：无法确信的事件要安全失败并留下诊断）。
//
// English:
//   Matches low-level hook events to Raw Input events to decide which device produced each
//   keystroke. Spec §17 calls it the highest-risk component in the product.
//
//   The problem it solves is that each channel has half the answer: the hook can stop a
//   keystroke before the business application sees it but does not know who pressed it,
//   while Raw Input knows the device but arrives too late to stop anything. Pairing the
//   two events belonging to one physical keystroke yields both halves.
//
//   Pairing is on (scan code, extended flag, direction), FIFO within a group, and never on
//   the virtual key. Task 4a measured a left Shift press reported as VK_LSHIFT (0xA0) by
//   the hook and VK_SHIFT (0x10) by Raw Input, with both reporting scan code 0x2A. Pairing
//   on virtual keys leaves every modifier permanently uncorrelated, which in the product
//   means failing to identify the source of any barcode containing a capital letter — and
//   warehouse barcodes are almost entirely capitals. The constraint is lifted into the type
//   system: KeyIdentity structurally cannot hold a virtual key (decision D-11).
//
//   Pairing by nearest timestamp is avoided because it would assume the thing being
//   measured: "the two closest events belong to the same keystroke" guarantees a small
//   measured delta by construction. Order-based pairing carries no such assumption.
//
//   Pending events must expire (decision D-12), a rule bought with a real incident. The
//   harness's first version had none, and after one channel missed an event the queue for
//   that scan code shifted permanently, pairing every later event with one from seconds
//   earlier and reporting a delta of minus 14.8 seconds. The danger was not that it was
//   wrong but that it looked right: the median stayed sane and only the tail betrayed it.
//   In the product the same shift means every subsequent keystroke is attributed to the
//   wrong device, indefinitely, with nothing pointing back at the cause. Expiry confines
//   the damage to the one event.
//
//   When the source cannot be determined, replay rather than swallow (decision D-13). Spec
//   §19 states that normal keyboard input must never be swallowed indefinitely, and the two
//   directions are asymmetric: a leaked scanner character lands visibly in a focused field
//   where the operator can correct it, whereas a swallowed keystroke is invisible — and "the
//   keyboard stopped working" is the disaster spec §5.7 exists to rescue, with no spare
//   keyboard available on a laptop (assumption A4). That path must nonetheless be counted
//   and surfaced: if it happens often the mechanism itself is faulty and must be seen
//   (spec §19 requires unresolvable events to fail safely and be surfaced diagnostically).
//
// 包含的成员 / Members in this file:
//   DefaultCorrelationWindow  默认关联窗口
//   BoundScannerDeviceId      当前绑定的扫码枪
//   UnresolvedEventCount      因超时而无法判定来源的事件数
//   DiscardedRawInputCount    始终等不到钩子对家的 Raw Input 事件数
//   AcceptHookEvent           接纳钩子事件并当场判断
//   AcceptRawInputEvent       接纳 Raw Input 事件，揭示来源
//   Advance                   推进时间，结掉等待过久的事件
//   Reset                     清空全部待配对状态
// =============================================================================

using ScannerHelper.Core.Abstractions;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：两条输入通道的关联器。
/// English: The correlator for the two input channels.
/// </summary>
public sealed class InputEventCorrelator : IInputEventCorrelator
{
    /// <summary>
    /// 中文：
    ///   默认关联窗口，50 毫秒。
    ///
    ///   取值依据 Task 4a 的实测：两条通道时差的中位数约 0.75 毫秒，p99 约 8 毫秒。
    ///   50 毫秒留了约六倍于 p99 的余量。
    ///
    ///   ★ 这个值是**两头都不能太过**的：
    ///     取太小 → 正常的按键被判成"关联不上"，走进重放路径。而重放会打断
    ///              中文输入法的组字（规格 §4.3 单列的验收项），且扫码枪的字符
    ///              会泄漏进业务软件。
    ///     取太大 → 一次漏事件要卡这么久才被清掉，期间工人的按键被扣着不放，
    ///              手感上就是"打字有延迟"。
    ///
    ///   ★ 它与扫描无活动超时是**两个独立的值**，互不推导（决策 D-17）。
    ///     两者回答的问题完全不同：这个问的是"两条通道最多能差多久"，
    ///     那个问的是"多长的空隙算一枪结束"，实测相差约两个数量级。
    ///     规格已经提醒过 300 毫秒的扫描超时与 LowLevelHooksTimeout 数字相同
    ///     但毫无关系——这是同一个陷阱在下一层。
    ///
    ///   开发机上的实测值不代表现场机器（规格 §22.5 同一条告诫）。这个默认值
    ///   在试点机器上必须重新验证。
    /// English:
    ///   The default correlation window, 50 ms.
    ///
    ///   Chosen from Task 4a's measurements: the inter-channel delta had a median of about
    ///   0.75 ms and a p99 of about 8 ms, so 50 ms leaves roughly six times the p99 as
    ///   headroom.
    ///
    ///   The value must not err in either direction. Too small and ordinary keystrokes are
    ///   judged uncorrelatable and take the replay path — which disrupts IME composition
    ///   (spec §4.3's own acceptance item) and leaks scanner characters into the business
    ///   application. Too large and a single lost event blocks for that long before being
    ///   cleared, with the operator's keystrokes withheld meanwhile, felt as typing lag.
    ///
    ///   It is independent of the scan inactivity timeout and neither is derived from the
    ///   other (decision D-17): one asks how long the two channels may disagree, the other
    ///   how long a gap ends a scan, and the measurements put them two orders of magnitude
    ///   apart. The spec already warns that the 300 ms scan timeout shares a number with
    ///   LowLevelHooksTimeout and nothing else; this is the same trap one layer down.
    ///
    ///   Measurements from a development machine do not represent the pilot hardware (the
    ///   same caution as spec §22.5). This default must be re-verified on site.
    /// </summary>
    public static readonly TimeSpan DefaultCorrelationWindow = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 中文：
    ///   待配对事件的总数上限。
    ///
    ///   正常情况下队列里最多积压几个事件——毕竟时差中位数不到 1 毫秒。
    ///   这个上限防的是异常情况：某条通道完全停止工作（例如钩子被 Windows
    ///   摘掉了，规格 §19.1），另一条却还在源源不断地送事件进来。
    ///
    ///   没有上限的话，那种情况下队列会一直涨，最终吃光内存——而这一切发生在
    ///   扫描处理链路上。规格 §19 要求无法确信的情况安全失败，而不是悄悄劣化。
    /// English:
    ///   The cap on total pending events.
    ///
    ///   Normally only a handful queue up, the median delta being under a millisecond. The
    ///   cap guards the abnormal case: one channel stopping entirely — the hook removed by
    ///   Windows, say (spec §19.1) — while the other keeps delivering.
    ///
    ///   Uncapped, the queues would grow until memory ran out, and all of this sits on the
    ///   scan-handling path. Spec §19 requires an unresolvable situation to fail safely
    ///   rather than degrade quietly.
    /// </summary>
    public const int MaximumPendingEvents = 512;

    private static readonly IReadOnlyList<ResolvedKeyEvent> NoResolutions = [];

    private readonly ISystemClock _clock;
    private readonly TimeSpan _correlationWindow;

    /// <summary>
    /// 中文：已扣留、等待 Raw Input 揭示来源的钩子事件，按按键身份分组。
    /// English: Withheld hook events awaiting a Raw Input event to reveal their source,
    ///          grouped by key identity.
    /// </summary>
    private readonly Dictionary<KeyIdentity, Queue<KeyEvent>> _pendingHookEvents = [];

    /// <summary>
    /// 中文：先于钩子事件到达、正在等待钩子事件的 Raw Input 事件。
    ///       实测约占 0.6%，但确实会发生，因此必须处理（用例 CR3）。
    /// English: Raw Input events that arrived before their hook counterpart. Measured at
    ///          roughly 0.6% — rare but real, and therefore handled (case CR3).
    /// </summary>
    private readonly Dictionary<KeyIdentity, Queue<RawInputEvent>> _pendingRawInputEvents = [];

    /// <summary>
    /// 中文：
    ///   构造关联器。
    ///   输入：clock 注入的时间源，不得为 null；
    ///         correlationWindow 关联窗口，null 时取 <see cref="DefaultCorrelationWindow"/>，
    ///         必须为正值。
    /// English:
    ///   Creates the correlator. clock must not be null; correlationWindow defaults to
    ///   <see cref="DefaultCorrelationWindow"/> and must be positive.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：clock 为 null。 English: clock is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：关联窗口非正。 English: A non-positive correlation window.
    /// </exception>
    public InputEventCorrelator(ISystemClock clock, TimeSpan? correlationWindow = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var resolvedWindow = correlationWindow ?? DefaultCorrelationWindow;
        if (resolvedWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(correlationWindow), resolvedWindow,
                "关联窗口必须为正值。窗口为零意味着每一个事件在到达的同一刻就过期，"
                + "所有按键都会走进重放路径。"
                + " The correlation window must be positive. A zero window expires every"
                + " event at the instant it arrives, sending every keystroke down the"
                + " replay path.");
        }

        _clock = clock;
        _correlationWindow = resolvedWindow;
    }

    /// <inheritdoc />
    public long? BoundScannerDeviceId { get; set; }

    /// <summary>
    /// 中文：
    ///   因为等不到 Raw Input 对家而无法判定来源、按决策 D-13 重放的事件数。
    ///
    ///   ★ 这个数字正常应当极小甚至为零。它持续增长意味着关联机制本身失效了，
    ///     此时程序仍在"工作"，但每一次按键的来源判定都是猜的——扫码枪的字符
    ///     会泄漏进业务软件。规格 §19.1 的原则在这里同样适用：绝不显示一个
    ///     自己没验证过的状态。调用方应当把它暴露到诊断界面上。
    /// English:
    ///   Events whose source could not be determined because no Raw Input counterpart
    ///   arrived, and which were replayed under decision D-13.
    ///
    ///   This should be tiny or zero. Sustained growth means correlation itself has failed:
    ///   the program still "works" while every keystroke's source is a guess, and scanner
    ///   characters leak into the business application. Spec §19.1's principle applies —
    ///   never display a state that has not been verified — so callers should surface this
    ///   in diagnostics.
    /// </summary>
    public int UnresolvedEventCount { get; private set; }

    /// <summary>
    /// 中文：始终等不到钩子对家、被丢弃的 Raw Input 事件数。
    ///
    ///       它不为零指向一个特定的原因：钩子那一次被 Windows 跳过了
    ///       （规格 §19.1 的 LowLevelHooksTimeout）。Task 4a 实测到过这种事件。
    ///       这类按键**没有被吞掉**，原始字符直接进了业务软件，而我们的缓冲区
    ///       里少了它——两头都错。
    /// English:
    ///   Raw Input events discarded because no hook counterpart ever arrived.
    ///
    ///   A non-zero value points at one specific cause: the hook being skipped for that
    ///   event (spec §19.1's LowLevelHooksTimeout), which Task 4a observed. Such a keystroke
    ///   was *not* swallowed — the raw character reached the business application while our
    ///   buffer is missing it, wrong in both directions.
    /// </summary>
    public int DiscardedRawInputCount { get; private set; }

    /// <inheritdoc />
    public HookCorrelationOutcome AcceptHookEvent(in KeyEvent hookEvent)
    {
        // 步骤 1 —— 合成事件立刻放行，绝不进入关联（规格 §5.6）。
        //
        // 必须在关联**之前**判断。Task 4a 已确认合成事件根本不出现在 Raw Input
        // 通道上，所以让它进入关联的结果是：等一个不可能存在的对家、超时、
        // 然后被重放——把本程序自己的输出重新喂回自己的管道。那正是规格 §5.6
        // 禁止的递归，只是以"看起来正确的超时处理"这条路径抵达。
        //
        // Step 1 — synthesized events pass through immediately and never enter correlation
        // (spec §5.6). This must precede correlation: Task 4a confirmed synthesized events
        // never appear on the Raw Input channel at all, so letting one in means waiting for
        // a counterpart that cannot exist, expiring, and being replayed — feeding our own
        // output back into our own pipeline. That is exactly the recursion spec §5.6
        // forbids, arriving by way of what looks like correct timeout handling.
        if (hookEvent.IsInjected)
        {
            return new HookCorrelationOutcome(CorrelationDecision.PassThrough, NoResolutions);
        }

        // 步骤 2 —— 先清理过期事件，再配对（决策 D-12）。
        // Step 2 — sweep expired events before pairing (decision D-12).
        var resolved = SweepExpired(hookEvent.Timestamp);

        // 步骤 3 —— Raw Input 是否已经先到？实测约 0.6% 的情形（用例 CR3）。
        //
        // 能在这里确定来源，就省掉一次扣留-重放：判定不是扫码枪时直接放行，
        // Windows 原样送达，中文输入法的组字完全不受影响。而重放要合成事件，
        // 那是最容易打断组字的东西。
        //
        // Step 3 — did Raw Input already arrive? Measured at roughly 0.6% (case CR3).
        // Settling the source here saves a withhold-and-replay round trip: a non-scanner
        // passes straight through, Windows delivers it untouched, and IME composition is
        // left entirely alone — whereas replay synthesizes an event, the thing most likely
        // to break composition.
        if (TryDequeue(_pendingRawInputEvents, hookEvent.Identity, out var rawInputEvent))
        {
            var decision = IsBoundScanner(rawInputEvent.DeviceId)
                ? CorrelationDecision.Swallow
                : CorrelationDecision.PassThrough;

            return new HookCorrelationOutcome(decision, resolved);
        }

        // 步骤 4 —— 来源未知，扣留等待。
        //
        // 这是实测中 99.4% 的路径：钩子恒先于 WM_INPUT 到达，最小时差 110 微秒。
        // 调用方收到 Undecided 必须**吞掉**这次按键并等待后续结论，详见
        // CorrelationDecision 的说明。
        //
        // Step 4 — source unknown; withhold. This is 99.4% of the measured traffic, the hook
        // always preceding WM_INPUT with a minimum delta of 110 µs. A caller receiving
        // Undecided must swallow the keystroke and await the answer; see CorrelationDecision.
        Enqueue(_pendingHookEvents, hookEvent.Identity, hookEvent);
        EnforcePendingLimit(ref resolved);

        return new HookCorrelationOutcome(CorrelationDecision.Undecided, resolved);
    }

    /// <inheritdoc />
    public IReadOnlyList<ResolvedKeyEvent> AcceptRawInputEvent(in RawInputEvent rawInputEvent)
    {
        // 步骤 1 —— 先清理过期事件（决策 D-12）。
        // Step 1 — sweep expired events first (decision D-12).
        var resolved = SweepExpired(rawInputEvent.Timestamp);

        // 步骤 2 —— 找到对应的、正被扣留的钩子事件。
        // Step 2 — find the withheld hook event this identifies.
        if (TryDequeue(_pendingHookEvents, rawInputEvent.Identity, out var hookEvent))
        {
            var decision = IsBoundScanner(rawInputEvent.DeviceId)
                ? CorrelationDecision.Swallow
                : CorrelationDecision.Replay;

            return Append(resolved, new ResolvedKeyEvent(hookEvent, decision, rawInputEvent.DeviceId));
        }

        // 步骤 3 —— 没有对家，排队等钩子事件到来（用例 CR3 的另一半）。
        // Step 3 — no counterpart; queue and wait for the hook event (the other half of CR3).
        Enqueue(_pendingRawInputEvents, rawInputEvent.Identity, rawInputEvent);
        EnforcePendingLimit(ref resolved);

        return resolved;
    }

    /// <inheritdoc />
    public IReadOnlyList<ResolvedKeyEvent> Advance() => SweepExpired(_clock.MonotonicNow);

    /// <summary>
    /// 中文：清空全部待配对状态与计数。用于重新绑定扫码枪、或从 PAUSED 恢复
    ///       之后重新开始——那些时刻之前积压的事件都已经失去意义。
    /// English: Clears all pending state and counters, for use after rebinding the scanner or
    ///          resuming from PAUSED, at which points any backlog has lost its meaning.
    /// </summary>
    public void Reset()
    {
        _pendingHookEvents.Clear();
        _pendingRawInputEvents.Clear();
        UnresolvedEventCount = 0;
        DiscardedRawInputCount = 0;
    }

    /// <summary>
    /// 中文：
    ///   结掉等待过久的事件（决策 D-12、D-13）。
    ///   输入：now 当前的单调时刻。
    ///   输出：因超时而得出结论的事件。
    ///
    ///   钩子事件超时 → 判定为 Replay 并计数（决策 D-13）。绝不吞掉：规格 §19
    ///   明写不得无限期吞掉普通键盘输入，而笔记本工位没有备用键盘（假设 A4）。
    ///
    ///   Raw Input 事件超时 → 直接丢弃并计数。它本来也拦不住什么，唯一的价值
    ///   （揭示某个被扣留事件的来源）已经随着对家的缺席而落空。
    ///
    ///   ★ 只检查队头即可：同一身份的队列内时间戳递增，队头最老，队头没过期
    ///     则后面都没过期。
    /// English:
    ///   Settles over-aged events (decisions D-12, D-13).
    ///
    ///   An expired hook event resolves to Replay and is counted (D-13), never swallowed:
    ///   spec §19 forbids swallowing normal keyboard input indefinitely and a laptop
    ///   workstation has no spare keyboard (assumption A4). An expired Raw Input event is
    ///   discarded and counted — it could never intercept anything, and its one value,
    ///   revealing a withheld event's source, died with the absent counterpart.
    ///
    ///   Only the head of each queue needs checking: timestamps increase along a queue, so
    ///   if the oldest has not expired none has.
    /// </summary>
    private IReadOnlyList<ResolvedKeyEvent> SweepExpired(TimeSpan now)
    {
        List<ResolvedKeyEvent>? resolved = null;

        foreach (var queue in _pendingHookEvents.Values)
        {
            while (queue.Count > 0 && now - queue.Peek().Timestamp >= _correlationWindow)
            {
                var expired = queue.Dequeue();
                UnresolvedEventCount++;

                resolved ??= [];
                resolved.Add(new ResolvedKeyEvent(
                    expired, CorrelationDecision.Replay, DeviceId: null));
            }
        }

        foreach (var queue in _pendingRawInputEvents.Values)
        {
            while (queue.Count > 0 && now - queue.Peek().Timestamp >= _correlationWindow)
            {
                queue.Dequeue();
                DiscardedRawInputCount++;
            }
        }

        return resolved ?? NoResolutions;
    }

    /// <summary>
    /// 中文：
    ///   待配对事件超出上限时，强制结掉最旧的那些。
    ///
    ///   正常情况永远走不到这里。它防的是某条通道彻底停摆（例如钩子被 Windows
    ///   摘掉了，规格 §19.1）而另一条还在源源不断送事件的情形——那时时间可能
    ///   根本没往前走多少，超时清理不触发，队列却在一直涨。
    ///
    ///   处理方式与超时一致：钩子事件重放，Raw Input 事件丢弃。宁可让一次按键
    ///   走进重放路径，也不能让内存一直涨下去（规格 §19：安全失败，不要悄悄劣化）。
    /// English:
    ///   Force-settles the oldest pending events once the cap is exceeded.
    ///
    ///   Unreachable in normal operation. It guards the case where one channel stops
    ///   entirely — the hook removed by Windows, say (spec §19.1) — while the other keeps
    ///   delivering: time may barely advance, so expiry never fires, yet the queues grow.
    ///
    ///   Handled exactly as expiry is: hook events replay, Raw Input events are discarded.
    ///   Better one keystroke down the replay path than memory growing without bound
    ///   (spec §19: fail safely rather than degrade quietly).
    /// </summary>
    private void EnforcePendingLimit(ref IReadOnlyList<ResolvedKeyEvent> resolved)
    {
        while (TotalPendingCount() > MaximumPendingEvents)
        {
            var oldestHook = OldestQueue(_pendingHookEvents, queue => queue.Peek().Timestamp);
            var oldestRawInput = OldestQueue(_pendingRawInputEvents, queue => queue.Peek().Timestamp);

            var takeHook = oldestRawInput is null
                || (oldestHook is not null
                    && oldestHook.Peek().Timestamp <= oldestRawInput.Peek().Timestamp);

            if (takeHook && oldestHook is not null)
            {
                var expired = oldestHook.Dequeue();
                UnresolvedEventCount++;
                resolved = Append(resolved, new ResolvedKeyEvent(
                    expired, CorrelationDecision.Replay, DeviceId: null));
            }
            else if (oldestRawInput is not null)
            {
                oldestRawInput.Dequeue();
                DiscardedRawInputCount++;
            }
            else
            {
                return;
            }
        }
    }

    private int TotalPendingCount()
        => _pendingHookEvents.Values.Sum(queue => queue.Count)
           + _pendingRawInputEvents.Values.Sum(queue => queue.Count);

    private static Queue<T>? OldestQueue<T>(
        Dictionary<KeyIdentity, Queue<T>> queues, Func<Queue<T>, TimeSpan> headTimestamp)
    {
        Queue<T>? oldest = null;
        var oldestTimestamp = TimeSpan.MaxValue;

        foreach (var queue in queues.Values)
        {
            if (queue.Count == 0)
            {
                continue;
            }

            var timestamp = headTimestamp(queue);
            if (timestamp < oldestTimestamp)
            {
                oldestTimestamp = timestamp;
                oldest = queue;
            }
        }

        return oldest;
    }

    /// <summary>
    /// 中文：判断某个设备是否为当前绑定的扫码枪。
    ///       未绑定（null）时恒为 false——没有任何按键会被吞掉，键盘完全正常。
    ///       这是安全的那一侧：首次运行与重新绑定期间都处于此状态（规格 §6）。
    /// English: Whether a device is the bound scanner. Always false while nothing is bound,
    ///          so nothing is swallowed and the keyboard works normally — the safe side, and
    ///          the state during first run and rebinding (spec §6).
    /// </summary>
    private bool IsBoundScanner(long deviceId)
        => BoundScannerDeviceId is { } boundDeviceId && boundDeviceId == deviceId;

    private static void Enqueue<T>(
        Dictionary<KeyIdentity, Queue<T>> queues, KeyIdentity identity, in T item)
    {
        if (!queues.TryGetValue(identity, out var queue))
        {
            queue = new Queue<T>();
            queues[identity] = queue;
        }

        queue.Enqueue(item);
    }

    private static bool TryDequeue<T>(
        Dictionary<KeyIdentity, Queue<T>> queues, KeyIdentity identity, out T item)
    {
        if (queues.TryGetValue(identity, out var queue) && queue.Count > 0)
        {
            item = queue.Dequeue();
            return true;
        }

        item = default!;
        return false;
    }

    private static IReadOnlyList<ResolvedKeyEvent> Append(
        IReadOnlyList<ResolvedKeyEvent> existing, in ResolvedKeyEvent addition)
    {
        if (existing is List<ResolvedKeyEvent> list)
        {
            list.Add(addition);
            return list;
        }

        return new List<ResolvedKeyEvent>(existing) { addition };
    }
}
