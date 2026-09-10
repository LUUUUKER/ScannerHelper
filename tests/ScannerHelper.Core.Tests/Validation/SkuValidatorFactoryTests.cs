// =============================================================================
// SkuValidatorFactoryTests.cs
//
// 中文：
//   配置到组合校验器的桥接测试（用例编号 VF1~VF8）。
//
//   与 SkuParserFactoryTests 对称的另一半。三个关注点：
//
//   1. **什么都不配时一律通过**（VF1）。规格 §9 明确支持这种配置——很多站点
//      只需要解析，不需要额外校验。这不是边界情况而是常见配置。
//
//   2. **空白校验正则等于未启用**（VF3），与解析层刻意相反。若只判 null，
//      空串会被 new Regex("") 接受成一条"已启用"的规则，而它匹配一切——
//      界面上显示"校验已启用"，实际什么都没校验。这比不启用更糟：工人以为
//      有一道防线，其实没有。
//
//   3. ★ **IgnoreCase 绝不能漏进校验正则**（VF6）。
//
//      本工厂是整个项目里最容易犯这个错的地方：它同时手握 IgnoreCase 和
//      正则模式两样东西，把开关顺手传给正则校验器，写起来只多两个字符，
//      看上去还很"一致"。规格 §9.2 规定该开关只作用于字符集校验，测试计划
//      的 V5 已经从校验器一侧守住了一次，VF6 从工厂这一侧再守一次。
//
//      两侧都要守：V5 保证 RegexSkuValidator 不接受大小写参数，VF6 保证
//      工厂不会绕过它（比如自己往模式前面拼一个 (?i)）。
//
// English:
//   Tests for the bridge from configuration to composite validator (VF1–VF8), the
//   symmetric half of SkuParserFactoryTests. Three concerns.
//
//   First, with nothing configured everything passes (VF1). Spec §9 supports this
//   explicitly — many sites need parsing only — so it is a common configuration
//   rather than an edge case.
//
//   Second, a blank validation regex means "not enabled" (VF3), deliberately the
//   opposite of the parsing layer. Checking only for null would let an empty string
//   be accepted as an enabled rule via new Regex(""), which matches everything: the
//   screen reports validation as enabled while nothing is validated. That is worse
//   than being off, because the operator believes a safeguard exists when it does not.
//
//   Third, IgnoreCase must never leak into the validation regex (VF6). This factory
//   is the likeliest place in the project to get that wrong: it holds both the toggle
//   and the pattern, and passing one to the other costs two characters and looks
//   consistent. Spec §9.2 confines the toggle to character-set validation. Test V5
//   already guards it from the validator's side; VF6 guards it from the factory's.
//   Both are needed — V5 ensures RegexSkuValidator accepts no case parameter, VF6
//   ensures the factory does not route around that, for instance by prefixing the
//   pattern with (?i) itself.
//
// 包含的测试 / Tests in this file:
//   Nothing_configured_accepts_anything                VF1
//   Null_settings_are_rejected                         VF2
//   Blank_validation_regex_means_not_enabled           VF3
//   Configured_validation_regex_is_enforced            VF4
//   Ignore_case_reaches_the_character_set_rule         VF5
//   Ignore_case_never_reaches_the_validation_regex     VF6
//   Invalid_validator_configuration_propagates         VF7
//   All_failures_are_collected_through_the_factory     VF8
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Validation;

public class SkuValidatorFactoryTests
{
    /// <summary>
    /// 中文：
    ///   VF1 — 全新安装的默认配置（三条规则都没启用）对任何内容都通过。
    ///   输入：无。输出：无（断言）。
    ///
    ///   规格 §9："没有启用任何校验规则时，解析成功即视为有效。"这是很多站点
    ///   的实际配置——只需要从条码里取出 SKU，不需要额外判断它长得对不对。
    ///
    ///   用一个明显"不像 SKU"的字符串来测，是为了确保通过的原因是"没有规则
    ///   施加约束"，而不是碰巧满足了某条默认开启的规则。
    ///
    /// English:
    ///   VF1 — the fresh-install defaults, with no rule enabled, accept anything.
    ///   Spec §9: with no rule enabled, a successful parse is valid. That is the real
    ///   configuration at many sites, which need the SKU extracted and nothing more.
    ///
    ///   A deliberately implausible string is used so that passing can only mean "no
    ///   rule constrains anything", rather than coincidentally satisfying some rule
    ///   that defaults to on.
    /// </summary>
    [Fact]
    public void Nothing_configured_accepts_anything()
    {
        var validator = SkuValidatorFactory.Create(new AppSettings().SkuValidation);

        Assert.IsType<ValidationResult.Valid>(validator.Validate("任何内容 anything !@#$"));
        Assert.IsType<ValidationResult.Valid>(validator.Validate(string.Empty));
    }

