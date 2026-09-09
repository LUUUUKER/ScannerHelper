// =============================================================================
// ResultTypeGuardTests.cs
//
// 中文：
//   结果类型的守卫测试（对应测试清单 D1~D6）。
//
//   这一组与前面各组性质不同：它们测的不是行为，而是**类型系统本身的形状**。
//   现有实现天然满足这些性质，因此写完即绿——它们的价值不在今天，而在
//   拦住将来那些"看起来很合理"的改动：
//     D4  有人给 Failure 加一个 Sku 属性，或把判别式改回带 IsSuccess 标志的单一类
//     D5  有人把原因码从枚举改成 string Message
//     D6  有人在 Core 里放一份文案表
//
//   ★ D6 的实际覆盖范围必须说清楚，不能夸大。
//     反射看不到方法体内的字符串字面量，因此本测试**抓不到**某人在某个
//     方法里 return "The scan was empty" 这种写法。它能可靠抓到的是另一类
//     ——也是更现实的一类——改动：在 Core 的公开 API 上出现文案载体，
//     例如给失败类型加一个 Message 属性，或新增一个 XxxMessages 静态类。
//     范围比理想中小，但抓得到的部分没有假阳性，不会误伤合法的技术字符串
//     （异常消息、正则模式、配置键名）。
//
//   为什么 Core 不能有面向用户的文案：规格 §12 要求界面同时支持英文和简体
//   中文，全部用户可见文本必须来自资源文件。Core 里一旦写死英文句子，中文
//   界面就无法正确显示，而这个缺陷要等到做本地化时才会暴露——那时相关代码
//   已经写了很多，改起来要翻遍每一处。
//
// English:
//   Guard tests for the result types (test plan D1–D6).
//
//   Unlike the other groups these test the *shape of the type system* rather than
//   behavior. The current implementation satisfies them by construction, so they
//   pass on arrival. Their value is not today but in blocking future changes that
//   look entirely reasonable in isolation: adding an Sku property to Failure,
//   replacing the reason code with a string Message, or dropping a message catalog
//   into Core.
//
//   D6's real coverage must be stated plainly rather than oversold. Reflection
//   cannot see string literals inside method bodies, so this test cannot catch
//   someone returning "The scan was empty" from a method. What it does catch
//   reliably — and this is the more realistic regression — is prose appearing in
//   Core's public API: a Message property on a failure type, or a new XxxMessages
//   class. Narrower than ideal, but what it catches it catches without false
//   positives, and it does not flag legitimate technical strings such as exception
//   messages, regex patterns, or configuration keys.
//
//   Why Core carries no user-facing prose: spec §12 requires English and
//   Simplified Chinese, with all visible text coming from resource files. An
//   English sentence baked into Core cannot render in Chinese, and the defect
//   stays hidden until localization begins — by which point there is a great deal
//   of code to comb through.
//
// 包含的测试 / Tests in this file:
//   Success_carries_its_value                        D1
//   Failure_carries_a_reason_code                    D2
//   Parse_failures_always_retain_the_raw_code        D3
//   Failed_result_exposes_no_success_value           D4
//   Failure_reasons_are_codes_not_prose              D5
//   Core_public_api_exposes_no_user_facing_text      D6
// =============================================================================

using System.Reflection;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;

namespace ScannerHelper.Core.Tests.Domain;

public class ResultTypeGuardTests
{
    /// <summary>
    /// 中文：D1 — 成功结果携带它的值。
    ///       解析成功携带 SKU；校验通过没有需要携带的值，因此复用共享实例。
    /// English: D1 — a successful result carries its value. A successful parse
    ///          carries the SKU; a passing validation has nothing to carry and so
    ///          reuses the shared instance.
    /// </summary>
    [Fact]
    public void Success_carries_its_value()
    {
        var parseSuccess = new ParseResult.Success("12345678");
        Assert.Equal("12345678", parseSuccess.Sku);

        Assert.Same(ValidationResult.Valid.Instance, ValidationResult.Valid.Instance);
    }

