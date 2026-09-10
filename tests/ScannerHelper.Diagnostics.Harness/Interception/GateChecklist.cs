// =============================================================================
// GateChecklist.cs
//
// 中文：
//   实施计划里 Task 4b 的九条验收项，以及其中哪几条属于**硬性关卡**。
//
//   ★ 为什么把它做成程序里的一张表，而不是让人对着文档打勾。
//
//     计划原文写着：第 2 到第 6 条若不能可靠达成，就**不要**继续，更不要
//     用时序上的经验值把症状掩盖过去。这句话的分量在于它是一条会否决整个
//     架构的判断——而这种判断最容易在"差不多能用了"的兴奋里被悄悄跳过。
//
//     做成表就要求每一条都被明确地标成通过或失败，导出的报告里也就留下了
//     记录：是谁、在哪台机器上、看到了什么。第 2 到第 6 条只要有一条是失败，
//     报告顶部就会写明这道关卡没过——而不是让人自己去数。
//
//   ★ 「未测」不等于「通过」。
//
//     默认状态是未测，且导出时未测与失败一样会挡住关卡。这是有意的：一条
//     没人去验的规则和一条验失败的规则，对现场工人的风险是一样的。
//
// English:
//   Task 4b's nine acceptance items from the implementation plan, and which of them form the
//   hard gate.
//
//   Why a table in the program rather than ticking boxes against a document: the plan says that
//   if items 2 through 6 are not reliably achievable, do not continue and do not paper over the
//   symptoms with timing heuristics. That sentence carries weight because it is a judgment that
//   can veto the whole architecture — and such judgments are the easiest to skip quietly in the
//   excitement of "it mostly works now".
//
//   A table forces every item to be marked pass or fail, and the exported report then records
//   who saw what, on which machine. If any of items 2–6 fails, the report says at the top that
//   the gate did not pass, rather than leaving someone to count.
//
//   "Not tested" is not "passed": it is the default, and on export it blocks the gate exactly as
//   a failure does. Deliberately so — to the operator on the floor, a rule nobody verified
//   carries the same risk as a rule that failed.
//
// 包含的类型 / Types in this file:
//   GateResult     一条验收项的结论
//   GateItem       一条验收项
//   GateChecklist  九条验收项与关卡判定
// =============================================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ScannerHelper.Diagnostics.Harness.Interception;

/// <summary>
/// 中文：一条验收项的结论。
/// English: One acceptance item's verdict.
/// </summary>
public enum GateResult
{
    /// <summary>中文：未测。 English: Not tested.</summary>
    NotTested,

    /// <summary>中文：通过。 English: Passed.</summary>
    Passed,

    /// <summary>中文：失败。 English: Failed.</summary>
    Failed,
}

/// <summary>
/// 中文：一条验收项。
/// English: One acceptance item.
/// </summary>
public sealed class GateItem : INotifyPropertyChanged
{
    private GateResult _result = GateResult.NotTested;
    private string _note = string.Empty;

