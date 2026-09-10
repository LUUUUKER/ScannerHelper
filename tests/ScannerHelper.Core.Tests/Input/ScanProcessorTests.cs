// =============================================================================
// ScanProcessorTests.cs
//
// 中文：
//   一次扫描进来之后发生的全部事情（架构变更后，用例编号 SP1~SP17）。
//
//   ★ 本组是架构变更之后**唯一**的输入状态机测试，因为状态机只剩这一半了。
//
//     原来的另一半——把扫码枪的字符从键盘流里认出来——在 2026-09-10 被实测
//     证明做不到（TASK_4B_FINDING_20260910.md），随架构一起退役。留下来的这
//     一半从来都是对的，本组把它重新钉在新的入口上。
//
//   ★ 分量最重的三条：
//
//     SP6  暂停时**绝不碰解析器**。用一个"一旦被调用就抛异常"的解析器替身钉住。
//          决策 D-26 说暂停就是要绕开解析与校验；先咨询它们再看暂停标志，等于
//          把它们的每一个缺陷都继承进这个唯一用来逃离缺陷的状态。这与旧架构里
//          用例 CO1 对 PAUSED 的守法完全一致——形式变了，道理没变。
//
//     SP5  空码被忽略。串口上一个孤零零的终止符（CRLF 被拆在两次读取里）会切出
//          一个空帧，那不是一枪扫描。当成扫描处理，SKU 模式下会报一个工人完全
//          无法理解的解析失败。这一条是串口架构特有的，旧架构不存在。
//
//     SP12 补回车的次数必须**精确**：关闭时恰好 0，开启时恰好 1，四条输出路径
//          （SN、SKU、强制发送、暂停原样）一视同仁。规格 §10 特意说明默认关闭
//          时网页收到的回车是 0 而不是「少了一个」。
//
// English:
//   Everything that happens once a scan arrives (post-change; cases SP1–SP17).
//
//   This is the only input state-machine test group left, because only this half of the state
//   machine remains. The other half — picking the scanner's characters out of the keyboard stream
//   — was proven impossible on 2026-09-10 (TASK_4B_FINDING_20260910.md) and retired with the
//   architecture. The half that stayed was always correct, and this group re-pins it at the new
//   entry point.
//
//   Three carry the most weight. SP6 pins that pausing never touches the parser, using a parser
//   substitute that throws if called: decision D-26 makes PAUSED the bypass around parsing and
//   validation, and consulting them first would inherit their every defect into the one state
//   meant to escape them — the same guard case CO1 held over the old PAUSED. SP5 pins that an
//   empty code is ignored: a lone terminator on the wire cuts an empty frame, and treating that as
//   a scan reports a parse failure the operator cannot make sense of; this case exists only
//   because of the serial architecture. SP12 pins the Enter count exactly — zero when off, one
//   when on, across all four output paths — spec §10 having gone out of its way to say the page
//   receives zero by default rather than one fewer.
//
// 包含的测试 / Tests in this file:
//   Sn_mode_emits_the_raw_code_unchanged                    SP1
//   Sku_mode_emits_the_parsed_sku                           SP2
//   Parse_failure_enters_pending_error_and_emits_nothing    SP3
//   Validation_failure_enters_pending_error_emits_nothing   SP4
//   Empty_scan_is_ignored_entirely                          SP5
//   Pausing_emits_raw_without_consulting_the_parser         SP6
//   Pausing_clears_a_pending_error                          SP7
//   Pausing_does_not_change_the_mode                        SP8
//   Resuming_processes_under_the_rules_again                SP9
//   Force_send_emits_the_raw_code                           SP10
//   Cancel_emits_nothing                                    SP11
//   Append_enter_is_exact_on_every_output_path              SP12
//   Paused_output_is_reported_as_its_own_outcome            SP13
//   Updating_rules_keeps_a_pending_error                    SP14
//   A_new_processor_is_never_paused                         SP15
//   Force_send_is_recorded_as_its_own_outcome               SP16
//   Cancel_is_recorded_as_its_own_outcome                   SP17
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Tests.TestDoubles;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Input;

