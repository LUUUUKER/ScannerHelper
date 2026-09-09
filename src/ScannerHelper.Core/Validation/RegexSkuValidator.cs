// =============================================================================
// RegexSkuValidator.cs
//
// 中文：
//   用正则校验已解析出的 SKU（规格 §9.3）。
//
//   这是与解析正则**完全独立**的第二条正则。两者作用对象不同：
//     解析正则  作用于原始条码，把 SKU 取出来
//     校验正则  作用于取出来的 SKU，判断它长得对不对
//   例：解析正则 ^.{4}([A-Z0-9]{8}) 取出 12345678，
//       校验正则 ^[A-Z]{3}\d{8}$ 再判断它是否符合业务约定的形态。
//   两条规则各自配置、互不影响（测试 V6）。
//
//   ★ 本类刻意**不接受**任何大小写开关。
//     规格 §9.2 的「忽略大小写」只作用于字符集校验。若这里也加上，就等于
//     让一个开关同时改变两条互不相干的规则的语义，工人将无法预测勾选它
//     之后到底有多少条规则跟着变了。需要大小写不敏感的校验正则，应当由
//     规则作者自己写成 (?i)^[a-z]+$ ——那是规则的一部分，看得见、可审阅。
//     测试 V5 守住这一点：开关开着时，^[A-Z]+$ 校验 "abc" 仍须失败。
//
//   与 RegexSkuParser 共享的约束（规格 §8.2、§19）：
//     - 必须使用有限超时，且超时必须是独立于"不匹配"的失败原因；
//     - 正则语法错误在构造时拒绝，不进入运行期；
//     - 对任何非 null 输入都不抛异常。
//
//   "未启用" 用可空模式表达（string? pattern），与长度校验器的 int?、
//   字符集校验器的可空预设保持一致。
//
// English:
//   Validates a parsed SKU with a regular expression (spec §9.3).
//
//   A second regex, fully independent of the parsing one. The parsing regex acts
//   on the raw code to extract an SKU; this one acts on the extracted SKU to judge
//   its shape. Each is configured separately (test V6).
//
//   This class deliberately accepts no case-sensitivity toggle. Spec §9.2's
//   "ignore case" applies to character-set validation only. Wiring it in here
//   would let one checkbox change the meaning of two unrelated rules, leaving the
//   operator unable to predict how much shifted when they ticked it. A rule author
//   who wants case-insensitive matching writes (?i)^[a-z]+$ — visible in the rule
//   itself and reviewable. Test V5 guards this: with the toggle on, ^[A-Z]+$ must
//   still reject "abc".
//
//   Constraints shared with RegexSkuParser (spec §8.2, §19): a finite timeout with
//   a reason code distinct from a plain mismatch; syntax errors rejected at
//   construction; never throws for non-null input.
//
//   "Not enabled" is a nullable pattern, matching int? on the length validator and
//   the nullable preset on the character-set validator.
//
// 包含的成员 / Members in this file:
//   DefaultMatchTimeout  默认匹配超时
//   Pattern              校验正则，null 表示未启用
//   MatchTimeout         实际生效的匹配超时
//   Validate             执行校验
// =============================================================================

using System.Text.RegularExpressions;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：校验正则校验器。构造后不可变，可安全复用。
/// English: Validation-regex validator. Immutable after construction and safe to
///          reuse.
/// </summary>
public sealed class RegexSkuValidator : ISkuValidator
{
    /// <summary>
    /// 中文：默认匹配超时，100 毫秒。取值理由与 RegexSkuParser 相同：SKU 只有
    ///       几十个字符，写法正常的规则微秒级完成，留约三个数量级余量；同时
    ///       足够短，即便每次都超时，工人感受到的也只是轻微延迟。
    /// English: Default match timeout, 100 ms, for the same reasons as
    ///          RegexSkuParser: an SKU is a few dozen characters, a sane rule
    ///          finishes in microseconds, and even a rule that always times out
    ///          feels like a slight delay rather than a freeze.
    /// </summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 中文：编译好的正则。未启用时为 null。
    /// English: The compiled regex; null when the rule is not enabled.
    /// </summary>
    private readonly Regex? _regex;

