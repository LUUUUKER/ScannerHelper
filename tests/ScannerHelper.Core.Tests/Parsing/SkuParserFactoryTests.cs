// =============================================================================
// SkuParserFactoryTests.cs
//
// 中文：
//   配置到解析器的桥接测试（用例编号 PF1~PF10）。
//
//   在此之前，SkuParsingSettings 和两个解析器实现互不相识：配置知道用户填了
//   什么，解析器知道怎么解析，中间没有任何代码。这一组测的就是那一段桥。
//
//   两个关注点：
//
//   1. **默认配置必须开箱可用**（PF10）。全新安装、技术顾问还没来配规则时，
//      程序也得能起来。若默认配置直接让工厂抛异常，第一次启动就崩，而且
//      现场根本无从判断"是没配规则"还是"程序坏了"。
//
//   2. **空白正则模式是配置错误，不是"未启用"**（PF4~PF6）。这与校验层刻意
//      相反：校验规则可选，留空即不启用；解析却是 SKU 模式的必经步骤，
//      没有规则就产不出 SKU。三种空白写法（null、空串、纯空格）都要拦，
//      因为设置页的文本框被清空后交出来的通常是空串而不是 null。
//
//   工厂本身不做任何合法性判断，全部交给解析器构造函数（决策 D-2）。
//   PF7、PF8 验证的就是这些异常确实**穿透**了工厂而不是被吞掉——设置页
//   正是靠"调一次 Create 看抛不抛"来判断配置能否保存（规格 §13.4）。
//
// English:
//   Tests for the bridge from configuration to parser (cases PF1–PF10).
//
//   Until this bridge existed, SkuParsingSettings and the two parsers did not know
//   about each other: the settings knew what the user typed, the parsers knew how to
//   parse, and nothing joined them.
//
//   Two concerns. First, the default configuration must work out of the box (PF10):
//   on a fresh install, before the advisor has configured a rule, the application
//   still has to start. A default that made the factory throw would crash the first
//   launch, with no way on site to tell "no rule configured" from "the program is
//   broken".
//
//   Second, a blank regex pattern is a configuration error rather than "not enabled"
//   (PF4–PF6) — deliberately the opposite of the validation layer, where rules are
//   optional and blank means off. Parsing is mandatory in SKU mode and cannot yield
//   an SKU without a rule. All three spellings of blank (null, empty, whitespace) are
//   rejected, since a cleared text box usually yields an empty string, not null.
//
//   The factory itself judges nothing; validity belongs to the parser constructors
//   (decision D-2). PF7 and PF8 confirm those exceptions propagate *through* the
//   factory rather than being swallowed — the Settings page decides whether a
//   configuration may be saved precisely by calling Create and watching for a throw
//   (spec §13.4).
//
// 包含的测试 / Tests in this file:
//   Builds_the_parser_described_by_the_configuration      PF1 PF2
//   Default_configuration_is_usable_out_of_the_box        PF10
//   Null_settings_are_rejected                            PF3
//   Blank_regex_pattern_is_rejected                       PF4 PF5 PF6
//   Invalid_parser_configuration_propagates               PF7 PF8
//   Undefined_rule_type_is_rejected                       PF9
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Parsing;

