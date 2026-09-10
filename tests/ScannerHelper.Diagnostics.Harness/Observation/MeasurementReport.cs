// =============================================================================
// MeasurementReport.cs
//
// 中文：
//   把一次观测的结果渲染成 Markdown 报告（规格 §4.3、实施计划 Task 4a）。
//
//   计划要求把测量报告提交进 docs/，它是"后续每一个架构论断的证据基础"。
//   既然是证据，它就必须能被后来的人独立读懂和质疑，因此这里有三条规矩：
//
//   1. **不下结论到超出数据的地方。** 报告陈述观测到了什么，并把"这意味着
//      什么"限制在数据直接支持的范围内。规格 §4.3 明确禁止用时序启发式
//      掩盖问题，而掩盖往往就是从一句"看起来没问题"开始的。
//
//   2. **数据不完整必须写在最显眼处，而不是脚注里。** 丢弃的事件、未配对的
//      事件，都直接动摇结论的可信度。一份悄悄少了几个样本的分布，比没有
//      数据更危险——它看起来同样可信。
//
//   3. **必须写清楚是在哪台机器上测的。** 事件时序是机器的性质，不是代码的
//      性质。开发机与试点机不是同一台硬件，因此这里的每一个数字在现场机器上
//      复现之前都只是暂定值。不写清楚，读者会默认它代表现场。
//
// English:
//   Renders one observation run as a Markdown report (spec §4.3, plan Task 4a).
//
//   The plan requires committing the report into docs/ as "the evidence base for every
//   later architectural argument". Being evidence, it must be independently readable
//   and challengeable by whoever comes next, which imposes three rules.
//
//   First, draw no conclusion past the data. The report states what was observed and
//   confines "what it means" to what the data directly supports. Spec §4.3 forbids
//   papering over problems with timing heuristics, and papering over usually begins
//   with a sentence like "seems fine".
//
//   Second, incompleteness goes at the top, not in a footnote. Dropped and unpaired
//   events both undermine the conclusions. A distribution quietly missing samples is
//   more dangerous than no data — it looks equally credible.
//
//   Third, name the machine. Event timing is a property of the machine, not of the
//   code. The development machine and the pilot machines are different hardware, so
//   every figure here is provisional until reproduced on site. Left unstated, a reader
//   assumes it is site-representative.
//
// 包含的成员 / Members in this file:
//   Build  渲染报告
// =============================================================================

using System.Globalization;
using System.Text;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：把观测结果渲染成 Markdown。
/// English: Renders observation results as Markdown.
/// </summary>
public static class MeasurementReport
{
    /// <summary>
    /// 中文：
    ///   渲染报告。
    ///   输入：session 观测会话；machineDescription 测量所用机器的描述；
    ///         operatorNotes 人工补充的观察记录，可为空。
    ///   输出：Markdown 文本。
    /// English:
    ///   Renders the report from a session, a description of the machine used, and
    ///   optional notes the operator typed.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：session 为 null。 English: session is null.
    /// </exception>
    public static string Build(
        ObservationSession session, string machineDescription, string? operatorNotes)
    {
        ArgumentNullException.ThrowIfNull(session);

        var report = new StringBuilder();
        var deltas = session.Pairing.Pairs
            .Select(pair => pair.DeltaMicroseconds)
            .ToArray();

        report.AppendLine("# Task 4a — 输入观测测量报告 / Input Observation Measurements");
        report.AppendLine();
        report.AppendLine(
            $"- 生成时间 / Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(
            $"- 观测开始 / Run started: {session.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—"}");
        report.AppendLine($"- 测量机器 / Machine: {Describe(machineDescription)}");
        report.AppendLine();

        AppendValidity(report, session, deltas.Length);
        AppendOrdering(report, session, deltas);
        AppendVirtualKeyDisagreement(report, session);
        AppendDelta(report, deltas);
        AppendDevices(report, session);
        AppendReconstructedScans(report, session);
        AppendAttachedKeyboards(report, session);
        AppendOperatorNotes(report, operatorNotes);
        AppendOpenQuestions(report);

        return report.ToString();
    }