public class ScanProcessorTests
{
    private const string RawCode = "ABCDEFG123456789";

    private readonly ModeManager _modeManager = new();
    private readonly RecordingKeyboardOutputService _output = new();

    /// <summary>
    /// 中文：默认的解析规则取原始码的前 7 位，也就是 ABCDEFG——一个和原始码
    ///       明显不同的结果，这样"发出去的到底是哪一个"在断言里一眼可辨。
    /// English: The default rule takes the first seven characters, ABCDEFG — visibly different
    ///          from the raw code, so which one was emitted is unmistakable in an assertion.
    /// </summary>
    private static ISkuParser SevenCharacterParser
        => SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 1,
            Length = 7,
        });

    private ScanProcessor CreateProcessor(
        ISkuParser? parser = null, ISkuValidator? validator = null)
        => new(
            _modeManager,
            parser ?? SevenCharacterParser,
            validator ?? SkuValidatorFactory.Create(new SkuValidationSettings()),
            _output);

    [Fact] // SP1
    public void Sn_mode_emits_the_raw_code_unchanged()
    {
        var processor = CreateProcessor();

        processor.OnScanReceived(RawCode);

        Assert.Equal([RawCode], _output.EmittedText);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
        Assert.Null(processor.PendingError);
    }

    [Fact] // SP2
    public void Sku_mode_emits_the_parsed_sku()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();

        processor.OnScanReceived(RawCode);

        Assert.Equal(["ABCDEFG"], _output.EmittedText);
        Assert.Null(processor.PendingError);
    }

    [Fact] // SP3
    public void Parse_failure_enters_pending_error_and_emits_nothing()
    {
        _modeManager.SetMode(ScanMode.Sku);

        // 起始位置远在码长之外，必然解析失败。
        // A start position far beyond the code's length must fail.
        var processor = CreateProcessor(SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 90,
            Length = 4,
        }));

        processor.OnScanReceived(RawCode);

        Assert.True(_output.EmittedNothing);
        Assert.Equal(ScanPipelineState.PendingError, processor.State);
        Assert.Equal(RawCode, processor.PendingError?.RawCode);
    }

    [Fact] // SP4
    public void Validation_failure_enters_pending_error_and_emits_nothing()
    {
        _modeManager.SetMode(ScanMode.Sku);

        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 99,
            }));

        processor.OnScanReceived(RawCode);

        Assert.True(_output.EmittedNothing);
        Assert.Equal(ScanPipelineState.PendingError, processor.State);

        // 待决错误里存的是**完整的原始码**，不是解析出来的候选值——
        // 强制发送要发的是前者（规格 §10）。
        // The pending error holds the complete raw code rather than the parsed candidate: Force
        // Send emits the former (spec §10).
        Assert.Equal(RawCode, processor.PendingError?.RawCode);
    }

    [Fact] // SP5
    public void Empty_scan_is_ignored_entirely()
    {
        _modeManager.SetMode(ScanMode.Sku);

        // ★ 用会抛异常的解析器：空码若被当成一枪，这里就会炸。
        // A throwing parser: were an empty code treated as a scan, this would blow up.
        var processor = CreateProcessor(new ThrowingSkuParser());

        processor.OnScanReceived(string.Empty);

        Assert.True(_output.EmittedNothing);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
        Assert.Null(processor.PendingError);
    }

    [Fact] // SP6
    public void Pausing_emits_raw_without_consulting_the_parser()
    {
        _modeManager.SetMode(ScanMode.Sku);

        // ★ 决策 D-26 的守卫：暂停必须在解析之前判断。
        //   把顺序写反，这个替身就会抛异常，本用例立刻变红。
        // The guard for decision D-26: paused must be checked before parsing. Reverse the order
        // and this substitute throws, turning the case red immediately.
        var processor = CreateProcessor(new ThrowingSkuParser());
        processor.Pause();

        processor.OnScanReceived(RawCode);

        Assert.Equal([RawCode], _output.EmittedText);
        Assert.True(processor.IsPaused);
        Assert.Null(processor.PendingError);
    }

    [Fact] // SP7
    public void Pausing_clears_a_pending_error()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 90,
            Length = 4,
        }));

        processor.OnScanReceived(RawCode);
        Assert.NotNull(processor.PendingError);

        processor.Pause();

        Assert.Null(processor.PendingError);
        Assert.Equal(ScanPipelineState.Paused, processor.State);
    }

    [Fact] // SP8
    public void Pausing_does_not_change_the_mode()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();

        processor.Pause();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);

        processor.Resume();
        Assert.Equal(ScanMode.Sku, _modeManager.CurrentMode);
    }

    [Fact] // SP9
    public void Resuming_processes_under_the_rules_again()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor();

        processor.Pause();
        processor.OnScanReceived(RawCode);
        processor.Resume();
        processor.OnScanReceived(RawCode);

        // 暂停时原样，恢复后按规则解析。
        // Raw while paused, parsed once resumed.
        Assert.Equal([RawCode, "ABCDEFG"], _output.EmittedText);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }

    [Fact] // SP10
    public void Force_send_emits_the_raw_code()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 99,
            }));

        processor.OnScanReceived(RawCode);
        Assert.True(_output.EmittedNothing);

        Assert.True(processor.ForceSend());

        // 发的是原始码，不是解析出来的 ABCDEFG（规格 §10）。
        // The raw code, not the parsed ABCDEFG (spec §10).
        Assert.Equal([RawCode], _output.EmittedText);
        Assert.Null(processor.PendingError);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }

    [Fact] // SP11
    public void Cancel_emits_nothing()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 99,
            }));

        processor.OnScanReceived(RawCode);
        Assert.True(processor.Cancel());

        Assert.True(_output.EmittedNothing);
        Assert.Null(processor.PendingError);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }

    [Theory] // SP12
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Append_enter_is_exact_on_every_output_path(bool appendEnter, int expectedPerEmit)
    {
        // 四条输出路径各发一次，回车总数必须恰好是四倍。
        // One emission down each of the four paths; the Enter count must be exactly four times.
        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings()));
        processor.AppendEnterAfterScan = appendEnter;

        // 路径 1：SN / Path 1: SN
        processor.OnScanReceived(RawCode);

        // 路径 2：SKU / Path 2: SKU
        _modeManager.SetMode(ScanMode.Sku);
        processor.OnScanReceived(RawCode);

        // 路径 3：强制发送 / Path 3: Force Send
        processor.UpdateSkuRules(
            SkuParserFactory.Create(new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition,
                StartPosition = 90,
                Length = 4,
            }),
            SkuValidatorFactory.Create(new SkuValidationSettings()));
        processor.OnScanReceived(RawCode);
        Assert.True(processor.ForceSend());

        // 路径 4：暂停原样 / Path 4: raw while paused
        processor.Pause();
        processor.OnScanReceived(RawCode);

        Assert.Equal(4, _output.EmittedText.Count);
        Assert.Equal(expectedPerEmit * 4, _output.EnterCount);
    }

    [Fact] // SP13
    public void Paused_output_is_reported_as_its_own_outcome()
    {
        var outcomes = new List<ScanOutcome>();
        var processor = CreateProcessor();
        processor.ScanProcessed += (_, args) => outcomes.Add(args.Outcome);

        processor.OnScanReceived(RawCode);
        processor.Pause();
        processor.OnScanReceived(RawCode);

        // ★ 两次发出去的字节完全一样，含义却相反：一次是"按规则处理过"，
        //   一次是"没按规则处理，因为在暂停"。界面必须分得清——暂停是非常态，
        //   看不见它就会有人在暂停下干一整天。
        // The two emissions are byte-identical and opposite in meaning: processed under the rules
        // versus not processed at all because we are paused. The UI must tell them apart — PAUSED
        // is exceptional, and unseen it lets somebody work a whole shift in it.
        Assert.Collection(
            outcomes,
            first => Assert.IsType<ScanOutcome.Emit>(first),
            second => Assert.IsType<ScanOutcome.EmitRawWhilePaused>(second));
    }

    [Fact] // SP14
    public void Updating_rules_keeps_a_pending_error()
    {
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 90,
            Length = 4,
        }));

        processor.OnScanReceived(RawCode);
        Assert.NotNull(processor.PendingError);

        processor.UpdateSkuRules(
            SevenCharacterParser,
            SkuValidatorFactory.Create(new SkuValidationSettings()));

        // 工人正在决定的那一枪与规则变更无关；替他取消掉等于把他手里的码悄悄丢了。
        // The scan the operator is deciding about has nothing to do with a rule change, and
        // cancelling it on their behalf quietly discards the code in their hand.
        Assert.Equal(RawCode, processor.PendingError?.RawCode);
    }

    [Fact] // SP15
    public void A_new_processor_is_never_paused()
    {
        // 规格 §5.7：暂停绝不持久化，程序每次启动都是未暂停。
        // Spec §5.7: PAUSED is never persisted; every launch starts un-paused.
        var processor = CreateProcessor();

        Assert.False(processor.IsPaused);
        Assert.Equal(ScanPipelineState.Idle, processor.State);
    }

    [Fact] // SP16
    public void Force_send_is_recorded_as_its_own_outcome()
    {
        var outcomes = new List<ScanOutcome>();
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 99,
            }));
        processor.ScanProcessed += (_, args) => outcomes.Add(args.Outcome);

        processor.OnScanReceived(RawCode);
        Assert.True(processor.ForceSend());

        // ★ 强制发送是产品里唯一一个「明知有问题还是发出去」的动作，因此是最需要
        //   留痕的一次输出。不记录的话，记录里就是「失败、什么都没发」之后直接跳到
        //   下一枪，中间那次绕过规则的发送无迹可寻——规格 §19.1 的「静默的错误
        //   数据」，具体形态就是这个。
        // Force Send is the one action that emits something already known to be questionable, and
        // therefore the output most in need of a record. Unrecorded, the trail shows a failure that
        // emitted nothing followed by the next scan, with the rule-bypassing emission between them
        // leaving no trace — the concrete shape of spec §19.1's silently wrong data.
        Assert.Collection(
            outcomes,
            first => Assert.IsType<ScanOutcome.ValidationFailed>(first),
            second => Assert.Equal(RawCode, Assert.IsType<ScanOutcome.ForceSent>(second).RawCode));
    }

    [Fact] // SP17
    public void Cancel_is_recorded_as_its_own_outcome()
    {
        var outcomes = new List<ScanOutcome>();
        _modeManager.SetMode(ScanMode.Sku);
        var processor = CreateProcessor(
            validator: SkuValidatorFactory.Create(new SkuValidationSettings
            {
                MinimumLength = 99,
            }));
        processor.ScanProcessed += (_, args) => outcomes.Add(args.Outcome);

        processor.OnScanReceived(RawCode);
        Assert.True(processor.Cancel());

        // 取消意味着一枪数据没有进系统。工人多半会重扫，但如果他没有，那件货就漏了；
        // 事后能看出「这里有一枪被丢掉了」，比看不出来强得多。
        // A cancellation means one scan never reached the system. The operator usually rescans, but
        // if they do not the item is simply missed, and seeing that afterwards beats not seeing it.
        Assert.Collection(
            outcomes,
            first => Assert.IsType<ScanOutcome.ValidationFailed>(first),
            second => Assert.Equal(RawCode, Assert.IsType<ScanOutcome.Cancelled>(second).RawCode));

        Assert.True(_output.EmittedNothing);
    }

    /// <summary>
    /// 中文：一旦被调用就抛异常的解析器。用来证明某条路径**根本没走到解析**。
    /// English: A parser that throws if called, used to prove a path never reaches parsing.
    /// </summary>
    private sealed class ThrowingSkuParser : ISkuParser
    {
        public ParseResult Parse(string rawCode)
            => throw new InvalidOperationException(
                "这条路径不应当调用解析器。 This path must not consult the parser.");
    }
}
