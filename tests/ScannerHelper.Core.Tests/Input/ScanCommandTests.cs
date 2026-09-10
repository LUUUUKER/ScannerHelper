// =============================================================================
// ScanCommandTests.cs
//
// 中文：
//   命令条码（用例编号 CB1~CB10）。
//
//   ★ 这一组钉的是同一类事故的两个方向。
//
//     一个方向是**该认的没认出来**：贴在墙上的纸扫下去没反应。这个方向不危险，
//     因为工人立刻就知道——他扫了、什么都没发生。
//
//     另一个方向危险得多：**不该认的认出来了**，或者**认出来了却发了出去**。
//     前者意味着一件真货被当成命令，模式在工人毫不知情的情况下翻掉，接下来
//     几十枪全按错的规则进仓库系统；后者意味着 #SH:XXX# 变成业务软件里的一行
//     脏数据，而没有人会想到它来自一张贴在墙上的纸。
//
//     两个危险方向都不会报错，都只能靠用例钉住。
//
// English:
//   Command barcodes (cases CB1–CB10).
//
//   These hold two directions of the same accident. One is failing to recognize what should be
//   recognized: the sheet on the wall does nothing when scanned. That direction is not dangerous,
//   because the operator finds out immediately — they scanned and nothing happened.
//
//   The other is far worse: recognizing what should not be, or recognizing it and sending it anyway.
//   The first means a real item is taken for a command, the mode flips without the operator knowing,
//   and the next dozens of scans enter the warehouse system under the wrong rule. The second puts
//   #SH:XXX# into the business application as a line of dirty data whose origin nobody would think
//   to trace to a sheet of paper on a wall.
//
//   Neither dangerous direction raises an error. Only cases hold them.
//
// 包含的测试 / Tests in this file:
//   Ordinary_codes_are_not_commands                  CB1
//   The_two_printed_sheets_are_recognized            CB2
//   Case_is_ignored                                  CB3
//   Surrounding_whitespace_is_ignored                CB4
//   A_prefixed_but_unknown_command_is_flagged        CB5
//   Empty_input_is_not_a_command                     CB6
//   Command_switches_the_mode_and_emits_nothing      CB7
//   Command_is_recognized_from_either_mode           CB8
//   A_paused_command_sheet_goes_through_untouched    CB9
//   An_unknown_command_is_never_emitted              CB10
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Tests.TestDoubles;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Input;

public class ScanCommandTests
{
    private readonly ModeManager _modeManager = new();
    private readonly RecordingKeyboardOutputService _output = new();