    /// <summary>
    /// 中文：
    ///   数据有效性。放在报告最前面，而不是末尾的脚注里。
    ///
    ///   读者读到任何一个数字之前，必须先知道这批数据完不完整。丢弃事件意味着
    ///   分布有缺口，未配对事件意味着两条通道看到的东西对不上——两者都可能
    ///   让下面的结论整体失效。放在末尾等于默认没人会读到那里。
    /// English:
    ///   Data validity, placed first rather than as a closing footnote. A reader must
    ///   know whether the data is complete before reading any figure. Dropped events
    ///   mean gaps in the distribution; unpaired events mean the two channels disagree
    ///   about what happened. Either can invalidate everything below. Putting this last
    ///   assumes nobody reads that far.
    /// </summary>
    private static void AppendValidity(
        StringBuilder report, ObservationSession session, int pairCount)
    {
        var dropped = session.DroppedEventCount;
        var unpairedHook = session.Pairing.UnpairedHookCount;
        var unpairedRaw = session.Pairing.UnpairedRawInputCount;
        var isClean = dropped == 0 && unpairedHook == 0 && unpairedRaw == 0;

        report.AppendLine("## 0. 数据有效性 / Data validity");
        report.AppendLine();
        report.AppendLine("| 项 / Item | 值 / Value |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| 成功配对的按键事件 / Paired key events | {pairCount} |");
        report.AppendLine($"| 缓冲区丢弃 / Dropped by buffer | {dropped} |");
        report.AppendLine($"| 钩子看到、Raw Input 未见 / Hook only | {unpairedHook} |");
        report.AppendLine($"| Raw Input 看到、钩子未见 / Raw Input only | {unpairedRaw} |");
        report.AppendLine();

        if (isClean)
        {
            report.AppendLine(
                "两条通道观测到的事件完全对得上，缓冲区没有丢弃。下面的统计基于完整数据。");
            report.AppendLine();
            report.AppendLine(
                "The two channels agree event for event and nothing was dropped. "
                + "The statistics below rest on complete data.");
        }
        else
        {
            report.AppendLine(
                "⚠️ **本次数据不完整，下面的统计不能作为完整证据使用。**");
            report.AppendLine();
            report.AppendLine(
                "- 缓冲区丢弃不为零：说明界面线程被拖住的时间超过了缓冲区容量所能覆盖的范围，"
                + "分布中间存在缺口，尾部百分位尤其不可信。");
            report.AppendLine(
                "- 未配对事件不为零：说明某一条通道看到了另一条完全没看到的按键。"
                + "**这本身可能是比时序更重要的发现**，不要当作噪声略过。下面的明细"
                + "给出了具体是哪一类按键。");
            report.AppendLine();
            report.AppendLine(
                "⚠️ **This run is incomplete and the statistics below are not complete evidence.**");
        }

        report.AppendLine();

        AppendUnmatchedBreakdown(report, session);
    }

