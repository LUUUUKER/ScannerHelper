// =============================================================================
// RegexSkuParserTests.cs
//
// 中文：
//   正则 SKU 解析器的行为测试（对应测试清单 R1~R13）。
//
//   本文件的重点不在"能不能匹配"，而在**失败路径能否被区分**。规格 §8.2
//   列出了四种不同的失败，它们指向完全不同的处理动作：
//     NoMatch              规则与这个条码不符——可能是规则写错，也可能扫错了码
//     MissingCaptureGroup  捕获组索引配错——规则本身有匹配，但取不到那一组
//     RegexTimeout         正则太慢被超时中断——规则写法有问题，需要改写
//     InvalidPattern       正则语法错误——构造时即拒绝，不进入运行期
//
//   其中 NoMatch 与 RegexTimeout 的区分尤其关键：若两者共用一个原因码，
//   现场排查时无法判断到底是"规则匹配不上"还是"规则卡住了"，而这两者的
//   修复方式完全不同。R8 钉住这一点。
//
//   三条已决策的行为在此固化：
//     D-3  捕获组索引 0 合法，表示整个匹配（R2）。
//     D-4  捕获组匹配到空串算**解析成功**（R6）。规格 §9 明确解析与校验分层，
//          解析只负责提取，不负责判断提取结果是否合理——那是校验层的事。
//     D-1  null 抛异常，空串走结构化失败（R11、R13）。
//
// English:
//   Behavior tests for the regex SKU parser (test plan R1–R13).
//
//   The emphasis is not on matching but on whether failures are *distinguishable*.
//   Spec §8.2 lists four distinct failures pointing at different corrective
//   actions: NoMatch (rule does not fit this code), MissingCaptureGroup (the
//   group index is wrong), RegexTimeout (the pattern is too slow and must be
//   rewritten), InvalidPattern (syntax error, rejected at construction).
//
//   Separating NoMatch from RegexTimeout matters most: sharing one code would
//   make it impossible on site to tell "the rule does not match" from "the rule
//   hung", and those have entirely different fixes. R8 pins this down.
//
// 包含的测试 / Tests in this file:
//   Extracts_configured_capture_group                       R1  R2  R6  R10
//   Non_matching_pattern_fails_with_NoMatch                 R3
//   Absent_capture_group_fails_with_MissingCaptureGroup     R4  R5
//   Invalid_pattern_is_rejected_at_construction             R7
//   Negative_capture_group_index_is_rejected_at_construction R12
//   Catastrophic_backtracking_fails_with_RegexTimeout       R8
//   Match_timeout_is_finite                                 R9
//   Empty_raw_code_fails_with_EmptyInput                    R13
//   Null_raw_code_throws                                    R11
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;

namespace ScannerHelper.Core.Tests.Parsing;

public class RegexSkuParserTests
{
    /// <summary>
    /// 中文：
    ///   R1、R2、R6、R10 — 按配置的捕获组索引正确提取。
    ///   输入：caseId 用例编号；pattern 正则；captureGroupIndex 捕获组索引；
    ///         rawCode 原始条码；expectedSku 期望结果。
    ///   输出：无（断言）。
    ///
    ///   覆盖点：
    ///     R1   规格 §8.2 的原例，取第 1 组
    ///     R2   索引 0，表示整个匹配（决策 D-3）
    ///     R6   捕获组匹配到空串，仍算成功（决策 D-4）
    ///     R10  多个捕获组时取第 2 组
    ///
    ///   R6 是这一组里唯一需要解释的：模式 (a*) 作用于 "b" 时，正则在位置 0
    ///   匹配到空串，捕获组存在且参与了匹配，只是内容为空。这与 R5"捕获组
    ///   根本没参与匹配"是两种不同情形，实现必须靠 Group.Success 区分，
    ///   而不能靠"值是不是空字符串"来判断——后者会把 R6 误判为失败。
    ///
    /// English:
    ///   R1, R2, R6, R10 — extracts the configured capture group.
    ///   R6 is the one needing explanation: (a*) against "b" matches the empty
    ///   string at position 0; the group participated but captured nothing. That
    ///   differs from R5, where the group did not participate at all. The
    ///   implementation must distinguish them via Group.Success, not by testing
    ///   whether the value is empty — the latter would misreport R6 as a failure.
    /// </summary>
    [Theory]
    [InlineData("R1", @"^.{4}([A-Z0-9]{8})", 1, "ABCD12345678XYZ", "12345678")]
    [InlineData("R2", @"^.{4}([A-Z0-9]{8})", 0, "ABCD12345678XYZ", "ABCD12345678")]
    [InlineData("R6", @"(a*)", 1, "b", "")]
    [InlineData("R10", @"^(\w{4})(\d{8})", 2, "ABCD12345678XYZ", "12345678")]
    public void Extracts_configured_capture_group(
        string caseId, string pattern, int captureGroupIndex, string rawCode, string expectedSku)
    {
        var parser = new RegexSkuParser(pattern, captureGroupIndex);

        var result = parser.Parse(rawCode);

        var success = Assert.IsType<ParseResult.Success>(result);
        Assert.True(success.Sku == expectedSku,
            $"{caseId}: 模式 {pattern} 组 {captureGroupIndex} 作用于 \"{rawCode}\""
            + $" 应得到 \"{expectedSku}\"，实际为 \"{success.Sku}\"");
    }