    /// <summary>
    /// 中文：D2 — 失败结果携带原因码。
    ///       解析失败携带一个 ParseFailureReason；校验失败携带一组
    ///       ValidationFailure，且集合永不为空（"无效但没有原因"是自相矛盾的）。
    /// English: D2 — a failed result carries a reason. A parse failure carries a
    ///          ParseFailureReason; a validation failure carries a non-empty set of
    ///          ValidationFailure, since "invalid for no reason" is contradictory.
    /// </summary>
    [Fact]
    public void Failure_carries_a_reason_code()
    {
        var parseFailure = new ParseResult.Failure(ParseFailureReason.NoMatch, "ABC");
        Assert.Equal(ParseFailureReason.NoMatch, parseFailure.Reason);

        var validationFailure = new ValidationResult.Invalid(
            new ValidationFailure.TooShort(ActualLength: 6, MinimumLength: 8));
        Assert.NotEmpty(validationFailure.Failures);

        Assert.Throws<ArgumentException>(
            () => new ValidationResult.Invalid(Array.Empty<ValidationFailure>()));
    }

    /// <summary>
    /// 中文：
    ///   D3 — 任何解析失败都必须保留原始条码。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 用两种解析器分别制造多种失败；
    ///     2. 断言每种失败携带的 RawCode 都与输入完全一致。
    ///
    ///   这不是形式要求。规格 §10 规定 F10 强制发送时要原样发出**扫到的码**，
    ///   错误界面也要展示它。若某条失败路径忘了带上原始码，工人在那种错误下
    ///   就无法强制发送，只能重扫——而这种缺失只会在特定失败类型触发时才
    ///   暴露，平时完全看不出来。本条把全部失败路径一起钉住。
    ///
    /// English:
    ///   D3 — every parse failure retains the raw code.
    ///   Not a formality: spec §10 requires Force Send to emit exactly what was
    ///   scanned and the error UI to display it. A failure path that forgot the raw
    ///   code would leave the operator unable to force-send under that particular
    ///   error and force a rescan — and the omission would only surface when that
    ///   specific failure type occurred. This pins every path at once.
    /// </summary>
    [Fact]
    public void Parse_failures_always_retain_the_raw_code()
    {
        const string outOfBoundsInput = "AB";
        const string noMatchInput = "ABC";
        const string missingGroupInput = "ABC";
        const string emptyInput = "";

        (ISkuParser Parser, string RawCode)[] failureScenarios =
        [
            (new FixedPositionSkuParser(startPosition: 5, length: 8), outOfBoundsInput),
            (new FixedPositionSkuParser(startPosition: 1, length: 1), emptyInput),
            (new RegexSkuParser(@"^\d+$", captureGroupIndex: 0), noMatchInput),
            (new RegexSkuParser(@"^([A-Z]+)", captureGroupIndex: 2), missingGroupInput),
            (new RegexSkuParser(@"^\d*$", captureGroupIndex: 0), emptyInput),
        ];

        foreach (var (parser, rawCode) in failureScenarios)
        {
            var failure = Assert.IsType<ParseResult.Failure>(parser.Parse(rawCode));
            Assert.True(failure.RawCode == rawCode,
                $"{parser.GetType().Name} 在 {failure.Reason} 失败下未原样保留原始码。"
                + $" 期望 \"{rawCode}\"，实际 \"{failure.RawCode}\"");
        }
    }

