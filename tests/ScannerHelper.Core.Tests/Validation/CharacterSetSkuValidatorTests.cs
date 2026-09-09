// =============================================================================
// CharacterSetSkuValidatorTests.cs
//
// 中文：
//   字符集校验器的行为测试（对应测试清单 C1~C14）。
//
//   规格 §9.2 给出四个预设，外加一个独立的「忽略大小写」开关：
//     Numbers                        仅数字
//     Letters                        仅字母
//     LettersNumbers                 字母加数字
//     LettersNumbersDashUnderscore   字母、数字、连字符、下划线
//
//   开关的作用范围必须被夹死：预设决定**允许哪几类字符**，开关只决定
//   **字母的大小写算不算数**。本文件用 C10 从正面钉住"开关对数字无影响"；
//   下一轮 RegexSkuValidator 的 V5 从反面钉住"开关不会污染校验正则"。
//   一正一反两条，把这个开关关进笼子里，防止它日后被顺手扩大作用范围。
//
//   默认开启（规格 §9.2）：因为仅仅由于条码里出现一个小写字母就拒收，
//   比放过它要糟得多。确实需要强制大写的站点应当用校验正则 ^[A-Z]+$ 表达，
//   而那条路径不受本开关影响——这正是 V5 要证明的。
//
// English:
//   Behavior tests for the character-set validator (test plan C1–C14).
//
//   Spec §9.2 defines four presets plus an independent "ignore case" toggle. The
//   toggle's scope must be pinned from both sides: the preset decides which
//   *kinds* of character are allowed, the toggle decides only whether letter case
//   matters. C10 here pins it positively (the toggle does nothing to digits); V5
//   in the next round pins it negatively (the toggle never leaks into the
//   validation regex). Together they cage the toggle so its scope cannot quietly
//   grow later.
//
//   Enabled by default (spec §9.2): rejecting an otherwise-valid barcode purely
//   for containing a lowercase letter is a worse failure than accepting one. A
//   site that genuinely requires uppercase should say so with the validation
//   regex ^[A-Z]+$, a path this toggle does not touch — which is what V5 proves.
//
// 包含的测试 / Tests in this file:
//   Passes_when_every_character_is_allowed    C1 C3 C5 C6 C7 C9 C10 C12 C13
//   Fails_reporting_the_first_illegal_character  C2 C4 C8 C11
//   Null_sku_throws                           C14
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Validation;

public class CharacterSetSkuValidatorTests
{
    /// <summary>
    /// 中文：
    ///   C1、C3、C5、C6、C7、C9、C10、C12、C13 — 所有字符都被允许时通过。
    ///   输入：caseId 用例编号；preset 字符集预设，null 表示该项未启用；
    ///         ignoreCase 是否忽略大小写；sku 待校验的值。
    ///   输出：无（断言）。
    ///
    ///   覆盖点：
    ///     C1   仅数字，全是数字
    ///     C3   仅字母 + 忽略大小写，全小写 → 通过
    ///     C5   仅字母 + 区分大小写，全大写 → 通过
    ///     C6   仅字母 + 忽略大小写，大小写混合 → 通过
    ///     C7   字母加数字
    ///     C9   字母、数字、连字符、下划线
    ///     C10  仅数字，开关开与关两种情况结果相同——开关对数字无影响
    ///     C12  空串通过（决策 D-5）
    ///     C13  预设未启用，任何输入都通过
    ///
    ///   C12 的理由：空串不含任何非法字符，字符集校验没有理由拒绝它。
    ///   "不能为空" 是长度校验的职责，两层各管各的，否则一个 SKU 为空时
    ///   会同时冒出两条失败，工人看到重复的抱怨。
    ///
    ///   C13 的表示法与长度校验器一致：用可空类型表达"未启用"，而不是
    ///   在枚举里塞一个 None 值。这样设置层可以无条件构造全部校验器，
    ///   由每个校验器自己声明它是否施加约束。
    ///
    /// English:
    ///   C1, C3, C5, C6, C7, C9, C10, C12, C13 — passes when every character is
    ///   allowed.
    ///
    ///   C12: an empty string contains no illegal character, so the character-set
    ///   rule has no grounds to reject it. "Must not be empty" belongs to the
    ///   length rule; keeping the layers separate avoids an empty SKU producing
    ///   two failures and showing the operator a duplicated complaint.
    ///
    ///   C13 mirrors the length validator: "not enabled" is a nullable value
    ///   rather than a None member in the enum, so the settings layer can build
    ///   every validator unconditionally and let each declare whether it
    ///   constrains anything.
    /// </summary>
    [Theory]
    [InlineData("C1", CharacterSetPreset.Numbers, true, "12345")]
    [InlineData("C3", CharacterSetPreset.Letters, true, "abc")]
    [InlineData("C5", CharacterSetPreset.Letters, false, "ABC")]
    [InlineData("C6", CharacterSetPreset.Letters, true, "AbC")]
    [InlineData("C7", CharacterSetPreset.LettersNumbers, true, "AB12")]
    [InlineData("C9", CharacterSetPreset.LettersNumbersDashUnderscore, true, "AB-12_3")]
    [InlineData("C10a", CharacterSetPreset.Numbers, true, "12345")]
    [InlineData("C10b", CharacterSetPreset.Numbers, false, "12345")]
    [InlineData("C12", CharacterSetPreset.Numbers, true, "")]
    [InlineData("C13", null, true, "任何内容 anything at all !@#$")]
    public void Passes_when_every_character_is_allowed(
        string caseId, CharacterSetPreset? preset, bool ignoreCase, string sku)
    {
        var validator = new CharacterSetSkuValidator(preset, ignoreCase);

        var result = validator.Validate(sku);

        Assert.True(result is ValidationResult.Valid,
            $"{caseId}: 预设 {preset?.ToString() ?? "未启用"} 忽略大小写={ignoreCase}"
            + $" 作用于 \"{sku}\" 应通过，实际为 {result.GetType().Name}");
    }

