// =============================================================================
// FixedPositionSkuParserTests.cs
//
// 中文：
//   固定位置 SKU 解析器的行为测试（对应测试清单 F1~F16）。
//
//   规则很简单：给定起始位置和长度，从原始条码中截取一段作为 SKU。
//   复杂度全在边界和失败路径上，因此本文件的大部分篇幅都在测那些地方。
//
//   三条已决策的行为在此固化（见测试计划 §2）：
//     D-1  Parse(null) 抛 ArgumentNullException；空串 "" 走结构化失败。
//          null 是程序缺陷，不是预期的坏输入，把两者混为一谈会让真 bug
//          伪装成一次寻常的扫描失败。
//     D-2  起始位置 < 1 或长度 < 1 属于配置错误，在**构造时**就拒绝，
//          而不是每次解析时返回失败。这类错误与扫到什么码无关，必须在
//          工人点"保存设置"时就被拦下，而不是等到他扫码才报错。
//     D-9  原始码首尾空白**不做 trim**。扫码枪本不该发出空白，发了就是
//          异常，应当被校验层暴露，而不是被解析器悄悄抹掉。
//
//   一个刻意的判定顺序：空串检查先于越界检查。空串配任何起始位置在数学上
//   都是越界的，但对工人来说"没扫到内容"和"规则位置超出条码长度"是两种
//   完全不同的故障，诊断日志里必须能区分（见 F8）。
//
// English:
//   Behavior tests for the fixed-position SKU parser (test plan F1–F16).
//
//   The rule is simple: take a substring at a given 1-based position and length.
//   All the complexity lives in the boundaries and failure paths, which is where
//   most of this file goes.
//
//   Three resolved decisions are pinned here (test plan §2):
//     D-1  Parse(null) throws; "" returns a structured failure. null is a defect,
//          not expected bad input; conflating them lets a real bug masquerade as
//          a routine scan failure.
//     D-2  Start < 1 or Length < 1 is a configuration error rejected at
//          construction, not per-parse. It is independent of what was scanned and
//          must be caught when the operator saves Settings.
//     D-9  Surrounding whitespace is never trimmed. A scanner should not emit it;
//          if it does, validation must surface it rather than the parser hiding it.
//
//   One deliberate ordering: the empty-input check precedes the bounds check.
//   An empty string is mathematically out of bounds for any position, but
//   "nothing was scanned" and "the rule points past the end of the code" are
//   different faults to an operator and must be distinguishable in the logs (F8).
//
// 包含的测试 / Tests in this file:
//   Extracts_substring_at_one_based_position   F1  F2  F3  F4  F13  F14
//   Out_of_range_position_fails_with_OutOfBounds   F5  F6  F7
//   Empty_raw_code_fails_with_EmptyInput       F8
//   Invalid_configuration_is_rejected_at_construction   F9  F10  F11  F12
//   Never_throws_for_any_non_null_raw_code     F15
//   Null_raw_code_throws                       F16
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;

namespace ScannerHelper.Core.Tests.Parsing;

public class FixedPositionSkuParserTests
{
    /// <summary>
    /// 中文：
    ///   F1~F4、F13、F14 — 在合法范围内按 1-based 位置正确截取。
    ///   输入：caseId 用例编号（用于失败信息定位）；rawCode 原始条码；
    ///         startPosition 起始位置（1-based）；length 截取长度；
    ///         expectedSku 期望结果。
    ///   输出：无（断言）。
    ///
    ///   覆盖点：
    ///     F1   规格 §8.1 的原例，起点在中间
    ///     F2   截取整串
    ///     F3   首字符
    ///     F4   末字符（起点恰好等于串长）
    ///     F13  条码内含空格，空格按普通字符处理
    ///     F14  条码首尾有空白，不做 trim（决策 D-9）
    ///
    ///   F3 与 F4 是 off-by-one 的两个方向。位置是**面向用户的 1-based**，
    ///   而 C# 的 Substring 是 0-based，这里正是最容易写错的地方，因此两端
    ///   都必须钉住。
    ///
    /// English:
    ///   F1–F4, F13, F14 — extracts correctly at a 1-based position within range.
    ///   F3 and F4 pin both directions of the off-by-one: user-facing positions
    ///   are 1-based while Substring is 0-based, which is exactly where this goes
    ///   wrong, so both ends are nailed down.
    /// </summary>
    [Theory]
    [InlineData("F1", "ABCD12345678XYZ", 5, 8, "12345678")]
    [InlineData("F2", "ABCDEF", 1, 6, "ABCDEF")]
    [InlineData("F3", "ABCDEF", 1, 1, "A")]
    [InlineData("F4", "ABCDEF", 6, 1, "F")]
    [InlineData("F13", "AB CD", 1, 5, "AB CD")]
    [InlineData("F14", "  ABCDEF  ", 1, 3, "  A")]
    public void Extracts_substring_at_one_based_position(
        string caseId, string rawCode, int startPosition, int length, string expectedSku)
    {
        var parser = new FixedPositionSkuParser(startPosition, length);

        var result = parser.Parse(rawCode);

        var success = Assert.IsType<ParseResult.Success>(result);
        Assert.True(success.Sku == expectedSku,
            $"{caseId}: 起点 {startPosition} 长度 {length} 作用于 \"{rawCode}\""
            + $" 应得到 \"{expectedSku}\"，实际为 \"{success.Sku}\"");
    }