    /// <summary>
    /// 中文：
    ///   构造校验器并校验配置。
    ///   输入：pattern 校验正则，null 表示该项未启用；
    ///         matchTimeout 匹配超时，null 时取 DefaultMatchTimeout，
    ///                      必须为正的有限值。
    ///   输出：校验器实例。
    ///   步骤：
    ///     1. 确定并校验超时值，非正值（含 Regex.InfiniteMatchTimeout，
    ///        其值为 -1 毫秒）一律拒绝；
    ///     2. 模式为 null 时记为未启用，不构造 Regex，直接返回；
    ///     3. 构造 Regex；语法错误时包装为 ArgumentException 后抛出；
    ///     4. 保存配置。
    ///
    ///   步骤 1 先于步骤 2：即便本次未启用，非法的超时值也应当被报出来，
    ///   而不是因为"反正没启用"就被静默接受——那样工人启用规则的那一刻
    ///   才会突然报错，而他刚改的明明是别的设置项。
    ///
    /// English:
    ///   Steps: (1) resolve and validate the timeout, rejecting any non-positive
    ///   value including Regex.InfiniteMatchTimeout (-1 ms); (2) treat a null
    ///   pattern as not enabled and build no Regex; (3) build the Regex, wrapping
    ///   a syntax error as ArgumentException; (4) store.
    ///
    ///   Step 1 precedes step 2 so an invalid timeout is reported even when the
    ///   rule is disabled. Accepting it silently would surprise the operator later,
    ///   at the moment they enable the rule, over a setting they did not just touch.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：超时非正。 English: A non-positive timeout.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：正则语法错误。 English: The pattern is not valid regex syntax.
    /// </exception>
    public RegexSkuValidator(string? pattern, TimeSpan? matchTimeout = null)
    {
        // 步骤 1 / Step 1
        var resolvedTimeout = matchTimeout ?? DefaultMatchTimeout;
        if (resolvedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchTimeout), resolvedTimeout,
                "匹配超时必须是正的有限值；规格 §9.3、§19 禁止无限超时。"
                + " The match timeout must be positive and finite; spec §9.3 and"
                + " §19 forbid an infinite timeout.");
        }

        MatchTimeout = resolvedTimeout;

        // 步骤 2 / Step 2
        if (pattern is null)
        {
            return;
        }

        // 步骤 3 / Step 3
        try
        {
            _regex = new Regex(pattern, RegexOptions.CultureInvariant, resolvedTimeout);
        }
        catch (ArgumentException syntaxError)
        {
            throw new ArgumentException(
                $"校验正则语法错误。 Invalid validation regex syntax: {pattern}",
                nameof(pattern),
                syntaxError);
        }

        // 步骤 4 / Step 4
        Pattern = pattern;
    }

    /// <summary>
    /// 中文：配置的校验正则。null 表示该项未启用，不施加任何约束。
    /// English: The configured pattern; null means the rule is not enabled and no
    ///          constraint is imposed.
    /// </summary>
    public string? Pattern { get; }

    /// <summary>
    /// 中文：实际生效的匹配超时。始终为正的有限值（测试 V7）。
    /// English: The effective match timeout, always positive and finite (test V7).
    /// </summary>
    public TimeSpan MatchTimeout { get; }

    /// <summary>
    /// 中文：
    ///   用校验正则校验 SKU。
    ///   输入：sku 候选 SKU，不得为 null。
    ///   输出：Valid，或 Invalid 携带一条 PatternMismatch / RegexTimeout。
    ///   步骤：
    ///     1. sku 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. 未启用时直接通过；
    ///     3. 执行匹配；超时被中断则返回 RegexTimeout 失败；
    ///     4. 未匹配则返回 PatternMismatch 失败；
    ///     5. 匹配成功则通过。
    ///
    ///   步骤 3、4 返回的失败都携带当时生效的模式。诊断日志据此可以自解释：
    ///   光记一条"校验失败"无法还原当时用的是哪条规则，而规则可能在事后
    ///   被改过（规格 §15）。
    ///
    ///   与字符集校验不同，这里对空串**不作特殊处理**：空串是否合法完全由
    ///   规则自己决定。^\d+$ 会拒绝它，^\d*$ 会接受它——这正是规则作者的
    ///   意图，校验器不该替他判断。
    ///
    /// English:
    ///   Steps: (1) throw on null; (2) pass if not enabled; (3) match, converting a
    ///   timeout into RegexTimeout; (4) PatternMismatch if it did not match;
    ///   (5) otherwise valid.
    ///
    ///   Both failures carry the pattern in force, so the diagnostic log is
    ///   self-describing: a bare "validation failed" cannot reconstruct which rule
    ///   applied, and the rule may have been edited since (spec §15).
    ///
    ///   Unlike character-set validation, the empty string gets no special case:
    ///   whether it is acceptable is entirely the rule's decision. ^\d+$ rejects
    ///   it, ^\d*$ accepts it — which is the rule author's intent, and not
    ///   something the validator should override.
    /// </summary>
    public ValidationResult Validate(string sku)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(sku);

        // 步骤 2 / Step 2
        if (_regex is null || Pattern is null)
        {
            return ValidationResult.Valid.Instance;
        }

        // 步骤 3 / Step 3
        bool isMatch;
        try
        {
            isMatch = _regex.IsMatch(sku);
        }
        catch (RegexMatchTimeoutException)
        {
            return new ValidationResult.Invalid(
                new ValidationFailure.RegexTimeout(Pattern));
        }

        // 步骤 4 / Step 4
        if (!isMatch)
        {
            return new ValidationResult.Invalid(
                new ValidationFailure.PatternMismatch(Pattern));
        }

        // 步骤 5 / Step 5
        return ValidationResult.Valid.Instance;
    }
}
