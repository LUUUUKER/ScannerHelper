// =============================================================================
// LengthSkuValidatorTests.cs
//
// 中文：
//   长度校验器的行为测试（对应测试清单 L1~L12）。
//
//   规格 §9.1 的定义很短：可选的最小长度、可选的最大长度，精确长度用
//   min == max 表达。因此本文件的重点全在边界与"未启用"这两件事上。
//
//   校验层与解析层的一个关键差异在此首次体现：解析失败只有一个原因，
//   而校验失败可能有多个（规格 §9 要求所有启用的规则都必须通过，
//   决策 D-6 进一步要求组合校验器收集全部失败原因）。因此
//   ValidationResult.Invalid 携带的是一个失败**集合**而非单个原因。
//   单个校验器最多产生一条，这一点由本文件断言钉住——若某个校验器
//   一次吐出多条，组合层的计数和 UI 的展示都会错乱。
//
//   失败原因携带结构化数据而非文案（测试计划 D5/D6）：TooShort 带上实际
//   长度与要求的下限，UI 才能组装出 "SKU 长度应为 8 位，实际 6 位" 这样
//   的中文提示。若 Core 直接产出英文句子，中文界面就无法正确显示，而且
//   这个缺陷要到做本地化时才会暴露。
//
// English:
//   Behavior tests for the length validator (test plan L1–L12).
//
//   Spec §9.1 is short: an optional minimum, an optional maximum, with an exact
//   length expressed as min == max. The weight therefore falls on boundaries and
//   on the "not enabled" cases.
//
//   A key difference from parsing appears here for the first time: a parse has
//   one failure reason, while validation may have several (spec §9 requires all
//   enabled rules to pass; decision D-6 requires the composite to collect them
//   all). ValidationResult.Invalid therefore carries a *collection*. A single
//   validator produces at most one, which this file pins down — a validator
//   emitting several would corrupt both the composite's accounting and the UI.
//
//   Failures carry structured data, not prose (test plan D5/D6): TooShort reports
//   the actual length and the required minimum so the UI can compose localized
//   text. English sentences produced inside Core could never render in Chinese,
//   and the defect would stay hidden until localization began.
//
// 包含的测试 / Tests in this file:
//   Passes_when_length_is_acceptable          L1 L3 L5 L7 L8 L9
//   Fails_with_TooShort_below_minimum         L2 L6
//   Fails_with_TooLong_above_maximum          L4
//   Invalid_configuration_is_rejected_at_construction  L10 L11
//   Null_sku_throws                           L12
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Validation;

public class LengthSkuValidatorTests
{
    /// <summary>
    /// 中文：
    ///   L1、L3、L5、L7、L8、L9 — 长度可接受时通过校验。
    ///   输入：caseId 用例编号；minimumLength 最小长度，null 表示未启用；
    ///         maximumLength 最大长度，null 表示未启用；sku 待校验的值。
    ///   输出：无（断言）。
    ///
    ///   覆盖点：
    ///     L1  恰好等于下限（边界）
    ///     L3  恰好等于上限（边界）
    ///     L5  min == max，即规格 §9.1 所说的"精确长度"
    ///     L7  两项都未启用——此时任何输入都通过。规格 §9 规定：
    ///         没有启用任何校验规则时，解析成功即视为有效
    ///     L8  只启用下限，超长值仍应通过（上限未启用就不该约束上界）
    ///     L9  只启用上限，空串仍应通过（下限未启用就不该约束下界）
    ///
    ///   L8 与 L9 是"未启用即不约束"的两个方向。最容易写错的实现是给未启用的
    ///   那一项填一个默认值（比如下限默认 0、上限默认某个大数），那样看似
    ///   等价，实则会在边界上产生与规格不符的行为，而且默认值一旦被写进
    ///   配置文件就再也说不清"用户到底有没有启用过这一项"。
    ///
    /// English:
    ///   L1, L3, L5, L7, L8, L9 — passes when the length is acceptable.
    ///   L7 encodes spec §9: with no rule enabled, a successful parse is valid.
    ///   L8 and L9 are the two directions of "not enabled means not constrained".
    ///   The tempting wrong implementation substitutes defaults for the disabled
    ///   bound (0 for the minimum, some large number for the maximum); that looks
    ///   equivalent but misbehaves at the boundary, and once a default reaches the
    ///   config file it becomes impossible to tell whether the user ever enabled
    ///   the setting at all.
    /// </summary>
    [Theory]
    [InlineData("L1", 8, null, "12345678")]
    [InlineData("L3", null, 8, "12345678")]
    [InlineData("L5", 8, 8, "12345678")]
    [InlineData("L7", null, null, "any length at all")]
    [InlineData("L8", 3, null, "a very long sku value indeed")]
    [InlineData("L9", null, 8, "")]
    public void Passes_when_length_is_acceptable(
        string caseId, int? minimumLength, int? maximumLength, string sku)
    {
        var validator = new LengthSkuValidator(minimumLength, maximumLength);

        var result = validator.Validate(sku);

        Assert.True(result is ValidationResult.Valid,
            $"{caseId}: 下限 {minimumLength?.ToString() ?? "未启用"}"
            + $" 上限 {maximumLength?.ToString() ?? "未启用"}"
            + $" 作用于长度 {sku.Length} 的值应通过，实际为 {result.GetType().Name}");
    }

