// =============================================================================
// RegexSkuParser.cs
//
// 中文：
//   用正则表达式从原始条码中提取 SKU（规格 §8.2）。
//
//   例：
//     原始码    ABCD12345678XYZ
//     模式      ^.{4}([A-Z0-9]{8})
//     捕获组    1
//     SKU       12345678
//
//   规则由技术顾问提供，工人只是粘贴进设置页。因此本类必须假定模式可能
//   写得很糟，并保证无论多糟都不会拖垮扫描链路。
//
//   四种失败被刻意分开，因为它们指向完全不同的修复动作：
//     EmptyInput           扫码枪没送出内容——查硬件或捕获链路
//     NoMatch              规则与这个条码不符——改规则内容，或确认扫对了码
//     MissingCaptureGroup  整体匹配上了，但取不到指定的组——改捕获组索引
//     RegexTimeout         规则本身写法有问题——重写规则
//
//   NoMatch 与 RegexTimeout 的区分最关键。共用一个原因码会让现场无法判断
//   到底是"规则匹配不上"还是"规则卡住了"，而前者改内容、后者改写法。
//
//   关于 RegexOptions.NonBacktracking：
//     .NET 7 起提供该选项，能从根本上消除灾难性回溯，看起来比超时更彻底。
//     这里没有采用，有两个原因：
//       1. 它不支持环视和反向引用，而技术顾问给出的规则完全可能用到；
//          为了防御一个可控的风险而限制规则的表达能力，代价不对等。
//       2. 规格 §8.2 与 §19 明确要求"正则必须使用有限超时"。超时是被写进
//          验收标准的机制，不应被悄悄替换成另一种。
//     超时方案还有一个附带好处：它同样能兜住非回溯类的慢模式。
//
// English:
//   Extracts an SKU with a regular expression (spec §8.2).
//
//   Rules come from a technical advisor and are pasted into Settings by the
//   operator, so this class must assume the pattern may be poor and guarantee
//   that no pattern can stall the scan-handling path.
//
//   Four failures are kept separate because their fixes differ entirely:
//   EmptyInput (check hardware), NoMatch (change what the rule matches),
//   MissingCaptureGroup (change the group index), RegexTimeout (rewrite the rule).
//
//   On RegexOptions.NonBacktracking: available since .NET 7 and it eliminates
//   catastrophic backtracking outright, which looks stronger than a timeout. It
//   is not used here because (1) it forbids lookarounds and backreferences that
//   an advisor's rule may legitimately need — a poor trade for defending against
//   a risk the timeout already contains, and (2) spec §8.2 and §19 mandate a
//   finite timeout, which is the mechanism written into the acceptance criteria.
//   The timeout also covers slow patterns that are not backtracking-related.
//
// 包含的成员 / Members in this file:
//   DefaultMatchTimeout  默认匹配超时
//   Pattern              正则模式
//   CaptureGroupIndex    捕获组索引
//   MatchTimeout         实际生效的匹配超时
//   Parse                执行提取
// =============================================================================

using System.Text.RegularExpressions;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Parsing;

/// <summary>
/// 中文：正则 SKU 解析器。构造后不可变，可安全复用。
/// English: Regex SKU parser. Immutable after construction and safe to reuse.
/// </summary>
public sealed class RegexSkuParser : ISkuParser
{
    /// <summary>
    /// 中文：默认匹配超时，100 毫秒。
    ///       条码通常只有几十个字符，任何写法正常的规则都在微秒级完成，
    ///       100 毫秒留了约三个数量级的余量。同时它足够短，即便某条规则
    ///       每次都超时，工人感受到的也只是一次轻微延迟，而不是卡死。
    /// English: Default match timeout, 100 ms. A barcode is a few dozen
    ///          characters and any sanely written rule finishes in microseconds,
    ///          leaving roughly three orders of magnitude of headroom. It is also
    ///          short enough that a rule timing out on every scan feels like a
    ///          slight delay rather than a freeze.
    /// </summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 中文：编译好的正则对象。超时已绑定在其上，每次 Match 调用自动生效。
    /// English: The compiled regex, with the timeout bound to it so every Match
    ///          call enforces it automatically.
    /// </summary>
    private readonly Regex _regex;

