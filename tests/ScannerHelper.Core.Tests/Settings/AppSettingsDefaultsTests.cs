// =============================================================================
// AppSettingsDefaultsTests.cs
//
// 中文：
//   设置模型的默认值与禁止持久化项（对应测试清单 S1~S13）。
//
//   默认值不是随手填的，每一个都对应规格里的一句话，而且都偏向"出厂即可用、
//   出错也安全"：
//     声音默认开    工人需要听得见模式切换与错误，静音要由他主动选择
//     记住窗口位置默认开  换班的工人希望窗口还在原处
//     置顶默认关    主窗口默认不挡住业务软件；Compact 的置顶是强制的，
//                   不由此项控制（规格 §11.4）
//     开机启动默认关 装了就自动跑属于对使用者环境的擅自决定
//
//   ★ S11~S13 是本文件真正的重点：当前扫描模式与暂停状态**绝不能被持久化**。
//     规格 §3 要求启动恒为 SN、§5.7 要求启动恒为未暂停。这两条不是偏好而是
//     安全要求：工人每次启动看到的必须是同一个已知状态。如果上次退出时停在
//     SKU 模式、这次启动还是 SKU，工人扫第一枪时并不知道自己处在哪个模式，
//     错误数据就是这么产生的。
//
//     防线有三层：ModeManager 构造函数不接受任何持久化依赖（测试 M7）、
//     设置模型里根本没有这两个字段（S11、S12）、序列化产物里也不出现
//     （S13）。三层各自独立，任何一层单独失守都还有另外两层。
//
// English:
//   Default values and never-persisted fields for the settings model (S1–S13).
//
//   No default is arbitrary; each maps to a line in the spec and each leans toward
//   "usable out of the box, safe when wrong": sounds on (the operator must hear
//   mode changes and errors; silence should be a deliberate choice), remember
//   window position on, always-on-top off (the main window should not cover the
//   business app by default — Compact's topmost is mandatory and not governed by
//   this setting, spec §11.4), start-with-Windows off (auto-running on install
//   presumes too much about someone else's machine).
//
//   S11–S13 are the real point of this file: the current scan mode and the paused
//   state must never be persisted. Spec §3 requires every launch to start in SN and
//   §5.7 requires it to start un-paused. These are safety requirements, not
//   preferences: the operator must see the same known state at every launch. If a
//   session that ended in SKU mode reopened in SKU mode, the first scan would
//   happen without the operator knowing which mode they were in — which is exactly
//   how wrong data gets written.
//
//   Three independent layers defend this: ModeManager takes no persistence
//   dependency (M7), the settings model has no such field (S11, S12), and the
//   serialized output contains no such key (S13). Any one layer failing leaves two.
//
// 包含的测试 / Tests in this file:
//   Defaults_match_the_specification            S1~S7 S9 S10
//   Hotkey_defaults_are_F8_F10_Escape_and_unassigned_pause  S8
//   No_settings_type_carries_the_scan_mode      S11
//   No_settings_type_carries_the_paused_state   S12
//   Serialized_settings_contain_no_mode_or_paused_key  S13
//   Non_nullable_settings_sections_reject_null  S31
// =============================================================================

using System.Reflection;
using System.Text.Json;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Settings;

