// =============================================================================
// InputEventCorrelatorTests.cs
//
// 中文：
//   关联器的行为测试（对应 TEST_PLAN_TASK_6.md 的 CR1~CR17）。
//
//   规格 §17 称关联器是全产品风险最高的组件，而它之所以能被穷尽测试，
//   靠的是被刻意保持为**对时间戳事件序列的纯函数**：喂进合成的事件序列，
//   收回判断，不需要扫码枪、不需要 Windows、不需要人按键。
//
//   本文件里分量最重的三条：
//     CR4   两条通道对修饰键报告的虚拟键码不同。缺了它，每一个含大写字母的
//           条码都会认错来源——而仓库条码几乎都含大写字母。
//     CR11  合成事件必须在关联**之前**被放行。缺了它，本程序自己的输出会
//           被喂回自己的管道，形成规格 §5.6 禁止的递归。
//     CR17  待配对事件必须过期。缺了它，一次漏事件会让后面每一次来源判定
//           都错下去，而统计看起来依然健康。
//
// English:
//   Behavior tests for the correlator (CR1–CR17 in TEST_PLAN_TASK_6.md).
//
//   Spec §17 calls the correlator the highest-risk component in the product, and what makes
//   it exhaustively testable is being deliberately kept a pure function over a sequence of
//   timestamped events: feed synthetic sequences in, get decisions back, with no scanner, no
//   Windows and no human pressing keys.
//
//   Three carry the most weight. CR4: the two channels disagree about modifiers' virtual
//   keys, and without it every barcode containing a capital letter has its source
//   misidentified — and warehouse barcodes are almost entirely capitals. CR11: synthesized
//   events must pass through *before* correlation, or our own output re-enters our own
//   pipeline in the recursion spec §5.6 forbids. CR17: pending events must expire, or one
//   lost event misattributes every keystroke after it while the statistics still look healthy.
//
// 包含的测试 / Tests in this file:
//   Hook_first_from_bound_scanner_is_withheld_then_swallowed        CR1
//   Hook_first_from_other_device_is_withheld_then_replayed          CR2
//   Raw_input_first_decides_immediately_without_withholding         CR3
//   Correlates_on_scan_code_when_virtual_keys_disagree              CR4
//   Same_scan_code_with_different_extended_flag_does_not_correlate  CR5
//   Key_down_does_not_correlate_with_key_up                         CR6
//   Repeated_presses_of_one_key_correlate_in_order                  CR7
//   Interleaved_keys_correlate_independently                        CR8
//   Raw_input_arriving_out_of_order_still_finds_its_own_counterpart CR9
//   Hook_event_without_counterpart_is_replayed_after_the_window     CR10
//   Injected_event_passes_through_without_entering_correlation      CR11
//   Raw_input_without_counterpart_is_discarded_and_counted          CR12
//   Expiry_follows_the_injected_clock_not_the_wall_clock            CR13
//   Pending_events_are_bounded                                      CR14
//   Correlator_exposes_no_platform_dependency                       CR15
//   Null_clock_is_rejected / Non_positive_window_is_rejected        CR16
//   Stale_pending_event_does_not_pair_with_a_much_later_event       CR17
// =============================================================================

using System.Reflection;
using ScannerHelper.Core.Abstractions;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Tests.TestDoubles;

namespace ScannerHelper.Core.Tests.Input;

public class InputEventCorrelatorTests
{
    private const long ScannerDeviceId = 1001;
    private const long KeyboardDeviceId = 2002;

    /// <summary>
    /// 中文：字母 D 的扫描码。用一个具体的、真实存在的扫描码而不是随手的数字，
    ///       是为了让测试读起来贴近实际事件流。
    /// English: The scan code for the letter D. A real one rather than an arbitrary number,
    ///          so the tests read like the event stream they describe.
    /// </summary>
    private const ushort ScanCodeD = 0x20;

    private const ushort ScanCodeK = 0x25;

    /// <summary>
    /// 中文：左 Shift 的扫描码。CR4 用它——Task 4a 实测正是在这个键上发现
    ///       两条通道的虚拟键码不一致。
    /// English: Left Shift's scan code, used by CR4 — the key on which Task 4a found the two
    ///          channels disagreeing about virtual keys.
    /// </summary>
    private const ushort ScanCodeLeftShift = 0x2A;

    private const ushort VkLeftShift = 0xA0;
    private const ushort VkShift = 0x10;

    private readonly TestSystemClock _clock = new();

    private InputEventCorrelator CreateCorrelator(TimeSpan? window = null)
        => new(_clock, window) { BoundScannerDeviceId = ScannerDeviceId };

