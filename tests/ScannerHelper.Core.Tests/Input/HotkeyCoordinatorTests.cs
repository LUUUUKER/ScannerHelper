// =============================================================================
// HotkeyCoordinatorTests.cs
//
// 中文：
//   热键路由规则（Task 8a，用例编号 HK1~HK14）。
//
//   规格 §7 给了一张七行的表格，本组把每一行都测一遍。逐行测是必要的：
//   七行里任意一行漏掉，都不会影响其余六行的表现，因此只测几行的话，
//   漏掉的那几行会一路活到现场。
//
//   本文件里分量最重的三条：
//     HK7   暂停期间不拦截**任何**热键，包括 F8（规格 §5.7）。这是 PAUSED
//           这个状态的定义本身。
//     HK8   弹起事件不触发动作。漏了的表现是"F8 按了没反应"——因为它切了
//           两次，而排查方向会全跑偏。
//     HK11  暂停热键在暂停期间**也**不生效。这是规格明说的反直觉后果：
//           它只能用来暂停，永远不能用来恢复。
//
// English:
//   Hotkey routing rules (Task 8a, cases HK1–HK14).
//
//   Spec §7 gives a seven-row table and this group tests every row. Row by row is necessary:
//   omitting any one row affects none of the other six, so testing only some would let the
//   omitted ones survive all the way to the shop floor.
//
//   Three carry the most weight. HK7: no hotkey at all is intercepted while paused, F8 included
//   (spec §5.7) — that is the definition of the state. HK8: key-ups fire nothing, whose absence
//   reads as "F8 does nothing" because it toggled twice, sending the investigation the wrong
//   way. HK11: the pause hotkey does not work while paused either — the spec's own
//   counter-intuitive consequence that it can only ever pause, never resume.
//
// 包含的测试 / Tests in this file:
//   F8_from_a_normal_keyboard_toggles_the_mode       HK1
//   F8_from_the_bound_scanner_is_never_a_command     HK2
//   F10_acts_only_while_an_error_is_pending          HK3 HK4
//   Escape_acts_only_while_an_error_is_pending       HK5 HK6
//   Nothing_is_intercepted_while_paused              HK7
//   Key_up_events_trigger_nothing                    HK8
//   Unassigned_hotkeys_never_match                   HK9
//   Unrelated_keys_pass_through                      HK10
//   Pause_hotkey_cannot_resume                       HK11
//   Injected_events_never_trigger_a_hotkey           HK12
//   Conflicting_assignments_are_detected             HK13
//   Unassigned_entries_are_not_conflicts             HK14
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Input;

public class HotkeyCoordinatorTests
{
    private const ushort VkF8 = 0x77;
    private const ushort VkF10 = 0x79;
    private const ushort VkEscape = 0x1B;
    private const ushort VkF12 = 0x7B;
    private const ushort VkLetterA = 0x41;

    /// <summary>
    /// 中文：默认绑定：F8 / F10 / Esc，暂停热键未分配（规格 §13.3 的默认值）。
    /// English: The default bindings — F8, F10, Esc, with pause unassigned (spec §13.3).
    /// </summary>
    private static HotkeyCoordinator CreateCoordinator(ushort? pauseResume = null)
        => new(new HotkeyBindings(VkF8, VkF10, VkEscape, pauseResume));

    private static KeyEvent Key(
        ushort virtualKey, bool isKeyUp = false, bool isInjected = false)
        => new(TimeSpan.Zero, ScanCode: 0x42, IsExtended: false, isKeyUp,
            virtualKey, Character: null, isInjected);

    private static HotkeyContext Context(
        bool fromScanner = false, bool paused = false, bool pendingError = false)
        => new(fromScanner, paused, pendingError);