public class AppSettingsDefaultsTests
{
    /// <summary>
    /// 中文：
    ///   S1~S7、S9、S10 — 新建的设置对象各字段取值符合规格。
    ///   输入：无。输出：无（断言）。
    ///
    ///   逐项对应：
    ///     S1   SchemaVersion = 1              从 V1 起就带版本号，为将来迁移留路
    ///     S2   Language 未设置                首次启动才去看系统语言（规格 §12）
    ///     S3   StartWithWindows = false
    ///     S4   ModeSwitchSoundEnabled = false 决策 D-28：默认安静
    ///     S5   ErrorSoundEnabled = false      决策 D-28：同上
    ///     S6   RememberWindowPosition = true
    ///     S7   FullWindowAlwaysOnTop = false
    ///     S9   SkuValidation.IgnoreCase = true 规格 §9.2 默认开启
    ///     S10  AppendEnterAfterScan = false    决策 D-10
    ///
    ///   S2 用"未设置"而不是直接填 "en-US"：规格 §12 要求首次启动时检查
    ///   Windows 界面语言，中文系统用中文、其余用英文，并在用户手动选择后
    ///   才固定下来。若默认值直接写成某个具体语言，就无法区分"用户选了英文"
    ///   和"用户还没选过"——前者应当保持英文，后者应当跟随系统。
    ///
    /// English:
    ///   S1–S7, S9, S10 — a new settings object matches the specification.
    ///
    ///   S2 is "unset" rather than "en-US" because spec §12 requires the first
    ///   launch to follow the Windows UI culture and only fix the choice once the
    ///   user makes one explicitly. A concrete default would make "the user chose
    ///   English" indistinguishable from "the user has not chosen" — the first must
    ///   stay English, the second must follow the system.
    /// </summary>
    [Fact]
    public void Defaults_match_the_specification()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.SchemaVersion);                    // S1
        Assert.Null(settings.Language);                             // S2
        Assert.False(settings.StartWithWindows);                    // S3
        // ★ S4、S5 —— 决策 D-28 把这两项从"默认开启"改成了"默认关闭"，
        //   取代规格 §7 的「切换模式时默认出声」。
        //
        //   仓库本来就不安静。一个没人要求就每次切模式都响的程序发出的是噪声，
        //   而被无视的提示音不只是没用——它会让人对这个程序发出的**所有**声音
        //   都变得不敏感，包括真正要紧的那一声。默认安静、要用的人自己打开，
        //   才能让"响了"保持分量。
        //
        //   声音关着并不削弱告知：出错时整块面板变红、带白色斜条纹、Compact
        //   自动展开成 Full（规格 §11.7）。声音始终是辅助通道。
        // S4 and S5 — decision D-28 turned both off by default, superseding spec §7's "play a
        // short mode-change sound by default". A warehouse is never quiet, an unrequested beep on
        // every mode change is noise, and an ignored sound dulls the operator to every sound this
        // program makes, including the one that matters. Being off weakens nothing: an error turns
        // the panel red with a hazard stripe and expands Compact to Full (spec §11.7).
        Assert.False(settings.ModeSwitchSoundEnabled);              // S4
        Assert.False(settings.ErrorSoundEnabled);                   // S5
        Assert.True(settings.RememberWindowPosition);               // S6
        Assert.False(settings.FullWindowAlwaysOnTop);               // S7
        Assert.True(settings.SkuValidation.IgnoreCase);             // S9
        Assert.False(settings.AppendEnterAfterScan);                // S10
    }

    /// <summary>
    /// 中文：
    ///   S8 — 热键默认为 F8 / F10 / Esc，暂停热键默认未分配。
    ///   输入：无。输出：无（断言）。
    ///
    ///   暂停热键默认不分配是刻意的（规格 §13.3）：规格要求暂停控件必须是
    ///   **鼠标可点**的，因为这个安全阀要救的正是"键盘失灵"的场景。默认给它
    ///   配一个热键会造成一种错觉，让人以为按键就能脱困——而在最需要它的时候
    ///   恰恰按不出去。热键是可选的补充，不是主要入口。
    ///
    /// English:
    ///   S8 — hotkeys default to F8 / F10 / Esc, with pause unassigned.
    ///
    ///   Leaving pause unassigned is deliberate (spec §13.3). The pause control must
    ///   be mouse-reachable because the failure it rescues is "the keyboard stopped
    ///   working". Shipping a default hotkey would suggest a keystroke is the way
    ///   out — precisely the thing that does not work when it is needed most. The
    ///   hotkey is an optional addition, never the primary route.
    /// </summary>
    [Fact]
    public void Hotkey_defaults_are_F8_F10_Escape_and_unassigned_pause()
    {
        var hotkeys = new AppSettings().Hotkeys;

        Assert.Equal("F8", hotkeys.ToggleMode);
        Assert.Equal("F10", hotkeys.ForceSend);
        Assert.Equal("Escape", hotkeys.Cancel);
        Assert.Null(hotkeys.PauseResume);
    }

    /// <summary>
    /// 中文：
    ///   S11 — 任何设置类型都不得携带当前扫描模式。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 收集设置模型涉及的全部类型；
    ///     2. 断言没有任何属性的**类型**是 ScanMode；
    ///     3. 断言没有任何属性的**名字**表明它存的是当前模式。
    ///
    ///   ★ 名字检查必须精确，不能简单地查"包含 Mode"。
    ///     ModeSwitchSoundEnabled 是一个完全合法的设置项，名字里就带 Mode。
    ///     若用子串匹配，这条测试会立刻误报，然后要么被加例外、要么逼着把
    ///     一个命名恰当的字段改名。因此这里用两条精确的判据：类型是 ScanMode
    ///     （最可靠，因为存模式最自然的写法就是用这个类型），以及一组具体的
    ///     名字（CurrentMode / ScanMode / LastMode / StartupMode）。
    ///
    /// English:
    ///   S11 — no settings type carries the current scan mode.
    ///   Steps: collect the settings types, assert no property is *typed* ScanMode,
    ///   assert no property is *named* as if it stored the current mode.
    ///
    ///   The name check must be precise rather than a substring search for "Mode".
    ///   ModeSwitchSoundEnabled is a perfectly legitimate setting whose name
    ///   contains it; a substring match would fire immediately and would either
    ///   acquire an exception or force a well-named field to be renamed. Two precise
    ///   criteria are used instead: the property type being ScanMode — the most
    ///   reliable signal, since storing the mode would naturally use that type — and
    ///   a specific list of names.
    /// </summary>
    [Fact]
    public void No_settings_type_carries_the_scan_mode()
    {
        string[] prohibitedNames = ["CurrentMode", "ScanMode", "LastMode", "StartupMode"];

        // 步骤 2 / Step 2
        var scanModeTypedProperties = SettingsProperties()
            .Where(property => property.PropertyType == typeof(ScanMode)
                            || property.PropertyType == typeof(ScanMode?))
            .Select(Describe)
            .ToArray();

        Assert.True(scanModeTypedProperties.Length == 0,
            "规格 §3 要求启动恒为 SN、当前模式绝不持久化。"
            + $" 发现类型为 ScanMode 的设置属性：{string.Join(", ", scanModeTypedProperties)}");

        // 步骤 3 / Step 3
        var modeNamedProperties = SettingsProperties()
            .Where(property => prohibitedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(Describe)
            .ToArray();

        Assert.True(modeNamedProperties.Length == 0,
            $"发现疑似存放当前模式的设置属性：{string.Join(", ", modeNamedProperties)}");
    }

    /// <summary>
    /// 中文：
    ///   S12 — 任何设置类型都不得携带暂停状态。
    ///   输入：无。输出：无（断言）。
    ///   规格 §5.7 要求程序启动恒为未暂停状态。理由与模式相同：暂停是一个
    ///   会让扫码枪完全不受管控的状态，若跨重启保留，工人可能在毫不知情的
    ///   情况下以为程序在工作。
    ///
    ///   同样使用精确名字而非子串匹配：将来若出现 PauseSoundEnabled 之类的
    ///   合法设置，不应被误伤。
    ///
    /// English:
    ///   S12 — no settings type carries the paused state. Spec §5.7 requires every
    ///   launch to start un-paused. Same reasoning as the mode: paused is a state in
    ///   which the scanner is entirely unmanaged, and persisting it could leave an
    ///   operator believing the tool is working when it is bypassed.
    ///
    ///   Precise names again rather than a substring match, so a future legitimate
    ///   setting such as PauseSoundEnabled would not be caught.
    /// </summary>
    [Fact]
    public void No_settings_type_carries_the_paused_state()
    {
        string[] prohibitedNames = ["IsPaused", "Paused", "PauseState", "StartPaused"];

        var pausedProperties = SettingsProperties()
            .Where(property => prohibitedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(Describe)
            .ToArray();

        Assert.True(pausedProperties.Length == 0,
            "规格 §5.7 要求启动恒为未暂停状态，暂停绝不持久化。"
            + $" 发现：{string.Join(", ", pausedProperties)}");
    }

    /// <summary>
    /// 中文：
    ///   S13 — 序列化产物里不出现表示模式或暂停的键。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 把默认设置序列化为 JSON；
    ///     2. 递归遍历文档中出现的全部属性名；
    ///     3. 断言其中没有被禁止的键名。
    ///
    ///   为什么不直接在 JSON 文本里查子串："modeSwitchSoundEnabled" 这个
    ///   合法键名里就含有 "mode"，子串查找必定误报。因此这里解析文档、
    ///   逐个比对**完整的属性名**，判据精确且不会误伤。
    ///
    ///   本条与 S11/S12 并不重复：那两条查的是 C# 类型的形状，这一条查的是
    ///   真正写进文件的内容。两者可能不一致——例如有人给某个属性加了
    ///   [JsonPropertyName("currentMode")]，类型检查看不出来，而这条能。
    ///
    /// English:
    ///   S13 — the serialized output contains no mode or paused key.
    ///   Steps: serialize the defaults, walk every property name in the document,
    ///   assert none is prohibited.
    ///
    ///   Not a substring search over the JSON text: the legitimate key
    ///   "modeSwitchSoundEnabled" contains "mode", so a substring search would
    ///   always misfire. Parsing and comparing whole property names is precise.
    ///
    ///   This does not duplicate S11/S12. Those inspect the shape of the C# types;
    ///   this inspects what is actually written to disk. The two can disagree — a
    ///   property annotated [JsonPropertyName("currentMode")] would slip past a type
    ///   check and be caught here.
    /// </summary>
    [Fact]
    public void Serialized_settings_contain_no_mode_or_paused_key()
    {
        string[] prohibitedKeys =
            ["currentmode", "scanmode", "lastmode", "startupmode",
             "ispaused", "paused", "pausestate", "startpaused"];

        // 步骤 1 / Step 1
        var json = JsonSerializer.Serialize(new AppSettings());

        // 步骤 2 / Step 2
        using var document = JsonDocument.Parse(json);
        var propertyNames = new List<string>();
        CollectPropertyNames(document.RootElement, propertyNames);

        // 步骤 3 / Step 3
        var offendingKeys = propertyNames
            .Where(name => prohibitedKeys.Contains(name.ToLowerInvariant()))
            .ToArray();

        Assert.True(offendingKeys.Length == 0,
            "配置文件中绝不能出现当前模式或暂停状态（规格 §3、§5.7、§14）。"
            + $" 发现键：{string.Join(", ", offendingKeys)}");
    }

    /// <summary>
    /// 中文：
    ///   S31 — 不可为空的配置分组，赋 null 后必须仍然是一个可用实例。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 用 NullabilityInfoContext 挑出 AppSettings 上所有"声明为不可空"
    ///        且类型属于设置命名空间的属性；
    ///     2. 断言至少挑出了一个——否则筛选条件写错时，这条测试会在什么都没测
    ///        的情况下变绿；
    ///     3. 逐个反射赋 null；
    ///     4. 断言取回来的值仍然非空。
    ///
    ///   ★ 这条与 JsonSettingsStoreTests 的 S30 是同一个不变式的两面。
    ///
    ///     S30 从**行为**一侧验证：一份把某分组写成 null 的配置文件，读回来
    ///     是默认实例。本条从**类型**一侧验证：不管谁、以什么方式赋 null，
    ///     属性都不会真的变成 null。
    ///
    ///     两条都要，因为它们失效的方式不同。S30 只覆盖了 JSON 这一条路径，
    ///     将来换一个序列化器、或者有人手写 new AppSettings { Hotkeys = null! }，
    ///     S30 依然是绿的。而本条会随着 AppSettings 新增分组**自动**覆盖到它——
    ///     不需要有人记得回来补一条用例，而需要人记得的守卫迟早会漏。
    ///
    ///   为什么这个不变式值得守：.NET 8 的 System.Text.Json 完全忽略可空性
    ///   标注，所以"这个属性声明成不可空"在反序列化时一点约束力都没有。
    ///   声明与实际行为之间的这道缝，必须由 setter 自己合上。
    ///
    /// English:
    ///   S31 — a section declared non-nullable stays usable after null is assigned.
    ///   Steps: (1) use NullabilityInfoContext to select every AppSettings property
    ///   that is declared non-nullable and typed in the settings namespace; (2) assert
    ///   at least one was selected, so a mistaken filter cannot leave this test green
    ///   while testing nothing; (3) assign null to each by reflection; (4) assert the
    ///   value read back is still not null.
    ///
    ///   This and JsonSettingsStoreTests' S30 are two sides of one invariant. S30
    ///   checks the behavior: a file writing a section as null loads as a default
    ///   instance. This one checks the type: no assignment, by anyone, through any
    ///   route, actually leaves the property null.
    ///
    ///   Both are needed because they fail differently. S30 covers the JSON path
    ///   alone and would stay green under a different serializer, or against a
    ///   hand-written new AppSettings { Hotkeys = null! }. This one, in exchange,
    ///   extends automatically to any section added to AppSettings later — no one has
    ///   to remember to come back and add a case, and a guard that depends on
    ///   remembering eventually misses one.
    ///
    ///   Why the invariant is worth guarding: .NET 8's System.Text.Json ignores
    ///   nullability annotations outright, so declaring a property non-nullable
    ///   constrains deserialization not at all. That gap between the declaration and
    ///   the actual behavior has to be closed by the setter itself.
    /// </summary>
    [Fact]
    public void Non_nullable_settings_sections_reject_null()
    {
        var nullabilityContext = new NullabilityInfoContext();
        var settingsNamespace = typeof(AppSettings).Namespace;

        // 步骤 1 / Step 1
        var requiredSections = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.Namespace == settingsNamespace)
            .Where(property =>
                nullabilityContext.Create(property).WriteState == NullabilityState.NotNull)
            .ToArray();

        // 步骤 2 / Step 2
        Assert.True(requiredSections.Length > 0,
            "没有挑出任何不可为空的配置分组，说明筛选条件本身写错了——"
            + "这条测试会在什么都没检查的情况下变绿。");

        var settings = new AppSettings();

        foreach (var section in requiredSections)
        {
            // 步骤 3 / Step 3
            section.SetValue(settings, null);

            // 步骤 4 / Step 4
            Assert.True(section.GetValue(settings) is not null,
                $"AppSettings.{section.Name} 声明为不可空，但赋 null 之后真的变成了 null。"
                + " System.Text.Json 会忽略可空性标注，因此一份把该分组写成 null 的"
                + "配置文件会让 Load 返回半空对象，Phase B 里表现为启动即空引用异常。"
                + " 请让该属性的 setter 把 null 折成默认实例。");
        }
    }

    /// <summary>
    /// 中文：递归收集一个 JSON 元素中出现的全部属性名。
    /// English: Recursively collects every property name in a JSON element.
    /// </summary>
    private static void CollectPropertyNames(JsonElement element, List<string> collected)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    collected.Add(property.Name);
                    CollectPropertyNames(property.Value, collected);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPropertyNames(item, collected);
                }

                break;
        }
    }

    /// <summary>
    /// 中文：
    ///   收集设置模型涉及的全部公开实例属性，包括嵌套的设置类型。
    ///   实现：从 AppSettings 出发，把它自己以及它所有属性的类型中位于
    ///   ScannerHelper.Core.Settings 命名空间下的那些一并纳入。这样新增一个
    ///   嵌套设置类型时，S11/S12 会自动覆盖到它，而不需要有人记得来更新
    ///   这份清单——那种需要手工维护的清单迟早会漏。
    /// English:
    ///   Collects every public instance property across the settings model,
    ///   including nested settings types. Starting from AppSettings, any property
    ///   type living in ScannerHelper.Core.Settings is included too, so a newly
    ///   added nested settings type is covered automatically rather than depending
    ///   on someone remembering to update a hand-maintained list — the kind of list
    ///   that eventually goes stale.
    /// </summary>
    private static IEnumerable<PropertyInfo> SettingsProperties()
    {
        var settingsNamespace = typeof(AppSettings).Namespace;

        var settingsTypes = new[] { typeof(AppSettings) }
            .Concat(typeof(AppSettings)
                .GetProperties()
                .Select(property => Nullable.GetUnderlyingType(property.PropertyType)
                                    ?? property.PropertyType)
                .Where(type => type.Namespace == settingsNamespace))
            .Distinct();

        return settingsTypes.SelectMany(type =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// 中文：把一个属性描述成 "类型名.属性名"，用于失败信息。
    /// English: Renders a property as "TypeName.PropertyName" for failure messages.
    /// </summary>
    private static string Describe(PropertyInfo property)
        => $"{property.DeclaringType?.Name}.{property.Name}";
}