    private KeyEvent HookEvent(
        ushort scanCode = ScanCodeD,
        bool isKeyUp = false,
        bool isExtended = false,
        ushort virtualKey = 0x44,
        char? character = 'D',
        bool isInjected = false)
        => new(_clock.MonotonicNow, scanCode, isExtended, isKeyUp, virtualKey, character, isInjected);

    private RawInputEvent RawEvent(
        ushort scanCode = ScanCodeD,
        bool isKeyUp = false,
        bool isExtended = false,
        long deviceId = ScannerDeviceId)
        => new(_clock.MonotonicNow, scanCode, isExtended, isKeyUp, deviceId);

    /// <summary>
    /// 中文：
    ///   CR1 —— 钩子先到、随后 Raw Input 说明是绑定的扫码枪：先扣留，再吞掉。
    ///
    ///   这是实测中 99.4% 的路径。钩子恒先于 WM_INPUT 到达（Task 4a：1049/1049，
    ///   最小时差 110 微秒），所以决策时设备身份从来不在手上，只能先扣留。
    /// English:
    ///   CR1 — hook first, then Raw Input naming the bound scanner: withheld, then swallowed.
    ///   This is 99.4% of measured traffic; the hook always precedes WM_INPUT (Task 4a:
    ///   1049/1049, minimum delta 110 µs), so identity is never in hand at decision time.
    /// </summary>
    [Fact]
    public void Hook_first_from_bound_scanner_is_withheld_then_swallowed()
    {
        var correlator = CreateCorrelator();
        var hookEvent = HookEvent();

        var hookOutcome = correlator.AcceptHookEvent(hookEvent);
        Assert.Equal(CorrelationDecision.Undecided, hookOutcome.Decision);
        Assert.Empty(hookOutcome.Resolved);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var resolved = correlator.AcceptRawInputEvent(RawEvent(deviceId: ScannerDeviceId));

        var resolution = Assert.Single(resolved);
        Assert.Equal(CorrelationDecision.Swallow, resolution.Decision);
        Assert.Equal(hookEvent, resolution.Event);
        Assert.Equal(ScannerDeviceId, resolution.DeviceId);
    }

    /// <summary>
    /// 中文：
    ///   CR2 —— 钩子先到、随后确认来自其他设备：先扣留，再**重放**。
    ///
    ///   重放不是可选的。事件在步骤一已经被调用方吞掉了，不补发就等于工人的
    ///   按键凭空消失。规格 §19 明写绝不无限期吞掉普通键盘输入，而笔记本工位
    ///   没有备用键盘可插（假设 A4）。
    /// English:
    ///   CR2 — hook first, then confirmed to be another device: withheld, then replayed.
    ///   Replay is not optional. The caller already swallowed the event, so not re-emitting
    ///   makes the operator's keystroke vanish. Spec §19 forbids swallowing normal keyboard
    ///   input indefinitely, and a laptop has no spare keyboard (assumption A4).
    /// </summary>
    [Fact]
    public void Hook_first_from_other_device_is_withheld_then_replayed()
    {
        var correlator = CreateCorrelator();
        var hookEvent = HookEvent();

        Assert.Equal(CorrelationDecision.Undecided, correlator.AcceptHookEvent(hookEvent).Decision);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var resolved = correlator.AcceptRawInputEvent(RawEvent(deviceId: KeyboardDeviceId));

        var resolution = Assert.Single(resolved);
        Assert.Equal(CorrelationDecision.Replay, resolution.Decision);
        Assert.Equal(hookEvent, resolution.Event);
        Assert.Equal(KeyboardDeviceId, resolution.DeviceId);
    }

    /// <summary>
    /// 中文：
    ///   CR3 —— Raw Input 先到时，钩子事件当场就能判定，**不进入扣留**。
    ///
    ///   实测约占 0.6%（Task 4a 第二轮：3799 次里有 24 次）。少见但真实存在，
    ///   而且值得专门处理：能在回调那一刻确定不是扫码枪，就直接放行，Windows
    ///   原样送达，中文输入法的组字完全不受影响。走扣留-重放则要合成事件，
    ///   而合成事件正是最容易打断组字的东西（规格 §4.3 把 IME 单列为验收项）。
    ///
    ///   一个"钩子必然先到"的实现会在 99.4% 的情况下正确，约每四十枪错一次——
    ///   频繁到足以在现场造成困扰，又稀少到足以躲过一次演示。
    /// English:
    ///   CR3 — when Raw Input arrives first, the hook event is decided on the spot and never
    ///   withheld. Measured at roughly 0.6% (24 of 3799 in Task 4a's second run): rare but
    ///   real, and worth handling separately, because settling the source at callback time
    ///   lets a non-scanner pass through untouched and leaves IME composition alone, whereas
    ///   withhold-and-replay synthesizes an event — the thing most likely to break
    ///   composition (spec §4.3 lists IME as its own acceptance item).
    ///
    ///   An implementation assuming hook-always-first is correct 99.4% of the time and wrong
    ///   about once every forty scans: frequent enough to matter on site, rare enough to
    ///   survive a demonstration.
    /// </summary>
    [Fact]
    public void Raw_input_first_decides_immediately_without_withholding()
    {
        var correlator = CreateCorrelator();

        // 扫码枪先送 Raw Input / scanner's Raw Input arrives first
        Assert.Empty(correlator.AcceptRawInputEvent(RawEvent(deviceId: ScannerDeviceId)));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var scannerOutcome = correlator.AcceptHookEvent(HookEvent());

        Assert.Equal(CorrelationDecision.Swallow, scannerOutcome.Decision);
        Assert.Empty(scannerOutcome.Resolved);

        // 其他设备先送 Raw Input —— 应当直接放行，而不是重放
        // Another device's Raw Input first — pass through, not replay
        Assert.Empty(correlator.AcceptRawInputEvent(
            RawEvent(scanCode: ScanCodeK, deviceId: KeyboardDeviceId)));

        var keyboardOutcome = correlator.AcceptHookEvent(HookEvent(scanCode: ScanCodeK));

        Assert.Equal(CorrelationDecision.PassThrough, keyboardOutcome.Decision);
    }