    /// <summary>
    /// 中文：构造一条验收项。
    /// English: Creates one acceptance item.
    /// </summary>
    public GateItem(int number, string title, string howTo, bool isBlocking)
    {
        Number = number;
        Title = title;
        HowTo = howTo;
        IsBlocking = isBlocking;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>中文：编号，与实施计划一致。 English: The number, matching the plan.</summary>
    public int Number { get; }

    /// <summary>中文：这一条要验什么。 English: What this item verifies.</summary>
    public string Title { get; }

    /// <summary>中文：怎么验。 English: How to verify it.</summary>
    public string HowTo { get; }

    /// <summary>
    /// 中文：是否属于硬性关卡（第 2~6 条）。失败就不得继续。
    /// English: Whether it belongs to the hard gate (items 2–6). A failure forbids continuing.
    /// </summary>
    public bool IsBlocking { get; }

    /// <summary>中文：结论。 English: The verdict.</summary>
    public GateResult Result
    {
        get => _result;
        set
        {
            if (_result == value)
            {
                return;
            }

            _result = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 中文：备注。失败时尤其要写——计划要求把观察到的事件顺序与失败方式记下来。
    /// English: A note, especially when failing: the plan requires recording the observed event
    ///          ordering and failure modes.
    /// </summary>
    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>中文：显示用的标题。 English: The title as displayed.</summary>
    public string DisplayTitle
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}. {1}{2}",
            Number,
            Title,
            IsBlocking ? "  ★硬性关卡 / hard gate" : string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 中文：Task 4b 的九条验收项。
/// English: Task 4b's nine acceptance items.
/// </summary>
public sealed class GateChecklist
{
    /// <summary>
    /// 中文：构造清单。条目文字直接取自实施计划的 Task 4b，编号一一对应，
    ///       这样导出的报告能和计划逐条对上。
    /// English: Creates the checklist. The wording comes from the plan's Task 4b with matching
    ///          numbers, so an exported report lines up with the plan item by item.
    /// </summary>
    public GateChecklist()
    {
        Items =
        [
            new GateItem(
                1,
                "Raw Input 能把普通键盘和扫码枪认成两个不同的设备",
                "在设备下拉里应当同时看到笔记本内置键盘和扫码枪，两者的设备路径不同。"
                + "扫一枪，看「绑定设备」这一行是不是扫码枪那一个。",
                isBlocking: false),

            new GateItem(
                2,
                "钩子事件能和它的 Raw Input 来源关联上（前提：扫描进行中不打字）",
                "扫 10 枪，看统计里「Raw Input 事件」与「吞掉+放行」的数量是否同量级；"
                + "「异常次数」应当为 0。数量差得远说明两条通道不同步，关联无从谈起。",
                isBlocking: true),

            new GateItem(
                3,
                "普通键盘输入能被暂时扣留并原样补发——包括中文输入法组字",
                "切到中文输入法，在测试输入框里打一段话（例如「今天天气不错」）。"
                + "字要全部出现、顺序正确、组字过程不被打断。这一条最能暴露扣留-重放的问题。",
                isBlocking: true),

            new GateItem(
                4,
                "修饰键、按住重复、CapsLock 经过补发之后仍然正确",
                "在测试输入框里：按住 Shift 打几个大写字母；按住一个键不放看是否连续重复；"
                + "开 CapsLock 打几个字母再关掉。三样都要和平时一样。",
                isBlocking: true),

            new GateItem(
                5,
                "扫码枪的原始内容在到达焦点输入框之前就被拦下",
                "把光标放在测试输入框里扫一枪。框里应当只出现处理后的结果，"
                + "不能出现原始条码本身，也不能出现「原始码 + 结果」两份。",
                isBlocking: true),

            new GateItem(
                6,
                "扫码枪的第一个字符不泄漏",
                "反复扫同一个条码 20 次，逐次看测试输入框。"
                + "任何一次多出一个开头字符（例如结果前面多了个字母）就算失败。"
                + "这一条单列，是因为第一个字符是最可能漏掉的——钩子恒先于 Raw Input 到达，"
                + "开头那一下的来源最晚才确定。",
                isBlocking: true),

            new GateItem(
                7,
                "扫码枪自带的终止回车被吞掉，不转发（规格 §10）",
                "关掉「扫描后补回车」，在测试输入框里扫一枪：光标应当**留在原处**，"
                + "不换行、不跳到下一个控件。再打开该选项扫一枪，这时才应当有一个回车。",
                isBlocking: false),

            new GateItem(
                8,
                "本程序自己用 SendInput 发出的内容被认出并忽略，不会递归",
                "扫一枪之后，「吞掉」的计数不应当因为我们自己的输出而继续上涨；"
                + "「异常次数」保持 0，程序不卡死。递归一旦发生，表现是一枪之后计数疯涨。",
                isBlocking: false),

            new GateItem(
                9,
                "连续 500 枪不出现可见的损坏或泄漏",
                "连续扫 500 次，每 50 次看一眼测试输入框和「最长回调」。"
                + "内容损坏、漏出原始码、或「超预算次数」不为 0，都要记下来。",
                isBlocking: false),
        ];
    }

    /// <summary>
    /// 中文：九条验收项。
    /// English: The nine acceptance items.
    /// </summary>
    public ObservableCollection<GateItem> Items { get; }

    /// <summary>
    /// 中文：
    ///   硬性关卡是否通过：第 2~6 条**全部**明确通过才算过。
    ///   未测与失败同等对待——没人验过的规则和验失败的规则，对工人的风险一样。
    /// English:
    ///   Whether the hard gate passed: every one of items 2–6 must be explicitly marked passed.
    ///   Not-tested counts the same as failed — to the operator, an unverified rule carries the
    ///   same risk as one that failed.
    /// </summary>
    public bool HasPassedHardGate
        => Items.Where(item => item.IsBlocking).All(item => item.Result == GateResult.Passed);

    /// <summary>
    /// 中文：
    ///   找出「人打的钩」与「运行时计数」互相矛盾的地方。
    ///
    ///   ★ 这个方法是一次真实事故的产物。
    ///
    ///     2026-09-10 的第一次实测导出了一份写着「硬性关卡通过」的报告，而同一份
    ///     报告里的计数是：处理完成的扫描 0 枪、钩子回调最长 3379 毫秒、381 次
    ///     超预算。一枪都没成功处理过，第 5、6、7 条（验的全是扫码枪内容怎么被
    ///     处理）就不可能被验证过——但工具照单全收，把这个结论签发了出去。
    ///
    ///     一个会在自己的数据面前签发相反结论的关卡，等于没有关卡。所以判定不能
    ///     只看人打的钩：人看到的是屏幕上的字符，而「吞掉到底生没生效」这种事，
    ///     人在屏幕上根本看不出来——回调超时之后 Windows 会无视我们的返回值把
    ///     按键照常投递，看起来和「我们主动放行」一模一样。
    ///
    ///   返回空列表表示没有矛盾。
    /// English:
    ///   Finds contradictions between the ticked boxes and the runtime counters.
    ///
    ///   This method comes from a real failure. The first run, on 2026-09-10, exported a report
    ///   saying the hard gate had passed while the counters in that same report read: zero scans
    ///   processed, a longest hook callback of 3379 ms, 381 callbacks over budget. With not one
    ///   scan processed, items 5, 6 and 7 — all of which concern how scanner content is handled —
    ///   cannot have been verified. The tool accepted it and issued the verdict anyway.
    ///
    ///   A gate that certifies a conclusion its own data contradicts is not a gate. So the verdict
    ///   cannot rest on ticked boxes alone: a person sees characters on a screen, and whether the
    ///   swallow actually took effect is not something a screen shows — after a timeout Windows
    ///   disregards our return value and delivers the keystroke anyway, which looks exactly like
    ///   passing it through on purpose.
    ///
    ///   An empty list means no contradiction.
    /// </summary>
    public IReadOnlyList<string> FindContradictions(PipelineSnapshot snapshot)
    {
        var contradictions = new List<string>();

        // 1 —— 一枪都没处理完，却把「扫码枪内容怎么处理」那几条标成了通过。
        // 1 — no scan was processed, yet items about handling scanner content are marked passed.
        var scannerItems = Items
            .Where(item => item.Number is 5 or 6 or 7 or 9 && item.Result == GateResult.Passed)
            .Select(item => item.Number)
            .ToArray();

        if (snapshot.ScanCount == 0 && scannerItems.Length > 0)
        {
            contradictions.Add(string.Format(
                CultureInfo.InvariantCulture,
                "处理完成的扫描为 0，但第 {0} 条被标成通过。这几条验的都是扫码枪的内容如何被处理，"
                + "一枪都没成功处理过就不可能验证它们。"
                + " / Zero scans were processed, yet items {0} are marked passed. Those items all"
                + " concern how scanner content is handled and cannot be verified without a single"
                + " processed scan.",
                string.Join("、", scannerItems)));
        }

        // 2 —— 回调超时意味着「吞掉」可能根本没生效，而这在屏幕上看不出来。
        // 2 — a timed-out callback means the swallow may never have taken effect, and a screen
        //     cannot show that.
        if (snapshot.HookCallbackBudgetExceededCount > 0)
        {
            contradictions.Add(string.Format(
                CultureInfo.InvariantCulture,
                "有 {0} 次钩子回调超过 {1:0} 毫秒预算（最长 {2:0.000} 毫秒）。回调超时之后 Windows"
                + " 会无视我们返回的「吞掉」、把按键照常投递出去，并可能把钩子摘掉（规格 §19.1）"
                + "——此时第 5、6 条在屏幕上看到的一切都不能作数。"
                + " / {0} hook callbacks exceeded the {1:0} ms budget (longest {2:0.000} ms). After"
                + " a timeout Windows disregards our swallow and delivers the keystroke anyway,"
                + " possibly removing the hook (spec §19.1) — nothing observed on screen for items"
                + " 5 and 6 can be trusted in that state.",
                snapshot.HookCallbackBudgetExceededCount,
                Win32.Win32ScannerInputSource.HookCallbackBudget.TotalMilliseconds,
                snapshot.MaximumHookCallbackDuration.TotalMilliseconds));
        }

        // 3 —— 什么都没被拦下来过。
        // 3 — nothing was ever intercepted.
        if (snapshot.SwallowedCount == 0 && HasPassedHardGate)
        {
            contradictions.Add(
                "吞掉的按键数为 0：整个过程里没有任何一次拦截发生，第 3~6 条无从谈起。"
                + " / Zero keystrokes were swallowed: no interception happened at all, leaving"
                + " items 3-6 without a subject.");
        }

        // 4 —— 吞掉与补发完全相等，说明没有任何一个按键被判定为来自扫码枪。
        //      扫码枪的按键会被吞掉而**不**补发（内容进扫描会话），所以两个数
        //      相等就意味着扫码枪从头到尾没被认出来过。
        // 4 — swallowed exactly equals replayed, so not one keystroke was attributed to the
        //     scanner: a scanner's keystrokes are swallowed and not replayed, their content going
        //     into the scan session instead.
        if (snapshot.SwallowedCount > 0 && snapshot.SwallowedCount == snapshot.ReplayedCount)
        {
            contradictions.Add(string.Format(
                CultureInfo.InvariantCulture,
                "吞掉 {0} 等于补发 {0}：没有任何一个按键被判定为来自扫码枪（扫码枪的按键会被吞掉"
                + "而不补发）。要么绑定没生效，要么整个过程里扫码枪根本没被用到。"
                + " / Swallowed {0} equals replayed {0}: not one keystroke was attributed to the"
                + " scanner, whose keystrokes are swallowed without being replayed. Either the"
                + " binding did not take effect, or the scanner was never used.",
                snapshot.SwallowedCount));
        }

        // 4.5 —— 有按键始终没等到 Raw Input，只好按超时规则原样重放。
        //        扫码期间这等于条码的原始字符漏进了业务软件（规格禁止），
        //        现场表现就是「结果里多了一两个字母」「一枪被回车劈成两行」。
        // 4.5 — keystrokes never met their Raw Input counterpart and were replayed by the expiry
        //       rule. During a scan that is the barcode's raw characters leaking into the business
        //       application, which the spec forbids; on screen it is "an extra letter or two" and
        //       "one scan split across two lines".
        if (snapshot.UnresolvedEventCount > 0)
        {
            contradictions.Add(string.Format(
                CultureInfo.InvariantCulture,
                "有 {0} 个按键始终没等到 Raw Input，被按超时规则当成普通键盘原样重放（决策 D-13）。"
                + "如果其中有扫码枪的字符，那就是条码原文漏进了业务软件——第 5、6 条不成立。"
                + " / {0} keystrokes never met their Raw Input counterpart and were replayed as"
                + " ordinary typing by the expiry rule (decision D-13). Any scanner characters"
                + " among them are raw barcode content leaking into the business application,"
                + " which voids items 5 and 6.",
                snapshot.UnresolvedEventCount));
        }

        // 5 —— 流水线抛过异常。
        // 5 — the pipeline threw.
        if (snapshot.FaultCount > 0)
        {
            contradictions.Add(string.Format(
                CultureInfo.InvariantCulture,
                "流水线抛出过 {0} 次异常（最近：{1}）。"
                + " / The pipeline threw {0} times (most recent: {1}).",
                snapshot.FaultCount,
                snapshot.LastFault));
        }

        return contradictions;
    }

    /// <summary>
    /// 中文：关卡是否真的通过：人打的钩全过，**并且**运行时计数不与之矛盾。
    /// English: Whether the gate genuinely passed: every box ticked, and the runtime counters not
    ///          contradicting them.
    /// </summary>
    public bool HasPassedGate(PipelineSnapshot snapshot)
        => HasPassedHardGate && FindContradictions(snapshot).Count == 0;

    /// <summary>
    /// 中文：
    ///   导出一份 Markdown 报告。
    ///   输入：machineDescription 这次是在哪台机器、什么条件下测的。
    ///
    ///   ★ 报告顶部先写结论，再写明细。计划要求的是一个能否继续的判断，
    ///     把它埋在九行表格后面，等于让读的人自己去下那个判断。
    /// English:
    ///   Exports a Markdown report. The verdict comes first and the detail after: the plan asks
    ///   for a go/no-go decision, and burying it beneath a nine-row table leaves the reader to
    ///   make that call themselves.
    /// </summary>
    public string BuildReport(string machineDescription, PipelineSnapshot snapshot)
    {
        var report = new StringBuilder();

        report.AppendLine("# Task 4b 输入拦截关卡 / Input Interception Hard Gate");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"生成时间 / Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"机器与条件 / Machine and conditions: {machineDescription}");
        report.AppendLine();

        var contradictions = FindContradictions(snapshot);

        report.AppendLine("## 结论 / Verdict");
        report.AppendLine();
        report.AppendLine(HasPassedHardGate && contradictions.Count == 0
            ? "**硬性关卡通过。** 第 2~6 条全部明确通过，可以继续构建完整应用。"
              + "  \n**Hard gate passed.** Items 2–6 are all explicitly passed; building the full application may continue."
            : "**硬性关卡未通过。** 第 2~6 条中存在未测或失败的条目。"
              + "按实施计划：不得继续，也不得用时序上的经验值把症状掩盖过去；"
              + "应当记录观察到的事件顺序与失败方式，并重新审视架构。"
              + "  \n**Hard gate not passed.** Among items 2–6 something is untested or failed. Per the"
              + " implementation plan: do not continue and do not paper over the symptoms with timing"
              + " heuristics; document the observed event ordering and failure modes, and revisit the"
              + " architecture.");
        report.AppendLine();

        if (contradictions.Count > 0)
        {
            report.AppendLine("### 打钩与运行时计数矛盾 / Ticked boxes contradict the counters");
            report.AppendLine();
            report.AppendLine(
                "以下每一条都说明同一件事：这次测量看到的东西，不足以支撑上面那些「通过」。"
                + "  \n"
                + "Each of these says the same thing: what this run observed does not support the"
                + " passes ticked above.");
            report.AppendLine();

            foreach (var contradiction in contradictions)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"- {contradiction}");
            }

            report.AppendLine();
        }

        report.AppendLine("## 逐条结果 / Item by item");
        report.AppendLine();
        report.AppendLine("| # | 关卡 | 项目 | 结论 | 备注 |");
        report.AppendLine("|---|---|---|---|---|");

        foreach (var item in Items)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {item.Number} | {(item.IsBlocking ? "★" : "")} | {item.Title} |"
                + $" {DescribeResult(item.Result)} | {item.Note.Replace('|', '/')} |");
        }