    /// <summary>
    /// 中文：
    ///   构造解析器并校验配置。
    ///   输入：pattern 正则模式，不得为 null；
    ///         captureGroupIndex 捕获组索引，必须 ≥ 0（0 表示整个匹配）；
    ///         matchTimeout 匹配超时，为 null 时取 DefaultMatchTimeout，
    ///                      必须为正的有限值。
    ///   输出：解析器实例。
    ///   步骤：
    ///     1. pattern 为 null 时抛 ArgumentNullException；
    ///     2. 捕获组索引为负时抛 ArgumentOutOfRangeException；
    ///     3. 确定并校验超时值，非正值（含 Regex.InfiniteMatchTimeout，其值为
    ///        -1 毫秒）一律拒绝；
    ///     4. 构造 Regex；语法错误时包装为 ArgumentException 后抛出；
    ///     5. 保存配置。
    ///
    ///   步骤 2、3、4 都是与"扫到什么码"无关的配置错误，按决策 D-2 一律在
    ///   构造时拒绝，使设置页在保存那一刻就能拦住，而不是等工人扫码才发现。
    ///
    ///   步骤 4 的包装：.NET 抛出的是 RegexParseException，它派生自
    ///   ArgumentException。直接放行会迫使调用方了解正则库的异常体系；
    ///   包装后原异常作为 InnerException 保留，语法错误的具体位置信息不丢失。
    ///
    /// English:
    ///   Creates the parser and validates its configuration.
    ///   Steps: (1) reject null pattern; (2) reject negative group index;
    ///   (3) resolve and validate the timeout, rejecting any non-positive value
    ///   including Regex.InfiniteMatchTimeout (which is -1 ms); (4) build the
    ///   Regex, wrapping a syntax error as ArgumentException; (5) store.
    ///
    ///   Steps 2–4 are configuration errors independent of what was scanned and
    ///   are rejected at construction per decision D-2, so Settings catches them
    ///   on save rather than the operator discovering them mid-shift.
    ///
    ///   Step 4 wraps .NET's RegexParseException — itself an ArgumentException —
    ///   so callers need no knowledge of the regex library's hierarchy, while the
    ///   original is preserved as InnerException and its position detail survives.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：pattern 为 null。 English: pattern is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：捕获组索引为负，或超时非正。
    /// English: Negative group index, or a non-positive timeout.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：正则语法错误。 English: The pattern is not valid regex syntax.
    /// </exception>
    public RegexSkuParser(string pattern, int captureGroupIndex, TimeSpan? matchTimeout = null)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(pattern);

