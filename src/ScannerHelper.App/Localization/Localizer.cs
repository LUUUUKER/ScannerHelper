// =============================================================================
// Localizer.cs
//
// 中文：
//   当前语言，以及把文字接到 XAML 上的那座桥。
//
//   ★ 做成"带索引器、会通知变更的单例"，是为了满足规格 §12 的一句要求：
//     **切换语言立即生效，不重启**。
//
//     XAML 里这样用：
//         Text="{Binding Source={x:Static loc:Localizer.Strings}, Path=[ModeHeading]}"
//
//     切换语言时抛一次 PropertyChanged("Item[]")，WPF 会重新求值**每一个**
//     索引器绑定。于是"立即生效"不是靠遍历界面去逐个改，而是绑定系统自己完成的
//     ——那意味着新加的界面元素天然就支持切换，不需要谁记得去登记它。
//
//   ★ 首次启动按 Windows 的界面语言选（规格 §12）：中文系统用中文，否则英文。
//     工人手动选过之后就持久化，此后不再看系统语言——他选了，就是他的意思。
//
// English:
//   The current language, and the bridge that connects strings to XAML.
//
//   A change-notifying singleton with an indexer, because spec §12 requires language changes to
//   apply immediately without a restart. XAML binds as
//   {Binding Source={x:Static loc:Localizer.Strings}, Path=[ModeHeading]}, and switching raises
//   PropertyChanged("Item[]") so WPF re-evaluates every indexer binding. "Immediately" is then the
//   binding system's doing rather than a walk over the UI — which means new UI elements support
//   switching by construction, with nobody having to remember to register them.
//
//   On first launch the language follows the Windows UI culture (spec §12): Chinese system, Chinese
//   UI; otherwise English. Once the operator chooses, that choice is persisted and the system
//   language is never consulted again — they chose, and that is the answer.
//
// 包含的类型 / Types in this file:
//   Localizer
//   LocalizedStrings
// =============================================================================

using System.ComponentModel;
using System.Globalization;

namespace ScannerHelper.App.Localization;

/// <summary>
/// 中文：当前语言的持有者。
/// English: Holds the current language.
/// </summary>
public static class Localizer
{
    /// <summary>
    /// 中文：绑定用的字符串源。XAML 通过 <c>Path=[Key]</c> 取值。
    /// English: The binding source for strings; XAML reads through <c>Path=[Key]</c>.
    /// </summary>
    public static LocalizedStrings Strings { get; } = new();

    /// <summary>
    /// 中文：语言变了。给需要自己刷新的代码用；XAML 绑定不需要订阅它。
    /// English: The language changed. For code that refreshes itself; XAML bindings need not
    ///          subscribe.
    /// </summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// 中文：当前语言。
    /// English: The current language.
    /// </summary>
    public static UiLanguage Current { get; private set; } = UiLanguage.English;

    /// <summary>
    /// 中文：
    ///   按持久化的设置决定语言；没存过就看 Windows 的界面语言（规格 §12）。
    ///   输入：persistedLanguage 设置里存的值，null 或无法识别都视作"没存过"。
    ///   输出：最终采用的语言。
    ///
    ///   ★ 无法识别的值不报错，回落到系统语言。设置文件是可以被人手改的，
    ///     一个打错的语言名不该让程序起不来——规格 §14 对配置损坏的处理原则
    ///     是"恢复到可用状态并告知"，而不是拒绝启动。
    /// English:
    ///   Chooses the language from the persisted setting, falling back to the Windows UI culture
    ///   when there is none (spec §12). An unrecognized value falls back rather than failing: the
    ///   settings file can be hand-edited, and a mistyped language name must not stop the program
    ///   from starting — spec §14's principle for damaged configuration is to recover into a usable
    ///   state and say so, not to refuse to run.
    /// </summary>
    public static UiLanguage Initialize(string? persistedLanguage)
    {
        var language = Parse(persistedLanguage) ?? FromSystemCulture();
        SetLanguage(language);
        return language;
    }

    /// <summary>
    /// 中文：切换语言，立即生效。
    /// English: Switches the language, taking effect immediately.
    /// </summary>
    public static void SetLanguage(UiLanguage language)
    {
        Current = language;
        Strings.NotifyAllChanged();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：取一条文字，供代码里格式化用（XAML 请用绑定）。
    /// English: Returns one string for code that formats it; XAML should bind instead.
    /// </summary>
    public static string Get(string key) => UiStrings.Get(Current, key);

    /// <summary>
    /// 中文：取一条带占位符的文字并填充。
    ///       用不变文化格式化：这里填进去的是端口名、分钟数这类**标识性**内容，
    ///       不是给人做数值比较的量，随区域变形只会让日志与截图对不上。
    /// English: Returns one formatted string, using the invariant culture: what goes in is
    ///          identifying content such as a port name or a count of minutes rather than a
    ///          quantity to be compared, and varying it by locale only makes logs and screenshots
    ///          disagree.
    /// </summary>
    public static string Format(string key, params object[] arguments)
        => string.Format(CultureInfo.InvariantCulture, Get(key), arguments);

    /// <summary>
    /// 中文：把语言写成可持久化的字符串。
    /// English: Renders the language as a persistable string.
    /// </summary>
    public static string ToPersistedValue(UiLanguage language)
        => language == UiLanguage.ChineseSimplified ? "zh-CN" : "en-US";

    private static UiLanguage? Parse(string? value)
        => value switch
        {
            null or "" => null,
            _ when value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                => UiLanguage.ChineseSimplified,
            _ when value.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                => UiLanguage.English,
            _ => null,
        };

    /// <summary>
    /// 中文：看 Windows 的界面语言。中文系统用中文，其余一律英文（规格 §12）。
    /// English: Consults the Windows UI culture: Chinese systems get Chinese, everything else
    ///          English (spec §12).
    /// </summary>
    private static UiLanguage FromSystemCulture()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.ChineseSimplified
            : UiLanguage.English;
}

/// <summary>
/// 中文：给 XAML 绑定用的字符串源。
/// English: The string source XAML binds to.
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 中文：按键取文字。
    /// English: Returns the string for a key.
    /// </summary>
    public string this[string key] => UiStrings.Get(Localizer.Current, key);

    /// <summary>
    /// 中文：
    ///   通知所有索引器绑定重新求值。
    ///
    ///   ★ 属性名用 "Item[]" 是 WPF 的约定：它表示"这个索引器的**全部**取值都
    ///     变了"。逐个 key 去通知既做不到（我们不知道界面上用了哪些），也没必要。
    /// English:
    ///   Tells every indexer binding to re-evaluate. The property name "Item[]" is WPF's
    ///   convention for "every value of this indexer changed" — notifying key by key is neither
    ///   possible (we do not know which the UI uses) nor necessary.
    /// </summary>
    internal void NotifyAllChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