    /// <summary>
    /// 中文：VF2 — settings 为 null 抛 ArgumentNullException（决策 D-1）。
    /// English: VF2 — null settings throws (decision D-1).
    /// </summary>
    [Fact]
    public void Null_settings_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => SkuValidatorFactory.Create(null!));
    }

    /// <summary>
    /// 中文：
    ///   VF3 — 空白校验正则视为未启用，任何内容都通过。
    ///   输入：caseId；pattern 三种空白写法。输出：无（断言）。
    ///
    ///   ★ 若实现只判 `== null`，空串会走到 new Regex("")。那是一条**合法**的
    ///     正则，匹配任何字符串——于是校验器处于"已启用但形同虚设"的状态：
    ///     设置页显示校验规则开着，实际上一个字符都没检查。
    ///
    ///     这比干脆不启用更危险，因为它制造了一道并不存在的防线。工人和技术
    ///     顾问都会以为"有正则兜着"，直到某天发现一批错误的 SKU 已经进了系统。
    ///
    ///   与解析层刻意相反（见 SkuParserFactoryTests 的 PF4~PF6）：那边空白模式
    ///   直接抛异常，因为解析是 SKU 模式的必经步骤，没有规则就产不出 SKU；
    ///   这边校验规则本来就是可选的，留空是完全正当的配置。
    ///
    /// English:
    ///   VF3 — a blank validation regex means "not enabled" and accepts anything, in
    ///   all three spellings of blank.
    ///
    ///   Checking only for null would send an empty string to new Regex(""), a *valid*
    ///   regex matching every string, leaving the validator enabled-but-inert:
    ///   Settings shows the rule as on while not a single character is checked.
    ///
    ///   That is more dangerous than being off, because it manufactures a safeguard
    ///   that does not exist. Operator and advisor alike believe the regex has them
    ///   covered, until a batch of wrong SKUs turns up in the system.
    ///
    ///   Deliberately the opposite of the parsing layer (PF4–PF6), where a blank
    ///   pattern throws because parsing is mandatory in SKU mode. Here the rule is
    ///   optional to begin with and leaving it blank is entirely legitimate.
    /// </summary>
    [Theory]
    [InlineData("VF3a", null)]
    [InlineData("VF3b", "")]
    [InlineData("VF3c", "   ")]
    public void Blank_validation_regex_means_not_enabled(string caseId, string? pattern)
    {
        var validator = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            ValidationRegexPattern = pattern,
        });

        var result = validator.Validate("anything at all");

        Assert.True(result is ValidationResult.Valid,
            $"{caseId}: 空白校验正则应视为未启用，实际为 {result.GetType().Name}");
    }

    /// <summary>
    /// 中文：VF4 — 配置了校验正则时确实生效，不匹配的 SKU 被拒绝，
    ///       且失败原因为 PatternMismatch（而不是别的原因码）。
    ///       与 VF3 构成对照：VF3 证明留空真的不启用，VF4 证明填了真的启用。
    ///       只有其中一条时，"永远通过"的实现也能蒙混过关。
    /// English: VF4 — a configured validation regex is actually enforced and a
    ///          non-matching SKU fails with PatternMismatch. The counterpart to VF3:
    ///          VF3 proves blank really is off, VF4 proves a value really is on.
    ///          With only one of them, an implementation that always passes would slip
    ///          through.
    /// </summary>
    [Fact]
    public void Configured_validation_regex_is_enforced()
    {
        var validator = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            ValidationRegexPattern = @"^[A-Z]{3}\d{8}$",
        });

        Assert.IsType<ValidationResult.Valid>(validator.Validate("ABC12345678"));

        var invalid = Assert.IsType<ValidationResult.Invalid>(validator.Validate("AB12345678"));
        Assert.IsType<ValidationFailure.PatternMismatch>(Assert.Single(invalid.Failures));
    }

    /// <summary>
    /// 中文：
    ///   VF5 — IgnoreCase 确实被传给了字符集校验器。
    ///   输入：无。输出：无（断言）。
    ///
    ///   同样的输入 "abc" 配同样的预设"仅字母"，开关打开时通过、关闭时失败。
    ///   若工厂忘了传这个字段（用了构造函数默认值，或者干脆传了个常量），
    ///   两次结果就会相同，本条随即变红。
    ///
    ///   与 VF6 是一对：本条证明开关**有**作用，VF6 证明它**不越界**。
    ///
    /// English:
    ///   VF5 — IgnoreCase does reach the character-set validator. The same input
    ///   "abc" under the same "letters only" preset passes with the toggle on and
    ///   fails with it off. A factory that forgot to pass the field — using a
    ///   constructor default, or a constant — would produce identical results and turn
    ///   this red.
    ///
    ///   Paired with VF6: this proves the toggle does something, VF6 proves it does
    ///   not do too much.
    /// </summary>
    [Fact]
    public void Ignore_case_reaches_the_character_set_rule()
    {
        var ignoringCase = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            CharacterSet = CharacterSetPreset.Letters,
            IgnoreCase = true,
        });

        var respectingCase = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            CharacterSet = CharacterSetPreset.Letters,
            IgnoreCase = false,
        });

        Assert.IsType<ValidationResult.Valid>(ignoringCase.Validate("abc"));
        Assert.IsType<ValidationResult.Invalid>(respectingCase.Validate("abc"));
    }

    /// <summary>
    /// 中文：
    ///   VF6 — IgnoreCase 绝不能影响校验正则。
    ///   输入：无。输出：无（断言）。
    ///
    ///   开关打开、校验正则为 ^[A-Z]+$、SKU 为 "abc" —— 必须**仍然失败**。
    ///
    ///   ★ 本工厂是整个项目里最容易犯这个错的地方：它同时手握 IgnoreCase 和
    ///     正则模式两样东西。把开关顺手传下去只多两个字符，或者在模式前面拼
    ///     一个 (?i) 只多四个字符，写的人当时多半觉得这叫"行为一致"。
    ///
    ///     规格 §9.2 明确规定该开关只作用于字符集校验。理由是可预测性：
    ///     工人勾选"忽略大小写"时，必须能确定自己改变的是**哪一条**规则。
    ///     若一个勾选框同时改变两条互不相干的规则的语义，出问题时根本无从
    ///     判断该回退哪一处。需要大小写不敏感的校验正则，应当由规则作者
    ///     自己写成 (?i)^[a-z]+$ ——那是规则的一部分，看得见、可审阅。
    ///
    ///   测试计划的 V5 已经从校验器一侧守过一次（RegexSkuValidator 根本不接受
    ///   大小写参数）。本条从工厂一侧再守一次，因为工厂完全可以绕过那道防线
    ///   而不需要改动校验器。
    ///
    /// English:
    ///   VF6 — IgnoreCase must not affect the validation regex. With the toggle on, a
    ///   validation regex of ^[A-Z]+$ must still reject "abc".
    ///
    ///   This factory is the likeliest place in the project to get this wrong, holding
    ///   both the toggle and the pattern. Passing one into the other costs two
    ///   characters, or prefixing the pattern with (?i) costs four, and whoever does it
    ///   will most likely think of it as consistency.
    ///
    ///   Spec §9.2 confines the toggle to character-set validation, for
    ///   predictability: when the operator ticks "ignore case" they must be able to
    ///   tell *which* rule they changed. One checkbox altering the meaning of two
    ///   unrelated rules leaves no way to know what to revert when something goes
    ///   wrong. A rule author wanting case-insensitive matching writes (?i)^[a-z]+$ —
    ///   part of the rule, visible and reviewable.
    ///
    ///   Test V5 guards this from the validator's side (RegexSkuValidator accepts no
    ///   case parameter at all). This guards it from the factory's, since the factory
    ///   could route around that without touching the validator.
    /// </summary>
    [Fact]
    public void Ignore_case_never_reaches_the_validation_regex()
    {
        var validator = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            IgnoreCase = true,
            ValidationRegexPattern = "^[A-Z]+$",
        });

        var result = validator.Validate("abc");

        Assert.True(result is ValidationResult.Invalid,
            "规格 §9.2 的「忽略大小写」只作用于字符集校验，绝不能影响校验正则。"
            + " 开关打开时，^[A-Z]+$ 校验 \"abc\" 仍须失败。"
            + " 需要大小写不敏感的校验正则应由规则作者写成 (?i)^[a-z]+$。");
    }

    /// <summary>
    /// 中文：
    ///   VF7 — 校验器构造函数抛出的配置错误必须穿透工厂。
    ///   输入：无。输出：无（断言）。
    ///
    ///   用最小长度大于最大长度这个例子（对应 L10）：这是一个自相矛盾的区间，
    ///   不存在任何长度能满足它。放行到运行期，现场表现为"所有扫描都校验失败"，
    ///   工人根本无从判断这是规则冲突还是条码有问题。
    ///
    ///   与 PF7、PF8 同理：工厂只组装、不判定，设置页靠"调一次 Create 看抛不抛"
    ///   来判断配置能否保存（规格 §13.4）。工厂若把异常吞了，一份不可能通过的
    ///   规则会被静默保存下来。
    ///
    /// English:
    ///   VF7 — configuration errors from the validator constructors propagate through
    ///   the factory. The example is a minimum above the maximum (matching L10): a
    ///   self-contradictory range no length can satisfy. Allowed through to runtime it
    ///   presents as "every scan fails validation", with no way for the operator to
    ///   tell a contradictory rule from a bad barcode.
    ///
    ///   Same reasoning as PF7 and PF8: the factory composes and does not judge, and
    ///   the Settings page decides whether a configuration may be saved by calling
    ///   Create and watching for a throw (spec §13.4). A swallowed exception would
    ///   silently save a rule nothing can ever satisfy.
    /// </summary>
    [Fact]
    public void Invalid_validator_configuration_propagates()
    {
        Assert.Throws<ArgumentException>(() => SkuValidatorFactory.Create(
            new SkuValidationSettings { MinimumLength = 9, MaximumLength = 8 }));
    }

    /// <summary>
    /// 中文：
    ///   VF8 — 经由工厂组装出来的校验器同样收集**全部**失败原因（决策 D-6）。
    ///   输入：无。输出：无（断言）。
    ///
    ///   "12A" 同时违反两条规则：长度不足 8，且含有非数字字符。工人必须一次
    ///   看到两个问题，而不是修好长度、重扫一次，才发现还有非法字符。
    ///
    ///   测试计划的 P7 已经用桩校验器验证过组合逻辑本身。本条验证的是工厂
    ///   **确实把多个校验器都装了进去**——一个只装了长度校验的工厂在 P7 下
    ///   依然是绿的，因为 P7 根本不经过工厂。
    ///
    ///   顺带钉住失败的排列顺序：长度在前、字符集在后，与工厂里成员的装配
    ///   顺序一致。顺序稳定，同一种错误组合每次在错误界面上看起来才一样。
    ///
    /// English:
    ///   VF8 — a validator assembled by the factory still collects *every* failure
    ///   (decision D-6). "12A" breaks two rules at once: shorter than 8 and containing
    ///   a non-digit. The operator must see both at once rather than fixing the length,
    ///   rescanning, and only then meeting the illegal character.
    ///
    ///   Test P7 already verified the combining logic itself with stub validators. This
    ///   verifies that the factory actually puts several validators in: a factory
    ///   wiring up only the length rule would leave P7 green, because P7 never goes
    ///   through the factory.
    ///
    ///   It also pins the order — length first, character set second, matching the
    ///   factory's assembly order — so the same combination of problems always looks
    ///   the same on the error screen.
    /// </summary>
    [Fact]
    public void All_failures_are_collected_through_the_factory()
    {
        var validator = SkuValidatorFactory.Create(new SkuValidationSettings
        {
            MinimumLength = 8,
            CharacterSet = CharacterSetPreset.Numbers,
        });

        var invalid = Assert.IsType<ValidationResult.Invalid>(validator.Validate("12A"));

        Assert.Equal(2, invalid.Failures.Count);
        Assert.IsType<ValidationFailure.TooShort>(invalid.Failures[0]);
        Assert.IsType<ValidationFailure.IllegalCharacter>(invalid.Failures[1]);
    }
}