    /// <summary>
    /// 中文：
    ///   未配对事件的分类明细。
    ///
    ///   ★ 只给总数是没法行动的。"有 40 个事件没配上"什么都说明不了；
    ///     "这 40 个都是扫描码 0x1F 的按下、只出现在 Raw Input 一侧"才指向原因。
    ///
    ///   两个方向的含义完全不同，必须分开读：
    ///     只有 Raw Input 看到 → 钩子那次被跳过了。规格 §19.1 的
    ///                           LowLevelHooksTimeout 就是干这个的：回调一旦超时，
    ///                           Windows 跳过它、通常还把钩子摘掉，且不发通知。
    ///                           **这对 4b 是要命的**——被跳过的那次按键不会被吞掉，
    ///                           原始字符直接进入业务软件，同时我们的缓冲区里少了它。
    ///     只有钩子看到       → 合成事件（本工具已排除），或者某类输入根本不经过
    ///                           Raw Input。
    /// English:
    ///   The breakdown of unpaired events.
    ///
    ///   A bare total is not actionable: "40 unpaired" says nothing, while "all 40 were
    ///   scan code 0x1F key-downs seen only by Raw Input" points at a cause.
    ///
    ///   The two directions mean entirely different things. Raw-Input-only means the hook
    ///   was skipped for that event — spec §19.1's LowLevelHooksTimeout does exactly
    ///   that, and it is serious for 4b, since a skipped keystroke is not swallowed: the
    ///   raw character reaches the business application while our buffer is missing it.
    ///   Hook-only means a synthesized event (already excluded here) or some input that
    ///   bypasses Raw Input.
    /// </summary>
    private static void AppendUnmatchedBreakdown(StringBuilder report, ObservationSession session)
    {
        var breakdown = session.Pairing.UnmatchedBreakdown;
        if (breakdown.Count == 0)
        {
            return;
        }

        report.AppendLine("### 0.1 未配对事件明细 / Unpaired event breakdown");
        report.AppendLine();
        report.AppendLine("| 只出现在 / Seen only by | 扫描码 / Scan | 虚拟键 / VK | 方向 / Dir | 次数 / Count |");
        report.AppendLine("|---|---|---|---|---|");

        foreach (var (kind, count) in breakdown.Take(20))
        {
            var channel = kind.Channel == InputChannel.Hook ? "钩子 / Hook" : "Raw Input";
            report.AppendLine(
                $"| {channel} | `0x{kind.ScanCode:X2}` | `0x{kind.VirtualKey:X2}` "
                + $"{DescribeVirtualKey(kind.VirtualKey)} | {(kind.IsKeyUp ? "up" : "down")} | {count} |");
        }

        report.AppendLine();
        report.AppendLine(
            "**只有 Raw Input 看到** 意味着那一次钩子被跳过了。规格 §19.1 描述的"
            + "`LowLevelHooksTimeout` 正是这个机制：回调超时一次，Windows 就跳过它、"
            + "通常还把钩子摘掉，且不发任何通知。");
        report.AppendLine();
        report.AppendLine(
            "⚠️ **这对 Task 4b 是要命的。** 被跳过的那次按键不会被吞掉——原始字符"
            + "直接进入业务软件，同时我们自己的缓冲区里也少了它。结果是网页里多出"
            + "一个不该有的字符，而我们发出去的码少一个字符，两头都错，界面却显示"
            + "一切正常。若这个比例在试点机器上仍然显著，4b 的硬性关卡就该拦下。");
        report.AppendLine();
        report.AppendLine(
            "Raw-Input-only means the hook was skipped for that event. In 4b that keystroke "
            + "would not be swallowed: the raw character reaches the business application "
            + "while our own buffer is missing it — wrong in both directions, with the UI "
            + "reporting success.");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：
    ///   第 1 问 —— 哪一条通道先到。整个架构就压在这个答案上（规格 §4.3）。
    /// English:
    ///   Question 1 — which channel arrives first. The entire architecture rests on
    ///   this answer (spec §4.3).
    /// </summary>
    private static void AppendOrdering(
        StringBuilder report, ObservationSession session, IReadOnlyCollection<double> deltas)
    {
        var hookFirst = session.Pairing.HookFirstCount;
        var rawFirst = session.Pairing.RawInputFirstCount;
        var total = hookFirst + rawFirst;

        report.AppendLine("## 1. 哪一条通道先到 / Which channel arrives first");
        report.AppendLine();
        report.AppendLine("| 顺序 / Order | 次数 / Count | 占比 / Share |");
        report.AppendLine("|---|---|---|");
        report.AppendLine(
            $"| 钩子先到 / Hook first | {hookFirst} | {FormatShare(hookFirst, total)} |");
        report.AppendLine(
            $"| Raw Input 先到 / Raw Input first | {rawFirst} | {FormatShare(rawFirst, total)} |");
        report.AppendLine();

        if (total == 0)
        {
            report.AppendLine("没有配对样本，无法回答本问。 No paired samples; unanswered.");
            report.AppendLine();
            return;
        }

        report.AppendLine("**这个数字为什么决定架构 / Why this decides the architecture**");
        report.AppendLine();
        report.AppendLine(
            "钩子回调必须**当场**决定放行还是吞掉，而且不能阻塞等待——等待超过 "
            + "`LowLevelHooksTimeout`（默认 300 毫秒），Windows 会跳过回调、通常还把钩子"
            + "摘掉且不发任何通知（规格 §19.1）。");
        report.AppendLine();

        if (hookFirst > rawFirst)
        {
            report.AppendLine(
                "本次观测中**钩子先到占多数**。这意味着回调必须做决定时，设备身份"
                + "还没到手，因此无法在回调里直接判断「这一下是不是扫码枪按的」。");
            report.AppendLine();
            report.AppendLine(
                "按规格 §5.3，此时唯一可行的设计是**扣留后重放**：先把按键全部吞掉，"
                + "等 `WM_INPUT` 说明来源之后，若是普通键盘再用 `SendInput` 补发回去。"
                + "代价是所有普通打字都要绕这一圈，中文输入法的组字过程最容易在这里"
                + "被打断——规格 §4.3 把 IME 单列为验收项，正是为此。");
            report.AppendLine();
            report.AppendLine(
                "Hook-first dominates. Device identity is therefore unavailable when the "
                + "callback must decide, so the callback cannot itself judge whether the "
                + "keystroke came from the scanner. Per spec §5.3 the only viable design is "
                + "withhold-then-replay, whose cost falls on ordinary typing and, above all, "
                + "on IME composition — which is why spec §4.3 lists IME as its own "
                + "acceptance item.");
        }
        else
        {
            report.AppendLine(
                "本次观测中**Raw Input 先到占多数**。若这个结论在试点机器上同样成立，"
                + "回调就有可能在做决定时已经拿到设备身份，从而**不必**对普通打字做"
                + "扣留-重放——那会显著降低 IME 被打断的风险。");
            report.AppendLine();
            report.AppendLine(
                "⚠️ 但**不要**据此直接删掉扣留-重放路径。占多数不等于永远成立，"
                + "下一节的尾部分布才是决定性的：只要有一部分按键是钩子先到，"
                + "那一部分就仍然需要退路。");
            report.AppendLine();
            report.AppendLine(
                "Raw Input first dominates. If this holds on pilot hardware, the callback may "
                + "already have device identity when it decides, and ordinary typing may not "
                + "need withhold-and-replay at all — substantially reducing the IME risk. "
                + "Do **not** delete the withhold path on the strength of this: a majority is "
                + "not an always, and the tail in the next section is what decides. Any share "
                + "of hook-first keystrokes still needs a fallback.");
        }

        report.AppendLine();

        if (deltas.Count > 0 && hookFirst > 0 && rawFirst > 0)
        {
            report.AppendLine(
                "⚠️ **两种顺序都出现过。** 这说明顺序不是恒定的，任何「总是先到」的假设"
                + "都不成立，设计必须同时处理两种情况。");
            report.AppendLine();
        }
    }

    /// <summary>
    /// 中文：
    ///   两条通道的虚拟键码是否一致。这一节是第一轮测量意外发现的，不在规格
    ///   §4.3 原本的问题清单里，但它直接决定关联器怎么写。
    /// English:
    ///   Whether the channels agree on virtual key codes. This section came out of the
    ///   first measurement run unplanned — it is not on spec §4.3's original question
    ///   list — but it settles how the correlator must be written.
    /// </summary>
    private static void AppendVirtualKeyDisagreement(
        StringBuilder report, ObservationSession session)
    {
        var disagreements = session.Pairing.VirtualKeyDisagreements;

        report.AppendLine("## 2. 两条通道的虚拟键码是否一致 / Virtual-key agreement");
        report.AppendLine();

        if (disagreements.Count == 0)
        {
            report.AppendLine(
                "本次观测中，两条通道对每一次按键报告的虚拟键码都相同。");
            report.AppendLine();
            report.AppendLine(
                "⚠️ 但**不要**据此就用虚拟键码去做跨通道关联。这只说明本次按到的键"
                + "恰好都不属于会分歧的那一类；只要按一次左 Shift 就可能出现分歧。"
                + "扫描码在两条通道上始终一致，用它没有这个风险。");
            report.AppendLine();
            report.AppendLine(
                "No disagreement was observed. Do not conclude that virtual keys are safe "
                + "for cross-channel correlation: it only means no key of the disagreeing "
                + "class happened to be pressed. Scan codes agree unconditionally.");
            report.AppendLine();
            return;
        }

        report.AppendLine(
            "**观测到分歧。同一次物理按键，两条通道报出的虚拟键码不同。**");
        report.AppendLine();
        report.AppendLine("| 钩子报告 / Hook | Raw Input 报告 / Raw Input |");
        report.AppendLine("|---|---|");

        foreach (var (hookKey, rawKey) in disagreements.OrderBy(entry => entry.Key))
        {
            report.AppendLine(
                $"| `0x{hookKey:X2}` {DescribeVirtualKey(hookKey)} "
                + $"| `0x{rawKey:X2}` {DescribeVirtualKey(rawKey)} |");
        }

        report.AppendLine();
        report.AppendLine("**这意味着什么 / What this means**");
        report.AppendLine();
        report.AppendLine(
            "钩子会区分左右修饰键（`VK_LSHIFT` / `VK_RSHIFT`），Raw Input 只给通用码"
            + "（`VK_SHIFT`）。两者的**扫描码始终相同**。");
        report.AppendLine();
        report.AppendLine(
            "因此 `InputEventCorrelator` 必须按 **(扫描码, 扩展位, 按下/弹起)** 来把"
            + "两条通道的事件对应起来，**绝不能用虚拟键码**。扩展位不能省——扫描码"
            + "本身会重复，右 Ctrl 与左 Ctrl 的扫描码相同，只有它能区分左右。");
        report.AppendLine();
        report.AppendLine(
            "> 这条是踩出来的，不是想出来的。本工具第一版按虚拟键码配对，"
            + "结果所有修饰键在两条队列里各自堆积、永远配不上，报出 400 多个"
            + "「未配对」事件。若这个坑留到 Task 4b 才踩，现场表现会是"
            + "「扫码枪扫含大写字母的条码时，识别不出来源」——而那时要同时"
            + "面对未验证的拦截逻辑和这个隐藏的配对错误，正是规格 §4.3 拆分"
            + "4a 与 4b 所要避免的局面。");
        report.AppendLine();
        report.AppendLine(
            "The hook distinguishes left and right modifiers while Raw Input reports the "
            + "generic code; scan codes always match. InputEventCorrelator must therefore "
            + "key on (scan code, extended flag, direction) and never on the virtual key. "
            + "This was discovered by hitting it: the tool's first version paired on "
            + "virtual keys and reported 400-odd unpaired modifier events.");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：把虚拟键码渲染成人能认的名字，只覆盖会出现分歧的那几个修饰键。
    /// English: Names the virtual keys involved in disagreements — the modifiers.
    /// </summary>
    private static string DescribeVirtualKey(ushort virtualKey) => virtualKey switch
    {
        0x10 => "VK_SHIFT",
        0x11 => "VK_CONTROL",
        0x12 => "VK_MENU",
        0xA0 => "VK_LSHIFT",
        0xA1 => "VK_RSHIFT",
        0xA2 => "VK_LCONTROL",
        0xA3 => "VK_RCONTROL",
        0xA4 => "VK_LMENU",
        0xA5 => "VK_RMENU",
        _ => string.Empty,
    };

    /// <summary>
    /// 中文：第 2 问 —— 时差分布。4b 的等待窗口要照着尾部选，不是照着中位数选。
    /// English: Question 2 — the delta distribution. 4b's wait window is chosen from the
    ///          tail, not from the median.
    /// </summary>
    private static void AppendDelta(StringBuilder report, IReadOnlyCollection<double> deltas)
    {
        report.AppendLine("## 3. 两条通道的时差分布 / Inter-channel delta");
        report.AppendLine();
        report.AppendLine(
            "时差 = Raw Input 时间戳 − 钩子时间戳。**正数表示钩子先到。**"
            + " 两条通道读的是同一个 `Stopwatch` 高分辨率时钟。");
        report.AppendLine();

        var absolute = deltas.Select(Math.Abs).ToArray();

        report.AppendLine("| 统计量 / Statistic | 有符号 / Signed (µs) | 绝对值 / Absolute (µs) |");
        report.AppendLine("|---|---|---|");
        report.AppendLine(
            $"| 最小 / Min | {Format(Percentiles.Minimum(deltas))} | {Format(Percentiles.Minimum(absolute))} |");
        report.AppendLine(
            $"| 中位 / Median | {Format(Percentiles.Median(deltas))} | {Format(Percentiles.Median(absolute))} |");
        report.AppendLine(
            $"| p99 | {Format(Percentiles.Percentile(deltas, 0.99))} | {Format(Percentiles.Percentile(absolute, 0.99))} |");
        report.AppendLine(
            $"| 最大 / Max | {Format(Percentiles.Maximum(deltas))} | {Format(Percentiles.Maximum(absolute))} |");
        report.AppendLine();
        report.AppendLine(
            "**看尾部，不要看中位数。** 假设一千次按键里九百九十九次时差是 50 微秒、"
            + "一次是 200 毫秒，平均值大约 250 微秒，看上去毫无问题——而那唯一的一次"
            + "恰恰是架构会出事的地方。4b 的等待窗口必须覆盖 p99 乃至最大值，"
            + "否则每一百次按键就有一次判断错源。");
        report.AppendLine();
        report.AppendLine(
            "Read the tail, not the median. 4b's wait window must cover p99 and beyond, or "
            + "one keystroke in a hundred is attributed to the wrong device.");
        report.AppendLine();
        report.AppendLine(
            "> 一处需要如实说明的口径：Raw Input 的时间戳是「消息循环处理到这条 "
            + "`WM_INPUT` 的时刻」，而不是「Windows 产生这条原始输入的时刻」，"
            + "两者之间隔着消息队列的排队时间。这不是误差——回调做决定时身份"
            + "**可不可用**，取决于消息何时被取到，而不是它何时被产生。但读者"
            + "不能把这个数字当成硬件时延。");
        report.AppendLine();
        report.AppendLine(
            "> **测量环境**：钩子与 `WM_INPUT` 都跑在一条专用线程上，那条线程只有"
            + "一个仅消息窗口，只泵消息、不碰任何界面。这一点是必须的：第一轮把"
            + "捕获放在界面线程上，界面每 100 毫秒重建一次列表控件，消息就排在"
            + "那些工作后面，量到的时差于是变成了「界面有多卡」而不是通道本身的"
            + "性质。");
        report.AppendLine();
        report.AppendLine(
            "> 这同时是一条**产品设计要求**：产品也必须在一条专用的、什么别的事"
            + "都不干的线程上泵 `WM_INPUT`。若那条线程兼做界面，身份到手的延迟"
            + "会被自己的界面拖大——而 4b 的扣留窗口必须覆盖那个延迟，界面越卡，"
            + "普通打字被扣留得越久。");
        report.AppendLine();
        report.AppendLine(
            "> Measurement environment: both channels run on a dedicated thread owning a "
            + "message-only window that pumps messages and touches no UI. This is required "
            + "— with capture on the UI thread the first run measured how sluggish the UI "
            + "was rather than a property of the channels. It is also a product design "
            + "requirement: a thread that also drives a UI inflates how long identity takes "
            + "to arrive, and 4b's withhold window must cover that.");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：第 3、5 问 —— 每台设备的字符间隔与连发特征。
    /// English: Questions 3 and 5 — per-device inter-character intervals and burst shape.
    /// </summary>
    private static void AppendDevices(StringBuilder report, ObservationSession session)
    {
        report.AppendLine("## 4. 各设备的输入特征 / Per-device input characteristics");
        report.AppendLine();

        if (session.Devices.Count == 0)
        {
            report.AppendLine("本次没有观测到任何设备的按键。 No device produced a keystroke.");
            report.AppendLine();
            return;
        }

        report.AppendLine(
            "| 设备 / Device | VID/PID | 按下 / Downs | 连发段 / Bursts | 最大段 / Largest "
            + "| 段内间隔最小 / Min (ms) | 中位 / Median | p99 | 最大 / Max | 乱序 / OoO |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        var anyOutOfOrder = false;

        foreach (var device in session.Devices.OrderByDescending(item => item.KeyDownCount))
        {
            var statistics = device.BuildIntervalStatistics();
            anyOutOfOrder |= device.OutOfOrderCount > 0;

            report.AppendLine(
                $"| {device.DisplayName} | {device.VendorProduct} | {device.KeyDownCount} "
                + $"| {statistics.BurstCount} | {statistics.LargestBurstSize} "
                + $"| {Format(statistics.MinimumMilliseconds)} "
                + $"| {Format(statistics.MedianMilliseconds)} "
                + $"| {Format(statistics.NinetyNinthMilliseconds)} "
                + $"| {Format(statistics.MaximumMilliseconds)} "
                + $"| {device.OutOfOrderCount} |");
        }

        report.AppendLine();

        if (anyOutOfOrder)
        {
            report.AppendLine(
                "⚠️ **「乱序」不为零，说明存在配错对的事件**——时间戳出现了倒退。"
                + "本表的间隔统计已经把负间隔排除在外，但既然配对本身出过错，"
                + "整表的可信度都要打折扣。先弄清未配对是怎么来的，再采信这些数字。");
            report.AppendLine();
        }

        report.AppendLine(
            "「按下」含修饰键，因此与还原出的字符数对不上是正常的：一个大写字母是"
            + "两次按下（Shift 加字母）换一个字符。真正的扫描内容见第 5 节。");
        report.AppendLine();
        report.AppendLine(
            $"「连发段」按大于 {DeviceActivity.BurstGap.TotalMilliseconds:0} 毫秒的空隙切分，"
            + "只统计**段内**间隔。不切分的话，两枪扫描之间几秒钟的空闲会被算成一个"
            + "字符间隔，中位数随即失去意义，而结果看上去仍然像个正常数字。");
        report.AppendLine();
        report.AppendLine(
            "**这组数字直接决定 Task 6 的扫描无活动超时。** 取小了会把一枪正常的扫描"
            + "从中间截断，取大了则每次扫描后都要多等那么久才认定结束。选值应当明显"
            + "大于扫码枪段内间隔的最大值，同时明显小于人两次有意识按键的间隔。");
        report.AppendLine();
        report.AppendLine(
            "> 该切分阈值仅用于**本报告的分析**，与 Task 6 里那个待定的扫描超时"
            + "是两回事，不要互相推导。规格也提醒过：约 300 毫秒的扫描超时与"
            + "`LowLevelHooksTimeout` 的 300 毫秒数字相同但毫无关系。");
        report.AppendLine();
        report.AppendLine(
            "> **本表的时刻取自钩子那一侧，不是 Raw Input。** 设备身份只有 Raw Input "
            + "知道，「这次按键什么时候发生」却只有钩子问得准——钩子回调是在输入"
            + "派发路径上被同步调用的，而 `WM_INPUT` 要先排队再被消息循环取出。"
            + "配对正好把两半凑齐：设备取自 Raw Input，时刻取自钩子。"
            + "第一轮直接用 Raw Input 的时间戳，量到扫码枪段内间隔 p99 48 毫秒——"
            + "扫码枪不可能有这种停顿，那是消息排队的时间。");
        report.AppendLine();
        report.AppendLine(
            "> Times here come from the hook side, not Raw Input. Only Raw Input knows the "
            + "device and only the hook answers \"when did this happen\" accurately, so "
            + "pairing supplies both halves. The first run used Raw Input timestamps and "
            + "measured a 48 ms within-burst p99 for the scanner — in fact queue latency.");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：
    ///   从钩子的事件流里还原出来的扫描内容。
    ///
    ///   ★ 这是当下最重要的一节。它回答的是：**字符究竟丢在钩子之前还是之后？**
    ///
    ///     用法：把这里列出的内容，与应用（记事本、业务网页）里实际收到的内容
    ///     **逐条比对**。
    ///
    ///       两边都完整       这次扫描没问题
    ///       这边完整、应用残缺
    ///                        丢在**钩子之后**。我们的架构恰好能修好它：原始按键
    ///                        全部吞掉，自己用 SendInput 按可控节奏重发。
    ///       这边也残缺       丢在**钩子之前**。我们会捕获到一个残缺的码再原样
    ///                        发出去，而界面显示一切正常——规格 §19.1 的"静默的
    ///                        错误数据"，4b 的硬性关卡必须拦下。
    ///
    ///     这个问题没法靠推理，只能靠对照。
    /// English:
    ///   The scans reconstructed from the hook's event stream — the most important
    ///   section right now, because it answers whether characters are lost before the
    ///   hook or after it.
    ///
    ///   Compare each entry against what the application actually received. Complete in
    ///   both places means the scan was fine. Complete here but damaged in the
    ///   application means loss *after* the hook, which this architecture repairs by
    ///   swallowing the raw keystrokes and re-emitting at a controlled pace. Damaged here
    ///   too means loss *before* the hook: we would capture a wrong code and emit it while
    ///   reporting success — spec §19.1's silently wrong data, which 4b's hard gate must
    ///   catch. Reasoning cannot settle this; only the comparison can.
    /// </summary>
    private static void AppendReconstructedScans(StringBuilder report, ObservationSession session)
    {
        report.AppendLine("## 5. 钩子看到的扫描内容 / Scans as the hook saw them");
        report.AppendLine();
        report.AppendLine(
            "**用法**：把下面的内容与应用里实际收到的内容逐条比对。");
        report.AppendLine();
        report.AppendLine("| 对照结果 / Comparison | 含义 / Meaning |");
        report.AppendLine("|---|---|");
        report.AppendLine("| 两边都完整 | 那次扫描没问题 |");
        report.AppendLine(
            "| 这边完整、应用残缺 | 丢在**钩子之后**。本产品的架构恰好能修好它——"
            + "原始按键全部吞掉，自己按可控节奏重发 |");
        report.AppendLine(
            "| 这边也残缺 | 丢在**钩子之前**。我们会捕获残缺的码再原样发出，"
            + "而界面显示正常。规格 §19.1 的静默错误数据，4b 硬性关卡必须拦下 |");
        report.AppendLine();

        if (session.Devices.Count == 0)
        {
            report.AppendLine("本次没有可还原的扫描。 Nothing to reconstruct.");
            report.AppendLine();
            return;
        }

        foreach (var device in session.Devices.OrderByDescending(item => item.KeyDownCount))
        {
            var scans = device.BuildReconstructedScans();
            if (scans.Count == 0)
            {
                continue;
            }

            report.AppendLine($"### {device.DisplayName}");
            report.AppendLine();

            // 相同的内容合并计数：扫同一个条码五十次，逐条列出五十行没有意义，
            // 而"这个内容出现了 47 次、那三个各出现一次"一眼就能看出哪些是异常。
            // Identical texts are grouped: fifty rows for fifty scans of one barcode say
            // nothing, whereas "this one 47 times, these three once each" shows at a
            // glance which are the anomalies.
            var grouped = scans
                .GroupBy(scan => (scan.Text, scan.EndedWithEnter))
                .Select(group => (group.Key.Text, group.Key.EndedWithEnter, Count: group.Count()))
                .OrderByDescending(entry => entry.Count)
                .ToArray();

            report.AppendLine("| 次数 / Count | 长度 / Len | 以回车结束 / Enter | 内容 / Text |");
            report.AppendLine("|---|---|---|---|");

            foreach (var (text, endedWithEnter, count) in grouped)
            {
                report.AppendLine(
                    $"| {count} | {text.Length} | {(endedWithEnter ? "是 / yes" : "**否 / no**")} "
                    + $"| `{text}` |");
            }

            report.AppendLine();

            var distinctCount = grouped.Length;
            if (distinctCount > 1)
            {
                report.AppendLine(
                    $"⚠️ 本设备出现了 **{distinctCount} 种**不同的内容。若实际只扫了同一个"
                    + "条码，那么**钩子这一侧本身就已经收到了残缺的数据**——问题出在"
                    + "扫码枪到 Windows 这一段，不是出在下游。");
                report.AppendLine();
                report.AppendLine(
                    "这种情况下，本产品的吞掉-重发架构**修不好它**，反而会把错码"
                    + "原样发进业务系统，界面还显示成功。可行的方向有两个：");
                report.AppendLine();
                report.AppendLine(
                    "1. **调扫码枪的字符间隔（inter-character delay）。** 多数 HID 扫码枪"
                    + "都能用配置条码把这个值调大（例如从 0 调到 5~10 毫秒）。"
                    + "本报告第 4 节里的段内间隔中位数就是当前值的实测结果——"
                    + "若只有 1 毫秒出头，那基本可以确定是发得太快。");
                report.AppendLine(
                    "2. **在 SKU 模式下用校验规则兜底。** 长度与字符集校验能拦下"
                    + "大部分截断（规格 §9），但**拦不住 SN 模式**——SN 原样输出，"
                    + "没有任何校验层。这一点必须让仓库知道。");
                report.AppendLine();
            }
        }

        report.AppendLine(
            "> 还原用的是**美式键盘布局的固定映射**，且假定 CapsLock 处于关闭状态。"
            + "仓库条码是 ASCII 字母数字，这个映射够用；若还原结果整体大小写颠倒，"
            + "那说明 CapsLock 开着，本身也是一条值得记下的观察。无法映射的键会显示"
            + "成 `<0x..>` 占位而不是被跳过——跳过会让残缺的还原结果看起来像一个"
            + "干净的短字符串，与真的少了字符无法区分。");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：第 4、6 问 —— 当前连接的键盘设备清单。
    ///
    ///   重新插拔或重启之后再导出一份，对比设备路径是否一致，就回答了第 4 问；
    ///   清单里内置键盘那一行的形状，回答第 6 问。
    /// English:
    ///   Questions 4 and 6 — the currently attached keyboard devices. Export again after
    ///   a replug or reboot and compare paths for question 4; the built-in keyboard's row
    ///   answers question 6.
    /// </summary>
    private static void AppendAttachedKeyboards(StringBuilder report, ObservationSession session)
    {
        report.AppendLine("## 6. 当前连接的键盘类设备 / Attached keyboard-class devices");
        report.AppendLine();
        report.AppendLine(
            "**用法**：重新插拔扫码枪、以及重启机器之后，各导出一份报告，"
            + "对比本表中的「设备路径」是否逐字相同。相同则说明设备路径可以作为"
            + "跨会话的绑定依据（规格 §6）；不同则绑定必须换一种身份。");
        report.AppendLine();

        var keyboards = session.EnumerateAttachedKeyboards();
        if (keyboards.Count == 0)
        {
            report.AppendLine("枚举不到任何键盘类设备。 No keyboard-class device enumerated.");
            report.AppendLine();
            return;
        }

        report.AppendLine("| 友好名 / Friendly name | VID/PID | 序列号 / Serial | 设备路径 / Device path |");
        report.AppendLine("|---|---|---|---|");

        foreach (var (_, identity) in keyboards)
        {
            var vendorProduct = identity.VendorId is null && identity.ProductId is null
                ? "—"
                : $"VID_{identity.VendorId ?? "?"} PID_{identity.ProductId ?? "?"}";

            report.AppendLine(
                $"| {identity.FriendlyName ?? "—"} | {vendorProduct} "
                + $"| {identity.SerialNumber ?? "—"} | `{identity.DevicePath ?? "—"}` |");
        }

        report.AppendLine();
        report.AppendLine(
            "笔记本内置键盘通常走 ACPI 或 PS/2 而不是 USB HID，因此设备路径形如 "
            + "`\\\\?\\ACPI#PNP0303#...`，且**没有 VID/PID**（规格假设 A4）。"
            + "若本表中内置键盘那一行的 VID/PID 是「—」，那不是缺陷，正是预期。"
            + "反过来，这也说明「靠 VID/PID 区分设备」的做法在笔记本上对内置键盘"
            + "直接失效，设备路径才是唯一对两类设备都成立的身份。");
        report.AppendLine();
    }

    /// <summary>
    /// 中文：第 5 问需要人来看 —— 修饰键、CapsLock、终止回车的实际表现，
    ///       这些只有观察者能判断，工具无法自动得出结论。
    /// English: Question 5 needs a human — how modifiers, CapsLock and the terminating
    ///          Enter actually behave is something only an observer can judge.
    /// </summary>
    private static void AppendOperatorNotes(StringBuilder report, string? operatorNotes)
    {
        report.AppendLine("## 7. 人工观察记录 / Operator notes");
        report.AppendLine();

        if (string.IsNullOrWhiteSpace(operatorNotes))
        {
            report.AppendLine(
                "_（空）本节需要人工填写：一次完整扫描里出现了哪些修饰键、"
                + "CapsLock 状态如何变化、终止用的回车是什么形式。这些只有观察者"
                + "能判断，工具不会替你下结论。_");
        }
        else
        {
            report.AppendLine(operatorNotes);
        }

        report.AppendLine();
    }

    /// <summary>
    /// 中文：把仍未回答的问题显式列出来。
    ///
    ///   一份报告最危险的地方不是写错，而是让读者**以为**某个问题已经回答过了。
    ///   把没答的问题写在纸面上，比默认读者会自己想起来可靠得多。
    /// English:
    ///   Lists what remains unanswered. A report's worst failure is not being wrong but
    ///   leaving a reader believing a question was settled. Writing the open ones down
    ///   is more reliable than assuming the reader will remember them.
    /// </summary>
    private static void AppendOpenQuestions(StringBuilder report)
    {
        report.AppendLine("## 8. 仍未回答 / Still open");
        report.AppendLine();
        report.AppendLine(
            "- **这些数字尚未在试点机器上复现。** 事件时序是机器的性质，不是代码的"
            + "性质；开发机与现场机不是同一台硬件。在现场机器上重跑一轮之前，"
            + "本报告的每一个数字都只是暂定值。");
        report.AppendLine(
            "- **原始扫描本身有多可靠。** 若第 5 节显示同一个条码还原出多种内容，"
            + "说明扫码枪到 Windows 这一段就已经在丢字符。必须先用「**完全关掉本工具**"
            + "再扫五十次」确认这个现象与本工具无关，然后才谈得上继续。"
            + "本工具装的低层钩子确实坐在输入路径上。");
        report.AppendLine(
            "- **设备身份跨重启是否稳定**，需要重启后再导出一份报告对比第 6 节。");
        report.AppendLine(
            "- **同一台物理设备是否会被枚举成多个 Raw Input 设备。** 第 5 节里出现"
            + "`HID#ConvertedDevice` 这类条目时尤其要注意——那是 Windows 为 PS/2 设备"
            + "生成的 HID 映射。若扫码枪也被枚举成不止一项（例如带 `&MI_01`、`&Col02` "
            + "后缀的兄弟条目），只绑定其中一个句柄就可能漏掉另一部分事件，"
            + "规格 §6 的绑定模型需要相应调整。判断方法：只用扫码枪扫码，看第 4 节"
            + "里出现几台设备。");
        report.AppendLine(
            "- **拦截与重放是否可行**，属于 Task 4b。本次观测**没有**拦截任何事件，"
            + "因此对此不提供任何证据。规格 §4.3 把两段分开，正是为了避免在"
            + "未验证的逻辑与未知的硬件行为之间同时排查——那是时序启发式被悄悄"
            + "引入的典型路径。");
        report.AppendLine();
        report.AppendLine(
            "- These figures have not been reproduced on pilot hardware; every one is "
            + "provisional until they are.");
        report.AppendLine(
            "- Whether device identity survives a reboot needs a second report to compare "
            + "section 4 against.");
        report.AppendLine(
            "- Whether interception and replay work is Task 4b. This run intercepted "
            + "nothing and offers no evidence either way.");
        report.AppendLine();
    }

    private static string Describe(string machineDescription)
        => string.IsNullOrWhiteSpace(machineDescription)
            ? "⚠️ **未填写——报告无法判断这批数据代表哪台机器** / not stated"
            : machineDescription;

    private static string Format(double? value)
        => value is null ? "—" : value.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatShare(int count, int total)
        => total == 0 ? "—" : (count * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