    /// <summary>
    /// 中文：HK1 —— 来自普通键盘的 F8 切换模式并被吞掉（规格 §7）。
    ///       吞掉是必需的：否则一次模式切换会同时在网页里触发一个不相干的
    ///       快捷键，而规格 §7 特意点明要防的正是这一条。
    /// English: HK1 — F8 from a normal keyboard toggles the mode and is swallowed (spec §7).
    ///          Swallowing is required, or one mode switch would simultaneously trigger an
    ///          unrelated shortcut in the web application — the very thing spec §7 calls out.
    /// </summary>
    [Fact]
    public void F8_from_a_normal_keyboard_toggles_the_mode()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal(HotkeyAction.ToggleMode, coordinator.Route(Key(VkF8), Context()));
    }

    /// <summary>
    /// 中文：
    ///   HK2 —— 来自绑定扫码枪的 F8 **绝不是**模式命令（规格 §7）。
    ///
    ///   ★ 这条不是假想的边界情况。条码内容里出现能被解成 F8 的字节完全可能，
    ///     而一旦扫码枪能切模式，现场会出现这样的场景：工人扫了一枪含 F8 的
    ///     条码，模式被悄悄切走，接下来几十枪全部以错误的模式发出去——
    ///     而他正低头看货，等发现时错误数据已经进系统了。
    ///
    ///   这也是为什么热键路由必须发生在**关联之后**：Task 4a 实测钩子 100%
    ///   先于 WM_INPUT 到达，所以在钩子回调那一刻根本不知道是谁按的。
    /// English:
    ///   HK2 — F8 from the bound scanner is never a mode command (spec §7).
    ///
    ///   Not a hypothetical edge case. A barcode can perfectly well contain bytes that decode to
    ///   F8, and a scanner able to switch modes produces this scene on site: the operator scans
    ///   one such barcode, the mode silently changes, and the next few dozen scans are emitted in
    ///   the wrong mode — while they are looking at goods, with the wrong data already in the
    ///   system by the time they notice.
    ///
    ///   It is also why hotkey routing must happen *after* correlation: Task 4a measured the hook
    ///   preceding WM_INPUT 100% of the time, so at callback time there is no way to know who
    ///   pressed the key.
    /// </summary>
    [Fact]
    public void F8_from_the_bound_scanner_is_never_a_command()
    {
        var coordinator = CreateCoordinator();

        Assert.True(
            coordinator.Route(Key(VkF8), Context(fromScanner: true)) == HotkeyAction.None,
            "扫码枪发出的 F8 永远是扫描数据，绝不是模式命令（规格 §7）。"
            + "否则一枪含 F8 的条码就能把模式悄悄切走，而工人正低头看货。");
    }

    /// <summary>
    /// 中文：HK3、HK4 —— F10 只在有待决错误时生效，否则透传（规格 §7）。
    ///       没有待决错误时透传，是因为 F10 在业务软件里可能另有用途，
    ///       无条件吞掉等于悄悄剥夺了工人的一个按键。
    /// English: HK3, HK4 — F10 acts only while an error is pending and passes through otherwise
    ///          (spec §7). Passing through matters because F10 may mean something in the business
    ///          application, and swallowing it unconditionally quietly takes a key away from the
    ///          operator.
    /// </summary>
    [Fact]
    public void F10_acts_only_while_an_error_is_pending()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal(HotkeyAction.ForceSend,
            coordinator.Route(Key(VkF10), Context(pendingError: true)));

        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkF10), Context(pendingError: false)));
    }

    /// <summary>
    /// 中文：
    ///   HK5、HK6 —— Esc 只在有待决错误时生效，否则透传（规格 §7）。
    ///
    ///   ★ 规格特意为 Esc 写了一句解释：它在网页里**太常用**了，无条件吞掉
    ///     会让工人发现"网页的取消键坏了"，却完全想不到是本程序所为。
    ///
    ///     这类故障的排查成本极高：症状出现在业务软件里，原因在一个后台
    ///     运行的小工具里，而两者之间没有任何看得见的联系。工人只会得出
    ///     "这个网站有问题"的结论。
    /// English:
    ///   HK5, HK6 — Esc acts only while an error is pending and passes through otherwise
    ///   (spec §7).
    ///
    ///   The spec writes a sentence specifically about Esc: it is far too common in web use, and
    ///   swallowing it unconditionally would leave the operator with a "broken" cancel key and no
    ///   reason to suspect this tool. Such faults are expensive to chase — the symptom appears in
    ///   the business application, the cause sits in a small tool running in the background, and
    ///   nothing visibly connects them. The operator simply concludes the website is broken.
    /// </summary>
    [Fact]
    public void Escape_acts_only_while_an_error_is_pending()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal(HotkeyAction.Cancel,
            coordinator.Route(Key(VkEscape), Context(pendingError: true)));

        Assert.True(
            coordinator.Route(Key(VkEscape), Context(pendingError: false)) == HotkeyAction.None,
            "没有待决错误时 Esc 必须透传。它在网页里太常用，无条件吞掉会让工人"
            + "发现\"网页的取消键坏了\"却完全想不到是本程序所为（规格 §7）。");
    }

    /// <summary>
    /// 中文：
    ///   HK7 —— 暂停期间不拦截**任何**热键，包括 F8（规格 §5.7）。
    ///
    ///   ★ "什么都不拦"就是 PAUSED 这个状态的定义本身。
    ///
    ///     规格 §5.7 要求暂停时整条流水线被完全旁路，输入原样到达业务软件，
    ///     就像本程序没装一样。只要还有哪怕一个键被拦，这句话就不成立了
    ///     ——而 PAUSED 存在的意义正是当捕获、关联、重放坏掉时救人。
    ///     一个仍然会拦某些键的"旁路"，救不了那个场景。
    ///
    ///   本条把待决错误也设成 true，确保 F10 与 Esc 那两条路径同样被压过。
    /// English:
    ///   HK7 — no hotkey at all is intercepted while paused, F8 included (spec §5.7).
    ///
    ///   "Nothing is intercepted" is the definition of the state. Spec §5.7 requires the pipeline
    ///   to be bypassed entirely while paused, with input reaching the business application
    ///   exactly as if this program were not installed. Intercepting even one key makes that
    ///   untrue — and PAUSED exists to rescue the operator when capture, correlation and replay
    ///   are broken, which a "bypass" that still intercepts some keys cannot do.
    ///
    ///   A pending error is set as well, so the F10 and Esc paths are shown to be overridden too.
    /// </summary>
    [Fact]
    public void Nothing_is_intercepted_while_paused()
    {
        var coordinator = CreateCoordinator();
        var paused = Context(paused: true, pendingError: true);

        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(VkF8), paused));
        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(VkF10), paused));
        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(VkEscape), paused));
    }

    /// <summary>
    /// 中文：
    ///   HK8 —— 弹起事件不触发任何动作。
    ///
    ///   ★ 漏掉这条的后果特别容易被误判。一次按键产生按下与弹起两个事件，
    ///     两个都触发的话，按一下 F8 就切了两次，净效果是**什么都没变**。
    ///
    ///     现场看到的是"F8 按了没反应"，而排查方向会全跑偏：工人会怀疑
    ///     热键没注册、键盘坏了、程序没在运行，没有人会想到它其实切了两次。
    ///     而在开发机上单步调试时，两次切换看得清清楚楚——这类缺陷因此
    ///     经常在演示时"莫名其妙好了"。
    /// English:
    ///   HK8 — key-up events trigger nothing.
    ///
    ///   Omitting this is unusually easy to misdiagnose. Each keystroke produces a down and an
    ///   up, so acting on both means one F8 press toggles twice for a net effect of nothing.
    ///
    ///   On site that reads as "F8 does nothing", and the investigation goes entirely the wrong
    ///   way: the operator suspects an unregistered hotkey, a broken keyboard, or the program not
    ///   running, and nobody suspects it toggled twice. Stepping through on a development machine
    ///   shows both toggles plainly — which is why defects of this kind so often "mysteriously
    ///   fix themselves" during a demonstration.
    /// </summary>
    [Fact]
    public void Key_up_events_trigger_nothing()
    {
        var coordinator = CreateCoordinator();

        Assert.True(
            coordinator.Route(Key(VkF8, isKeyUp: true), Context()) == HotkeyAction.None,
            "弹起不得触发动作。按下与弹起都触发的话，按一下 F8 会切两次，"
            + "净效果是什么都没变，而现场看到的是「F8 按了没反应」。");

        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkF10, isKeyUp: true), Context(pendingError: true)));

        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkEscape, isKeyUp: true), Context(pendingError: true)));
    }

    /// <summary>
    /// 中文：HK9 —— 未分配的热键永远不命中（规格 §13.3：暂停热键默认未分配）。
    ///       必须显式保证：否则未分配会与虚拟键码 0 相等，而 0 是一个合法的
    ///       按键值。这条路径是常态而非边界——默认配置里暂停热键就是空的。
    /// English: HK9 — an unassigned hotkey never matches (spec §13.3: pause is unassigned by
    ///          default). It must be guaranteed explicitly, or unassigned would equal virtual key
    ///          0, itself a legitimate value. This path is the norm rather than an edge case: the
    ///          default configuration leaves pause empty.
    /// </summary>
    [Fact]
    public void Unassigned_hotkeys_never_match()
    {
        var coordinator = CreateCoordinator(pauseResume: null);

        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(VkF12), Context()));
        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(0), Context()));
    }

    /// <summary>
    /// 中文：HK10 —— 与热键无关的普通按键一律透传。
    ///       这是最常走的那条路径：工人打字的每一个字符都要经过这里，
    ///       任何一次误判都会让一个字符消失在业务软件里。
    /// English: HK10 — ordinary keys unrelated to any hotkey pass through. This is the most
    ///          travelled path of all: every character the operator types passes through here, and
    ///          any misjudgement makes a character vanish from the business application.
    /// </summary>
    [Fact]
    public void Unrelated_keys_pass_through()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal(HotkeyAction.None, coordinator.Route(Key(VkLetterA), Context()));
        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkLetterA), Context(pendingError: true)));
    }

    /// <summary>
    /// 中文：
    ///   HK11 —— 暂停热键**不能用来恢复**。
    ///
    ///   ★ 这是规格明说、但反直觉的一条后果，必须被钉住，否则将来一定会有人
    ///     把它当成缺陷"修好"。
    ///
    ///     规格 §5.7：暂停期间不拦截任何热键。既然什么都不拦，暂停热键本身
    ///     当然也不会被拦——它会像普通按键一样直接进入业务软件。规格原话是
    ///     "这是旁路整条流水线的正确且预期的后果，不是缺陷"。
    ///
    ///     所以这个热键只能**单向**：能暂停，不能恢复。恢复只能靠鼠标点
    ///     界面上的按钮——而这正是规格 §5.7 硬性要求暂停控件必须鼠标可点的
    ///     原因之一。它要救的场景是"键盘失灵"，一个只能用键盘脱困的逃生口，
    ///     在最需要它的时候恰好按不出去。
    ///
    ///     规格假设 A4 把这一点变得更要紧：工位是笔记本，没有备用键盘可插，
    ///     触摸板是唯一的退路。
    /// English:
    ///   HK11 — the pause hotkey cannot resume.
    ///
    ///   A consequence the spec states explicitly and which is nonetheless counter-intuitive, so
    ///   it must be pinned or somebody will eventually "fix" it as a defect.
    ///
    ///   Spec §5.7: no hotkey is intercepted while paused. Nothing being intercepted, the pause
    ///   hotkey is not either — it reaches the business application like any other keystroke. The
    ///   spec's own words: "the correct, expected consequence of bypassing the pipeline, not a
    ///   bug."
    ///
    ///   The hotkey is therefore one-way: it can pause and cannot resume. Resuming requires the
    ///   mouse-clickable control, which is part of why spec §5.7 insists on one — the situation it
    ///   rescues is "the keyboard stopped working", and an escape hatch reachable only from the
    ///   keyboard fails exactly when it is needed. Assumption A4 sharpens this further: the
    ///   workstation is a laptop with no spare keyboard, and the touchpad is the only way back.
    /// </summary>
    [Fact]
    public void Pause_hotkey_cannot_resume()
    {
        var coordinator = CreateCoordinator(pauseResume: VkF12);

        // 未暂停时能暂停 / it can pause while not paused
        Assert.Equal(HotkeyAction.PauseResume,
            coordinator.Route(Key(VkF12), Context(paused: false)));

        // 暂停之后，它自己也被旁路了 / once paused, it is bypassed like everything else
        Assert.True(
            coordinator.Route(Key(VkF12), Context(paused: true)) == HotkeyAction.None,
            "暂停期间不拦截任何热键，暂停热键本身也不例外（规格 §5.7）。"
            + "所以它只能暂停、不能恢复——恢复必须靠鼠标点界面上的按钮，"
            + "而这正是规格硬性要求暂停控件鼠标可点的原因：它要救的是键盘失灵。");
    }

    /// <summary>
    /// 中文：
    ///   HK12 —— 本程序自己合成的事件绝不触发热键。
    ///
    ///   规格 §5.6 要求注入的输入不能被自己的捕获链路重新吃进去。在这一层，
    ///   若合成事件能触发热键，就会出现"程序自己把自己的模式切了"这种荒唐
    ///   情形——而规格 §19.1 的心跳探测**每隔几秒就发一次**合成事件，
    ///   一旦探测用的按键恰好撞上某个热键，模式会被规律地反复切换，
    ///   现场表现是"模式自己在跳"。
    /// English:
    ///   HK12 — this application's own synthesized events never fire a hotkey.
    ///
    ///   Spec §5.6 requires injected input never to re-enter this application's own pipeline. At
    ///   this layer, a synthesized event able to fire a hotkey would have the program switching
    ///   its own mode — and spec §19.1's heartbeat emits a synthesized event every few seconds,
    ///   so a probe key colliding with a hotkey would toggle the mode on a regular cycle,
    ///   presenting on site as "the mode changes by itself".
    /// </summary>
    [Fact]
    public void Injected_events_never_trigger_a_hotkey()
    {
        var coordinator = CreateCoordinator();

        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkF8, isInjected: true), Context()));

        Assert.Equal(HotkeyAction.None,
            coordinator.Route(Key(VkF10, isInjected: true), Context(pendingError: true)));
    }

    /// <summary>
    /// 中文：
    ///   HK13 —— 重复分配在保存设置时被检测出来（规格 §13.3）。
    ///
    ///   ★ 必须在保存那一刻拦下，不能留到运行时。
    ///
    ///     假设"取消"也被设成了 F8。运行时按顺序匹配，先命中"切换模式"，
    ///     于是 F8 切模式，而"取消"这个热键**永远不生效**，且毫无提示。
    ///
    ///     现场表现是：F8 能切模式，但错误状态下怎么按都取消不掉。工人会
    ///     以为错误处理坏了，而实际是两个热键撞了——症状与原因之间没有
    ///     任何看得见的联系，排查成本极高。
    ///
    ///   返回冲突的**键名**而不是布尔：设置页要能说"F8 被分配给了多个功能"，
    ///   而不是"热键配置有冲突"——后者会让工人对着四个输入框逐个猜。
    /// English:
    ///   HK13 — duplicate assignments are detected when settings are saved (spec §13.3).
    ///
    ///   It must be caught at save time. Suppose Cancel is also set to F8: at runtime Toggle Mode
    ///   matches first, F8 switches the mode, and the Cancel hotkey simply never works, with no
    ///   indication. On site F8 switches modes fine while nothing cancels a pending error, so the
    ///   operator concludes error handling is broken when two hotkeys have collided — symptom and
    ///   cause with nothing visibly connecting them, and correspondingly expensive to chase.
    ///
    ///   The conflicting names are returned rather than a boolean so Settings can say "F8 is
    ///   assigned to more than one function" instead of "the hotkey configuration has a conflict",
    ///   which leaves the operator guessing across four fields.
    /// </summary>
    [Fact]
    public void Conflicting_assignments_are_detected()
    {
        var conflicts = HotkeyConflictDetector.FindConflicts(new HotkeySettings
        {
            ToggleMode = "F8",
            ForceSend = "F10",
            Cancel = "F8",
            PauseResume = null,
        });

        Assert.Equal("F8", Assert.Single(conflicts));

        // 大小写不同指的是同一个键。区分大小写的话，工人手输一个小写就绕过了
        // 检测，然后在运行时撞车——而运行时的映射是不区分大小写的。
        // Different casing means the same key. Case-sensitive comparison would let a hand-typed
        // lowercase slip past and collide at runtime, where the mapping is case-insensitive.
        var casingConflicts = HotkeyConflictDetector.FindConflicts(new HotkeySettings
        {
            ToggleMode = "F8",
            ForceSend = "f8",
            Cancel = "Escape",
            PauseResume = null,
        });

        Assert.Single(casingConflicts);
    }

    /// <summary>
    /// 中文：
    ///   HK14 —— 未分配的项不算重复，默认配置必须能保存。
    ///
    ///   规格 §13.3 规定暂停热键默认未分配，理论上四个热键可以全都不分配。
    ///   若把"两个都是未分配"当成冲突，**默认配置本身就存不下去了**——
    ///   工人第一次打开设置页、什么都没改、点保存，就会被拒绝。
    ///
    ///   空串与纯空格同样按未分配处理：设置页的输入框被清空后交出来的通常是
    ///   空串而非 null。只判 null 的话，两个空串会被当成"重复分配了同一个键"，
    ///   而那个键根本不存在。
    /// English:
    ///   HK14 — unassigned entries are not duplicates, and the default configuration must save.
    ///
    ///   Spec §13.3 leaves pause unassigned by default and in principle all four could be, so
    ///   treating "both unassigned" as a conflict would make the default configuration unsavable:
    ///   the operator opens Settings for the first time, changes nothing, clicks save, and is
    ///   refused.
    ///
    ///   Empty and whitespace-only strings count as unassigned too, since a cleared text box
    ///   usually yields an empty string rather than null. Checking only for null would count two
    ///   empty strings as duplicate assignments of a key that does not exist.
    /// </summary>
    [Fact]
    public void Unassigned_entries_are_not_conflicts()
    {
        // 默认配置 / the default configuration
        Assert.Empty(HotkeyConflictDetector.FindConflicts(new HotkeySettings()));

        // 全部未分配 / everything unassigned
        Assert.Empty(HotkeyConflictDetector.FindConflicts(new HotkeySettings
        {
            ToggleMode = null,
            ForceSend = "",
            Cancel = "   ",
            PauseResume = null,
        }));

        Assert.Throws<ArgumentNullException>(
            () => HotkeyConflictDetector.FindConflicts(null!));
    }
}
