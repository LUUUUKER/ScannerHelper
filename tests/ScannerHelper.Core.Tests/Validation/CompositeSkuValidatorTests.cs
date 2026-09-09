// =============================================================================
// CompositeSkuValidatorTests.cs
//
// 中文：
//   组合校验器的行为测试（对应测试清单 P1~P11）。
//
//   规格 §9 的两条规则在这里落地：
//     1. 所有启用的校验规则都必须通过；
//     2. 没有启用任何规则时，解析成功即视为有效。
//
//   决策 D-6 是本文件的核心：组合校验器必须**收集全部失败原因**，而不是
//   遇到第一个就返回。P7 是这条决策的可执行版本。
//
//   为什么收集全部比短路重要：短路的实现让工人陷入"修一个冒一个"的循环——
//   改完长度再扫一次，才发现还有非法字符；改完字符再扫一次，又发现别的。
//   每一轮都要重新扫码、重新等待。一次把问题说全，工人一次就能改对。
//
//   本文件大量使用桩校验器（StubSkuValidator）而非真实校验器。原因是这里
//   要验证的是**组合逻辑本身**——多少条失败、怎么合并、顺序是否影响结果——
//   而不是长度或字符集的判断规则。用真实校验器会让"为什么这条失败了"混入
//   被测逻辑，一旦某个基础校验器的行为变化，本文件会跟着莫名其妙地红。
//   只有 P9 用真实校验器，用来确认三种类型确实能装配在一起。
//
// English:
//   Behavior tests for the composite validator (test plan P1–P11).
//
//   Spec §9 lands here: every enabled rule must pass, and with no rule enabled a
//   successful parse is valid. Decision D-6 is the core of this file: the
//   composite collects *all* failures rather than short-circuiting, and P7 is that
//   decision in executable form.
//
//   Why collecting matters: a short-circuiting implementation traps the operator
//   in fix-one-find-one. Correct the length, rescan, discover an illegal
//   character; correct that, rescan, discover something else — each round costs
//   another scan and another wait. Reporting everything at once lets them fix it
//   in one pass.
//
//   Stub validators are used throughout rather than real ones, because what is
//   under test is the *combining* logic — how many failures, how they merge,
//   whether order matters — not the length or character-set rules. Real
//   validators would mix "why did this one fail" into the subject under test, and
//   a change to any base validator would turn this file inexplicably red. Only P9
//   uses real validators, to confirm the three types do assemble together.
//
// 包含的测试 / Tests in this file:
//   No_validators_is_valid                       P1
//   Single_validator_result_is_passed_through    P2 P3
//   Any_failing_validator_makes_the_result_invalid  P4 P5 P6
//   All_failures_are_collected                   P7
//   Order_does_not_change_the_reported_failures  P8
//   Real_validators_compose                      P9
//   Null_sku_throws                              P10
//   Null_validator_collection_is_rejected        P11
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Validation;

namespace ScannerHelper.Core.Tests.Validation;

public class CompositeSkuValidatorTests
{
    /// <summary>
    /// 中文：
    ///   测试用桩校验器：永远返回预设的结果，不做任何实际判断。
    ///   用它可以精确控制"第几个校验器通过、第几个失败"，把组合逻辑单独
    ///   隔离出来测试。
    /// English:
    ///   A stub validator that always returns a preset result. It lets each test
    ///   control exactly which validators pass and which fail, isolating the
    ///   combining logic.
    /// </summary>
    private sealed class StubSkuValidator : ISkuValidator
    {
        private readonly ValidationResult _result;

        private StubSkuValidator(ValidationResult result) => _result = result;

        /// <summary>
        /// 中文：构造一个永远通过的桩。
        /// English: A stub that always passes.
        /// </summary>
        public static StubSkuValidator AlwaysPasses()
            => new(ValidationResult.Valid.Instance);

        /// <summary>
        /// 中文：构造一个永远以指定失败原因失败的桩。
        /// English: A stub that always fails with the given failure.
        /// </summary>
        public static StubSkuValidator AlwaysFailsWith(ValidationFailure failure)
            => new(new ValidationResult.Invalid(failure));

        public ValidationResult Validate(string sku) => _result;
    }

    private static readonly ValidationFailure TooShortFailure =
        new ValidationFailure.TooShort(ActualLength: 6, MinimumLength: 8);

    private static readonly ValidationFailure IllegalCharacterFailure =
        new ValidationFailure.IllegalCharacter(Character: '#', Position: 3);

    /// <summary>
    /// 中文：
    ///   P1 — 没有任何校验器时结果有效。
    ///   输入：无。输出：无（断言）。
    ///   规格 §9 的原话：如果没有启用任何校验规则，解析成功就意味着 SKU 有效。
    ///   这不是边界情况而是常见配置——很多站点只需要解析，不需要额外校验。
    /// English:
    ///   P1 — no validators means valid. Spec §9: with no rule enabled, a
    ///   successful parse means the SKU is valid. Not an edge case but a common
    ///   configuration — many sites need parsing only.
    /// </summary>
    [Fact]
    public void No_validators_is_valid()
    {
        var composite = new CompositeSkuValidator([]);

        var result = composite.Validate("ANYTHING");

        Assert.IsType<ValidationResult.Valid>(result);
    }

