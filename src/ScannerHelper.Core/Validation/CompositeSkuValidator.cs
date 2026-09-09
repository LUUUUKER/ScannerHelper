// =============================================================================
// CompositeSkuValidator.cs
//
// 中文：
//   把若干个校验器组合成一个（规格 §9）。
//
//   规格 §9 的两条规则在这里落地：
//     1. 所有启用的校验规则都必须通过；
//     2. 没有启用任何规则时，解析成功即视为有效。
//   第 2 条不是边界情况而是常见配置——很多站点只需要解析，不需要额外校验。
//
//   ★ 决策 D-6：必须收集**全部**失败原因，绝不能遇到第一个失败就返回。
//
//     短路实现在代码上更短、更"高效"，但它把工人推进"修一个冒一个"的循环：
//     改完长度再扫一次，才发现还有非法字符；改完字符再扫一次，又发现别的。
//     每一轮都要重新扫码、重新等待。一次把问题说全，工人一次就能改对。
//
//     这里省下的那几次多余判断，代价是仓库现场的若干轮返工。对一个每天要
//     扫几千次的工位来说，这个交换方向是明确的。
//
//   本类对成员校验器只做一件事：全部调用一遍，把失败合并起来。它不理解
//   任何具体的校验规则，也不该理解——新增一种校验只需实现 ISkuValidator，
//   本类无需改动。
//
// English:
//   Combines several validators into one (spec §9).
//
//   Spec §9 lands here: every enabled rule must pass, and with no rule enabled a
//   successful parse is valid. The second is not an edge case but a common
//   configuration — many sites need parsing only.
//
//   Decision D-6: collect *every* failure; never return on the first one.
//
//   A short-circuiting implementation is shorter and looks more efficient, but it
//   traps the operator in fix-one-find-one: correct the length, rescan, discover
//   an illegal character; correct that, rescan, discover something else. Each
//   round costs another scan and another wait. The few redundant checks saved here
//   are paid for in rounds of rework on the warehouse floor, and for a station
//   scanning thousands of times a day that trade runs one way.
//
//   This class does one thing to its members: run them all and merge the failures.
//   It understands no specific rule and should not — adding a new kind of
//   validation means implementing ISkuValidator, with no change here.
//
// 包含的成员 / Members in this file:
//   Validators  组合的成员校验器
//   Validate    依次执行全部成员并合并失败
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：组合校验器。构造后不可变，可安全复用。
/// English: Composite validator. Immutable after construction and safe to reuse.
/// </summary>
public sealed class CompositeSkuValidator : ISkuValidator
{
    /// <summary>
    /// 中文：
    ///   构造组合校验器。
    ///   输入：validators 成员校验器序列，不得为 null；空集合是合法配置。
    ///   输出：组合校验器实例。
    ///   步骤：
    ///     1. validators 为 null 时抛 ArgumentNullException；
    ///     2. 复制为只读数组并保存。
    ///
    ///   空集合与 null 被刻意区别对待：空集合表示"没有启用任何校验规则"，
    ///   是规格 §9 明确支持的常见配置；null 则只可能是调用方缺陷。这与解析器
    ///   区分空串与 null 是同一个道理（决策 D-1）。
    ///
    ///   步骤 2 的复制不是多余：若直接持有调用方传入的集合引用，对方之后
    ///   往里增删成员，本校验器的行为就会在运行中悄悄改变，而它对外宣称
    ///   是不可变的。
    ///
    /// English:
    ///   Creates the composite. validators must be non-null; an empty collection is
    ///   a legitimate configuration.
    ///   Steps: (1) reject null; (2) copy into a read-only array and store.
    ///
    ///   Empty and null are deliberately different: empty means "no rule enabled",
    ///   a common configuration spec §9 explicitly supports, while null can only be
    ///   a caller defect — the same distinction the parsers draw between "" and
    ///   null (D-1).
    ///
    ///   The copy in step 2 is not redundant: holding the caller's collection would
    ///   let them add or remove members later and silently change this validator's
    ///   behavior at runtime, despite its claim to be immutable.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：validators 为 null。 English: validators is null.
    /// </exception>
    public CompositeSkuValidator(IEnumerable<ISkuValidator> validators)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(validators);

        // 步骤 2 / Step 2
        Validators = validators.ToArray();
    }

    /// <summary>
    /// 中文：组合的成员校验器。可以为空，表示未启用任何校验规则。
    /// English: The member validators. May be empty, meaning no rule is enabled.
    /// </summary>
    public IReadOnlyList<ISkuValidator> Validators { get; }

    /// <summary>
    /// 中文：
    ///   依次执行全部成员校验器，并把它们的失败合并成一个结果。
    ///   输入：sku 候选 SKU，不得为 null。
    ///   输出：全部成员都通过时返回 Valid；否则返回 Invalid，携带所有成员
    ///         报告的全部失败，按成员的配置顺序排列。
    ///   步骤：
    ///     1. sku 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. 依次调用每个成员，把失败结果中的失败原因追加进收集列表——
    ///        **不在此处提前返回**（决策 D-6）；
    ///     3. 收集列表为空则通过；
    ///     4. 否则返回 Invalid，携带全部收集到的失败。
    ///
    ///   步骤 2 是本类存在的全部意义所在。写成"遇到失败即 return"只少一行，
    ///   却会让工人每修一个问题就得重新扫一次码才能发现下一个。
    ///
    ///   收集列表按成员顺序追加，因此失败在结果中的先后与配置顺序一致。
    ///   这让错误界面的展示顺序稳定，同一种错误组合每次看起来都一样，
    ///   工人不必每次重新定位。
    ///
    ///   步骤 3 直接返回共享的 Valid 实例，不分配对象——校验发生在每次扫描
    ///   的链路上，而绝大多数扫描都是通过的。
    ///
    /// English:
    ///   Runs every member and merges their failures.
    ///   Steps: (1) throw on null; (2) call each member and append any failures to
    ///   a list, *without returning early* (D-6); (3) valid if nothing was
    ///   collected; (4) otherwise Invalid carrying everything collected.
    ///
    ///   Step 2 is the whole point of this class. Returning on the first failure
    ///   saves one line and costs the operator a rescan for every problem they fix.
    ///
    ///   Failures are appended in member order, so their order in the result
    ///   follows configuration order. That keeps the error screen stable: the same
    ///   combination of problems always looks the same, so the operator does not
    ///   have to re-orient each time.
    ///
    ///   Step 3 returns the shared Valid instance without allocating — validation
    ///   sits on every scan's path and the overwhelming majority of scans pass.
    /// </summary>
    public ValidationResult Validate(string sku)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(sku);

        // 步骤 2 / Step 2 —— 不提前返回，全部跑完 / no early return; run them all
        List<ValidationFailure>? collectedFailures = null;

        foreach (var validator in Validators)
        {
            if (validator.Validate(sku) is ValidationResult.Invalid invalid)
            {
                collectedFailures ??= [];
                collectedFailures.AddRange(invalid.Failures);
            }
        }

        // 步骤 3 / Step 3
        if (collectedFailures is null)
        {
            return ValidationResult.Valid.Instance;
        }

        // 步骤 4 / Step 4
        return new ValidationResult.Invalid(collectedFailures);
    }
}