    /// <summary>
    /// 中文：
    ///   CR4 —— 两条通道报告的虚拟键码不同时，仍然按扫描码正确关联。
    ///
    ///   ★ 这是 Task 4a 的实测发现写成的可执行形式，也是本文件里最重要的一条。
    ///
    ///     实测日志（docs/TASK_4A_MEASUREMENTS_*.md 第 2 节）：
    ///       钩子      0xA0 VK_LSHIFT
    ///       Raw Input 0x10 VK_SHIFT
    ///     扫描码都是 0x2A。
    ///
    ///     钩子区分左右修饰键，Raw Input 只给通用码。若按虚拟键码关联，所有
    ///     修饰键永远配不上；而大写字母必须按 Shift，于是**每一个含大写字母的
    ///     条码都会认错来源**——仓库条码几乎都含大写字母。
    ///
    ///     诊断工具第一版就是这么写的，报出四百多个配不上的事件。这个坑若留到
    ///     Task 4b 才踩，现场表现是"扫含大写字母的条码识别不出来源"，而那时
    ///     要同时面对未验证的拦截逻辑和这个隐藏的配对错误。
    ///
    ///   ★ 一处必须诚实说明的事：本条**当前无法通过修改关联器让它变红**。
    ///
    ///     尝试过了。RawInputEvent 上根本没有虚拟键码字段，KeyIdentity 也没有，
    ///     所以"按虚拟键码配对"这件事在类型层面就写不出来——不是关联器小心
    ///     避开了它，而是它不可表达。
    ///
    ///     这比一条能变红的测试**更强**，但意味着本条守的不是今天的实现，
    ///     而是将来某次改动：若有人给 RawInputEvent 补上虚拟键码、又把它塞进
    ///     KeyIdentity，本条会立刻变红。守卫的范围就是这个，不该夸大成别的。
    ///
    ///     （与测试计划 D4 同样的诚实口径：测试不该断言一件比实际更强的事。）
    /// English:
    ///   CR4 — correlation still succeeds on scan code when the channels report different
    ///   virtual keys. Task 4a's measurement in executable form, and the most important case
    ///   in this file.
    ///
    ///   The log recorded the hook reporting 0xA0 VK_LSHIFT where Raw Input reported 0x10
    ///   VK_SHIFT, both with scan code 0x2A. The hook distinguishes left and right modifiers
    ///   while Raw Input gives only the generic code. Correlating on the virtual key leaves
    ///   every modifier permanently unpaired — and a capital letter requires Shift, so every
    ///   barcode containing one has its source misidentified. Warehouse barcodes are almost
    ///   entirely capitals.
    ///
    ///   The harness's first version did exactly this and reported four hundred-odd unpaired
    ///   events. Left until Task 4b it would present as "the source of barcodes with capitals
    ///   cannot be identified", to be diagnosed alongside unproven interception logic.
    ///
    ///   One thing must be stated honestly: this case cannot currently be made to fail by
    ///   changing the correlator. It was attempted. RawInputEvent carries no virtual key and
    ///   neither does KeyIdentity, so "pair on the virtual key" is not expressible — the
    ///   correlator does not avoid the mistake, the types make it impossible. That is
    ///   *stronger* than a test that can go red, but it means this case guards a future change
    ///   rather than today's implementation: adding a virtual key to RawInputEvent and folding
    ///   it into KeyIdentity turns it red immediately. That is the extent of the guard and it
    ///   should not be described as more. (The same honest accounting as the test plan's D4: a
    ///   test must not assert something stronger than what holds.)
    /// </summary>
    [Fact]
    public void Correlates_on_scan_code_when_virtual_keys_disagree()
    {
        var correlator = CreateCorrelator();

        // 钩子报左 Shift / the hook reports left Shift
        var hookEvent = HookEvent(
            scanCode: ScanCodeLeftShift, virtualKey: VkLeftShift, character: null);

        Assert.Equal(CorrelationDecision.Undecided, correlator.AcceptHookEvent(hookEvent).Decision);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        // Raw Input 对同一次按键报通用 Shift —— 虚拟键码不同，扫描码相同
        // Raw Input reports the generic Shift for the same keystroke: different virtual key,
        // same scan code
        var resolved = correlator.AcceptRawInputEvent(
            RawEvent(scanCode: ScanCodeLeftShift, deviceId: ScannerDeviceId));

        var resolution = Assert.Single(resolved);
        Assert.True(resolution.Decision == CorrelationDecision.Swallow,
            "两条通道对修饰键报告的虚拟键码不同（钩子 VK_LSHIFT，Raw Input VK_SHIFT），"
            + "而扫描码一致。关联必须按扫描码，否则每一个含大写字母的条码都会认错来源。"
            + $" 实际判定为 {resolution.Decision}。");

        // 顺带钉住前提：这两个虚拟键码确实不同，测试不是在自说自话
        // Pin the premise: the two virtual keys really do differ, so this test is not
        // asserting against itself
        Assert.NotEqual(VkLeftShift, VkShift);
    }