    /// <summary>
    /// 中文：
    ///   P2、P3 — 只有一个校验器时，组合结果就是它的结果。
    ///   输入：caseId；validatorPasses 该校验器是否通过。输出：无（断言）。
    ///   组合器不应在只有一个成员时改变语义，也不应额外包装或吞掉失败原因。
    /// English:
    ///   P2, P3 — with a single validator, the composite result is that
    ///   validator's result. The composite must not alter semantics or wrap away
    ///   the failure when there is only one member.
    /// </summary>
    [Theory]
    [InlineData("P2", true)]
    [InlineData("P3", false)]
    public void Single_validator_result_is_passed_through(string caseId, bool validatorPasses)
    {
        var validator = validatorPasses
            ? StubSkuValidator.AlwaysPasses()
            : StubSkuValidator.AlwaysFailsWith(TooShortFailure);
        var composite = new CompositeSkuValidator([validator]);

        var result = composite.Validate("ABC123");

        if (validatorPasses)
        {
            Assert.True(result is ValidationResult.Valid,
                $"{caseId}: 唯一的校验器通过，组合结果应为 Valid");
        }
        else
        {
            var invalid = Assert.IsType<ValidationResult.Invalid>(result);
            var failure = Assert.Single(invalid.Failures);
            Assert.True(ReferenceEquals(failure, TooShortFailure),
                $"{caseId}: 组合器应原样传递失败原因，不得包装或替换");
        }
    }

    /// <summary>
    /// 中文：
    ///   P4、P5、P6 — 只要有任何一个校验器失败，整体就无效。
    ///   输入：caseId；firstPasses；secondPasses。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     P4  两个都通过 → 有效
    ///     P5  第一个失败、第二个通过 → 无效
    ///     P6  第一个通过、第二个失败 → 无效
    ///
    ///   P5 与 P6 是同一件事的两个方向，两条都需要：只留 P5 无法排除"实现
    ///   只看第一个校验器"，只留 P6 无法排除"实现只看最后一个"。
    ///
    /// English:
    ///   P4, P5, P6 — any failing validator makes the whole result invalid.
    ///   P5 and P6 are the two directions of one property and both are needed:
    ///   P5 alone cannot rule out an implementation that only consults the first
    ///   validator, P6 alone cannot rule out one that only consults the last.
    /// </summary>
    [Theory]
    [InlineData("P4", true, true)]
    [InlineData("P5", false, true)]
    [InlineData("P6", true, false)]
    public void Any_failing_validator_makes_the_result_invalid(
        string caseId, bool firstPasses, bool secondPasses)
    {
        var composite = new CompositeSkuValidator(
        [
            firstPasses ? StubSkuValidator.AlwaysPasses()
                        : StubSkuValidator.AlwaysFailsWith(TooShortFailure),
            secondPasses ? StubSkuValidator.AlwaysPasses()
                         : StubSkuValidator.AlwaysFailsWith(IllegalCharacterFailure),
        ]);

        var result = composite.Validate("ABC123");

        var expectedValid = firstPasses && secondPasses;
        Assert.True((result is ValidationResult.Valid) == expectedValid,
            $"{caseId}: 第一个通过={firstPasses} 第二个通过={secondPasses}，"
            + $"期望 {(expectedValid ? "Valid" : "Invalid")}，实际 {result.GetType().Name}");
    }

    /// <summary>
    /// 中文：
    ///   P7 — 多个校验器都失败时，全部失败原因都必须出现在结果里（决策 D-6）。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 组装两个必定失败、且失败原因互不相同的校验器；
    ///     2. 执行校验；
    ///     3. 断言结果中恰好有两条失败，且两条原因都在。
    ///
    ///   这是本文件最重要的一条。短路实现会让工人陷入"修一个冒一个"：
    ///   改完长度再扫一次，才发现还有非法字符；改完字符再扫一次，又发现
    ///   别的。每一轮都要重新扫码、重新等待。一次把问题说全，工人一次就
    ///   能改对。
    ///
    ///   断言"恰好两条"而不只是"至少两条"：重复报告同一条失败同样会让
    ///   错误界面变得难读。
    ///
    /// English:
    ///   P7 — when several validators fail, every failure must appear (D-6).
    ///   The most important test here. A short-circuiting implementation traps the
    ///   operator in fix-one-find-one, each round costing another scan and another
    ///   wait. Asserting *exactly* two rather than *at least* two also rules out
    ///   duplicate reporting, which would make the error screen equally hard to read.
    /// </summary>
    [Fact]
    public void All_failures_are_collected()
    {
        // 步骤 1 / Step 1
        var composite = new CompositeSkuValidator(
        [
            StubSkuValidator.AlwaysFailsWith(TooShortFailure),
            StubSkuValidator.AlwaysFailsWith(IllegalCharacterFailure),
        ]);

        // 步骤 2 / Step 2
        var result = composite.Validate("AB#");

        // 步骤 3 / Step 3
        var invalid = Assert.IsType<ValidationResult.Invalid>(result);
        Assert.Equal(2, invalid.Failures.Count);
        Assert.Contains(TooShortFailure, invalid.Failures);
        Assert.Contains(IllegalCharacterFailure, invalid.Failures);
    }