    private ScanProcessor CreateProcessor()
        => new(
            _modeManager,
            SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition,
                StartPosition = 1,
                Length = 7,
            }),
            SkuValidatorFactory.Create(new SkuValidationSettings()),
            _output);

    [Theory] // CB1
    [InlineData("ABCDEFG123456789")]
    [InlineData("6901234567892")]
    [InlineData("SH:SKU")]
    [InlineData("SKU")]
    [InlineData("#SHOP123#")]
    public void Ordinary_codes_are_not_commands(string rawCode)
    {
        // ★ 最后两个是重点：一枚真货的条码只要被误认成命令，模式就会静默翻掉，
        //   而现场没有任何迹象。前缀必须窄到把它们排除在外。
        // The last two matter most: a real item's barcode mistaken for a command flips the mode
        // silently with nothing on site to show it. The prefix has to be narrow enough to exclude
        // them.
        Assert.Equal(ScanCommandKind.None, ScanCommand.Recognize(rawCode));
    }

    [Fact] // CB2
    public void The_two_printed_sheets_are_recognized()
    {
        Assert.Equal(ScanCommandKind.SwitchToSn, ScanCommand.Recognize(ScanCommand.SwitchToSnCode));
        Assert.Equal(ScanCommandKind.SwitchToSku, ScanCommand.Recognize(ScanCommand.SwitchToSkuCode));
    }

    [Theory] // CB3
    [InlineData("#sh:sku#", ScanCommandKind.SwitchToSku)]
    [InlineData("#Sh:Sn#", ScanCommandKind.SwitchToSn)]
    [InlineData("#SH:SKU#", ScanCommandKind.SwitchToSku)]
    public void Case_is_ignored(string rawCode, ScanCommandKind expected)
    {
        // ★ 不少扫码枪有"输出一律转大写"的设置。开着的时候我们收到的是哪一种
        //   由那台枪决定——区分大小写会让同一张纸在 A 工位有效、B 工位失效。
        // Many scanners have a "force uppercase" setting, and while it is on, which form arrives is
        // decided by that scanner: matching case would make one printed sheet work at one station
        // and fail at the next.
        Assert.Equal(expected, ScanCommand.Recognize(rawCode));
    }

    [Theory] // CB4
    [InlineData(" #SH:SN# ")]
    [InlineData("#SH:SN#\t")]
    public void Surrounding_whitespace_is_ignored(string rawCode)
        => Assert.Equal(ScanCommandKind.SwitchToSn, ScanCommand.Recognize(rawCode));

    [Theory] // CB5
    [InlineData("#SH:PAUSE#")]
    [InlineData("#SH:#")]
    [InlineData("#SH:SKU")]
    public void A_prefixed_but_unknown_command_is_flagged(string rawCode)
        => Assert.Equal(ScanCommandKind.Unknown, ScanCommand.Recognize(rawCode));

    [Theory] // CB6
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_not_a_command(string? rawCode)
        => Assert.Equal(ScanCommandKind.None, ScanCommand.Recognize(rawCode));

    [Fact] // CB7
    public void Command_switches_the_mode_and_emits_nothing()
    {
        var processor = CreateProcessor();
        ScanOutcome? outcome = null;
        processor.ScanProcessed += (_, args) => outcome = args.Outcome;

        processor.OnScanReceived(ScanCommand.SwitchToSkuCode);

        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);

        // ★ 这一行是这组用例里最要紧的一条断言。命令条码一旦被发进业务软件，
        //   仓库数据里就多了一行 #SH:SKU#，而追查的人手上没有任何线索指向
        //   一张贴在墙上的纸。
        // This is the most important assertion here. Once a command barcode reaches the business
        // application, the warehouse data carries a #SH:SKU# line and whoever investigates has
        // nothing pointing at a sheet of paper on a wall.
        Assert.Empty(_output.EmittedText);

        Assert.Equal(ScanPipelineState.Idle, processor.State);
        Assert.Null(processor.PendingError);
        var modeCommand = Assert.IsType<ScanOutcome.ModeCommand>(outcome);
        Assert.Equal(ScanMode.Sku, modeCommand.Mode);
    }

    [Fact] // CB8
    public void Command_is_recognized_from_either_mode()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();

        processor.OnScanReceived(ScanCommand.SwitchToSnCode);
        Assert.Equal(ScanMode.Sn, _modeManager.CurrentMode);

        processor.OnScanReceived(ScanCommand.SwitchToSkuCode);
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);

        Assert.Empty(_output.EmittedText);
    }

    [Fact] // CB9
    public void A_paused_command_sheet_goes_through_untouched()
    {
        var processor = CreateProcessor();
        processor.Pause();

        processor.OnScanReceived(ScanCommand.SwitchToSkuCode);

        // ★ 暂停的承诺是"程序什么都不管，扫什么发什么"——一条没有例外的规则。
        //   在这里开一个口子，这条承诺就需要附加说明，而安全阀最不该有的
        //   就是附加说明。
        // The pause valve promises that the program does nothing and sends what it reads, a rule
        // with no exceptions. An exception here would make that promise need a footnote, and a
        // safety valve is the last place for one.
        Assert.Equal([ScanCommand.SwitchToSkuCode], _output.EmittedText);
        Assert.Equal(ScanMode.Sn, _modeManager.CurrentMode);
    }

    [Fact] // CB10
    public void An_unknown_command_is_never_emitted()
    {
        var processor = CreateProcessor();
        ScanOutcome? outcome = null;
        processor.ScanProcessed += (_, args) => outcome = args.Outcome;

        processor.OnScanReceived("#SH:WHATEVER#");

        Assert.Empty(_output.EmittedText);
        Assert.IsType<ScanOutcome.UnknownCommand>(outcome);

        // ★ 认不出来**不进待决错误**。进了的话 F10 就被武装起来，而 F10 的语义是
        //   "把原始码强制发出去"——那正好把这条用例要防的事变成一个按键就能做到。
        // An unrecognized command does not enter the pending-error state. If it did, F10 would be
        // armed, and F10 means "force the raw code out" — turning exactly what this case prevents
        // into a single keypress.
        Assert.Null(processor.PendingError);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }
}