    /// <summary>
    /// 中文：
    ///   CR5 —— 扫描码相同但扩展位不同的两个键，不得互相关联。
    ///
    ///   右 Ctrl 与左 Ctrl 的扫描码都是 0x1D，只有 E0 前缀能区分。少了这一位，
    ///   左右修饰键会互相配错，而配错之后算出来的来源判定是纯噪声，却和真数据
    ///   长得一模一样。
    /// English:
    ///   CR5 — two keys sharing a scan code but differing in the extended flag must not
    ///   correlate. Right Ctrl and left Ctrl both use 0x1D and only the E0 prefix separates
    ///   them; without it they pair with each other, and an attribution built on a wrong pair
    ///   is pure noise that looks exactly like real data.
    /// </summary>
    [Fact]
    public void Same_scan_code_with_different_extended_flag_does_not_correlate()
    {
        var correlator = CreateCorrelator();
        const ushort scanCodeControl = 0x1D;

        // 左 Ctrl（非扩展）被扣留 / left Ctrl (not extended) is withheld
        correlator.AcceptHookEvent(HookEvent(scanCode: scanCodeControl, isExtended: false));

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        // 右 Ctrl（扩展）的 Raw Input 到达 —— 不该认领左 Ctrl 那个事件
        // Right Ctrl's (extended) Raw Input arrives — it must not claim the left Ctrl event
        var resolved = correlator.AcceptRawInputEvent(
            RawEvent(scanCode: scanCodeControl, isExtended: true));

        Assert.Empty(resolved);
    }

    /// <summary>
    /// 中文：CR6 —— 按下不得与弹起关联。
    ///       一次按键在两条通道上各产生按下与弹起两个事件；身份若不含方向，
    ///       某个键的弹起会跟另一次按下配成对，整条队列随即错位。
    /// English: CR6 — a key-down must not correlate with a key-up. Each keystroke produces a
    ///          down and an up on both channels, and without direction in the identity one
    ///          key's up pairs with another's down and the whole queue shifts.
    /// </summary>
    [Fact]
    public void Key_down_does_not_correlate_with_key_up()
    {
        var correlator = CreateCorrelator();

        correlator.AcceptHookEvent(HookEvent(isKeyUp: false));

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Empty(correlator.AcceptRawInputEvent(RawEvent(isKeyUp: true)));
    }

    /// <summary>
    /// 中文：
    ///   CR7 —— 同一个键连按两次时，按先进先出的顺序各自配对。
    ///
    ///   扫码枪一枪里出现重复字符是家常便饭（例如 DGKJRDC5679F5NF 里的两个 5）。
    ///   顺序错乱本身不会立刻表现为错误——两个事件的内容一样——但会让时差统计
    ///   失真，进而让关联窗口被定错。
    /// English:
    ///   CR7 — two presses of one key correlate in FIFO order. Repeated characters within a
    ///   scan are routine (the two 5s in DGKJRDC5679F5NF). Mis-ordering does not surface as
    ///   an immediate error, the two events having identical content, but it distorts the
    ///   delta statistics and through them the choice of correlation window.
    /// </summary>
    [Fact]
    public void Repeated_presses_of_one_key_correlate_in_order()
    {
        var correlator = CreateCorrelator();

        var firstPress = HookEvent();
        correlator.AcceptHookEvent(firstPress);

        _clock.Advance(TimeSpan.FromMilliseconds(2));
        var secondPress = HookEvent();
        correlator.AcceptHookEvent(secondPress);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var firstResolution = Assert.Single(correlator.AcceptRawInputEvent(RawEvent()));
        var secondResolution = Assert.Single(correlator.AcceptRawInputEvent(RawEvent()));

        Assert.Equal(firstPress, firstResolution.Event);
        Assert.Equal(secondPress, secondResolution.Event);
        Assert.NotEqual(firstResolution.Event.Timestamp, secondResolution.Event.Timestamp);
    }