    /// <summary>
    /// 中文：
    ///   P8 — 交换校验器的顺序，报告出的失败集合不变。
    ///   输入：无。输出：无（断言）。
    ///
    ///   注意断言的是**集合相同**，不是列表顺序相同。失败在结果中按校验器
    ///   的配置顺序排列，交换校验器自然会交换这两条的先后——那是预期行为，
    ///   有利于界面展示的稳定性。本条要排除的是更严重的情况：某种顺序下
    ///   某条失败干脆丢了，或者多冒出一条。
    ///
    /// English:
    ///   P8 — swapping validator order does not change the set of failures
    ///   reported. Note the assertion is on the *set*, not on list order: failures
    ///   appear in configuration order, so swapping validators naturally swaps
    ///   them, which is intended and keeps the UI stable. What this rules out is
    ///   worse: a failure being dropped, or an extra one appearing, under some
    ///   ordering.
    /// </summary>
    [Fact]
    public void Order_does_not_change_the_reported_failures()
    {
        var forwardOrder = new CompositeSkuValidator(
        [
            StubSkuValidator.AlwaysFailsWith(TooShortFailure),
            StubSkuValidator.AlwaysFailsWith(IllegalCharacterFailure),
        ]);
        var reversedOrder = new CompositeSkuValidator(
        [
            StubSkuValidator.AlwaysFailsWith(IllegalCharacterFailure),
            StubSkuValidator.AlwaysFailsWith(TooShortFailure),
        ]);

        var forwardResult = Assert.IsType<ValidationResult.Invalid>(forwardOrder.Validate("AB#"));
        var reversedResult = Assert.IsType<ValidationResult.Invalid>(reversedOrder.Validate("AB#"));

        Assert.Equal(
            forwardResult.Failures.OrderBy(failure => failure.GetType().Name).ToArray(),
            reversedResult.Failures.OrderBy(failure => failure.GetType().Name).ToArray());
    }

    /// <summary>
    /// 中文：
    ///   P9 — 三种真实校验器可以装配在一起，全部通过时结果有效。
    ///   输入：无。输出：无（断言）。
    ///   本文件其余用例都用桩校验器隔离组合逻辑，这一条则确认真实类型确实
    ///   实现了同一个接口、能被组合器接受——这是纯桩测试无法覆盖的装配面。
    /// English:
    ///   P9 — the three real validators assemble together and pass. Every other
    ///   test here uses stubs to isolate the combining logic; this one confirms the
    ///   real types do implement the same interface and are accepted by the
    ///   composite, an assembly surface pure stubbing cannot cover.
    /// </summary>
    [Fact]
    public void Real_validators_compose()
    {
        var composite = new CompositeSkuValidator(
        [
            new LengthSkuValidator(minimumLength: 3, maximumLength: 10),
            new CharacterSetSkuValidator(CharacterSetPreset.LettersNumbers, ignoreCase: true),
            new RegexSkuValidator(@"^[A-Z]{3}\d+$"),
        ]);

        var result = composite.Validate("ABC12345");

        Assert.IsType<ValidationResult.Valid>(result);
    }

    /// <summary>
    /// 中文：P10 — Validate(null) 抛 ArgumentNullException（决策 D-1）。
    /// English: P10 — Validate(null) throws (D-1).
    /// </summary>
    [Fact]
    public void Null_sku_throws()
    {
        var composite = new CompositeSkuValidator([StubSkuValidator.AlwaysPasses()]);

        Assert.Throws<ArgumentNullException>(() => composite.Validate(null!));
    }

    /// <summary>
    /// 中文：P11 — 校验器集合为 null 时构造失败。
    ///       空集合是合法配置（P1），null 则是调用方缺陷，两者必须区别对待——
    ///       与解析器对空串和 null 的区分是同一个道理（决策 D-1）。
    /// English: P11 — a null validator collection is rejected at construction. An
    ///          empty collection is a legitimate configuration (P1); null can only
    ///          be a caller defect. The same distinction the parsers draw between
    ///          "" and null (D-1).
    /// </summary>
    [Fact]
    public void Null_validator_collection_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeSkuValidator(null!));
    }
}
