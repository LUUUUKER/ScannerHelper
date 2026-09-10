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
        AppendDelta(report, deltas);
        AppendDevices(report, session);
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
                + "**这本身可能是比时序更重要的发现**，不要当作噪声略过——"
                + "先查清楚是哪一类按键，再决定这批数据还能不能用。");
            report.AppendLine();
            report.AppendLine(
                "⚠️ **This run is incomplete and the statistics below are not complete evidence.**");
        }

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
    /// 中文：第 2 问 —— 时差分布。4b 的等待窗口要照着尾部选，不是照着中位数选。
    /// English: Question 2 — the delta distribution. 4b's wait window is chosen from the
    ///          tail, not from the median.
    /// </summary>
    private static void AppendDelta(StringBuilder report, IReadOnlyCollection<double> deltas)
    {
        report.AppendLine("## 2. 两条通道的时差分布 / Inter-channel delta");
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

        return;
    }

    /// <summary>
    /// 中文：第 3、5 问 —— 每台设备的字符间隔与连发特征。
    /// English: Questions 3 and 5 — per-device inter-character intervals and burst shape.
    /// </summary>
    private static void AppendDevices(StringBuilder report, ObservationSession session)
    {
        report.AppendLine("## 3. 各设备的输入特征 / Per-device input characteristics");
        report.AppendLine();

        if (session.Devices.Count == 0)
        {
            report.AppendLine("本次没有观测到任何设备的按键。 No device produced a keystroke.");
            report.AppendLine();
            return;
        }

        report.AppendLine(
            "| 设备 / Device | VID/PID | 字符数 / Chars | 连发段 / Bursts | 最大段 / Largest "
            + "| 段内间隔最小 / Min (ms) | 中位 / Median | p99 | 最大 / Max |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var device in session.Devices.OrderByDescending(item => item.KeyDownCount))
        {
            var statistics = device.BuildIntervalStatistics();
            report.AppendLine(
                $"| {device.DisplayName} | {device.VendorProduct} | {device.KeyDownCount} "
                + $"| {statistics.BurstCount} | {statistics.LargestBurstSize} "
                + $"| {Format(statistics.MinimumMilliseconds)} "
                + $"| {Format(statistics.MedianMilliseconds)} "
                + $"| {Format(statistics.NinetyNinthMilliseconds)} "
                + $"| {Format(statistics.MaximumMilliseconds)} |");
        }

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
        report.AppendLine("## 4. 当前连接的键盘类设备 / Attached keyboard-class devices");
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
        report.AppendLine("## 5. 人工观察记录 / Operator notes");
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
        report.AppendLine("## 6. 仍未回答 / Still open");
        report.AppendLine();
        report.AppendLine(
            "- **这些数字尚未在试点机器上复现。** 事件时序是机器的性质，不是代码的"
            + "性质；开发机与现场机不是同一台硬件。在现场机器上重跑一轮之前，"
            + "本报告的每一个数字都只是暂定值。");
        report.AppendLine(
            "- **设备身份跨重启是否稳定**，需要重启后再导出一份报告对比第 4 节。");
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
