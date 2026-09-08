// =============================================================================
// FixedPositionSkuParser.cs
//
// 中文：
//   按固定位置从原始条码中截取 SKU（规格 §8.1）。
//
//   例：
//     原始码  ABCD12345678XYZ
//     起点    5     （面向用户，1-based）
//     长度    8
//     SKU     12345678
//
//   两处容易出错的地方，都在本文件里被显式处理：
//
//   1. 位置是 1-based，C# 的 Substring 是 0-based。转换只发生在一个地方
//      （ZeroBasedStartIndex），不在多处重复做减一，避免改了一处漏一处。
//
//   2. 越界判断不能写成 "起点 + 长度 > 串长"。起点接近 int.MaxValue 时相加
//      会溢出为负数，判断反而通过，随后 Substring 抛出异常——违反 ISkuParser
//      "对任何非 null 输入都不抛异常" 的契约。改用 "起点 > 串长 - 长度"，
//      两侧均不可能溢出（长度已保证 ≥ 1，串长 ≥ 0）。测试 F17 钉住这一点。
//
//   配置错误与解析失败被严格分开（决策 D-2）：
//     - 起点 < 1 或长度 < 1 与扫到什么码无关，是纯粹的配置错误，在构造时
//       抛异常。工人在设置页点保存的那一刻就该被拦住，而不是等到扫码时
//       才发现，更不该让错误伪装成"这个条码有问题"。
//     - 规则位置超出实际条码长度则取决于扫到了什么，属于运行期的解析失败，
//       返回 ParseResult.Failure。
//
// English:
//   Extracts an SKU at a fixed position (spec §8.1).
//
//   Two error-prone points, both handled explicitly here:
//
//   1. Positions are 1-based while Substring is 0-based. The conversion happens
//      in exactly one place (ZeroBasedStartIndex) rather than being repeated.
//
//   2. The bounds check must not be "start + length > text length". With a start
//      near int.MaxValue the addition overflows negative, the check passes, and
//      Substring throws — violating ISkuParser's "never throw for non-null input".
//      "start > textLength - length" cannot overflow on either side (length is
//      guaranteed >= 1, text length >= 0). Test F17 pins this down.
//
//   Configuration errors and parse failures are strictly separated (decision D-2):
//     - Start < 1 or Length < 1 is independent of what was scanned; it throws at
//       construction so the Settings page catches it on save, rather than letting
//       it masquerade later as "this barcode is bad".
//     - A rule pointing past the actual code's end depends on what was scanned and
//       is a runtime parse failure returning ParseResult.Failure.
//
// 包含的成员 / Members in this file:
//   StartPosition        起始位置（1-based）
//   Length               截取长度
//   ZeroBasedStartIndex  转换为 0-based 的起始下标
//   Parse                执行截取
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Parsing;

