// =============================================================================
// RegexSkuValidatorTests.cs
//
// 中文：
//   校验正则的行为测试（对应测试清单 V1~V9）。
//
//   这是**独立于解析正则**的第二个正则（规格 §9.3）。两者作用于不同的对象、
//   解决不同的问题：
//     解析正则  作用于原始条码，负责"从这一长串里把 SKU 取出来"
//     校验正则  作用于已取出的 SKU，负责"取出来的这段长得对不对"
//   它们各自独立配置，互不影响——V6 从行为上钉住这一点。
//
//   本文件承担一项跨类的守卫职责：V5 是「忽略大小写」开关作用范围的第三面。
//     C3 vs C4      证明开关确实起作用
//     C10a vs C10b  证明开关不影响数字
//     V5            证明开关碰不到校验正则  ← 本文件
//   规格 §9.2 明确：真要强制大写就用校验正则 ^[A-Z]+$ 表达，而那条路径
//   不受开关影响。若哪天有人把 IgnoreCase 顺手接到这里，V5 会立刻变红。
//
//   与解析正则共享的两条硬性要求（规格 §8.2、§19）：
//     - 必须使用有限超时（V7）；
//     - 超时必须是与"不匹配"不同的原因（V4）。两者的修复动作不同：
//       不匹配是规则内容不对，超时是规则写法不对。
//
// English:
//   Behavior tests for the validation regex (test plan V1–V9).
//
//   This is a second regex, independent of the parsing regex (spec §9.3). They
//   act on different things: the parsing regex extracts an SKU from the raw code;
//   the validation regex judges whether the extracted SKU looks right. They are
//   configured separately and do not influence each other — V6 pins that down.
//
//   This file also carries a cross-class guard: V5 is the third side of the
//   ignore-case toggle's cage. C3 vs C4 proves the toggle works; C10a vs C10b
//   proves it does not touch digits; V5 proves it cannot reach the validation
//   regex. Spec §9.2 is explicit that a site requiring uppercase should say so
//   with ^[A-Z]+$, a path the toggle does not affect. If anyone later wires
//   IgnoreCase into this class, V5 turns red immediately.
//
// 包含的测试 / Tests in this file:
//   Passes_when_pattern_matches                       V1
//   Fails_with_PatternMismatch_when_pattern_does_not_match  V2
//   Invalid_pattern_is_rejected_at_construction       V3
//   Catastrophic_backtracking_fails_with_RegexTimeout V4
//   Ignore_case_toggle_does_not_reach_the_validation_regex  V5
//   Parsing_regex_and_validation_regex_are_independent V6
//   Match_timeout_is_finite                           V7
//   Null_sku_throws                                   V8
//   Passes_anything_when_not_enabled                  V9
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Validation;

public class RegexSkuValidatorTests
{
    /// <summary>
    /// 中文：V1 — SKU 符合校验正则时通过。
    ///       用规格 §9.3 给出的示例：三个大写字母加八位数字。
    /// English: V1 — passes when the SKU matches. Uses spec §9.3's example:
    ///          three uppercase letters followed by eight digits.
    /// </summary>
    [Fact]
    public void Passes_when_pattern_matches()
    {
        var validator = new RegexSkuValidator(@"^[A-Z]{3}\d{8}$");

        var result = validator.Validate("ABC12345678");

        Assert.IsType<ValidationResult.Valid>(result);
    }

    /// <summary>
    /// 中文：
    ///   V2 — SKU 不符合校验正则时返回 PatternMismatch，并携带所用的模式。
    ///   输入：无。输出：无（断言）。
    ///   失败携带模式，是为了让诊断日志能自解释：光记一条 "校验失败" 无法
    ///   还原当时用的是哪条规则，而规则可能在事后被改过（规格 §15）。
    /// English:
    ///   V2 — PatternMismatch when the SKU does not match, carrying the pattern
    ///   used. Including it makes the diagnostic log self-describing: a bare
    ///   "validation failed" cannot reconstruct which rule was in force, and the
    ///   rule may have been edited since (spec §15).
    /// </summary>
    [Fact]
    public void Fails_with_PatternMismatch_when_pattern_does_not_match()
    {
        const string pattern = @"^[A-Z]{3}\d{8}$";
        var validator = new RegexSkuValidator(pattern);

        var result = validator.Validate("AB12345678");

        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        var failure = Assert.Single(invalid.Failures);
        var mismatch = Assert.IsType<ValidationFailure.PatternMismatch>(failure);
        Assert.Equal(pattern, mismatch.Pattern);
    }

