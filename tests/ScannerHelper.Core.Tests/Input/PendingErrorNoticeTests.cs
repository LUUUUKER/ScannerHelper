// =============================================================================
// PendingErrorNoticeTests.cs
//
// 中文：
//   一枪没能发出去之后会怎样（用例编号 PE1~PE8）。
//
//   ★ 这一组取代了 ForceSendWorkflowTests（FS1~FS15）。
//
//     2026-09-15 的规格变更去掉了强制发送与取消：错误不再是一个"等工人按 F10
//     或 Esc 决定"的状态，而是一条提示——那一枪**永远不会发出去**，工人换个
//     模式重扫即可，而提示自己消失。
//     变更记录见 docs/CHANGE_ERROR_HANDLING.md。
//
//   ★ 这一组真正钉住的是一句话：**判定不合格的码，绝不进业务软件。**
//
//     这件事错了不会有任何报错。它的表现是仓库系统里悄悄多了几条没解析过的
//     原始码，和正常记录长得一模一样；发现它要等到有人对账，而那时已经过了
//     几天、混进了几百条正常数据里。所以每一条能发出东西的路径都要有人钉着。
//
//   ★ 另一件要钉住的是"提示会自己消失"。
//
//     如果重扫成功之后那条红色的错误还挂在屏幕上，工人会以为这一枪也失败了，
//     于是再扫一次——同一件货就进系统两遍。界面上一条过期的错误不是"不好看"，
//     它会直接造成重复数据。
//
// English:
//   What happens after a scan fails to go out (cases PE1–PE8).
//
//   This group replaces ForceSendWorkflowTests (FS1–FS15). The spec change of 2026-09-15 removed
//   Force Send and Cancel: an error is no longer a state awaiting F10 or Esc but a notice — the scan
//   never goes out, the operator switches mode and rescans, and the notice clears itself. The change
//   record is docs/CHANGE_ERROR_HANDLING.md.
//
//   What these really hold is one sentence: a code the rules rejected never reaches the business
//   application. Getting that wrong raises no error. It shows up as a few unparsed raw codes sitting
//   quietly in the warehouse system, indistinguishable from correct records, found only when someone
//   reconciles the data days later among hundreds of good rows. So every path that can emit needs
//   someone holding it.
//
//   The second thing held is that the notice clears itself. A red error still on screen after a
//   successful rescan reads as "that one failed too", so the operator scans again and the same item
//   enters the system twice. A stale error on screen is not an aesthetic problem; it produces
//   duplicate data.
//
// 包含的测试 / Tests in this file:
//   Parse_failure_emits_nothing                          PE1
//   Validation_failure_emits_nothing                     PE2
//   A_notice_does_not_block_the_next_scan                PE3
//   A_successful_rescan_clears_the_notice                PE4
//   Switching_mode_clears_the_notice                     PE5
//   Switching_mode_never_re_emits_the_failed_scan        PE6
//   Pausing_is_the_way_an_unparseable_code_gets_through  PE7
//   Clearing_a_notice_that_is_not_there_is_harmless      PE8
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Tests.TestDoubles;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Input;

public class PendingErrorNoticeTests
{
    private const string RawCode = "ABCDEFG123456789";

    private readonly ModeManager _modeManager = new();
    private readonly RecordingKeyboardOutputService _output = new();