/// <summary>
/// 中文：固定位置 SKU 解析器。构造后不可变，可安全复用。
/// English: Fixed-position SKU parser. Immutable after construction and safe to
///          reuse.
/// </summary>
public sealed class FixedPositionSkuParser : ISkuParser
{
    /// <summary>
    /// 中文：
    ///   构造解析器并校验配置。
    ///   输入：startPosition 起始位置（1-based，必须 ≥ 1）；
    ///         length 截取长度（必须 ≥ 1）。
    ///   输出：解析器实例。
    ///   步骤：
    ///     1. 校验起始位置 ≥ 1，否则抛 ArgumentOutOfRangeException；
    ///     2. 校验长度 ≥ 1，否则抛 ArgumentOutOfRangeException；
    ///     3. 保存配置。
    ///
    ///   在此处校验而非在 Parse 中校验：这两项与扫到什么码无关，属于配置
    ///   错误（决策 D-2）。用 ArgumentOutOfRangeException 而非自定义异常，
    ///   是因为它正是 .NET 中"参数取值超出允许范围"的标准表达，调用方
    ///   无需了解本项目的异常体系。
    ///
    /// English:
    ///   Creates the parser and validates its configuration.
    ///   Steps: (1) require startPosition >= 1; (2) require length >= 1; (3) store.
    ///   Validated here rather than in Parse because neither depends on what was
    ///   scanned (decision D-2). ArgumentOutOfRangeException is the standard .NET
    ///   expression of "argument outside the allowed range", so callers need no
    ///   knowledge of a project-specific exception hierarchy.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：起始位置 &lt; 1 或长度 &lt; 1 时抛出。
    /// English: Thrown when startPosition &lt; 1 or length &lt; 1.
    /// </exception>
    public FixedPositionSkuParser(int startPosition, int length)
    {
        // 步骤 1 / Step 1
        if (startPosition < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startPosition), startPosition,
                "起始位置是面向用户的 1-based 值，必须大于等于 1。"
                + " Start position is user-facing and 1-based; it must be >= 1.");
        }

        // 步骤 2 / Step 2
        if (length < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                "截取长度必须大于等于 1。 Length must be >= 1.");
        }

        // 步骤 3 / Step 3
        StartPosition = startPosition;
        Length = length;
    }

    /// <summary>
    /// 中文：起始位置，面向用户的 1-based 值（规格 §8.1）。
    /// English: Start position, user-facing and 1-based (spec §8.1).
    /// </summary>
    public int StartPosition { get; }

    /// <summary>
    /// 中文：截取长度。
    /// English: Number of characters to take.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// 中文：起始位置对应的 0-based 下标。1-based 到 0-based 的转换只在这里
    ///       发生一次，避免在多处重复减一而漏改其中之一。
    /// English: The 0-based index for StartPosition. The 1-based-to-0-based
    ///          conversion happens here and nowhere else.
    /// </summary>
    private int ZeroBasedStartIndex => StartPosition - 1;

    /// <summary>
    /// 中文：
    ///   按固定位置截取 SKU。
    ///   输入：rawCode 原始条码，不得为 null。
    ///   输出：Success 携带截取结果，或 Failure 携带原因码与原始码。
    ///   步骤：
    ///     1. rawCode 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. rawCode 为空串时返回 EmptyInput 失败；
    ///     3. 以不会溢出的方式判断越界，越界则返回 OutOfBounds 失败；
    ///     4. 截取并返回成功。
    ///
    ///   步骤 2 必须先于步骤 3。空串对任何起始位置在数学上都是越界的，
    ///   返回 OutOfBounds 也说得通，但两者指向完全不同的排查动作：
    ///   EmptyInput 说明扫码枪什么都没送来，要查硬件或捕获链路；
    ///   OutOfBounds 说明规则位置超出条码长度，要改配置。诊断日志必须
    ///   能区分（规格 §15）。测试 F8 钉住这个顺序。
    ///
    ///   步骤 3 写成 "起点下标 > 串长 - 长度" 而非 "起点下标 + 长度 > 串长"：
    ///   后者在起点接近 int.MaxValue 时会溢出为负数，使越界条码被误判为
    ///   合法，随后 Substring 抛异常。测试 F17 钉住这一点。
    ///
    /// English:
    ///   Steps: (1) throw on null; (2) EmptyInput on ""; (3) overflow-safe bounds
    ///   check; (4) take the substring.
    ///
    ///   Step 2 must precede step 3. An empty string is mathematically out of
    ///   bounds for any position, but the two point at different corrective
    ///   actions — EmptyInput means the scanner sent nothing (check hardware),
    ///   OutOfBounds means the rule overshoots (check configuration). The
    ///   diagnostic log must distinguish them (spec §15). Test F8 pins the order.
    ///
    ///   Step 3 is written as "startIndex > textLength - length" rather than
    ///   "startIndex + length > textLength": the latter overflows negative for a
    ///   start near int.MaxValue, admitting an out-of-range rule and letting
    ///   Substring throw. Test F17 pins this down.
    /// </summary>
    public ParseResult Parse(string rawCode)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(rawCode);

        // 步骤 2 / Step 2
        if (rawCode.Length == 0)
        {
            return new ParseResult.Failure(ParseFailureReason.EmptyInput, rawCode);
        }

        // 步骤 3 / Step 3 —— 两侧均不可能溢出 / neither side can overflow
        if (ZeroBasedStartIndex > rawCode.Length - Length)
        {
            return new ParseResult.Failure(ParseFailureReason.OutOfBounds, rawCode);
        }

        // 步骤 4 / Step 4
        return new ParseResult.Success(rawCode.Substring(ZeroBasedStartIndex, Length));
    }
}