    /// <summary>
    /// 中文：
    ///   F5~F7 — 规则位置超出条码长度时返回 OutOfBounds 失败。
    ///   输入：caseId；rawCode；startPosition；length。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     F5   起点刚好越过末尾（长度 6 的串，起点 7）
    ///     F6   起点合法但长度溢出（起点 6，还要取 2 个字符）
    ///     F7   起点远超串长
    ///     F17  起点为 int.MaxValue——防止越界检查本身发生整数溢出
    ///
    ///   F17 针对一个具体的实现陷阱：若用 "起点 + 长度 > 串长" 来判越界，
    ///   起点接近 int.MaxValue 时相加会溢出为负数，判断反而通过，随后
    ///   Substring 抛出异常——违反 F15"任何非 null 输入都不抛异常"。
    ///   正确写法是 "起点 > 串长 - 长度"，两侧均不可能溢出。
    ///
    ///   同时断言失败结果**保留了原始码**。规格 §10 要求 F10 强制发送时
    ///   能原样发出扫到的码，且错误界面要展示它——若失败结果丢掉原始码，
    ///   这两件事都做不到。
    ///
    /// English:
    ///   F5–F7 — returns an OutOfBounds failure when the rule points past the end.
    ///   Also asserts the failure retains the raw code: spec §10 requires Force
    ///   Send to emit exactly what was scanned and the error UI to display it,
    ///   neither of which is possible if the failure discards it.
    /// </summary>
    [Theory]
    [InlineData("F5", "ABCDEF", 7, 1)]
    [InlineData("F6", "ABCDEF", 6, 2)]
    [InlineData("F7", "ABCDEF", 100, 1)]
    [InlineData("F17", "ABCDEF", int.MaxValue, 10)]
    public void Out_of_range_position_fails_with_OutOfBounds(
        string caseId, string rawCode, int startPosition, int length)
    {
        var parser = new FixedPositionSkuParser(startPosition, length);

        var result = parser.Parse(rawCode);

        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.True(failure.Reason == ParseFailureReason.OutOfBounds,
            $"{caseId}: 起点 {startPosition} 长度 {length} 作用于 \"{rawCode}\""
            + $" 应为 OutOfBounds，实际为 {failure.Reason}");
        Assert.Equal(rawCode, failure.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   F8 — 空原始码返回 EmptyInput，而不是 OutOfBounds。
    ///   输入：无。输出：无（断言）。
    ///
    ///   为什么要区分：空串对任何起始位置都是越界的，返回 OutOfBounds 在
    ///   数学上也说得通。但对现场排查而言，"扫码枪什么都没送来"和"解析规则
    ///   配错了、位置超出条码长度"是两种完全不同的故障，前者要查硬件，
    ///   后者要改配置。诊断日志必须能区分（规格 §15）。
    ///
    /// English:
    ///   F8 — an empty raw code fails with EmptyInput, not OutOfBounds.
    ///   The distinction matters on site: "the scanner sent nothing" points at
    ///   hardware, "the rule points past the end" points at configuration. The
    ///   diagnostic log must tell them apart (spec §15).
    /// </summary>
    [Fact]
    public void Empty_raw_code_fails_with_EmptyInput()
    {
        var parser = new FixedPositionSkuParser(startPosition: 1, length: 1);

        var result = parser.Parse(string.Empty);

        var failure = Assert.IsType<ParseResult.Failure>(result);
        Assert.Equal(ParseFailureReason.EmptyInput, failure.Reason);
    }

    /// <summary>
    /// 中文：
    ///   F9~F12 — 非法配置在构造时即被拒绝（决策 D-2）。
    ///   输入：caseId；startPosition；length。输出：无（断言构造抛异常）。
    ///
    ///   覆盖点：
    ///     F9   起点 0——1-based 语义下 0 无意义，最常见的误填
    ///     F10  起点为负
    ///     F11  长度 0——截取零个字符没有意义
    ///     F12  长度为负
    ///
    ///   为什么在构造时而不是解析时：这类错误与扫到什么码毫无关系，是纯粹的
    ///   配置错误。若留到解析时才返回失败，工人要先扫一次码才会知道规则配错，
    ///   而且错误会伪装成"这个条码有问题"。放在构造时，设置页保存的那一刻
    ///   就能拦住。
    ///
    ///   选用 ArgumentOutOfRangeException 而非自定义异常：这是 .NET 中表达
    ///   "参数取值超出允许范围"的标准类型，调用方无需了解本项目的异常体系。
    ///
    /// English:
    ///   F9–F12 — invalid configuration is rejected at construction (D-2).
    ///   These errors are independent of what was scanned. Deferring them to
    ///   parse time would mean the operator must scan once to discover the rule
    ///   is wrong, and the error would masquerade as "this barcode is bad".
    ///   Rejecting at construction catches it the moment Settings is saved.
    /// </summary>
    [Theory]
    [InlineData("F9", 0, 3)]
    [InlineData("F10", -1, 3)]
    [InlineData("F11", 3, 0)]
    [InlineData("F12", 3, -1)]
    public void Invalid_configuration_is_rejected_at_construction(
        string caseId, int startPosition, int length)
    {
        var exception = Record.Exception(
            () => new FixedPositionSkuParser(startPosition, length));

        Assert.True(exception is ArgumentOutOfRangeException,
            $"{caseId}: 起点 {startPosition} 长度 {length} 属非法配置，构造时应抛"
            + $" ArgumentOutOfRangeException，实际为 {exception?.GetType().Name ?? "未抛异常"}");
    }

    /// <summary>
    /// 中文：
    ///   F15 — 对任何非 null 的原始码都不抛异常。
    ///   输入：无。输出：无（断言）。
    ///   实现：用一组刁钻输入逐个调用 Parse，只要求"不抛异常"，不关心
    ///   返回成功还是失败。
    ///
    ///   为什么这条重要：Parse 的调用点在扫描处理链路上，规格 §19 要求任何
    ///   无法确信处理的事件都必须安全失败并被诊断出来，而不是让异常冒泡。
    ///   一次未捕获的异常在这条链路上意味着工人扫了码却毫无反应。
    ///   预期的坏输入必须表现为结构化失败（ParseResult.Failure），而不是异常。
    ///
    /// English:
    ///   F15 — never throws for any non-null raw code.
    ///   Parse sits on the scan-handling path, where spec §19 requires anything
    ///   unresolvable to fail safely and be surfaced diagnostically rather than
    ///   letting an exception propagate. An uncaught exception here means the
    ///   worker scans and nothing happens at all. Expected bad input must appear
    ///   as ParseResult.Failure, never as an exception.
    /// </summary>
    [Fact]
    public void Never_throws_for_any_non_null_raw_code()
    {
        var parser = new FixedPositionSkuParser(startPosition: 5, length: 8);

        string[] adversarialRawCodes =
        [
            string.Empty,
            " ",
            "A",
            "ABCD",
            "ABCD12345678XYZ",
            "中文条码内容",
            "\t\n\r",
            new string('X', 10_000),
        ];

        foreach (var rawCode in adversarialRawCodes)
        {
            var exception = Record.Exception(() => parser.Parse(rawCode));
            Assert.True(exception is null,
                $"Parse 不得对非 null 输入抛异常，但输入 \"{rawCode[..Math.Min(rawCode.Length, 20)]}\""
                + $" 抛出了 {exception?.GetType().Name}");
        }
    }

    /// <summary>
    /// 中文：
    ///   F16 — Parse(null) 抛 ArgumentNullException（决策 D-1）。
    ///   输入：无。输出：无（断言抛异常）。
    ///
    ///   null 与空串被刻意区别对待。空串是"扫码枪送来了空内容"，属于预期的
    ///   坏输入，走结构化失败；null 只可能是调用方代码有缺陷，把它也吞成
    ///   一次普通的解析失败，会让真正的 bug 伪装成一次寻常的扫描异常，
    ///   在日志里与成千上万条正常失败混在一起，再也找不出来。
    ///
    /// English:
    ///   F16 — Parse(null) throws ArgumentNullException (D-1).
    ///   null and "" are deliberately treated differently. "" means the scanner
    ///   delivered empty content — expected bad input, structured failure. null
    ///   can only mean caller-side defect; swallowing it as an ordinary parse
    ///   failure would bury a real bug among thousands of legitimate failures in
    ///   the log where it can never be found again.
    /// </summary>
    [Fact]
    public void Null_raw_code_throws()
    {
        var parser = new FixedPositionSkuParser(startPosition: 1, length: 1);

        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
    }
}
