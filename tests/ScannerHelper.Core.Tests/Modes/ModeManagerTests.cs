// =============================================================================
// ModeManagerTests.cs
//
// 中文：
//   ModeManager 的行为测试（对应测试清单 M1~M7）。
//
//   ModeManager 的职责被刻意限制得很窄：它只持有当前扫描模式（SN / SKU）
//   并提供安全的切换。它不知道 WPF，不知道扫码枪，也不知道配置文件的存在。
//
//   其中两条是产品规格的硬性要求，不是普通的功能点：
//     - M1：启动恒为 SN（规格 §3、§7）。工人每次启动程序看到的都必须是
//           同一个已知状态，绝不能"上次退出时是 SKU，这次启动还是 SKU"。
//     - M7：构造函数不接受任何配置/持久化类型（规格 §14）。这不是靠约定
//           约束，而是让"把当前模式存起来"这件事在编译期就无从下手。
//
//   M6 看似琐碎但有实际意义：模式变更会触发 UI 重绘并播放提示音，把同值
//   重复设置也当成变更，会造成无意义的闪烁和响声。
//
// English:
//   Behavior tests for ModeManager (test plan M1–M7).
//
//   ModeManager's responsibility is deliberately narrow: it holds the current
//   scan mode and offers a safe toggle. It knows nothing of WPF, of scanners,
//   or of configuration files.
//
//   Two of these are hard product requirements rather than ordinary features:
//     - M1: startup is always SN (spec §3, §7). Every launch must present the
//           same known state, never "it was SKU when you quit, so it is SKU now".
//     - M7: no constructor accepts a settings/persistence type (spec §14).
//           Persisting the mode is prevented at compile time, not by convention.
//
//   M6 looks trivial but earns its place: a mode change repaints the UI and
//   plays a sound, so treating a same-value set as a change causes pointless
//   flicker and noise.
//
// 包含的测试 / Tests in this file:
//   M1  Starts_in_Sn
//   M2  Toggle_from_Sn_gives_Sku
//   M3  Toggle_from_Sku_gives_Sn
//   M4  Toggle_twice_returns_to_original
//   M5  Toggle_raises_event_with_previous_and_current
//   M6  Setting_same_mode_raises_no_event
//   M7  Constructor_accepts_no_persistence_dependency
// =============================================================================

using System.Reflection;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Modes;

namespace ScannerHelper.Core.Tests.Modes;

public class ModeManagerTests
{
    /// <summary>
    /// 中文：M1 — 新建的 ModeManager 必须处于 SN 模式。
    ///       规格 §3 要求启动模式恒为 SN 且不持久化。
    /// English: M1 — a new ModeManager must be in SN mode.
    ///          Spec §3 requires startup mode to always be SN, never persisted.
    /// </summary>
    [Fact]
    public void M1_Starts_in_Sn()
    {
        var modeManager = new ModeManager();

        Assert.Equal(ScanMode.Sn, modeManager.CurrentMode);
    }

    /// <summary>
    /// 中文：M2 — 从 SN 切换后应为 SKU。
    /// English: M2 — toggling from SN yields SKU.
    /// </summary>
    [Fact]
    public void M2_Toggle_from_Sn_gives_Sku()
    {
        var modeManager = new ModeManager();

        modeManager.Toggle();

        Assert.Equal(ScanMode.Sku, modeManager.CurrentMode);
    }

    /// <summary>
    /// 中文：M3 — 从 SKU 切换后应为 SN。
    ///       用 SetMode 而非连续两次 Toggle 来构造前置状态，使本测试与 M2 相互独立：
    ///       M2 若失败不应连带影响 M3 的判断。
    /// English: M3 — toggling from SKU yields SN.
    ///          The precondition is arranged with SetMode rather than two Toggle
    ///          calls, so this test stays independent of M2's outcome.
    /// </summary>
    [Fact]
    public void M3_Toggle_from_Sku_gives_Sn()
    {
        var modeManager = new ModeManager();
        modeManager.SetMode(ScanMode.Sku);

        modeManager.Toggle();

        Assert.Equal(ScanMode.Sn, modeManager.CurrentMode);
    }

    /// <summary>
    /// 中文：M4 — 连续切换两次回到起点。
    ///       这条约束保证只存在 SN 与 SKU 两个模式，不会因为将来新增模式
    ///       而让 F8 变成一个三态或多态轮转键——那会让工人无法凭肌肉记忆
    ///       预测按下 F8 之后的结果。
    /// English: M4 — two toggles return to the starting mode.
    ///          This pins the cycle to exactly two modes, so F8 never becomes a
    ///          three-or-more-state rotation that a worker cannot predict.
    /// </summary>
    [Fact]
    public void M4_Toggle_twice_returns_to_original()
    {
        var modeManager = new ModeManager();
        var originalMode = modeManager.CurrentMode;

        modeManager.Toggle();
        modeManager.Toggle();

        Assert.Equal(originalMode, modeManager.CurrentMode);
    }