    /// <summary>
    /// 中文：
    ///   R3 — 正则完全匹配不上时返回 NoMatch 失败，并保留原始条码。
    ///   输入：无。输出：无（断言）。
    ///   保留原始码是为了支持 F10 强制发送与错误界面展示（规格 §10）。
    /// English:
    ///   R3 — returns a NoMatch failure and retains the raw code, which Force Send
    ///   and the error UI both need (spec §10).
    /// </summary>
    [Fact]
    public void Non_matching_pattern_fails_with_NoMatch()
    {
        var parser = new RegexSkuParser(@"^\d+$", captureGroupIndex: 0);

        var result = parser.Parse("ABC");

        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.Equal(ParseFailureReason.NoMatch, failure.Reason);
        Assert.Equal("ABC", failure.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   R4、R5 — 捕获组取不到时返回 MissingCaptureGroup。
    ///   输入：caseId；pattern；captureGroupIndex；rawCode。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     R4  索引超出模式中实际存在的组数——最常见的配置笔误
    ///     R5  组存在但本次未参与匹配（可选组 (a)? 未命中）
    ///
    ///   两者都必须与 NoMatch 区分开：整体是匹配上了的，只是取不到指定的
    ///   那一组。若混同为 NoMatch，工人会以为是条码不对，而实际要改的是
    ///   捕获组索引这一项配置。
    ///
    /// English:
    ///   R4, R5 — MissingCaptureGroup when the group cannot be read.
    ///   Both must stay distinct from NoMatch: the pattern *did* match; only the
    ///   requested group is unavailable. Conflating them would send the operator
    ///   looking at the barcode when the fix is the capture-group index setting.
    /// </summary>
    [Theory]
    [InlineData("R4", @"^([A-Z]+)", 2, "ABC")]
    [InlineData("R5", @"^(?:x)(a)?", 1, "x")]
    public void Absent_capture_group_fails_with_MissingCaptureGroup(
        string caseId, string pattern, int captureGroupIndex, string rawCode)
    {
        var parser = new RegexSkuParser(pattern, captureGroupIndex);

        var result = parser.Parse(rawCode);

        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.True(failure.Reason == ParseFailureReason.MissingCaptureGroup,
            $"{caseId}: 模式 {pattern} 组 {captureGroupIndex} 作用于 \"{rawCode}\""
            + $" 应为 MissingCaptureGroup，实际为 {failure.Reason}");
    }

    /// <summary>
    /// 中文：
    ///   R7 — 正则语法错误在构造时即被拒绝，且绝不抛出未包装的正则异常。
    ///   输入：无。输出：无（断言抛 ArgumentException）。
    ///
    ///   与固定位置解析器的 F9~F12 保持一致：与扫到什么码无关的配置错误，
    ///   一律在构造时拒绝（决策 D-2），使设置页在保存那一刻就能拦住。
    ///
    ///   实现需把 .NET 的 RegexParseException 包装为 ArgumentException：
    ///   前者是 ArgumentException 的派生类型，直接放行会让调用方必须了解
    ///   正则库的异常体系。包装后原异常作为 InnerException 保留，语法错误
    ///   的具体位置信息不丢失。
    ///
    /// English:
    ///   R7 — an invalid pattern is rejected at construction and never surfaces a
    ///   raw regex exception. Consistent with F9–F12: configuration errors
    ///   independent of the scanned code are rejected at construction (D-2) so
    ///   Settings catches them on save.
    ///   The implementation wraps .NET's RegexParseException as ArgumentException,
    ///   keeping the original as InnerException so the syntax-error detail
    ///   survives while callers need no knowledge of the regex library's hierarchy.
    /// </summary>
    [Fact]
    public void Invalid_pattern_is_rejected_at_construction()
    {
        var exception = Record.Exception(
            () => new RegexSkuParser("[", captureGroupIndex: 0));

        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.NotNull(argumentException.InnerException);
    }

    /// <summary>
    /// 中文：
    ///   R12 — 负数捕获组索引在构造时即被拒绝。
    ///   输入：无。输出：无（断言抛 ArgumentOutOfRangeException）。
    ///
    ///   本条为计划外补充。捕获组索引是设置页里的一个数字输入框，负数与
    ///   F9 的"起始位置填了 0"属于同一类误填。若不在构造时拦下，运行期
    ///   取组时的行为将依赖 .NET 内部实现，难以给出确定的失败原因码。
    ///
    /// English:
    ///   R12 — a negative capture-group index is rejected at construction.
    ///   Added beyond the plan. The index is a numeric field in Settings, and a
    ///   negative value is the same class of typo as F9's zero start position.
    ///   Left unchecked, the runtime behavior would depend on .NET internals,
    ///   making a definite failure reason hard to guarantee.
    /// </summary>
    [Fact]
    public void Negative_capture_group_index_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RegexSkuParser(@"^(\d+)", captureGroupIndex: -1));
    }

