// =============================================================================
// ForceSendWorkflowTests.cs
//
// 中文：
//   输出契约与强制发送流程（Task 7a，用例编号 FS1~FS15）。
//
//   本组测的是整条流水线的**唯一出口**。错误数据要写进仓库系统，只能从这里
//   出去——规格 §19.1 反复强调的"静默的错误数据"，具体形态就是：程序看起来
//   一切正常、界面显示成功，而这里发出去的内容是错的。
//
//   因此本组有一半用例断言的是"**什么都没发**"。规格 §10 规定失败绝不自动
//   发送，而"没有发生的事"恰恰是最容易在实现里被漏掉、也最难在现场被发现的
//   ——工人只会看到一个错误提示，不会知道后台其实已经悄悄发过一次了。
//
//   分量最重的三条：
//     FS3/FS4/FS5  AppendEnterAfterScan 的两种状态必须**精确**：关闭时回车数
//                  恰好为 0，开启时恰好为 1。规格 §10 特意说明默认关闭时网页
//                  收到的回车是 0 而不是「少了一个」。
//     FS9          F10 发的是**原始码**而不是解析出来的候选值（规格 §10）。
//     FS15         原样发送，不修剪不补齐不改大小写（规格 §10、决策 D-9）。
//
// English:
//   The output contract and the Force Send workflow (Task 7a, cases FS1–FS15).
//
//   This group tests the pipeline's only exit. Wrong data can reach the warehouse system only
//   through here, and spec §19.1's silently wrong data takes exactly this shape: the program
//   looking fine, the UI reporting success, and what left through this door being wrong.
//
//   Half the cases therefore assert that *nothing* was emitted. Spec §10 forbids failures from
//   auto-emitting, and a thing that did not happen is both the easiest to omit in an
//   implementation and the hardest to notice on site — the operator sees an error message and
//   has no way to know something was quietly sent anyway.
//
//   Three carry the most weight. FS3/FS4/FS5 pin AppendEnterAfterScan exactly: zero Enters when
//   off and exactly one when on, spec §10 having gone out of its way to say the page receives
//   zero by default rather than one fewer. FS9 pins that F10 emits the raw code rather than the
//   parsed candidate (spec §10). FS15 pins emission exactly as produced, with no trimming,
//   padding or case conversion (spec §10, decision D-9).
//
// 包含的测试 / Tests in this file:
//   Successful_sn_scan_emits_the_raw_string                 FS1
//   Successful_sku_scan_emits_the_parsed_sku                FS2
//   Append_enter_disabled_emits_no_enter                    FS3
//   Append_enter_enabled_emits_exactly_one_enter_in_sn      FS4
//   Append_enter_enabled_emits_exactly_one_enter_in_sku     FS5
//   Parse_failure_emits_nothing                             FS6
//   Validation_failure_emits_nothing                        FS7
//   Scan_timeout_emits_nothing                              FS8
//   Force_send_emits_the_raw_code_not_the_parsed_candidate  FS9
//   Force_send_honours_append_enter                         FS10
//   Cancel_emits_nothing                                    FS11
//   Force_send_and_cancel_preserve_the_mode                 FS12 FS13
//   Force_send_and_cancel_without_a_pending_error_do_nothing FS14
//   Emitted_content_is_never_trimmed_or_altered             FS15
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Tests.TestDoubles;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Input;

public class ForceSendWorkflowTests
{
    private const long ScannerDeviceId = 1001;

    private readonly TestSystemClock _clock = new();
    private readonly ModeManager _modeManager = new();
    private readonly RecordingKeyboardOutputService _output = new();