        // 步骤 2 / Step 2
        if (captureGroupIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureGroupIndex), captureGroupIndex,
                "捕获组索引必须大于等于 0，其中 0 表示整个匹配。"
                + " Capture group index must be >= 0, where 0 means the whole match.");
        }

        // 步骤 3 / Step 3
        var resolvedTimeout = matchTimeout ?? DefaultMatchTimeout;
        if (resolvedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchTimeout), resolvedTimeout,
                "匹配超时必须是正的有限值；规格 §8.2、§19 禁止无限超时。"
                + " The match timeout must be positive and finite; spec §8.2 and"
                + " §19 forbid an infinite timeout.");
        }

        // 步骤 4 / Step 4
        try
        {
            _regex = new Regex(pattern, RegexOptions.CultureInvariant, resolvedTimeout);
        }
        catch (ArgumentException syntaxError)
        {
            throw new ArgumentException(
                $"正则语法错误，无法构造解析器。 Invalid regular expression syntax: {pattern}",
                nameof(pattern),
                syntaxError);
        }

        // 步骤 5 / Step 5
        Pattern = pattern;
        CaptureGroupIndex = captureGroupIndex;
        MatchTimeout = resolvedTimeout;
    }

    /// <summary>
    /// 中文：配置的正则模式。
    /// English: The configured pattern.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// 中文：配置的捕获组索引。0 表示整个匹配（决策 D-3）。
    /// English: The configured capture group index; 0 means the whole match (D-3).
    /// </summary>
    public int CaptureGroupIndex { get; }

    /// <summary>
    /// 中文：实际生效的匹配超时。始终为正的有限值——测试 R9 钉住这一点，
    ///       防止将来为了"让复杂规则跑通"而把超时关掉：那会让一条写坏的
    ///       规则直接冻结整条扫描处理链路。
    /// English: The effective match timeout, always positive and finite. Test R9
    ///          pins this down, guarding against someone later disabling it to
    ///          "make a complex rule work" — which would let one bad rule freeze
    ///          the entire scan-handling path.
    /// </summary>
    public TimeSpan MatchTimeout { get; }

    /// <summary>
    /// 中文：
    ///   用正则提取 SKU。
    ///   输入：rawCode 原始条码，不得为 null。
    ///   输出：Success 携带捕获组内容，或 Failure 携带原因码与原始码。
    ///   步骤：
    ///     1. rawCode 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. rawCode 为空串时返回 EmptyInput 失败；
    ///     3. 执行匹配；超时被中断则返回 RegexTimeout 失败；
    ///     4. 整体未匹配则返回 NoMatch 失败；
    ///     5. 取出配置指定的捕获组，该组未参与匹配则返回 MissingCaptureGroup；
    ///     6. 返回捕获组内容。
    ///
    ///   步骤 2 与固定位置解析器保持一致（测试 R13）。空扫描说明扫码枪或捕获
    ///   链路没送出内容，与规则无关；若把空串交给正则去跑，结果会是 NoMatch，
    ///   工人便会被误导去检查规则，而实际该查的是硬件。
    ///
    ///   步骤 5 用 Group.Success 判断，而不是判断值是否为空串。两者不等价：
    ///   模式 (a*) 作用于 "b" 时，捕获组参与了匹配但内容为空，按决策 D-4
    ///   这是**成功**；而可选组 (a)? 未命中时组根本没参与，才是失败。
    ///   用空串判断会把前者误报为失败。
    ///
    ///   本方法对任何非 null 输入都不抛异常。RegexMatchTimeoutException 是唯一
    ///   可能从匹配过程逃逸的异常，已在步骤 3 就地捕获并转为结构化失败。
    ///
    /// English:
    ///   Steps: (1) throw on null; (2) EmptyInput on ""; (3) match, converting a
    ///   timeout into RegexTimeout; (4) NoMatch if the pattern did not match;
    ///   (5) read the configured group, MissingCaptureGroup if it did not
    ///   participate; (6) return its value.
    ///
    ///   Step 5 tests Group.Success rather than whether the value is empty. The
    ///   two are not equivalent: (a*) against "b" produces a participating group
    ///   with an empty capture, which decision D-4 calls success, whereas an
    ///   unmatched optional group (a)? did not participate at all and is a
    ///   failure. Testing for an empty string would misreport the former.
    ///
    ///   Never throws for non-null input. RegexMatchTimeoutException is the only
    ///   exception that can escape matching and is converted in step 3.
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

        // 步骤 3 / Step 3
        Match match;
        try
        {
            match = _regex.Match(rawCode);
        }
        catch (RegexMatchTimeoutException)
        {
            return new ParseResult.Failure(ParseFailureReason.RegexTimeout, rawCode);
        }

        // 步骤 4 / Step 4
        if (!match.Success)
        {
            return new ParseResult.Failure(ParseFailureReason.NoMatch, rawCode);
        }

        // 步骤 5 / Step 5
        var captureGroup = match.Groups[CaptureGroupIndex];
        if (!captureGroup.Success)
        {
            return new ParseResult.Failure(ParseFailureReason.MissingCaptureGroup, rawCode);
        }

        // 步骤 6 / Step 6
        return new ParseResult.Success(captureGroup.Value);
    }
}