    /// <summary>
    /// 中文：V3 — 正则语法错误在构造时即被拒绝，与 RegexSkuParser 的 R7 同一
    ///       套契约：与扫到什么码无关的配置错误一律在构造时拦下（决策 D-2）。
    /// English: V3 — an invalid pattern is rejected at construction, matching R7's
    ///          contract on the parser: configuration errors independent of the
    ///          scanned code are caught at construction (D-2).
    /// </summary>
    [Fact]
    public void Invalid_pattern_is_rejected_at_construction()
    {
        var exception = Record.Exception(() => new RegexSkuValidator("["));

        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.NotNull(argumentException.InnerException);
    }

    /// <summary>
    /// 中文：
    ///   V4 — 灾难性回溯被超时中断，返回 RegexTimeout，且与 PatternMismatch 区分。
    ///   输入：无。输出：无（断言）。
    ///
    ///   与解析正则的 R8 同理。校验正则同样由技术顾问提供、由工人粘贴进设置页，
    ///   同样可能写出指数级回溯的模式。两条链路都必须能在超时后给出**独立的**
    ///   原因码，否则现场无法区分"规则内容不对"（改规则）与"规则写法不对"
    ///   （重写规则）。
    ///
    /// English:
    ///   V4 — catastrophic backtracking is cut short and reported as RegexTimeout,
    ///   distinct from PatternMismatch. Same reasoning as R8 on the parser: the
    ///   validation regex is equally advisor-supplied and equally capable of
    ///   exponential backtracking, and both paths need a separate reason code so
    ///   the shop floor can tell "wrong rule content" from "wrong rule form".
    /// </summary>
    [Fact]
    public void Catastrophic_backtracking_fails_with_RegexTimeout()
    {
        var validator = new RegexSkuValidator(@"^(a+)+$");

        var result = validator.Validate(new string('a', 40) + "X");

        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        var failure = Assert.Single(invalid.Failures);
        Assert.IsType<ValidationFailure.RegexTimeout>(failure);
    }

    /// <summary>
    /// 中文：
    ///   V5 — 「忽略大小写」开关碰不到校验正则。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 构造一个开启忽略大小写的字符集校验器，确认它接受小写 "abc"；
    ///     2. 构造一个要求全大写的校验正则 ^[A-Z]+$；
    ///     3. 用同一个 "abc" 校验，断言**仍然失败**。
    ///
    ///   这是开关作用范围的第三面守卫。若哪天有人为了"让规则更宽松"而把
    ///   IgnoreCase 接到 RegexSkuValidator 上、或给正则加上 RegexOptions
    ///   .IgnoreCase，本条会立刻变红。
    ///
    ///   规格 §9.2 的原话是：确实需要强制大写的站点应当用校验正则表达，
    ///   而该路径不受开关影响。这条测试就是那句话的可执行版本。
    ///
    /// English:
    ///   V5 — the ignore-case toggle cannot reach the validation regex.
    ///   Steps: (1) a character-set validator with the toggle on accepts "abc";
    ///   (2) a validation regex demanding uppercase; (3) the same "abc" still
    ///   fails.
    ///
    ///   The third side of the toggle's cage. If anyone later wires IgnoreCase
    ///   into this class or adds RegexOptions.IgnoreCase to "loosen" rules, this
    ///   turns red. It is the executable form of spec §9.2's statement that a site
    ///   requiring uppercase should express it through the validation regex, a
    ///   path the toggle does not affect.
    /// </summary>
    [Fact]
    public void Ignore_case_toggle_does_not_reach_the_validation_regex()
    {
        // 步骤 1 / Step 1
        var characterSetValidator = new CharacterSetSkuValidator(
            CharacterSetPreset.Letters, ignoreCase: true);
        Assert.IsType<ValidationResult.Valid>(characterSetValidator.Validate("abc"));

        // 步骤 2 / Step 2
        var regexValidator = new RegexSkuValidator(@"^[A-Z]+$");

        // 步骤 3 / Step 3
        var result = regexValidator.Validate("abc");

        Assert.True(result is ValidationResult.Invalid,
            "「忽略大小写」开关只作用于字符集校验，绝不能影响校验正则。"
            + " 规格 §9.2：需要强制大写的站点应当用校验正则表达。");
    }