    private ScanInputCoordinator CreateCoordinator(
        ISkuParser? parser = null, ISkuValidator? validator = null)
        => new(
            new InputEventCorrelator(_clock) { BoundScannerDeviceId = ScannerDeviceId },
            new ScanSession(_clock),
            _modeManager,
            parser ?? SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition, StartPosition = 5, Length = 8,
            }),
            validator ?? SkuValidatorFactory.Create(new SkuValidationSettings()),
            _output,
            new HotkeyCoordinator(default));

    /// <summary>
    /// 中文：模拟扫码枪送出一整枪。每个字符先走钩子再走 Raw Input，
    ///       中间推进 1 毫秒，贴近实测的约 1.1 毫秒节奏。
    /// English: Simulates a full scan: each character through the hook then Raw Input, 1 ms
    ///          apart, close to the measured ~1.1 ms cadence.
    /// </summary>
    private void Scan(ScanInputCoordinator coordinator, string content)
    {
        ushort scanCode = 0x20;

        foreach (var character in content + '\r')
        {
            coordinator.OnHookEvent(new KeyEvent(
                _clock.MonotonicNow, scanCode, IsExtended: false, IsKeyUp: false,
                VirtualKey: 0x44, character, IsInjected: false));
            _clock.Advance(TimeSpan.FromMilliseconds(1));

            coordinator.OnRawInputEvent(new RawInputEvent(
                _clock.MonotonicNow, scanCode, IsExtended: false, IsKeyUp: false, ScannerDeviceId));
            _clock.Advance(TimeSpan.FromMilliseconds(1));

            scanCode = (ushort)(scanCode == 0x2F ? 0x20 : scanCode + 1);
        }
    }

    /// <summary>
    /// 中文：FS1 —— SN 模式发出完整的原始码（规格 §3）。
    /// English: FS1 — SN mode emits the complete raw code (spec §3).
    /// </summary>
    [Fact]
    public void Successful_sn_scan_emits_the_raw_string()
    {
        var coordinator = CreateCoordinator();

        Scan(coordinator, "DGKJRDC5679F5NF");

        Assert.Equal("DGKJRDC5679F5NF", Assert.Single(_output.EmittedText));
    }

    /// <summary>
    /// 中文：FS2 —— SKU 模式发出解析出来的 SKU，而不是原始码。
    /// English: FS2 — SKU mode emits the parsed SKU rather than the raw code.
    /// </summary>
    [Fact]
    public void Successful_sku_scan_emits_the_parsed_sku()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABCD12345678XYZ");

        Assert.Equal("12345678", Assert.Single(_output.EmittedText));
    }

    /// <summary>
    /// 中文：
    ///   FS3 —— AppendEnterAfterScan 默认关闭，**一个回车都不发**（决策 D-10）。
    ///
    ///   ★ 关闭时网页收到的回车数是 **0**，不是「少了一个」。
    ///
    ///     整段原始扫描都被吞掉，扫码枪自带的那个终止回车也不例外。规格 §10
    ///     特意点明这一点，因为条码录入的网页表单很常见地靠回车提交或跳到
    ///     下一个字段——而这个页面到底靠不靠，在拿到真实页面之前无从知道。
    ///
    ///     这也是为什么它是一个设置项而不是常量：一个布尔加一个分支，换来
    ///     这个答案能在试点当天现场切换，而不必改代码重新部署。
    /// English:
    ///   FS3 — AppendEnterAfterScan is off by default and emits no Enter at all (decision
    ///   D-10). The page receives zero Enters, not one fewer: the whole raw scan is swallowed
    ///   including the scanner's terminating Enter. Spec §10 makes the point explicitly because
    ///   barcode entry in web forms commonly relies on Enter to submit or advance — and whether
    ///   this page does cannot be known before the real page is in hand. Hence a setting rather
    ///   than a constant: one boolean and one branch buys an answer switchable on the pilot
    ///   floor instead of a rebuild and redeployment.
    /// </summary>
    [Fact]
    public void Append_enter_disabled_emits_no_enter()
    {
        var coordinator = CreateCoordinator();
        Assert.False(coordinator.AppendEnterAfterScan);

        Scan(coordinator, "DGKJRDC5679F5NF");

        Assert.Equal("DGKJRDC5679F5NF", Assert.Single(_output.EmittedText));
        Assert.True(_output.EnterCount == 0,
            "默认关闭时一个回车都不该发。扫码枪自带的终止回车已经被吞掉，"
            + $"因此网页收到的回车数应当是 0。实际发出 {_output.EnterCount} 次。");
    }

    /// <summary>
    /// 中文：FS4 —— 开启时在 SN 模式下**恰好**发一次回车。
    ///       断言的是"恰好一次"而不是"至少一次"：多发一次在网页上可能就是
    ///       多提交了一次表单，那在仓库系统里是一条多余的记录。
    /// English: FS4 — with the setting on, SN mode emits exactly one Enter. The assertion is
    ///          "exactly" rather than "at least": one extra could mean one extra form
    ///          submission, which in the warehouse system is a spurious record.
    /// </summary>
    [Fact]
    public void Append_enter_enabled_emits_exactly_one_enter_in_sn()
    {
        var coordinator = CreateCoordinator();
        coordinator.AppendEnterAfterScan = true;

        Scan(coordinator, "DGKJRDC5679F5NF");

        Assert.Equal("DGKJRDC5679F5NF", Assert.Single(_output.EmittedText));
        Assert.Equal(1, _output.EnterCount);
    }

    /// <summary>
    /// 中文：FS5 —— 开启时在 SKU 模式下同样恰好发一次。
    ///       规格 §10 要求这个设置对 SN 输出、SKU 输出、强制发送**一视同仁**，
    ///       所以三条路径都要各测一遍——只测一条的话，另外两条各自漏掉都不会
    ///       有任何东西变红。
    /// English: FS5 — exactly one in SKU mode too. Spec §10 requires the setting to apply
    ///          equally to SN output, SKU output and Force Send, so all three paths are tested;
    ///          with only one covered, either of the others could omit it with nothing turning
    ///          red.
    /// </summary>
    [Fact]
    public void Append_enter_enabled_emits_exactly_one_enter_in_sku()
    {
        var coordinator = CreateCoordinator();
        coordinator.AppendEnterAfterScan = true;
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABCD12345678XYZ");

        Assert.Equal("12345678", Assert.Single(_output.EmittedText));
        Assert.Equal(1, _output.EnterCount);
    }

    /// <summary>
    /// 中文：FS6 —— 解析失败**绝不自动发送**（规格 §10）。
    ///       这条断言的是"没有发生的事"，而那恰恰是实现里最容易漏、现场最难
    ///       发现的：工人只会看到一个错误提示，不会知道后台其实已经悄悄发过
    ///       一次了。
    /// English: FS6 — a parse failure never auto-emits (spec §10). This asserts a thing that
    ///          did not happen, which is both the easiest to omit and the hardest to notice on
    ///          site: the operator sees an error and has no way to know something was quietly
    ///          sent anyway.
    /// </summary>
    [Fact]
    public void Parse_failure_emits_nothing()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        // 规则要从第 5 位取 8 个字符，这枪只有 3 个
        // The rule takes 8 characters from position 5; this scan has 3
        Scan(coordinator, "ABC");

        Assert.True(_output.EmittedNothing,
            "解析失败绝不自动发送（规格 §10）。工人只会看到错误提示，"
            + "不会知道后台其实已经发过一次了。");
        Assert.NotNull(coordinator.PendingError);
    }

    /// <summary>
    /// 中文：FS7 —— 校验失败同样绝不自动发送。
    /// English: FS7 — a validation failure likewise never auto-emits.
    /// </summary>
    [Fact]
    public void Validation_failure_emits_nothing()
    {
        var coordinator = CreateCoordinator(
            parser: SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition, StartPosition = 1, Length = 3,
            }),
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                CharacterSet = CharacterSetPreset.Numbers,
            }));

        _modeManager.SetMode(ScanMode.Sku);
        Scan(coordinator, "12ABCDEF");

        Assert.True(_output.EmittedNothing);
        Assert.NotNull(coordinator.PendingError);
    }

    /// <summary>
    /// 中文：
    ///   FS8 —— 扫描超时绝不发送任何内容（规格 §5.4）。
    ///
    ///   ★ 这条与 FS6、FS7 的区别在于**它连待决错误都不进**，因此工人连
    ///     F10 这条路都没有。手里那半截根本不是完整条码，把它发出去等于
    ///     主动往仓库系统里写错误数据——而 Task 4a 已经实测到这类残缺确实
    ///     会发生（规格 §22.5）。
    /// English:
    ///   FS8 — a scan timeout emits nothing (spec §5.4). Unlike FS6 and FS7 it does not even
    ///   enter the pending-error state, so there is no F10 path either: half a code was never a
    ///   complete barcode and emitting it would volunteer bad data into the warehouse system —
    ///   and Task 4a confirmed such damage genuinely occurs (spec §22.5).
    /// </summary>
    [Fact]
    public void Scan_timeout_emits_nothing()
    {
        var coordinator = CreateCoordinator();
        ushort scanCode = 0x20;

        foreach (var character in "DGKJRD")
        {
            coordinator.OnHookEvent(new KeyEvent(
                _clock.MonotonicNow, scanCode, false, false, 0x44, character, false));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            coordinator.OnRawInputEvent(new RawInputEvent(
                _clock.MonotonicNow, scanCode, false, false, ScannerDeviceId));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            scanCode++;
        }

        _clock.Advance(TimeSpan.FromMilliseconds(300));
        coordinator.Tick();

        Assert.True(_output.EmittedNothing);
        Assert.True(coordinator.PendingError is null,
            "扫描超时不进入待决错误，因此连 F10 这条路都没有（规格 §5.4）。");
    }

    /// <summary>
    /// 中文：
    ///   FS9 —— F10 发的是**原始码**，不是解析出来的候选 SKU。
    ///
    ///   ★ 规格 §10 写得很直白：「Force Send 始终发送原始扫描码，而不是部分
    ///     解析出来的候选值。」
    ///
    ///     道理在于工人按 F10 时的处境：他看到错误提示，并且判断"这个码本身
    ///     是对的，是规则还没配好"。这时他想送出去的，是他扫到的那个东西。
    ///     发一个解析了一半的候选值，等于用一条他不信任的规则的中间产物
    ///     去覆盖他的判断——而他按 F10 恰恰是因为不信任那条规则。
    ///
    ///   本条特意用一个**校验失败**的场景：此时候选 SKU 是存在的（解析成功了），
    ///   所以"发原始码还是发候选值"是一个真实的二选一。若只用解析失败来测，
    ///   候选值根本不存在，这条断言就变得空洞。
    ///
    ///   ★ 一处诚实说明：本条**无法通过改实现让它变红**，试过了。
    ///
    ///     PendingScanError 只存了原始码，根本没有候选 SKU 这个字段，所以
    ///     "强制发送时发候选值"在类型层面就写不出来。这比一条能变红的测试
    ///     更强，但意味着本条守的是将来的改动而非今天的实现：若有人给
    ///     PendingScanError 补上候选值字段、又在 ForceSend 里用了它，本条会
    ///     立刻变红。守卫的范围就是这个，与 CR4、测试计划 D4 同样的口径。
    /// English:
    ///   FS9 — F10 emits the raw code, not the parsed candidate. Spec §10 is blunt: "Force Send
    ///   always emits the raw scanned code, not a partially parsed candidate."
    ///
    ///   The reason lies in the operator's situation: they have read the error and judged that
    ///   the code is right and the rule is not yet configured. What they want sent is what they
    ///   scanned. Emitting a half-parsed candidate would override that judgement with an
    ///   intermediate product of the very rule they pressed F10 because they do not trust.
    ///
    ///   This deliberately uses a *validation* failure, where a candidate SKU does exist because
    ///   parsing succeeded, making "raw or candidate" a genuine choice. Tested only against a
    ///   parse failure, where no candidate exists, the assertion would be hollow.
    ///
    ///   One honest caveat: this case cannot be made to fail by changing the implementation, and
    ///   that was attempted. PendingScanError stores only the raw code and has no candidate
    ///   field, so "force-send the candidate" is not expressible. That is stronger than a test
    ///   that can go red, but it means this guards a future change rather than today's code:
    ///   adding a candidate field to PendingScanError and using it in ForceSend turns it red at
    ///   once. The same accounting as CR4 and the test plan's D4.
    /// </summary>
    [Fact]
    public void Force_send_emits_the_raw_code_not_the_parsed_candidate()
    {
        var coordinator = CreateCoordinator(
            parser: SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition, StartPosition = 1, Length = 3,
            }),
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                CharacterSet = CharacterSetPreset.Numbers,
            }));

        _modeManager.SetMode(ScanMode.Sku);
        Scan(coordinator, "12ABCDEF");

        // 解析成功，取出了 "12A"；校验失败，因为含非数字
        // Parsing succeeded, extracting "12A"; validation failed on the non-digit
        Assert.True(_output.EmittedNothing);

        Assert.True(coordinator.ForceSend());

        Assert.True(Assert.Single(_output.EmittedText) == "12ABCDEF",
            "F10 必须发送完整的原始扫描码，而不是解析出来的候选值 \"12A\"。"
            + "工人按 F10 正是因为不信任那条规则，发它的中间产物等于覆盖他的判断。");

        Assert.True(coordinator.PendingError is null);
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：FS10 —— 强制发送同样遵守 AppendEnterAfterScan（规格 §10 要求
    ///       该设置对 SN、SKU、强制发送一视同仁）。
    /// English: FS10 — Force Send honors AppendEnterAfterScan too (spec §10 requires the setting
    ///          to apply equally to SN, SKU and Force Send).
    /// </summary>
    [Fact]
    public void Force_send_honours_append_enter()
    {
        var coordinator = CreateCoordinator();
        coordinator.AppendEnterAfterScan = true;
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABC");
        Assert.True(_output.EmittedNothing);

        Assert.True(coordinator.ForceSend());

        Assert.Equal("ABC", Assert.Single(_output.EmittedText));
        Assert.Equal(1, _output.EnterCount);
    }

    /// <summary>
    /// 中文：FS11 —— Esc 取消，什么都不发（规格 §10）。
    /// English: FS11 — Esc cancels and emits nothing (spec §10).
    /// </summary>
    [Fact]
    public void Cancel_emits_nothing()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABC");
        Assert.NotNull(coordinator.PendingError);

        Assert.True(coordinator.Cancel());

        Assert.True(_output.EmittedNothing);
        Assert.True(coordinator.PendingError is null);
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：
    ///   FS12、FS13 —— 强制发送与取消都**保持当前模式不变**（规格 §10）。
    ///
    ///   这不是形式要求。工人处理一个错误，处理完之后模式若变了，他扫的下一枪
    ///   就会以他不知道的模式送出去——而那正是规格 §3 坚持"启动恒为 SN"
    ///   要避免的那类错误数据。错误处理本身绝不该改变工作状态。
    /// English:
    ///   FS12, FS13 — both Force Send and Cancel preserve the current mode (spec §10).
    ///
    ///   Not a formality. The operator deals with an error, and a mode that shifted afterwards
    ///   would send their next scan in a mode they do not know about — the same class of wrong
    ///   data spec §3's "always start in SN" exists to prevent. Handling an error must never
    ///   change the working state.
    /// </summary>
    [Fact]
    public void Force_send_and_cancel_preserve_the_mode()
    {
        var coordinator = CreateCoordinator();
        _modeManager.SetMode(ScanMode.Sku);

        Scan(coordinator, "ABC");
        coordinator.ForceSend();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);

        _output.Clear();
        Scan(coordinator, "ABC");
        coordinator.Cancel();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);
    }

    /// <summary>
    /// 中文：
    ///   FS14 —— 没有待决错误时，F10 与 Esc 都什么也不做，并返回 false。
    ///
    ///   ★ 返回值是给 Task 8 的热键路由用的。规格 §7 的表格规定：
    ///       F10  有待决错误 → 吞掉；没有 → **透传**
    ///       Esc  有待决错误 → 吞掉；没有 → **透传**
    ///
    ///     Esc 这一条尤其要紧，规格特意解释了原因：Esc 在网页里太常用，
    ///     无条件吞掉会让工人发现"网页的取消键坏了"，却完全想不到是本程序
    ///     所为——一个查不到源头的故障。
    ///
    ///     所以"没有待决错误时按 F10/Esc"是完全正常的操作，不是异常，
    ///     因此返回布尔而不是抛异常。
    /// English:
    ///   FS14 — with no pending error, F10 and Esc do nothing and return false.
    ///
    ///   The return value serves Task 8's hotkey routing. Spec §7's table swallows both keys
    ///   while an error is pending and passes them through otherwise. The Esc row matters most,
    ///   and the spec explains why: Esc is far too common in web use, and swallowing it
    ///   unconditionally would leave the operator with a "broken" cancel key and no reason to
    ///   suspect this tool — a fault whose source cannot be found.
    ///
    ///   Pressing either with no pending error is therefore entirely normal rather than
    ///   exceptional, which is why this returns a boolean instead of throwing.
    /// </summary>
    [Fact]
    public void Force_send_and_cancel_without_a_pending_error_do_nothing()
    {
        var coordinator = CreateCoordinator();

        Assert.False(coordinator.ForceSend());
        Assert.False(coordinator.Cancel());

        Assert.True(_output.EmittedNothing);
        Assert.Equal(ScanPipelineState.Idle, coordinator.State);
    }

    /// <summary>
    /// 中文：
    ///   FS15 —— 内容原样发出：不修剪、不补齐、不改大小写（规格 §10、决策 D-9）。
    ///
    ///   ★ 不修剪首尾空白这一条最容易被"顺手优化"掉。
    ///
    ///     扫码枪本不该发出空白；真发出了，那是异常，应当由校验层暴露出来
    ///     （规格 §9 的字符集校验会拦下空格）。在输出这一层悄悄抹掉，异常
    ///     就永远查不到了，而仓库系统里会多出一批看起来正常、实际来路不明
    ///     的数据。
    ///
    ///     大小写同理：规格 §9.2 已经把大小写的处理明确交给了校验层的
    ///     「忽略大小写」开关与校验正则，输出层再动手就等于有两个地方在
    ///     决定同一件事。
    /// English:
    ///   FS15 — content is emitted exactly as produced: no trimming, padding or case conversion
    ///   (spec §10, decision D-9).
    ///
    ///   The no-trimming rule is the one most likely to be "tidied away". A scanner should not
    ///   emit whitespace; if it does, that is an anomaly for validation to surface (spec §9's
    ///   character-set rule rejects a space). Removed quietly at the output layer it could never
    ///   be found, and the warehouse system would accumulate data that looks normal and came
    ///   from nowhere identifiable.
    ///
    ///   Case is the same: spec §9.2 already assigns case handling to validation's ignore-case
    ///   toggle and the validation regex, so touching it here would put two places in charge of
    ///   one decision.
    /// </summary>
    [Fact]
    public void Emitted_content_is_never_trimmed_or_altered()
    {
        var coordinator = CreateCoordinator();

        Scan(coordinator, "  dgk jrd  ");

        Assert.True(Assert.Single(_output.EmittedText) == "  dgk jrd  ",
            "输出必须原样发送：不修剪首尾空白、不补齐、不改大小写（规格 §10）。"
            + "扫码枪发出空白本身是异常，应当由校验层暴露，而不是在这里被抹掉。");
    }
}