public class SkuParserFactoryTests
{
    /// <summary>
    /// 中文：
    ///   PF1、PF2 — 按规则类型构造出对应的解析器，且配置被如实传递。
    ///   输入：无。输出：无（断言）。
    ///
    ///   两条都用规格 §8 的同一个示例条码 ABCD12345678XYZ，期望同一个结果
    ///   12345678。这不是巧合而是有意为之：固定位置与正则是同一件事的两种
    ///   写法，用同一组数据能直接看出桥接有没有把某一路的字段接错。
    ///
    ///   断言解析结果而不是断言"返回的对象是哪个类型"：类型正确但字段接错
    ///   （比如把 Length 接到了 StartPosition 上）依然是坏的，而那种错误只有
    ///   跑一遍解析才看得出来。
    ///
    /// English:
    ///   PF1, PF2 — builds the parser for each rule type with the configuration
    ///   carried across faithfully.
    ///
    ///   Both use spec §8's example code ABCD12345678XYZ and expect the same
    ///   12345678. Deliberately so: fixed position and regex are two spellings of one
    ///   thing, and one shared dataset makes a mis-wired field on either path visible.
    ///
    ///   The assertion is on the parse result rather than on the returned type: the
    ///   right type with fields crossed over — Length wired into StartPosition, say —
    ///   is still broken, and only running a parse reveals it.
    /// </summary>
    [Fact]
    public void Builds_the_parser_described_by_the_configuration()
    {
        const string rawCode = "ABCD12345678XYZ";

        // PF1 —— 固定位置 / fixed position
        var fixedPositionParser = SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = 5,
            Length = 8,
        });

        var fixedPositionResult = Assert.IsType<ParseResult.Success>(
            fixedPositionParser.Parse(rawCode));
        Assert.Equal("12345678", fixedPositionResult.Sku);

        // PF2 —— 正则 / regex
        var regexParser = SkuParserFactory.Create(new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.Regex,
            RegexPattern = @"^.{4}([A-Z0-9]{8})",
            CaptureGroupIndex = 1,
        });

        var regexResult = Assert.IsType<ParseResult.Success>(regexParser.Parse(rawCode));
        Assert.Equal("12345678", regexResult.Sku);
    }

    /// <summary>
    /// 中文：
    ///   PF10 — 全新安装的默认配置能直接构造出解析器，不抛异常。
    ///   输入：无。输出：无（断言）。
    ///
    ///   工人拿到的第一份配置就是这一份：规则类型固定位置、起点 1、长度 1，
    ///   技术顾问还没来配真正的规则。若默认值让工厂抛异常，程序第一次启动
    ///   就会崩在组装阶段——而现场看到的只是"打不开"，完全无从判断是没配
    ///   规则还是程序本身坏了。
    ///
    ///   这条同时钉住了 SkuParsingSettings 的默认值不能被随手改成非法组合
    ///   （例如把 Length 的默认值改成 0）。
    ///
    /// English:
    ///   PF10 — the fresh-install defaults build a parser without throwing.
    ///
    ///   These are the first settings an operator ever has: fixed position, start 1,
    ///   length 1, before the advisor has configured a real rule. Defaults that made
    ///   the factory throw would crash the first launch during composition, and all
    ///   the site would see is "it does not open" — no way to tell an unconfigured
    ///   rule from a broken program.
    ///
    ///   This also pins SkuParsingSettings' defaults against being casually changed
    ///   into an invalid combination, such as a default Length of 0.
    /// </summary>
    [Fact]
    public void Default_configuration_is_usable_out_of_the_box()
    {
        var parser = SkuParserFactory.Create(new AppSettings().SkuParsing);

        var result = Assert.IsType<ParseResult.Success>(parser.Parse("ABCDEF"));
        Assert.Equal("A", result.Sku);
    }

    /// <summary>
    /// 中文：PF3 — settings 为 null 抛 ArgumentNullException，与 Core 中其他
    ///       公开入口对 null 的处理保持一致（决策 D-1）。
    /// English: PF3 — null settings throws, consistent with how every other public
    ///          entry point in Core treats null (decision D-1).
    /// </summary>
    [Fact]
    public void Null_settings_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => SkuParserFactory.Create(null!));
    }

    /// <summary>
    /// 中文：
    ///   PF4~PF6 — 规则类型为正则时，空白模式一律被拒绝。
    ///   输入：caseId；pattern 三种空白写法。输出：无（断言抛 ArgumentException）。
    ///
    ///   覆盖点：
    ///     PF4  null      —— 全新安装尚未配置正则时的取值
    ///     PF5  空串      —— 设置页文本框被清空后最常见的取值
    ///     PF6  纯空格    —— 粘贴规则时带进来的常见误输入
    ///
    ///   ★ 三种都要测，而不只是 null。
    ///     若实现只判 `== null`，空串会一路走到 new Regex("")——那是一个**合法**
    ///     的正则，匹配任何位置的空串。于是 SKU 模式下每一枪都"解析成功"，
    ///     得到一个空 SKU，然后被发送出去。界面上显示着"规则已启用"，
    ///     实际上写进仓库系统的是一批空值。这比抛异常糟得多：抛异常至少
    ///     在设置页保存那一刻就被看见了。
    ///
    ///   与校验层的处理刻意相反（见 SkuValidatorFactoryTests 的 VF3）：
    ///   校验规则可选，留空即不启用；解析是 SKU 模式的必经步骤，没有规则
    ///   就产不出 SKU，留空只能是配置错误。
    ///
    /// English:
    ///   PF4–PF6 — a blank pattern is rejected for a regex rule, in all three
    ///   spellings: null (a fresh install), empty (a cleared text box, the most common
    ///   value by far) and whitespace (dragged in while pasting a rule).
    ///
    ///   All three matter, not just null. An implementation checking only `== null`
    ///   lets an empty string reach new Regex(""), which is a *valid* regex matching
    ///   the empty string anywhere. Every SKU-mode scan would then "parse
    ///   successfully" into an empty SKU and emit it: the screen reports the rule as
    ///   enabled while a run of blanks goes into the warehouse system. Far worse than
    ///   throwing, which at least surfaces when Settings is saved.
    ///
    ///   Deliberately the opposite of the validation layer (see VF3): validation rules
    ///   are optional and blank means off, whereas parsing is mandatory in SKU mode
    ///   and blank can only be a misconfiguration.
    /// </summary>
    [Theory]
    [InlineData("PF4", null)]
    [InlineData("PF5", "")]
    [InlineData("PF6", "   ")]
    public void Blank_regex_pattern_is_rejected(string caseId, string? pattern)
    {
        var settings = new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.Regex,
            RegexPattern = pattern,
            CaptureGroupIndex = 1,
        };

        var exception = Record.Exception(() => SkuParserFactory.Create(settings));

        Assert.True(exception is ArgumentException,
            $"{caseId}: 空白正则模式应在构造时被拒绝，"
            + $"实际为 {exception?.GetType().Name ?? "没有抛出任何异常"}");
    }

    /// <summary>
    /// 中文：
    ///   PF7、PF8 — 解析器构造函数抛出的配置错误必须穿透工厂，不被吞掉。
    ///   输入：无。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     PF7  起始位置为 0    —— 违反"1-based，必须 ≥ 1"（对应 F9）
    ///     PF8  正则语法错误    —— 对应 R7
    ///
    ///   工厂**只组装、不判定**：这两条规则的定义只存在于解析器构造函数里，
    ///   在工厂里再抄一遍就会有两份定义，而两份定义迟早不一致——不一致的
    ///   那一刻没有任何测试会变红。本条钉住的是"工厂没有自作主张地把异常
    ///   捕获掉、也没有自己重新实现一遍判定"。
    ///
    ///   这也是设置页所依赖的行为：保存前先调一次 Create，抛异常就说明这份
    ///   规则不能用（规格 §13.4：非法配置不能静默保存）。若工厂把异常吞了，
    ///   一份非法规则会被静默保存下来，直到工人扫第一枪才发现。
    ///
    /// English:
    ///   PF7, PF8 — configuration errors thrown by the parser constructors propagate
    ///   through the factory rather than being swallowed: a zero start position
    ///   (violating "1-based, must be >= 1", matching F9) and invalid regex syntax
    ///   (matching R7).
    ///
    ///   The factory composes and does not judge. Both rules are defined only in the
    ///   parser constructors; restating them in the factory would create two
    ///   definitions that eventually disagree, and nothing would turn red at the
    ///   moment they did. This test pins that the factory neither catches the
    ///   exception nor reimplements the check.
    ///
    ///   The Settings page depends on exactly this: call Create before saving and
    ///   treat a throw as "this rule cannot be used" (spec §13.4). A factory that
    ///   swallowed the exception would let an invalid rule save silently, to be
    ///   discovered at the operator's first scan.
    /// </summary>
    [Fact]
    public void Invalid_parser_configuration_propagates()
    {
        // PF7 —— 起始位置为 0，违反 1-based 约定 / zero start position
        Assert.Throws<ArgumentOutOfRangeException>(() => SkuParserFactory.Create(
            new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition,
                StartPosition = 0,
                Length = 3,
            }));

        // PF8 —— 正则语法错误 / invalid regex syntax
        Assert.Throws<ArgumentException>(() => SkuParserFactory.Create(
            new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.Regex,
                RegexPattern = "[",
                CaptureGroupIndex = 0,
            }));
    }

    /// <summary>
    /// 中文：
    ///   PF9 — 规则类型不是已定义的枚举值时被拒绝。
    ///   输入：无。输出：无（断言抛 ArgumentOutOfRangeException）。
    ///
    ///   C# 的枚举不做范围检查，(SkuParsingRuleType)99 是完全合法的表达式；
    ///   配置文件里写一个未知的规则类型名，或者手工把它改成一个数字，也可能
    ///   得到同样的结果。不拦下就会落进 switch 的兜底分支，产生一个无法解释
    ///   的失败——而"无法解释"正是仓库现场最查不动的那一类。
    ///
    ///   与 CharacterSetSkuValidator 构造函数里的同类检查是一致的做法。
    ///
    /// English:
    ///   PF9 — a rule type that is not a defined enum value is rejected.
    ///
    ///   C# does not range-check enums, so (SkuParsingRuleType)99 is a perfectly legal
    ///   expression, and a hand-edited configuration naming an unknown rule type can
    ///   produce the same. Unchecked it would fall into the switch's default arm and
    ///   yield an unexplainable failure — the hardest kind to chase on site.
    ///
    ///   The same approach as the equivalent check in CharacterSetSkuValidator's
    ///   constructor.
    /// </summary>
    [Fact]
    public void Undefined_rule_type_is_rejected()
    {
        var settings = new SkuParsingSettings { RuleType = (SkuParsingRuleType)99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => SkuParserFactory.Create(settings));
    }
}