    /// <summary>
    /// 中文：
    ///   V6 — 解析正则与校验正则各自独立，互不影响。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 用解析正则 ^(\w{3}) 从 "ABC123" 中取出 "ABC"；
    ///     2. 用一条**完全不同**的校验正则 ^\d+$ 校验刚取出的 "ABC"；
    ///     3. 断言解析成功而校验失败——两条规则各按自己的模式工作。
    ///
    ///   这条看似显然，但它守的是分层本身：若将来有人为了"少配一条规则"
    ///   而让校验复用解析的模式，两层就退化成一层，规格 §9 的
    ///   "原始码 → 解析 → 候选 SKU → 校验 → 有效 SKU" 也就不成立了。
    ///
    /// English:
    ///   V6 — the parsing regex and the validation regex are independent.
    ///   Steps: parse "ABC" out of "ABC123" with ^(\w{3}), then validate that
    ///   "ABC" against an entirely different ^\d+$, and assert the parse succeeded
    ///   while the validation failed.
    ///
    ///   Obvious-looking, but it guards the layering itself: if someone later has
    ///   validation reuse the parsing pattern to "save configuring one rule", the
    ///   two layers collapse into one and spec §9's raw → parse → candidate →
    ///   validate → valid pipeline no longer holds.
    /// </summary>
    [Fact]
    public void Parsing_regex_and_validation_regex_are_independent()
    {
        // 步骤 1 / Step 1
        var parser = new RegexSkuParser(@"^(\w{3})", captureGroupIndex: 1);
        var parseResult = parser.Parse("ABC123");
        var parsed = Assert.IsType<ParseResult.Success>(parseResult);
        Assert.Equal("ABC", parsed.Sku);

        // 步骤 2、3 / Steps 2–3
        var validator = new RegexSkuValidator(@"^\d+$");
        var validationResult = validator.Validate(parsed.Sku);

        Assert.IsType<ValidationResult.Invalid>(validationResult);
    }

    /// <summary>
    /// 中文：V7 — 校验正则的匹配超时必须是有限值（规格 §9.3、§19），
    ///       与解析正则的 R9 同一要求。
    /// English: V7 — the validation regex's match timeout must be finite
    ///          (spec §9.3, §19), the same requirement as R9 on the parser.
    /// </summary>
    [Fact]
    public void Match_timeout_is_finite()
    {
        var validator = new RegexSkuValidator(@"^\d+$");

        Assert.NotEqual(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout,
            validator.MatchTimeout);
        Assert.True(validator.MatchTimeout > TimeSpan.Zero);
    }

    /// <summary>
    /// 中文：V8 — Validate(null) 抛 ArgumentNullException（决策 D-1）。
    /// English: V8 — Validate(null) throws (D-1).
    /// </summary>
    [Fact]
    public void Null_sku_throws()
    {
        var validator = new RegexSkuValidator(@"^\d+$");

        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }

    /// <summary>
    /// 中文：V9 — 模式为 null 表示该项未启用，任何输入都通过。
    ///       与长度校验器的 L7、字符集校验器的 C13 使用同一套表示法：
    ///       用可空类型表达"未启用"，使设置层可以无条件构造全部校验器。
    /// English: V9 — a null pattern means the rule is not enabled and everything
    ///          passes, matching L7 and C13: "not enabled" is a nullable value, so
    ///          the settings layer can construct every validator unconditionally.
    /// </summary>
    [Fact]
    public void Passes_anything_when_not_enabled()
    {
        var validator = new RegexSkuValidator(pattern: null);

        var result = validator.Validate("任何内容 anything at all !@#$");

        Assert.IsType<ValidationResult.Valid>(result);
    }
}