    /// <summary>
    /// 中文：
    ///   R8 — 灾难性回溯被超时中断，返回 RegexTimeout，且原因码独立于 NoMatch。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 构造一个已知会灾难性回溯的模式 ^(a+)+$；
    ///     2. 用一段能触发指数级回溯的输入（大量 a 后接一个 X）调用 Parse；
    ///     3. 断言返回 RegexTimeout 失败，而不是 NoMatch，更不是抛异常。
    ///
    ///   这是本文件最重要的一条。^(a+)+$ 对 "aaa...aX" 的匹配复杂度是指数级的，
    ///   不设超时会让线程长时间占满 CPU。规格 §19 要求正则必须使用有限超时，
    ///   §8.2 进一步要求超时表现为一个**独立的**失败原因。
    ///
    ///   为什么必须与 NoMatch 分开：两者的修复动作完全不同。NoMatch 意味着
    ///   规则与条码不符，要检查规则或条码；RegexTimeout 意味着规则本身写法
    ///   有问题、需要重写。共用一个码会让现场无从判断。
    ///
    /// English:
    ///   R8 — catastrophic backtracking is cut short by the timeout and reported
    ///   as RegexTimeout, with a reason code distinct from NoMatch.
    ///   ^(a+)+$ against "aaa...aX" is exponential; without a timeout it pins a
    ///   CPU. Spec §19 requires a finite timeout and §8.2 requires the timeout to
    ///   appear as a *separate* failure reason, because the fixes differ entirely:
    ///   NoMatch means the rule does not fit the code; RegexTimeout means the rule
    ///   itself must be rewritten.
    /// </summary>
    [Fact]
    public void Catastrophic_backtracking_fails_with_RegexTimeout()
    {
        // 步骤 1 / Step 1
        var parser = new RegexSkuParser(@"^(a+)+$", captureGroupIndex: 1);

        // 步骤 2 / Step 2
        var pathologicalRawCode = new string('a', 40) + "X";
        var result = parser.Parse(pathologicalRawCode);

        // 步骤 3 / Step 3
        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.Equal(ParseFailureReason.RegexTimeout, failure.Reason);
        Assert.NotEqual(ParseFailureReason.NoMatch, failure.Reason);
    }

    /// <summary>
    /// 中文：
    ///   R9 — 匹配超时必须是有限值。
    ///   输入：无。输出：无（断言）。
    ///   规格 §8.2 与 §19 都要求正则使用有限超时。这条直接断言解析器暴露的
    ///   MatchTimeout 不是 Regex.InfiniteMatchTimeout，防止将来有人为了"让
    ///   复杂规则也能跑通"而把超时关掉——那会让一个写坏的规则直接冻结整条
    ///   扫描处理链路。
    /// English:
    ///   R9 — the match timeout must be finite (spec §8.2, §19). This guards
    ///   against someone later disabling it to "make a complex rule work", which
    ///   would let one bad rule freeze the entire scan-handling path.
    /// </summary>
    [Fact]
    public void Match_timeout_is_finite()
    {
        var parser = new RegexSkuParser(@"^(\d+)", captureGroupIndex: 1);

        Assert.NotEqual(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout,
            parser.MatchTimeout);
        Assert.True(parser.MatchTimeout > TimeSpan.Zero);
    }

    /// <summary>
    /// 中文：
    ///   R13 — 空原始码返回 EmptyInput，与固定位置解析器保持一致。
    ///   输入：无。输出：无（断言）。
    ///
    ///   本条为计划外补充，目的是让两种解析器对同一种故障给出同一个原因码。
    ///   空扫描意味着扫码枪或捕获链路没送出内容，这与解析规则无关，无论用
    ///   哪种解析方式都应指向同一个排查方向（查硬件）。若正则解析器把空串
    ///   交给正则去跑，结果会是 NoMatch，工人便会被误导去检查规则。
    ///
    /// English:
    ///   R13 — an empty raw code fails with EmptyInput, matching the fixed-position
    ///   parser. Added beyond the plan so both parsers report the same fault the
    ///   same way. An empty scan means the scanner or capture path delivered
    ///   nothing — unrelated to the rule. Letting the regex run on "" would yield
    ///   NoMatch and misdirect the operator toward the rule.
    /// </summary>
    [Fact]
    public void Empty_raw_code_fails_with_EmptyInput()
    {
        var parser = new RegexSkuParser(@"^(\d*)", captureGroupIndex: 1);

        var result = parser.Parse(string.Empty);

        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.Equal(ParseFailureReason.EmptyInput, failure.Reason);
    }

    /// <summary>
    /// 中文：R11 — Parse(null) 抛 ArgumentNullException（决策 D-1），
    ///       与 FixedPositionSkuParser 的 F16 保持一致的契约。
    /// English: R11 — Parse(null) throws (D-1), matching F16's contract on the
    ///          fixed-position parser.
    /// </summary>
    [Fact]
    public void Null_raw_code_throws()
    {
        var parser = new RegexSkuParser(@"^(\d+)", captureGroupIndex: 1);

        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
    }
}
