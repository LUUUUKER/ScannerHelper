// =============================================================================
// HotkeySettings.cs
//
// 中文：
//   四个全局控制热键的配置（规格 §7、§13.3）。
//
//   键用**字符串名**表示而不是枚举，原因是 Core 目标框架为 net8.0，不能引用
//   System.Windows.Input.Key 这类 Windows 专有类型——那会让 Core 变成
//   Windows 专用，Phase A 的全部工作立即失去承载平台（见测试 A3）。
//   字符串到虚拟键码的映射属于平台细节，放在 Phase B 的 Win32 层。
//
//   附带好处：字符串在 JSON 里可读。人打开配置文件看到 "F8" 就知道是什么，
//   而看到一个整数键码则需要查表。配置文件是要被技术顾问远程查看的。
//
// English:
//   Configuration for the four global control hotkeys (spec §7, §13.3).
//
//   Keys are string names rather than an enum because Core targets net8.0 and
//   cannot reference Windows-only types such as System.Windows.Input.Key — doing so
//   would make Core Windows-only and strand all of Phase A (see test A3). Mapping a
//   name to a virtual key code is a platform detail belonging to Win32 in Phase B.
//
//   A side benefit: strings are readable in JSON. Someone opening the config file
//   sees "F8" and knows what it means, where an integer key code would need a
//   lookup — and this file gets read remotely by the technical advisor.
//
// 包含的类型 / Types in this file:
//   HotkeySettings
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：全局控制热键。null 表示该热键未分配。
/// English: The global control hotkeys. null means unassigned.
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>
    /// 中文：
    ///   切换 SN / SKU 模式（规格 §7）。**默认 Insert，不再是 F8。**
    ///
    ///   ★ 改默认值是现场换来的：F8 在不同电脑上被不同的常驻软件占用，而"这台
    ///     机器上正好被占"是装完才会发现的事。Insert 在台式和笔记本上都有、位置
    ///     固定、单独按它的程序极少（Shift+Insert 粘贴是另一个组合，不冲突）。
    ///
    ///   ★ 但真正的解法不是换一个固定键，而是**这一项可配置**，并且注册失败时
    ///     界面要说出来（见 MainWindow 的 SwitchModeHintUnavailable）。没有哪个键
    ///     在每台电脑上都空着，所以程序必须能回答"这台机器上这个键能不能用"。
    ///
    ///   ★ 另有一条完全不碰键盘的路：命令条码（见 ScanCommand）。
    /// English:
    ///   Toggles SN/SKU (spec §7). The default is Insert rather than F8.
    ///
    ///   The change was paid for on site: F8 is taken by different resident software on different
    ///   PCs, and "taken on this particular machine" is discovered only after installing. Insert
    ///   exists on both desktop and laptop keyboards, sits in a fixed place, and is pressed alone by
    ///   very few programs (Shift+Insert paste is a different combination and does not collide).
    ///
    ///   The real answer, though, is not another fixed key but that this is configurable and that
    ///   the UI says so when registration fails (see MainWindow's SwitchModeHintUnavailable). No key
    ///   is free on every PC, so the program has to be able to answer whether this one is free here.
    ///
    ///   There is also a path that never touches the keyboard: command barcodes (see ScanCommand).
    /// </summary>
    public string? ToggleMode { get; set; } = "Insert";

    /// <summary>
    /// 中文：强制发送原始码，默认 F10。仅在有待处理错误时有效（规格 §10）。
    /// English: Force Send the raw code, default F10. Valid only while an error is
    ///          pending (spec §10).
    /// </summary>
    public string? ForceSend { get; set; } = "F10";

    /// <summary>
    /// 中文：取消失败的扫描，默认 Esc。仅在有待处理错误时有效；其余时间透传给
    ///       业务软件——Esc 在网页中太常用，无条件吞掉会让工人发现"网页的取消
    ///       键坏了"却完全想不到是本程序所为（规格 §7）。
    /// English: Cancels a failed scan, default Esc. Valid only while an error is
    ///          pending; otherwise it passes through — Esc is far too common in web
    ///          use, and swallowing it unconditionally would leave the operator with
    ///          a "broken" cancel key and no reason to suspect this tool (spec §7).
    /// </summary>
    public string? Cancel { get; set; } = "Escape";

    /// <summary>
    /// 中文：暂停 / 恢复接管，**默认未分配**（规格 §13.3）。
    ///
    ///       刻意不给默认值。暂停这个安全阀要救的正是"键盘失灵"的场景，
    ///       因此规格要求控件必须鼠标可点。默认配一个热键会制造一种错觉，
    ///       让人以为按键就能脱困——而在最需要它的时候恰恰按不出去。
    ///       热键是可选的补充，永远不是主要入口。
    /// English: Pause/Resume, **unassigned by default** (spec §13.3).
    ///
    ///          Deliberately absent. The pause valve exists to rescue "the keyboard
    ///          stopped working", which is why the spec requires a mouse-reachable
    ///          control. A default hotkey would suggest a keystroke is the way out —
    ///          exactly what fails when it is needed most. The hotkey is an optional
    ///          addition, never the primary route.
    /// </summary>
    public string? PauseResume { get; set; }
}
