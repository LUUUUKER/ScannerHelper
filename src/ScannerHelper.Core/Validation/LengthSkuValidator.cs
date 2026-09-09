// =============================================================================
// LengthSkuValidator.cs
//
// 中文：
//   按长度校验 SKU（规格 §9.1）。
//
//   两个边界都是可选的：
//     只配下限        长度不得少于该值，上不封顶
//     只配上限        长度不得超过该值，下不设限
//     两个都配        长度必须落在闭区间内
//     两个都不配      任何长度都通过
//     下限 == 上限    即规格所说的"精确长度"
//
//   用 int? 而不是给未启用的边界填默认值：
//     看似可以用 0 当下限默认、用 int.MaxValue 当上限默认，行为"等价"。
//     但这样一来配置里就再也分不清"用户把下限设成了 0"和"用户根本没启用
//     下限"——这两者在设置页上是不同的显示状态，写进 JSON 后也无法还原。
//     可空类型让"未启用"成为一个能被表达、能被持久化、能被还原的状态。
//
//   校验顺序：先查下限，再查上限。由于构造时已保证下限 ≤ 上限，一个 SKU
//   不可能同时违反两者，因此本校验器最多产生一条失败——这与 ISkuValidator
//   的约定一致，聚合多条是组合校验器的事。
//
// English:
//   Validates an SKU by length (spec §9.1).
//
//   Both bounds are optional: minimum only, maximum only, both (a closed range),
//   neither (everything passes), or equal bounds for the spec's "exact length".
//
//   int? rather than defaults for the disabled bound: substituting 0 and
//   int.MaxValue looks equivalent, but then the configuration can no longer
//   distinguish "the user set the minimum to 0" from "the user never enabled a
//   minimum" — two different states in the Settings UI that could not be restored
//   from JSON. A nullable type makes "not enabled" expressible, persistable, and
//   restorable.
//
//   Order: minimum first, then maximum. Because the constructor guarantees
//   minimum <= maximum, no SKU can violate both, so this validator produces at
//   most one failure — matching the ISkuValidator contract, with aggregation left
//   to the composite.
//
// 包含的成员 / Members in this file:
//   MinimumLength  最小长度，null 表示未启用
//   MaximumLength  最大长度，null 表示未启用
//   Validate       执行长度校验
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：长度校验器。构造后不可变，可安全复用。
/// English: Length validator. Immutable after construction and safe to reuse.
/// </summary>
public sealed class LengthSkuValidator : ISkuValidator
{
    /// <summary>
    /// 中文：
    ///   构造长度校验器并校验配置。
    ///   输入：minimumLength 最小长度，null 表示未启用，非 null 时必须 ≥ 0；
    ///         maximumLength 最大长度，null 表示未启用，非 null 时必须 ≥ 0。
    ///   输出：校验器实例。
    ///   步骤：
    ///     1. 下限非 null 且为负时抛 ArgumentOutOfRangeException；
    ///     2. 上限非 null 且为负时抛 ArgumentOutOfRangeException；
    ///     3. 两者都启用且下限大于上限时抛 ArgumentException；
    ///     4. 保存配置。
    ///
    ///   步骤 3 拦下的是一个**自相矛盾的区间**：下限大于上限意味着不存在任何
    ///   满足条件的长度，任何 SKU 都无法通过。若放行到运行期，现场表现为
    ///   "所有扫描都校验失败"，而工人根本无从判断这是规则冲突还是条码有问题。
    ///   按决策 D-2，与扫到什么码无关的配置错误一律在构造时拒绝。
    ///
    /// English:
    ///   Creates the validator and validates its configuration.
    ///   Steps: (1) reject a negative minimum; (2) reject a negative maximum;
    ///   (3) reject minimum > maximum; (4) store.
    ///
    ///   Step 3 catches a self-contradictory range that no length can satisfy.
    ///   Allowed through to runtime it would present as "every scan fails
    ///   validation", with no way for the operator to tell a contradictory rule
    ///   from a bad barcode. Per decision D-2, configuration errors independent of
    ///   the scanned code are rejected at construction.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：下限或上限为负。 English: A negative minimum or maximum.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：下限大于上限。 English: minimum is greater than maximum.
    /// </exception>
    public LengthSkuValidator(int? minimumLength, int? maximumLength)
    {
        // 步骤 1 / Step 1
        if (minimumLength is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumLength), minimumLength,
                "最小长度不能为负。 The minimum length cannot be negative.");
        }

        // 步骤 2 / Step 2
        if (maximumLength is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength), maximumLength,
                "最大长度不能为负。 The maximum length cannot be negative.");
        }

        // 步骤 3 / Step 3
        if (minimumLength is int lowerBound
            && maximumLength is int upperBound
            && lowerBound > upperBound)
        {
            throw new ArgumentException(
                $"最小长度 {lowerBound} 大于最大长度 {upperBound}，"
                + "该区间不包含任何长度，任何 SKU 都无法通过校验。"
                + $" Minimum length {lowerBound} exceeds maximum {upperBound};"
                + " no SKU could ever satisfy this range.",
                nameof(minimumLength));
        }

        // 步骤 4 / Step 4
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
    }

    /// <summary>
    /// 中文：最小长度。null 表示该项未启用，不对下界作任何约束。
    /// English: The minimum length; null means the rule is not enabled and no
    ///          lower bound is imposed.
    /// </summary>
    public int? MinimumLength { get; }

    /// <summary>
    /// 中文：最大长度。null 表示该项未启用，不对上界作任何约束。
    /// English: The maximum length; null means the rule is not enabled and no
    ///          upper bound is imposed.
    /// </summary>
    public int? MaximumLength { get; }

    /// <summary>
    /// 中文：
    ///   校验 SKU 的长度。
    ///   输入：sku 候选 SKU，不得为 null。
    ///   输出：Valid，或 Invalid 携带一条 TooShort / TooLong。
    ///   步骤：
    ///     1. sku 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. 下限已启用且长度不足时，返回 TooShort，携带实际长度与要求下限；
    ///     3. 上限已启用且长度超出时，返回 TooLong，携带实际长度与要求上限；
    ///     4. 否则通过。
    ///
    ///   步骤 2、3 都把**实际长度和要求的边界一并带回**，而不是只说"长度不对"。
    ///   UI 层据此组装 "SKU 长度应为 8 位，实际 6 位" 这样的提示；若只回一个
    ///   原因码，工人还得自己去设置页查规则才知道差多少。
    ///
    /// English:
    ///   Steps: (1) throw on null; (2) TooShort if below an enabled minimum;
    ///   (3) TooLong if above an enabled maximum; (4) otherwise valid.
    ///
    ///   Steps 2 and 3 return the actual length alongside the required bound
    ///   rather than merely "wrong length". The UI composes "SKU length should be
    ///   8, actually 6" from that; a bare reason code would force the operator to
    ///   open Settings to discover by how much it missed.
    /// </summary>
    public ValidationResult Validate(string sku)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(sku);

        // 步骤 2 / Step 2
        if (MinimumLength is int requiredMinimum && sku.Length < requiredMinimum)
        {
            return new ValidationResult.Invalid(
                new ValidationFailure.TooShort(sku.Length, requiredMinimum));
        }

        // 步骤 3 / Step 3
        if (MaximumLength is int requiredMaximum && sku.Length > requiredMaximum)
        {
            return new ValidationResult.Invalid(
                new ValidationFailure.TooLong(sku.Length, requiredMaximum));
        }

        // 步骤 4 / Step 4
        return ValidationResult.Valid.Instance;
    }
}
