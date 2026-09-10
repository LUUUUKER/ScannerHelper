// =============================================================================
// HotkeyKeyCatalogTests.cs
//
// 中文：
//   模式切换键的目录与解析（用例编号 KC1~KC6）。
//
//   ★ 这一组钉的是"认不出来时不许悄悄替换"。
//
//     一个拼错的键名如果被静默换成默认值，设置界面上显示的是人选的那个，
//     实际注册的是另一个。工人按了没反应，而去查的人看到界面上写得清清楚楚——
//     这种界面与现实的不一致，比"这个键用不了"难查十倍。
//
// English:
//   The mode-toggle key catalog and its parsing (cases KC1–KC6).
//
//   These hold the rule that an unrecognized name is never quietly substituted. Silently falling
//   back to a default leaves Settings displaying the key the person chose while a different one is
//   registered: the operator presses and nothing happens, and whoever investigates sees the UI
//   stating it plainly. That disagreement between UI and reality is far harder to chase than "this
//   key is unavailable".
//
// 包含的测试 / Tests in this file:
//   Bare_keys_parse                        KC1
//   Modifier_combinations_parse            KC2
//   Case_and_spacing_are_forgiven          KC3
//   Unknown_names_are_rejected             KC4
//   A_bare_modifier_is_rejected            KC5
//   The_default_is_the_first_choice        KC6
// =============================================================================

using ScannerHelper.Core.Input;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Input;

public class HotkeyKeyCatalogTests
{
    [Theory] // KC1
    [InlineData("Insert")]
    [InlineData("Pause")]
    [InlineData("ScrollLock")]
    [InlineData("NumpadAdd")]
    [InlineData("Apps")]
    [InlineData("F8")]
    public void Bare_keys_parse(string text)
    {
        Assert.True(HotkeyKeyCatalog.TryParse(text, out var chord));
        Assert.Equal(HotkeyModifiers.None, chord.Modifiers);
        Assert.Equal(text, chord.KeyName, ignoreCase: true);
    }

    [Fact] // KC2
    public void Modifier_combinations_parse()
    {
        Assert.True(HotkeyKeyCatalog.TryParse("Ctrl+Alt+S", out var chord));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, chord.Modifiers);
        Assert.Equal("S", chord.KeyName);
    }

    [Theory] // KC3
    [InlineData("insert")]
    [InlineData("  Insert  ")]
    [InlineData("CTRL + ALT + s")]
    public void Case_and_spacing_are_forgiven(string text)
        => Assert.True(HotkeyKeyCatalog.TryParse(text, out _));

    [Theory] // KC4
    [InlineData("Insrt")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Nope")]
    [InlineData("Meta+S")]
    public void Unknown_names_are_rejected(string? text)
    {
        // 拒绝，而不是回退。见文件头。
        // Rejected rather than fallen back from. See the file header.
        Assert.False(HotkeyKeyCatalog.TryParse(text, out var chord));
        Assert.Equal(default, chord);
    }

    [Theory] // KC5
    [InlineData("Alt")]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    public void A_bare_modifier_is_rejected(string text)
    {
        // ★ 现场提过"能不能用 Alt 切换"。Windows 的 RegisterHotKey 要的是
        //   「修饰键 + 一个键」，修饰键自己当不了那个键——所以这不是我们的取舍，
        //   是这条 API 表达不了的东西。
        // The field asked about using Alt to switch. Windows' RegisterHotKey takes modifiers plus a
        // key and a modifier cannot be that key, so this is not our trade-off but something the API
        // cannot express.
        Assert.False(HotkeyKeyCatalog.TryParse(text, out _));
    }

    [Fact] // KC6
    public void The_default_is_the_first_choice()
    {
        // 设置里的默认值必须和清单第一项一致。两处各写一遍迟早对不上，
        // 而对不上的表现是"下拉框里没选中任何东西"这种让人摸不着头脑的界面。
        // The configured default must match the first choice. Two copies eventually disagree, and
        // the way it shows up is a combo box with nothing selected — a baffling piece of UI.
        Assert.Equal(HotkeyKeyCatalog.ToggleModeChoices[0], new HotkeySettings().ToggleMode);
    }
}
