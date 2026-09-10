// =============================================================================
// HotkeyKeyCatalog.cs
//
// 中文：
//   模式切换键可以选哪些、"Ctrl+Alt+S"这种写法怎么解析。
//
//   ★ 为什么要有可选清单，而不是让人随便填。
//
//     现场的问题是"每台电脑的 F8 都可能被别的软件占用"。这意味着**换哪个键都
//     一样会中招**——真正需要的是让装机的人当场换一个。但"随便填一个键名"会
//     引来另一类失败：填了个 Windows 根本注册不了的东西（比如单独的 Alt、或者
//     拼错的 "Insrt"），程序悄悄注册失败，工人按了没反应，而没有任何人知道
//     原因出在一个拼写上。
//
//     给一份清单，每一项都是**已知可注册**的，就把那类失败整个去掉了。
//
//   ★ 清单里为什么没有 F5 / F11 / F12。
//
//     业务软件是网页应用。F5 刷新、F11 全屏、F12 开发者工具——本程序抢走它们，
//     工人会发现"网页的刷新坏了"，而绝不会想到是这个扫码程序干的。规格 §7
//     对 Esc 的处理是同一条理由。
//
//   ★ 单独的 Alt / Ctrl / Shift 不在清单里，因为 Windows 根本不允许。
//
//     RegisterHotKey 注册的是"修饰键 + 一个键"，修饰键自己不能当那个键。就算
//     能，Alt 在几乎每个程序里都会激活菜单栏、打断输入法——那是比 F8 冲突
//     严重得多的事。
//
// English:
//   Which keys may be chosen for the mode toggle, and how "Ctrl+Alt+S" is parsed.
//
//   There is a fixed list rather than free text because the field problem — F8 may be taken by other
//   software on any given PC — means every fixed key is equally exposed, and what is actually needed
//   is for whoever installs it to change the key on the spot. Free text introduces a different
//   failure: something Windows cannot register (a bare Alt, or a misspelled "Insrt") registers
//   silently as nothing, the operator presses it and nothing happens, and no one traces it to a
//   spelling. A list whose every entry is known-registrable removes that class entirely.
//
//   F5, F11 and F12 are absent: the business application is a web app, and taking its refresh,
//   full-screen or devtools key leaves the operator with a "broken" browser and no reason to suspect
//   the scanner program — the same reasoning spec §7 applies to Esc.
//
//   A bare Alt, Ctrl or Shift is absent because Windows does not allow it: RegisterHotKey takes
//   modifiers plus a key, and a modifier cannot be that key. Even if it could, Alt activates the
//   menu bar in nearly every program and interrupts IME composition — far worse than an F8 clash.
//
// 包含的类型 / Types in this file:
//   HotkeyModifiers
//   HotkeyChord
//   HotkeyKeyCatalog
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：热键的修饰键。
/// English: A hotkey's modifiers.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>中文：没有修饰键。 English: None.</summary>
    None = 0,

    /// <summary>中文：Alt。 English: Alt.</summary>
    Alt = 1,

    /// <summary>中文：Ctrl。 English: Control.</summary>
    Control = 2,

    /// <summary>中文：Shift。 English: Shift.</summary>
    Shift = 4,
}

/// <summary>
/// 中文：一个热键：修饰键 + 一个键名。
/// English: One hotkey: modifiers plus a key name.
/// </summary>
/// <param name="Modifiers">中文：修饰键。 English: The modifiers.</param>
/// <param name="KeyName">中文：键名，取自本目录。 English: The key name, from this catalog.</param>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, string KeyName);

/// <summary>
/// 中文：可选键的目录与解析。纯逻辑，不碰 Windows。
/// English: The catalog of choosable keys and their parsing. Pure logic; touches no Windows API.
/// </summary>
public static class HotkeyKeyCatalog
{
    /// <summary>
    /// 中文：
    ///   模式切换键的可选项，按推荐程度排序。第一项是默认值。
    ///
    ///   Insert 排第一：台式和笔记本都有、位置固定、单独按它的程序极少
    ///   （Shift+Insert 粘贴是另一个组合，不冲突）。
    ///   Pause 冲突概率最低，但部分笔记本没有或要配合 Fn。
    ///   小键盘那两个键最大最好按，但没有小键盘的笔记本用不了。
    /// English:
    ///   The choices for the mode toggle, most recommended first; the first is the default.
    ///
    ///   Insert leads: present on desktop and laptop keyboards, in a fixed place, and pressed alone
    ///   by very few programs (Shift+Insert paste is a different combination). Pause has the lowest
    ///   clash rate but is missing or Fn-shifted on some laptops. The two numpad keys are the
    ///   largest and easiest to hit blind, but are unavailable on a laptop without a numpad.
    /// </summary>
    public static IReadOnlyList<string> ToggleModeChoices { get; } =
    [
        "Insert",
        "Pause",
        "ScrollLock",
        "NumpadAdd",
        "NumpadMultiply",
        "Apps",
        "F7",
        "F8",
        "F9",
        "Ctrl+Alt+S",
    ];

    /// <summary>
    /// 中文：目录里认得的键名。解析时用它挡掉拼错的东西。
    /// English: The key names this catalog knows, used to reject misspellings when parsing.
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
        "Pause", "ScrollLock", "Apps",
        "NumpadAdd", "NumpadSubtract", "NumpadMultiply", "NumpadDivide",
        "F1", "F2", "F3", "F4", "F6", "F7", "F8", "F9", "F10",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "Escape",
    };

    /// <summary>
    /// 中文：
    ///   解析 "Insert"、"Ctrl+Alt+S" 这样的写法。
    ///   输出：认得就返回 true 并给出 chord；认不得返回 false。
    ///
    ///   ★ 认不得时返回 false 而不是回退到某个默认键。悄悄换一个键的后果是
    ///     工人按了没反应、而设置界面上明明写着他选的那个——那种不一致比
    ///     "这个键用不了"难查十倍。调用方负责把 false 变成界面上看得见的话。
    /// English:
    ///   Parses forms like "Insert" and "Ctrl+Alt+S", returning false for anything unrecognized.
    ///
    ///   False rather than a silent fallback to some default: quietly substituting a key leaves the
    ///   operator pressing something that does nothing while Settings displays the key they chose,
    ///   and that inconsistency is far harder to chase than "this key is unavailable". Turning false
    ///   into something visible is the caller's job.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1];

        if (!KnownKeys.Contains(keyName))
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, keyName);
        return true;
    }
}