    /// <summary>
    /// 中文：M5 — 切换时触发的事件必须同时携带旧值和新值。
    ///       只给新值是不够的：UI 需要旧值来决定过渡动画方向，而
    ///       SN→SKU 与 SKU→SN 需要播放可区分的提示音（规格 §7）。
    /// English: M5 — the change event must carry both the previous and the new
    ///          mode. The new value alone is insufficient: the UI needs the
    ///          previous mode for transition direction, and SN→SKU must be
    ///          audibly distinguishable from SKU→SN (spec §7).
    /// </summary>
    [Fact]
    public void M5_Toggle_raises_event_with_previous_and_current()
    {
        var modeManager = new ModeManager();
        ModeChangedEventArgs? capturedEvent = null;
        modeManager.ModeChanged += (_, eventArgs) => capturedEvent = eventArgs;

        modeManager.Toggle();

        Assert.NotNull(capturedEvent);
        Assert.Equal(ScanMode.Sn, capturedEvent.PreviousMode);
        Assert.Equal(ScanMode.Sku, capturedEvent.CurrentMode);
    }

    /// <summary>
    /// 中文：M6 — 把模式设为当前已有的值时不得触发事件。
    ///       模式变更会重绘 UI 并播放提示音；若同值重设也算变更，
    ///       工人会听到没有任何状态改变的"切换成功"提示音。
    /// English: M6 — setting the mode to its current value raises no event.
    ///          A change repaints the UI and plays a sound; treating a no-op as
    ///          a change would sound a "mode switched" cue when nothing switched.
    /// </summary>
    [Fact]
    public void M6_Setting_same_mode_raises_no_event()
    {
        var modeManager = new ModeManager();
        var raisedEventCount = 0;
        modeManager.ModeChanged += (_, _) => raisedEventCount++;

        modeManager.SetMode(ScanMode.Sn);   // 已经是 Sn / already Sn

        Assert.Equal(0, raisedEventCount);
    }

    /// <summary>
    /// 中文：
    ///   M7 — ModeManager 的任何公开构造函数都不得接受配置或持久化类型。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 取出 ModeManager 的全部公开构造函数；
    ///     2. 展开它们的参数类型名称；
    ///     3. 与可疑关键字（Settings / Store / Config / Persist / Repository）
    ///        逐一匹配；
    ///     4. 不得有任何匹配。
    ///
    ///   为什么用反射而不是靠代码评审：规格 §14 明确要求"绝不持久化当前
    ///   扫描模式"。将来某次改动很可能出于好意——"帮工人记住上次用的模式"
    ///   ——而给构造函数注入一个 ISettingsStore。那个改动本身看起来完全
    ///   合理，只有对照规格才知道它是错的。这条测试把规格变成了构建的
    ///   一部分，让这类改动无法悄悄通过。
    ///
    /// English:
    ///   M7 — no public constructor of ModeManager accepts a settings or
    ///   persistence type.
    ///   Steps: collect public constructors, flatten their parameter type names,
    ///   match against suspicious keywords, require zero matches.
    ///
    ///   Why reflection rather than code review: spec §14 requires that the
    ///   current scan mode is never persisted. A future change will plausibly
    ///   inject an ISettingsStore here with good intentions — "remember the
    ///   worker's last mode". That change looks entirely reasonable on its own;
    ///   only the spec says otherwise. This test makes the spec part of the
    ///   build so such a change cannot land quietly.
    /// </summary>
    [Fact]
    public void M7_Constructor_accepts_no_persistence_dependency()
    {
        string[] persistenceKeywords =
            ["Settings", "Store", "Config", "Persist", "Repository"];

        // 步骤 1、2 / Steps 1–2
        var constructorParameterTypeNames = typeof(ModeManager)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToArray();

        // 步骤 3 / Step 3
        var offendingParameterTypeNames = constructorParameterTypeNames
            .Where(typeName => persistenceKeywords.Any(
                keyword => typeName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // 步骤 4 / Step 4
        Assert.True(
            offendingParameterTypeNames.Length == 0,
            "规格 §14 要求当前扫描模式绝不持久化，启动恒为 SN。"
            + " ModeManager 的构造函数不得接受配置或持久化依赖。"
            + $" 发现 / Found: {string.Join(", ", offendingParameterTypeNames)}");
    }
}