    /// <summary>
    /// 中文：
    ///   D4 — 失败结果不暴露任何"成功值"（决策 D-7）。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 断言 ParseResult.Failure 上不存在名为 Sku / Value / Result 的属性；
    ///     2. 断言 ValidationResult.Valid 上不存在 Failures 属性；
    ///     3. 断言两个基类型的实例构造函数均为私有，外部无法平凡地派生；
    ///     4. 断言全部分支类型均为 sealed。
    ///
    ///   决策 D-7 要求"失败结果的值编译期不可达"。这件事无法用单元测试直接
    ///   验证（不能在测试里写一段不该编译的代码），但可以钉住**使它成立的
    ///   结构性前提**：Failure 上根本没有那个属性，分支集合被封闭。只要这些
    ///   前提还在，不可达性就还在。
    ///
    ///   ★ 一处诚实的说明：record 的封闭并不绝对。
    ///     C# 为非 sealed 的 record 自动生成一个 protected 拷贝构造函数，
    ///     语言规范不允许把它声明为 private。理论上外部程序集可以通过它派生
    ///     出第三个分支。这个洞在实践中无关紧要——那样写出来的代码显然是错的，
    ///     而且本项目 Core 不被外部程序集继承——但测试不该断言一件不成立的事，
    ///     所以这里只断言真正成立的部分。
    ///
    /// English:
    ///   D4 — a failed result exposes no success value (decision D-7).
    ///   D-7 requires the value to be unreachable at compile time. A unit test
    ///   cannot verify that directly — one cannot write code that must not compile
    ///   — but it can pin the structural preconditions that make it true: Failure
    ///   simply has no such property, and the case set is closed. While those hold,
    ///   so does the unreachability.
    ///
    ///   An honest caveat: record closure is not absolute. C# generates a protected
    ///   copy constructor for a non-sealed record and the language forbids declaring
    ///   it private, so an outside assembly could in principle derive a third case
    ///   through it. The hole does not matter in practice — such code is obviously
    ///   wrong and nothing outside inherits from Core — but a test should not assert
    ///   something untrue, so only what actually holds is asserted here.
    /// </summary>
    [Fact]
    public void Failed_result_exposes_no_success_value()
    {
        string[] successValuePropertyNames = ["Sku", "Value", "Result"];

        // 步骤 1 / Step 1
        var failurePropertyNames = typeof(ParseResult.Failure)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(failurePropertyNames, name => successValuePropertyNames.Contains(name));

        // 步骤 2 / Step 2
        var validPropertyNames = typeof(ValidationResult.Valid)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain("Failures", validPropertyNames);

        // 步骤 3 / Step 3 —— 只检查无参/主构造函数，拷贝构造函数见上方说明
        foreach (var baseType in new[] { typeof(ParseResult), typeof(ValidationResult) })
        {
            var nonCopyConstructors = baseType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(constructor => constructor.GetParameters().Length == 0)
                .ToArray();

            Assert.All(nonCopyConstructors, constructor =>
                Assert.True(constructor.IsPrivate,
                    $"{baseType.Name} 的无参构造函数必须是 private，"
                    + "否则外部可以派生出新的结果分支，模式匹配不再穷尽。"));
        }

        // 步骤 4 / Step 4
        Type[] caseTypes =
        [
            typeof(ParseResult.Success), typeof(ParseResult.Failure),
            typeof(ValidationResult.Valid), typeof(ValidationResult.Invalid),
            typeof(ValidationFailure.TooShort), typeof(ValidationFailure.TooLong),
            typeof(ValidationFailure.IllegalCharacter),
            typeof(ValidationFailure.PatternMismatch), typeof(ValidationFailure.RegexTimeout),
        ];
        Assert.All(caseTypes, caseType =>
            Assert.True(caseType.IsSealed, $"{caseType.Name} 必须是 sealed，分支不得被继续派生。"));
    }