    /// <summary>
    /// 中文：CR8 —— 不同的键交错到达时各自独立关联，互不干扰。
    /// English: CR8 — interleaved different keys correlate independently.
    /// </summary>
    [Fact]
    public void Interleaved_keys_correlate_independently()
    {
        var correlator = CreateCorrelator();

        var eventD = HookEvent(scanCode: ScanCodeD);
        var eventK = HookEvent(scanCode: ScanCodeK, virtualKey: 0x4B, character: 'K');

        correlator.AcceptHookEvent(eventD);
        correlator.AcceptHookEvent(eventK);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        var resolvedK = Assert.Single(correlator.AcceptRawInputEvent(RawEvent(scanCode: ScanCodeK)));
        var resolvedD = Assert.Single(correlator.AcceptRawInputEvent(RawEvent(scanCode: ScanCodeD)));

        Assert.Equal(eventK, resolvedK.Event);
        Assert.Equal(eventD, resolvedD.Event);
    }

    /// <summary>
    /// 中文：CR9 —— Raw Input 的到达顺序与钩子不同时，每个事件仍找到自己的对家。
    ///       与 CR8 的区别在于这里 Raw Input 整体乱序，而不只是交错。
    /// English: CR9 — each event still finds its own counterpart when the Raw Input events
    ///          arrive in a different order than the hook events. Unlike CR8, the Raw Input
    ///          side is reordered wholesale rather than merely interleaved.
    /// </summary>
    [Fact]
    public void Raw_input_arriving_out_of_order_still_finds_its_own_counterpart()
    {
        var correlator = CreateCorrelator();

        var down = HookEvent(isKeyUp: false);
        correlator.AcceptHookEvent(down);
        var up = HookEvent(isKeyUp: true);
        correlator.AcceptHookEvent(up);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        // 先送弹起、后送按下 / the up arrives before the down
        var resolvedUp = Assert.Single(correlator.AcceptRawInputEvent(RawEvent(isKeyUp: true)));
        var resolvedDown = Assert.Single(correlator.AcceptRawInputEvent(RawEvent(isKeyUp: false)));

        Assert.True(resolvedUp.Event.IsKeyUp);
        Assert.False(resolvedDown.Event.IsKeyUp);
    }