    /// <summary>
    /// 中文：
    ///   L2、L6 — 短于下限时返回 TooShort，并携带实际长度与要求的下限。
    ///   输入：caseId；minimumLength；maximumLength；sku。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     L2  只启用下限，差一位
    ///     L6  min == max 的精确长度场景下偏短
    ///
    ///   同时断言失败集合中**恰好只有一条**。单个校验器一次只回答一个问题，
    ///   多条失败是组合校验器的职责（决策 D-6）。若单个校验器也吐出多条，
    ///   组合层的计数与 UI 的展示都会错乱。
    ///
    /// English:
    ///   L2, L6 — TooShort below the minimum, carrying the actual length and the
    ///   required minimum. Also asserts the failure collection holds exactly one
    ///   entry: a single validator answers one question, and aggregating several
    ///   is the composite's job (D-6).
    /// </summary>
    [Theory]
    [InlineData("L2", 8, null, "1234567")]
    [InlineData("L6", 8, 8, "1234567")]
    public void Fails_with_TooShort_below_minimum(
        string caseId, int? minimumLength, int? maximumLength, string sku)
    {
        var validator = new LengthSkuValidator(minimumLength, maximumLength);

        var result = validator.Validate(sku);

        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        var failure = Assert.Single(invalid.Failures);
        var tooShort = Assert.IsType<ValidationFailure.TooShort>(failure);

        Assert.True(tooShort.ActualLength == sku.Length,
            $"{caseId}: 失败原因应携带实际长度 {sku.Length}，实际携带 {tooShort.ActualLength}");
        Assert.Equal(minimumLength, tooShort.MinimumLength);
    }

    /// <summary>
    /// 中文：
    ///   L4 — 长于上限时返回 TooLong，并携带实际长度与要求的上限。
    ///   输入：无。输出：无（断言）。
    /// English:
    ///   L4 — TooLong above the maximum, carrying the actual length and the
    ///   required maximum.
    /// </summary>
    [Fact]
    public void Fails_with_TooLong_above_maximum()
    {
        var validator = new LengthSkuValidator(minimumLength: null, maximumLength: 8);

        var result = validator.Validate("123456789");

        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        var failure = Assert.Single(invalid.Failures);
        var tooLong = Assert.IsType<ValidationFailure.TooLong>(failure);

        Assert.Equal(9, tooLong.ActualLength);
        Assert.Equal(8, tooLong.MaximumLength);
    }

    /// <summary>
    /// 中文：
    ///   L10、L11 — 非法配置在构造时即被拒绝（决策 D-2）。
    ///   输入：caseId；minimumLength；maximumLength。输出：无（断言抛异常）。
    ///
    ///   覆盖点：
    ///     L10  下限大于上限——这个区间不包含任何长度，配置本身自相矛盾，
    ///          任何 SKU 都无法通过。若不在构造时拦下，现场表现为"所有扫描
    ///          都校验失败"，而工人根本无从判断是规则冲突还是条码有问题
    ///     L11  下限为负——长度不可能为负
    ///
    ///   与 F9~F12、R12 保持一致：与扫到什么码无关的配置错误一律在构造时拒绝。
    ///
    /// English:
    ///   L10, L11 — invalid configuration rejected at construction (D-2).
    ///   L10 is a self-contradictory range that no SKU can satisfy; left
    ///   unchecked it presents on site as "every scan fails validation", with no
    ///   way for the operator to tell a contradictory rule from a bad barcode.
    /// </summary>
    [Theory]
    [InlineData("L10", 9, 8)]
    [InlineData("L11", -1, null)]
    public void Invalid_configuration_is_rejected_at_construction(
        string caseId, int? minimumLength, int? maximumLength)
    {
        var exception = Record.Exception(
            () => new LengthSkuValidator(minimumLength, maximumLength));

        Assert.True(exception is ArgumentOutOfRangeException or ArgumentException,
            $"{caseId}: 下限 {minimumLength} 上限 {maximumLength} 属非法配置，"
            + $"构造时应抛异常，实际为 {exception?.GetType().Name ?? "未抛异常"}");
    }

    /// <summary>
    /// 中文：L12 — Validate(null) 抛 ArgumentNullException（决策 D-1），
    ///       与解析器的 F16、R11 保持同一套契约。
    ///       本条为计划外补充，目的是让 Core 中所有接受字符串的公开入口
    ///       对 null 的处理保持一致，避免调用方需要逐个记忆哪些会抛、
    ///       哪些会返回失败。
    /// English: L12 — Validate(null) throws (D-1), matching F16 and R11. Added
    ///          beyond the plan so every public string-taking entry point in Core
    ///          treats null identically, sparing callers from memorizing which
    ///          ones throw and which return a failure.
    /// </summary>
    [Fact]
    public void Null_sku_throws()
    {
        var validator = new LengthSkuValidator(minimumLength: 1, maximumLength: 10);

        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }
}