    /// <summary>
    /// 中文：一条永远解析不出东西的规则——从第 90 位开始取 4 位，而码只有 16 位。
    /// English: A rule that can never succeed: four characters from position 90 of a 16-character
    ///          code.
    /// </summary>
    private static ISkuParser ImpossibleParser
        => SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 90,
            Length = 4,
        });

    private static ISkuParser WholeCodeParser
        => SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 1,
            Length = 16,
        });

    private ScanProcessor CreateProcessor(
        ISkuParser? parser = null, ISkuValidator? validator = null)
        => new(
            _modeManager,
            parser ?? ImpossibleParser,
            validator ?? SkuValidatorFactory.Create(new SkuValidationSettings()),
            _output);

    [Fact] // PE1
    public void Parse_failure_emits_nothing()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();

        processor.OnScanReceived(RawCode);

        Assert.Empty(_output.EmittedText);
        Assert.NotNull(processor.PendingError);
    }

    [Fact] // PE2
    public void Validation_failure_emits_nothing()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(
            parser: WholeCodeParser,
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 40,
            }));

        processor.OnScanReceived(RawCode);

        Assert.Empty(_output.EmittedText);
        Assert.NotNull(processor.PendingError);
    }

    [Fact] // PE3
    public void A_notice_does_not_block_the_next_scan()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();
        processor.OnScanReceived(RawCode);
        Assert.NotNull(processor.PendingError);

        // ★ 错误是提示，不是闸门。挡住后面的扫码，工人就得先想起来"怎么解除"——
        //   而出错那一刻恰恰最不适合让人多做一步判断。他此刻只想把手里这件货扫进去。
        // The error is a notice, not a gate. Blocking later scans makes the operator first recall
        // how to dismiss it — at the moment least suited to an extra decision. All they want is to
        // get the item in their hand into the system.
        _modeManager.SetMode(ScanMode.Sn);
        processor.OnScanReceived(RawCode);

        Assert.Equal([RawCode], _output.EmittedText);
    }

    [Fact] // PE4
    public void A_successful_rescan_clears_the_notice()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();
        processor.OnScanReceived(RawCode);
        Assert.NotNull(processor.PendingError);

        _modeManager.SetMode(ScanMode.Sn);
        processor.OnScanReceived(RawCode);

        // ★ 过期的错误会造成重复数据：工人看到红色还在，以为这一枪也没成，
        //   于是再扫一次，同一件货进系统两遍。见文件头。
        // A stale error produces duplicates: the operator sees red still showing, concludes this one
        // failed too, and scans again — the same item enters the system twice. See the file header.
        Assert.Null(processor.PendingError);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }

    [Fact] // PE5
    public void Switching_mode_clears_the_notice()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();
        processor.OnScanReceived(RawCode);
        Assert.NotNull(processor.PendingError);

        processor.OnScanReceived(ScanCommand.SwitchToSnCode);

        Assert.Null(processor.PendingError);
        Assert.Equal(ScanMode.Sn, _modeManager.CurrentMode);
    }

    [Fact] // PE6
    public void Switching_mode_never_re_emits_the_failed_scan()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();
        processor.OnScanReceived(RawCode);

        processor.OnScanReceived(ScanCommand.SwitchToSnCode);

        // ★ 这一条是决策 3 的守卫。重发看着省一次扫码，但发出去的数据来自换模式
        //   **之前**那一枪——工人此刻多半没在看屏幕，手里的货也未必还是刚才那件。
        //   规则只有一条：发出去的东西永远对应工人**当下**这一次扫码。
        // This holds decision 3. Re-emitting looks like it saves a scan, but the data would come
        // from the scan taken before the switch, while the operator is probably not watching and may
        // no longer hold that item. One rule: what goes out always corresponds to the scan just
        // taken.
        Assert.Empty(_output.EmittedText);
    }

    [Fact] // PE7
    public void Pausing_is_the_way_an_unparseable_code_gets_through()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();
        processor.OnScanReceived(RawCode);
        Assert.Empty(_output.EmittedText);

        // ★ 强制发送去了哪里：就是这里。
        //
        //   一个规则怎么也解析不了的码，工人点暂停、扫、再点恢复，原文照样进业务
        //   软件。和 F10 做的事完全一样，但它是显式的、界面上明晃晃写着"已暂停"、
        //   日志里记成 EmittedWhilePaused——而 F10 按下去之后，业务系统里那条记录
        //   和正常记录长得一模一样。
        // This is where Force Send went. For a code no rule can parse, the operator pauses, scans,
        // and resumes, and the raw text reaches the business application. The same act as F10, but
        // explicit, labelled on screen, and recorded as EmittedWhilePaused — whereas after F10 the
        // resulting row in the business system looked exactly like a correct one.
        processor.Pause();
        processor.OnScanReceived(RawCode);

        Assert.Equal([RawCode], _output.EmittedText);
        Assert.Null(processor.PendingError);
    }

    [Fact] // PE8
    public void Clearing_a_notice_that_is_not_there_is_harmless()
    {
        var processor = CreateProcessor();

        processor.ClearPendingError();
        processor.ClearPendingError();

        // 暂停中调用不该把状态踹回 Idle——暂停是安全阀，任何顺手的清理都不该
        // 悄悄把它关掉。
        // Calling it while paused must not knock the state back to Idle: pausing is the safety
        // valve, and no incidental cleanup should quietly switch it off.
        processor.Pause();
        processor.ClearPendingError();

        Assert.True(processor.IsPaused);
        Assert.Equal(ScanPipelineState.Paused, processor.State);
    }
}