    /// <summary>
    /// 中文：
    ///   CR10 —— 等不到 Raw Input 对家的钩子事件，超过关联窗口后按**重放**结掉，
    ///           并计入 UnresolvedEventCount（决策 D-13）。
    ///
    ///   方向的选择是有取舍的，且取舍方向是明确的：
    ///     重放 → 万一它其实是扫码枪的，一个原始字符会泄漏进业务软件。
    ///            但它会显眼地落在输入框里，工人看得见、能改。
    ///     吞掉 → 万一它其实是键盘的，工人的按键凭空消失，而且是隐形的。
    ///            "键盘失灵"正是规格 §5.7 要救的那个灾难，笔记本工位
    ///            没有备用键盘可插（假设 A4）。
    ///
    ///   规格 §19 已经替我们做了这个选择："绝不无限期吞掉普通键盘输入。"
    /// English:
    ///   CR10 — a hook event whose Raw Input counterpart never arrives is settled as Replay
    ///   once the window passes, and counted in UnresolvedEventCount (decision D-13).
    ///
    ///   The direction is a trade, and the trade is clear. Replaying risks leaking one raw
    ///   scanner character into the business application, where it lands visibly in a focused
    ///   field and the operator can correct it. Swallowing risks the operator's keystroke
    ///   vanishing invisibly — and "the keyboard stopped working" is the disaster spec §5.7
    ///   exists to rescue, with no spare keyboard on a laptop (assumption A4). Spec §19 has
    ///   already made the choice: normal keyboard input must never be swallowed indefinitely.
    /// </summary>
    [Fact]
    public void Hook_event_without_counterpart_is_replayed_after_the_window()
    {
        var correlator = CreateCorrelator(TimeSpan.FromMilliseconds(50));
        var abandoned = HookEvent();

        Assert.Equal(CorrelationDecision.Undecided, correlator.AcceptHookEvent(abandoned).Decision);

        // 窗口未到 —— 还在等 / before the window: still waiting
        _clock.Advance(TimeSpan.FromMilliseconds(49));
        Assert.Empty(correlator.Advance());
        Assert.Equal(0, correlator.UnresolvedEventCount);

        // 窗口到了 —— 按重放结掉 / at the window: settled as replay
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var resolved = correlator.Advance();

        var resolution = Assert.Single(resolved);
        Assert.Equal(CorrelationDecision.Replay, resolution.Decision);
        Assert.Equal(abandoned, resolution.Event);
        Assert.True(resolution.DeviceId is null,
            "来源始终没能确定，DeviceId 必须为 null——把它伪装成某台设备，"
            + "等于显示一个未经验证的状态（规格 §19.1）。");
        Assert.Equal(1, correlator.UnresolvedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   CR11 —— 合成事件立刻放行，**绝不进入关联**（规格 §5.6）。
    ///
    ///   ★ "绝不进入关联"这半句才是重点，而且必须发生在关联之前。
    ///
    ///     Task 4a 已确认合成事件根本不出现在 Raw Input 通道上。因此一个进入了
    ///     关联的合成事件会：等一个不可能存在的对家 → 超时 → 按 CR10 被重放。
    ///     而重放意味着本程序把自己刚发出去的输出又发了一遍，那一遍又会被钩子
    ///     看到……形成规格 §5.6 明令禁止的
    ///         SendInput → 钩子 → SendInput → …
    ///     无限递归。
    ///
    ///     可怕之处在于抵达这个后果的路径**看起来完全正确**：一个合理的超时
    ///     处理，加上一条合理的"判不出来就重放"规则。只有把合成事件挡在关联
    ///     之外，这条路才被彻底堵死。
    /// English:
    ///   CR11 — a synthesized event passes through immediately and never enters correlation
    ///   (spec §5.6). The second half is the point, and it must happen before correlation.
    ///
    ///   Task 4a confirmed synthesized events never appear on the Raw Input channel, so one
    ///   that entered correlation would wait for a counterpart that cannot exist, expire, and
    ///   be replayed under CR10 — meaning the application re-emits what it just emitted, which
    ///   the hook sees again, producing the endless SendInput → hook → SendInput recursion
    ///   spec §5.6 forbids.
    ///
    ///   What makes it dangerous is that the route there looks entirely correct: reasonable
    ///   timeout handling plus a reasonable "replay when unresolvable" rule. Only excluding
    ///   synthesized events from correlation closes it.
    /// </summary>
    [Fact]
    public void Injected_event_passes_through_without_entering_correlation()
    {
        var correlator = CreateCorrelator(TimeSpan.FromMilliseconds(50));

        var outcome = correlator.AcceptHookEvent(HookEvent(isInjected: true));

        Assert.Equal(CorrelationDecision.PassThrough, outcome.Decision);
        Assert.Empty(outcome.Resolved);

        // 关键断言：它没有被扣留，因此超时后不会冒出一个重放请求。
        // 若它进入了关联，这里会得到一条 Replay —— 也就是把自己的输出再发一遍。
        // The load-bearing assertion: it was not withheld, so no replay request appears after
        // the window. Had it entered correlation, a Replay would surface here — the
        // application re-emitting its own output.
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Empty(correlator.Advance());
        Assert.Equal(0, correlator.UnresolvedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   CR12 —— 等不到钩子对家的 Raw Input 事件被丢弃并计数。
    ///
    ///   它不为零指向一个具体的原因：钩子那一次被 Windows 跳过了（规格 §19.1 的
    ///   LowLevelHooksTimeout）。Task 4a 实测到过这类事件。
    ///
    ///   ★ 这类按键**没有被吞掉**——原始字符直接进了业务软件，而我们的缓冲区里
    ///     少了它。两头都错，界面却显示一切正常。所以它必须被计数并暴露出去，
    ///     不能悄悄丢掉（规格 §19：无法确信的事件要安全失败并留下诊断）。
    /// English:
    ///   CR12 — a Raw Input event whose hook counterpart never arrives is discarded and
    ///   counted. A non-zero count points at one cause: the hook being skipped for that event
    ///   (spec §19.1's LowLevelHooksTimeout), which Task 4a observed.
    ///
    ///   Such a keystroke was not swallowed — the raw character reached the business
    ///   application while our buffer is missing it. Wrong in both directions with the UI
    ///   reporting success, which is why it must be counted and surfaced rather than dropped
    ///   quietly (spec §19: fail safely and surface diagnostically).
    /// </summary>
    [Fact]
    public void Raw_input_without_counterpart_is_discarded_and_counted()
    {
        var correlator = CreateCorrelator(TimeSpan.FromMilliseconds(50));

        Assert.Empty(correlator.AcceptRawInputEvent(RawEvent()));

        _clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Empty(correlator.Advance());

        Assert.Equal(1, correlator.DiscardedRawInputCount);
        Assert.Equal(0, correlator.UnresolvedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   CR13 —— 超时判定读的是注入的单调时钟，不是挂钟。
    ///
    ///   把挂钟推进很久、单调时刻纹丝不动，事件就不该过期。这条钉住的是
    ///   "不要拿挂钟算超时"：挂钟会因 NTP 校时跳变，用它算超时会在现场表现为
    ///   "偶尔莫名其妙丢一枪"，且完全无法复现——正是最难查的那一类故障。
    /// English:
    ///   CR13 — expiry reads the injected monotonic clock, not the wall clock. Advancing the
    ///   wall clock far while the monotonic instant stands still must expire nothing. This
    ///   pins "do not compute timeouts from the wall clock": it jumps under NTP correction,
    ///   presenting on site as "we occasionally lose a scan for no reason", irreproducibly —
    ///   the hardest kind of fault to chase.
    /// </summary>
    [Fact]
    public void Expiry_follows_the_injected_clock_not_the_wall_clock()
    {
        var correlator = CreateCorrelator(TimeSpan.FromMilliseconds(50));
        correlator.AcceptHookEvent(HookEvent());

        _clock.AdvanceWallClock(TimeSpan.FromHours(1));

        Assert.Empty(correlator.Advance());
        Assert.Equal(0, correlator.UnresolvedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   CR14 —— 待配对事件的数量有上限，不会无限增长。
    ///
    ///   防的是某条通道彻底停摆而另一条还在送事件的情形（例如钩子被 Windows
    ///   摘掉了，规格 §19.1）。那时时间可能几乎没往前走，超时清理不触发，
    ///   队列却在一直涨——而这一切发生在扫描处理链路上。
    ///
    ///   本条特意让时间**不推进**，从而把超时清理这条路完全排除，单独验证
    ///   数量上限确实起作用。
    /// English:
    ///   CR14 — pending events are bounded and cannot grow without limit. This guards the case
    ///   where one channel stops entirely while the other keeps delivering (the hook removed
    ///   by Windows, spec §19.1): time may barely advance, expiry never fires, and the queues
    ///   grow — all of it on the scan-handling path.
    ///
    ///   The test deliberately does not advance time, ruling out expiry entirely so that the
    ///   cap alone is under test.
    /// </summary>
    [Fact]
    public void Pending_events_are_bounded()
    {
        var correlator = CreateCorrelator(TimeSpan.FromHours(1));
        var replayed = 0;

        for (var index = 0; index < InputEventCorrelator.MaximumPendingEvents * 2; index++)
        {
            replayed += correlator.AcceptHookEvent(HookEvent()).Resolved.Count;
        }

        Assert.True(replayed > 0,
            "待配对事件超过上限后必须被强制结掉，否则队列会一直涨到吃光内存，"
            + "而这发生在扫描处理链路上（规格 §19）。");
        Assert.Equal(replayed, correlator.UnresolvedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   CR15 —— 关联器的公开表面不依赖任何平台类型（规格 §17、决策 D-18）。
    ///
    ///   它必须保持为"对时间戳事件序列的纯函数"：消费事件、返回判断，不发送
    ///   输入、不写日志、不碰 UI、不直接读时钟。这是它能被穷尽测试的全部前提，
    ///   而它是全产品风险最高的组件——一旦它开始自己做事，那些分支就只能靠
    ///   真实硬件去撞。
    /// English:
    ///   CR15 — the correlator's public surface depends on no platform type (spec §17,
    ///   decision D-18). It must remain a pure function over timestamped events: consume
    ///   events, return decisions; send no input, write no log, touch no UI, read no clock
    ///   directly. That is the entire precondition for testing it exhaustively — and it is the
    ///   highest-risk component in the product, so the moment it starts doing things on its
    ///   own, those branches can only be hit with real hardware.
    /// </summary>
    [Fact]
    public void Correlator_exposes_no_platform_dependency()
    {
        var parameterTypes = typeof(InputEventCorrelator)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => Nullable.GetUnderlyingType(parameter.ParameterType)
                                 ?? parameter.ParameterType)
            .ToArray();

        Assert.All(parameterTypes, parameterType =>
            Assert.True(
                parameterType == typeof(ISystemClock) || parameterType == typeof(TimeSpan),
                $"关联器只应依赖注入的时间源与配置值，发现构造参数类型 {parameterType.Name}。"));

        var memberTypeNames = typeof(InputEventCorrelator)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .OfType<MethodInfo>()
            .Select(method => method.ReturnType.Name)
            .ToArray();

        Assert.DoesNotContain(memberTypeNames,
            name => name.Contains("Task", StringComparison.Ordinal));
    }

    /// <summary>
    /// 中文：CR16 —— 非法构造参数在构造时即被拒绝。
    ///       窗口为零意味着每个事件在到达的同一刻就过期，所有按键都会走进
    ///       重放路径——中文输入法会被持续打断，而扫码枪字符会持续泄漏。
    ///       这与决策 D-2 一脉相承：与运行时输入无关的配置错误一律在构造时拒绝。
    /// English: CR16 — invalid construction arguments are rejected at construction. A zero
    ///          window expires every event on arrival, sending every keystroke down the replay
    ///          path, continuously disrupting IME composition and continuously leaking scanner
    ///          characters. Consistent with decision D-2: configuration errors independent of
    ///          runtime input are rejected at construction.
    /// </summary>
    [Fact]
    public void Invalid_construction_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new InputEventCorrelator(null!));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InputEventCorrelator(_clock, TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InputEventCorrelator(_clock, TimeSpan.FromMilliseconds(-1)));
    }

    /// <summary>
    /// 中文：
    ///   CR17 —— 等待过久的事件不得与很晚才到来的事件配对。
    ///
    ///   ★★ 这是本文件里最有价值的一条，因为它是一次真实事故的复现。
    ///
    ///     诊断工具第一版没有过期规则。某条通道漏掉一个事件之后，该扫描码的
    ///     队列整体错位——后面每一个事件都跟队列里那个早就该配走的老事件配成对，
    ///     于是报出**负 14.8 秒**的"时差"。
    ///
    ///     最危险的不是它错了，而是它**看起来没错**：中位数依然正常，只有尾部
    ///     离谱。若没人去看最小值，整份分布会被当成真数据拿去定关联窗口。
    ///
    ///     落到产品上，同样的错位意味着：一次漏事件之后，后续每一次按键的来源
    ///     判定都是错的，而且会一直错下去，没有任何东西指回起因。扫码枪的字符
    ///     会被当成键盘输入重放进业务软件，键盘输入会被当成扫码数据吞掉。
    ///
    ///   本条把损害限制在那一个事件上：老的按超时结掉，晚到的重新开始等。
    ///
    ///   验证方式：去掉过期规则，本条、CR10、CR12 一起变红——三条从三个方向
    ///   描述同一个性质。
    /// English:
    ///   CR17 — an over-aged event must not pair with one that arrives much later. The most
    ///   valuable case here, because it reproduces a real incident.
    ///
    ///   The harness's first version had no expiry. After one channel missed an event, the
    ///   queue for that scan code shifted permanently: every later event paired with one long
    ///   overdue, reporting a delta of minus 14.8 seconds. The danger was not being wrong but
    ///   looking right — the median stayed plausible and only the tail betrayed it, and had
    ///   nobody examined the minimum the whole distribution would have been used to size the
    ///   correlation window.
    ///
    ///   In the product the same shift means every keystroke after one lost event is
    ///   attributed to the wrong device, indefinitely, with nothing pointing back at the
    ///   cause: scanner characters replayed into the business application as keyboard input,
    ///   keyboard input swallowed as scan data.
    ///
    ///   This confines the damage to the single event. Verify by removing expiry: this, CR10
    ///   and CR12 turn red together, being three views of one property.
    /// </summary>
    [Fact]
    public void Stale_pending_event_does_not_pair_with_a_much_later_event()
    {
        var correlator = CreateCorrelator(TimeSpan.FromMilliseconds(50));

        var abandoned = HookEvent();
        correlator.AcceptHookEvent(abandoned);

        // 很久之后，同一个键再次被按下并正常关联
        // Much later, the same key is pressed again and correlates normally
        _clock.Advance(TimeSpan.FromSeconds(15));

        var freshPress = HookEvent();
        var freshOutcome = correlator.AcceptHookEvent(freshPress);

        // 那个被遗弃的事件在此刻按超时结掉，而不是等着跟新事件配对
        // The abandoned event is settled by expiry now, rather than waiting to pair with the
        // new one
        var expired = Assert.Single(freshOutcome.Resolved);
        Assert.Equal(abandoned, expired.Event);
        Assert.Equal(CorrelationDecision.Replay, expired.Decision);

        // 新事件正常等待自己的对家
        // The new event waits for its own counterpart
        Assert.Equal(CorrelationDecision.Undecided, freshOutcome.Decision);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var resolution = Assert.Single(correlator.AcceptRawInputEvent(RawEvent()));

        Assert.True(resolution.Event == freshPress,
            "Raw Input 必须配到刚才那次按键，而不是 15 秒前那个被遗弃的事件。"
            + "配错之后算出来的来源判定是纯噪声，却和真数据长得一模一样。");
    }
}
