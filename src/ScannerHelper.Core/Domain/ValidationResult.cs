// =============================================================================
// ValidationResult.cs
//
// 中文：
//   校验结果。有效，或无效并附带一组失败原因。
//
//   与 ParseResult 的关键差异：解析失败只有一个原因，校验失败可能有多个。
//   规格 §9 要求所有启用的校验规则都必须通过；决策 D-6 进一步要求组合
//   校验器**收集全部失败原因**而不是遇到第一个就返回。这样工人一次就能
//   看到"长度不对**且**含非法字符"，而不是修好一个再冒出下一个。
//
//   因此 Invalid 携带的是一个失败集合。单个校验器最多产生一条，聚合是
//   组合校验器的职责。
//
//   与 ParseResult 相同的一点：这也是封闭判别式类型，外部无法新增第三个
//   分支，模式匹配时 Valid 与 Invalid 就是全部可能。
//
// English:
//   A validation result: valid, or invalid with a set of failures.
//
//   The key difference from ParseResult: a parse has one failure reason, while
//   validation may have several. Spec §9 requires every enabled rule to pass, and
//   decision D-6 requires the composite to collect all failures rather than
//   short-circuiting, so the operator sees "too short *and* contains an illegal
//   character" at once instead of fixing one and meeting the next.
//
//   Invalid therefore carries a collection. A single validator produces at most
//   one entry; aggregation is the composite's job.
//
//   As with ParseResult, this is a closed discriminated type: no third case can
//   be added, so Valid and Invalid are exhaustive when matching.
//
// 包含的类型 / Types in this file:
//   ValidationResult          抽象基类型，构造函数私有，分支集合封闭
//   ValidationResult.Valid    通过校验
//   ValidationResult.Invalid  未通过，携带失败集合
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：校验结果。只可能是 <see cref="Valid"/> 或 <see cref="Invalid"/> 之一。
/// English: A validation result, always exactly one of <see cref="Valid"/> or
///          <see cref="Invalid"/>.
/// </summary>
public abstract record ValidationResult
{
    /// <summary>
    /// 中文：私有构造函数，封闭分支集合。
    /// English: Private constructor sealing the set of cases.
    /// </summary>
    private ValidationResult()
    {
    }

    /// <summary>
    /// 中文：通过校验。无状态，因此复用单一实例，避免每次校验都产生垃圾——
    ///       扫描是高频操作，绝大多数结果都是通过。
    /// English: Passed. Stateless, so a single instance is reused rather than
    ///          allocating on every scan — scanning is frequent and the
    ///          overwhelming majority of results are valid.
    /// </summary>
    public sealed record Valid : ValidationResult
    {
        /// <summary>
        /// 中文：共享实例。
        /// English: The shared instance.
        /// </summary>
        public static readonly Valid Instance = new();

        private Valid()
        {
        }
    }

    /// <summary>
    /// 中文：未通过校验，携带全部失败原因。
    /// English: Failed, carrying every failure.
    /// </summary>
    public sealed record Invalid : ValidationResult
    {
        /// <summary>
        /// 中文：
        ///   构造无效结果。
        ///   输入：failures 失败集合，不得为 null，且不得为空——"无效但没有
        ///         任何原因"是自相矛盾的状态，UI 将无从展示。
        ///   输出：无效结果实例。
        ///   实现：把传入序列复制为只读数组，防止调用方在构造之后修改集合
        ///   而使结果对象发生变化。
        /// English:
        ///   Creates an invalid result. failures must be non-null and non-empty:
        ///   "invalid for no reason" is a contradictory state the UI could not
        ///   display. The sequence is copied into a read-only array so a caller
        ///   cannot mutate the result after the fact.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// 中文：failures 为 null。 English: failures is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// 中文：failures 为空集合。 English: failures is empty.
        /// </exception>
        public Invalid(IEnumerable<ValidationFailure> failures)
        {
            ArgumentNullException.ThrowIfNull(failures);

            Failures = failures.ToArray();

            if (Failures.Count == 0)
            {
                throw new ArgumentException(
                    "无效结果必须至少携带一条失败原因；"
                    + "\"无效但没有任何原因\" 是自相矛盾的状态。"
                    + " An invalid result must carry at least one failure;"
                    + " \"invalid for no reason\" is a contradictory state.",
                    nameof(failures));
            }
        }

        /// <summary>
        /// 中文：
        ///   便捷构造：单条失败。单个校验器只会产生一条失败，用这个重载可以
        ///   避免在每个校验器里手写数组包装。
        /// English:
        ///   Convenience overload for a single failure. Individual validators
        ///   produce exactly one, so this spares each of them from wrapping it in
        ///   an array by hand.
        /// </summary>
        public Invalid(ValidationFailure failure)
            : this([failure])
        {
        }

        /// <summary>
        /// 中文：全部失败原因，至少一条。
        /// English: Every failure; never empty.
        /// </summary>
        public IReadOnlyList<ValidationFailure> Failures { get; }
    }
}