        report.AppendLine();
        report.AppendLine("## 运行时计数 / Runtime counters");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"- 吞掉 / Swallowed: {snapshot.SwallowedCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- 放行 / Passed through: {snapshot.PassedThroughCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- 补发 / Replayed: {snapshot.ReplayedCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- Raw Input 事件 / events: {snapshot.RawInputCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- 处理完成的扫描 / Scans processed: {snapshot.ScanCount}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- **超时未关联 / Unresolved (expired, replayed raw): {snapshot.UnresolvedEventCount}**");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- 丢弃的 Raw Input / Discarded raw input: {snapshot.DiscardedRawInputCount}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- 钩子回调最长耗时 / Longest hook callback: {snapshot.MaximumHookCallbackDuration.TotalMilliseconds:0.000} ms");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"- 超出回调预算次数 / Callbacks over budget: {snapshot.HookCallbackBudgetExceededCount}"
            + $"（预算 / budget {Win32.Win32ScannerInputSource.HookCallbackBudget.TotalMilliseconds:0} ms）");
        report.AppendLine(CultureInfo.InvariantCulture, $"- 异常次数 / Faults: {snapshot.FaultCount}");

        if (snapshot.LastFault is { } lastFault)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- 最近一次异常 / Last fault: {lastFault}");
        }

        report.AppendLine();
        report.AppendLine("## 说明 / Notes");
        report.AppendLine();
        report.AppendLine(
            "「超出回调预算次数」不为 0 是一个预警，不是失败：预算 50 毫秒远低于 "
            + "LowLevelHooksTimeout 的 300 毫秒。它在告诉你离「Windows 悄悄摘掉钩子」还有多远——"
            + "而真到那一刻不会有任何通知，进程照跑、界面照显示模式，原始条码却直接流进业务软件"
            + "（规格 §19.1）。");
        report.AppendLine();
        report.AppendLine(
            "A non-zero over-budget count is a warning rather than a failure: the 50 ms budget sits well"
            + " below LowLevelHooksTimeout's 300 ms. It measures how close this is to Windows silently"
            + " removing the hook — an event that arrives with no notification at all, the process still"
            + " running and the UI still showing a mode while raw barcodes flow into the business"
            + " application (spec §19.1).");

        return report.ToString();
    }

    private static string DescribeResult(GateResult result)
        => result switch
        {
            GateResult.Passed => "✅ 通过 / passed",
            GateResult.Failed => "❌ 失败 / failed",
            _ => "⬜ 未测 / not tested",
        };
}