    /// <summary>
    /// 中文：
    ///   D5 — 失败原因是**代码**而不是文案。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 断言 ParseResult.Failure.Reason 的类型是枚举；
    ///     2. 断言 ValidationFailure 的各分支只携带结构化数据，
    ///        不存在名为 Message / Description / Text 之类的属性。
    ///
    ///   注意这里**不能**简单地禁止 string 类型的属性：
    ///   PatternMismatch 携带的 Pattern 是一条正则，是数据不是文案，
    ///   它对诊断日志的自解释性是必要的（规格 §15）。区分标准是**属性名**
    ///   所表达的意图，而不是它的类型。
    ///
    /// English:
    ///   D5 — failure reasons are codes, not prose.
    ///   Note that string-typed properties cannot simply be banned: PatternMismatch
    ///   carries a Pattern, which is data rather than prose and is needed for the
    ///   log to be self-describing (spec §15). The discriminator is what the
    ///   property name claims to be, not its type.
    /// </summary>
    [Fact]
    public void Failure_reasons_are_codes_not_prose()
    {
        // 步骤 1 / Step 1
        var reasonPropertyType = typeof(ParseResult.Failure)
            .GetProperty(nameof(ParseResult.Failure.Reason))!
            .PropertyType;
        Assert.True(reasonPropertyType.IsEnum,
            $"解析失败原因必须是枚举，实际为 {reasonPropertyType.Name}。"
            + " 文案由 UI 层从 .resx 组装（规格 §12）。");

        // 步骤 2 / Step 2
        Assert.All(ProseSuggestingMemberNames, prohibitedName =>
            Assert.DoesNotContain(
                ValidationFailureCaseTypes.SelectMany(type => type.GetProperties()),
                property => property.Name.Contains(prohibitedName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 中文：
    ///   D6 — Core 的公开 API 上不出现面向用户的文案载体。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 取出 Core 程序集全部公开类型；
    ///     2. 断言没有类型名暗示它是文案表（Messages / Strings / Resources / Texts）；
    ///     3. 断言没有公开成员名暗示它承载文案（Message / Description / Text 等）。
    ///
    ///   ★ 覆盖范围的诚实说明：反射看不到方法体内的字符串字面量。若有人写
    ///     出 return "The scan was empty"，本测试**抓不到**。它能可靠抓到的是
    ///     更现实的那类改动：在公开 API 上冒出文案载体，例如给失败类型加一个
    ///     Message 属性，或新增一个 XxxMessages 静态类——那正是"顺手在 Core
    ///     里放一份文案"的通常形态。
    ///
    ///     选择这个范围是因为它没有假阳性。若改成扫描 IL 中的全部字符串
    ///     字面量，异常消息、正则模式、配置键名都会被误伤，而本项目的异常
    ///     消息恰恰是刻意写成中英双语长句的——那些是给开发者看的，不是给
    ///     工人看的，不该被拦。一条会频繁误报的测试很快就会被加例外、被忽略，
    ///     最后被删掉。
    ///
    /// English:
    ///   D6 — Core's public API exposes no user-facing text carrier.
    ///   Coverage stated honestly: reflection cannot see literals inside method
    ///   bodies, so `return "The scan was empty"` is not caught. What is caught
    ///   reliably is the more realistic regression — prose carriers appearing in the
    ///   public API, such as a Message property on a failure type or a new
    ///   XxxMessages class, which is the usual shape of "let me just put the text in
    ///   Core".
    ///
    ///   This scope was chosen because it has no false positives. Scanning every IL
    ///   string literal would flag exception messages, regex patterns and config
    ///   keys — and this project's exception messages are deliberately long
    ///   bilingual sentences aimed at developers, not operators, which must not be
    ///   blocked. A test that cries wolf acquires exceptions, then gets ignored,
    ///   then gets deleted.
    /// </summary>
    [Fact]
    public void Core_public_api_exposes_no_user_facing_text()
    {
        // 步骤 1 / Step 1
        var coreTypes = typeof(ParseResult).Assembly.GetExportedTypes();

        // 步骤 2 / Step 2
        string[] proseSuggestingTypeNames = ["Messages", "Strings", "Resources", "Texts", "Localization"];
        var offendingTypes = coreTypes
            .Where(type => proseSuggestingTypeNames.Any(
                suffix => type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .Select(type => type.FullName)
            .ToArray();

        Assert.True(offendingTypes.Length == 0,
            "Core 不得包含文案表。全部用户可见文本必须来自 UI 层的 .resx"
            + $"（规格 §12）。发现：{string.Join(", ", offendingTypes)}");

        // 步骤 3 / Step 3
        var offendingMembers = coreTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(member => member is PropertyInfo or FieldInfo)
            .Where(member => ProseSuggestingMemberNames.Any(
                prohibited => member.Name.Contains(prohibited, StringComparison.OrdinalIgnoreCase)))
            .Select(member => $"{member.DeclaringType?.Name}.{member.Name}")
            .ToArray();

        Assert.True(offendingMembers.Length == 0,
            "Core 的公开成员不得承载面向用户的文案，只应提供结构化的原因码与数据，"
            + $"由 UI 层组装本地化文本（规格 §12）。发现：{string.Join(", ", offendingMembers)}");
    }

    /// <summary>
    /// 中文：暗示"承载文案"的成员名。判别依据是名字表达的意图，而非类型——
    ///       PatternMismatch.Pattern 是 string 但属于数据，不在此列。
    /// English: Member names that claim to carry prose. The discriminator is intent
    ///          expressed by the name, not the type — PatternMismatch.Pattern is a
    ///          string but is data, and is not listed here.
    /// </summary>
    private static readonly string[] ProseSuggestingMemberNames =
        ["Message", "Description", "DisplayName", "Localized", "Caption", "Hint"];

    /// <summary>
    /// 中文：ValidationFailure 的全部分支类型。
    /// English: Every case type of ValidationFailure.
    /// </summary>
    private static readonly Type[] ValidationFailureCaseTypes =
    [
        typeof(ValidationFailure.TooShort),
        typeof(ValidationFailure.TooLong),
        typeof(ValidationFailure.IllegalCharacter),
        typeof(ValidationFailure.PatternMismatch),
        typeof(ValidationFailure.RegexTimeout),
    ];
}