    /// <summary>
    /// 中文：
    ///   C2、C4、C8、C11 — 含非法字符时失败，并报告**第一个**非法字符及其位置。
    ///   输入：caseId；preset；ignoreCase；sku；expectedCharacter 期望报告的
    ///         字符；expectedPosition 期望报告的位置（1-based）。
    ///   输出：无（断言）。
    ///
    ///   覆盖点：
    ///     C2   仅数字，中间夹一个字母
    ///     C4   仅字母 + **区分**大小写，全小写 → 失败（与 C3 构成对照）
    ///     C8   字母加数字，出现连字符
    ///     C11  非 ASCII 字符
    ///
    ///   C3 与 C4 是「忽略大小写」开关的核心对照：同样的输入 "abc"，
    ///   开关打开时通过，关闭时失败。两条必须同时存在，只留一条无法证明
    ///   开关真的起作用。
    ///
    ///   只报告第一个非法字符：单个校验器最多产生一条失败（ISkuValidator
    ///   契约）。而且对工人来说，"第 3 个字符 A 不合法" 已经足够定位问题；
    ///   罗列出全部非法字符只会让错误界面变得冗长。
    ///
    ///   位置是 **1-based**，与规格 §8.1 "面向用户的位置一律 1-based" 保持
    ///   一致。这个值唯一的用途就是显示给人看，工人数字符是从 1 开始数的。
    ///
    /// English:
    ///   C2, C4, C8, C11 — fails on an illegal character, reporting the *first*
    ///   one and its position.
    ///
    ///   C3 and C4 are the core contrast for the ignore-case toggle: the same
    ///   input "abc" passes with it on and fails with it off. Both must exist;
    ///   either alone cannot prove the toggle does anything.
    ///
    ///   Only the first illegal character is reported: a single validator produces
    ///   at most one failure (the ISkuValidator contract), and "character 3, 'A',
    ///   is not allowed" already locates the problem. Listing every offender would
    ///   only make the error screen long.
    ///
    ///   The position is 1-based, consistent with spec §8.1's "user-facing
    ///   positions are 1-based". Its only purpose is to be read by a person, and
    ///   people count characters from one.
    /// </summary>
    [Theory]
    [InlineData("C2", CharacterSetPreset.Numbers, true, "12A45", 'A', 3)]
    [InlineData("C4", CharacterSetPreset.Letters, false, "abc", 'a', 1)]
    [InlineData("C8", CharacterSetPreset.LettersNumbers, true, "AB-12", '-', 3)]
    [InlineData("C11", CharacterSetPreset.Letters, true, "AB中", '中', 3)]
    public void Fails_reporting_the_first_illegal_character(
        string caseId, CharacterSetPreset? preset, bool ignoreCase, string sku,
        char expectedCharacter, int expectedPosition)
    {
        var validator = new CharacterSetSkuValidator(preset, ignoreCase);

        var result = validator.Validate(sku);

        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        var failure = Assert.Single(invalid.Failures);
        var illegalCharacter = Assert.IsType<ValidationFailure.IllegalCharacter>(failure);

        Assert.True(illegalCharacter.Character == expectedCharacter,
            $"{caseId}: 应报告字符 '{expectedCharacter}'，"
            + $"实际报告 '{illegalCharacter.Character}'");
        Assert.True(illegalCharacter.Position == expectedPosition,
            $"{caseId}: 位置为 1-based，应报告 {expectedPosition}，"
            + $"实际报告 {illegalCharacter.Position}");
    }

    /// <summary>
    /// 中文：C14 — Validate(null) 抛 ArgumentNullException（决策 D-1），
    ///       与 F16、R11、L12 保持同一套契约。
    /// English: C14 — Validate(null) throws (D-1), matching F16, R11 and L12.
    /// </summary>
    [Fact]
    public void Null_sku_throws()
    {
        var validator = new CharacterSetSkuValidator(
            CharacterSetPreset.Numbers, ignoreCase: true);

        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }
}
