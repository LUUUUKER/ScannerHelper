// =============================================================================
// HookDecision.cs
//
// 中文：
//   钩子回调对一次按键必须**当场**给出的决定，以及给出它的处理器签名。
//
//   ★ 只有两个取值，而且没有"稍后再说"这一档。
//
//     Windows 的钩子回调是同步的：它必须立刻返回放行或吞掉，不能阻塞等待。
//     等待超过 LowLevelHooksTimeout（默认 300 毫秒），系统会跳过回调、通常
//     还把钩子摘掉，且不发任何通知（规格 §19.1）。
//
//     Core 那一侧的 CorrelationDecision 有四个取值，其中 Undecided 表示
//     "先吞掉，稍后告诉你结果"。到了这一层，那个"稍后"已经不存在了——
//     Undecided 必须被翻译成 Swallow。这两个枚举因此不能合并：一个描述
//     业务判断，一个描述必须立刻交给 Windows 的答案。
//
// English:
//   The verdict a hook callback must give on the spot for each keystroke, and the signature of
//   whatever gives it.
//
//   Two values only, with no "I will say later". A Windows hook callback is synchronous: it must
//   return pass-or-swallow immediately and cannot block. Waiting past LowLevelHooksTimeout
//   (300 ms by default) has the system skip the callback and usually remove the hook, without
//   notification (spec §19.1).
//
//   Core's CorrelationDecision has four values, one of which — Undecided — means "swallow for now
//   and I will tell you shortly". By this layer that "shortly" no longer exists and Undecided must
//   be translated into Swallow. The two enums therefore cannot be merged: one describes a business
//   judgement, the other an answer Windows needs at once.
//
// 包含的类型 / Types in this file:
//   HookDecision          放行还是吞掉
//   KeyboardEventHandler  处理一次按键并给出决定
// =============================================================================

using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：钩子回调对一次按键的处置。
/// English: What the hook callback does with a keystroke.
/// </summary>
public enum HookDecision
{
    /// <summary>
    /// 中文：放行。业务软件照常收到这次按键。
    /// English: Pass it through; the business application receives the keystroke as usual.
    /// </summary>
    PassThrough,

    /// <summary>
    /// 中文：
    ///   吞掉。业务软件**永远**不会看到这次按键（规格 §5.4）。
    ///
    ///   ★ 吞掉之后，把它还给工人就成了调用方的责任。规格 §19 明写"绝不
    ///     无限期吞掉普通键盘输入"，而笔记本工位没有备用键盘可插
    ///     （规格假设 A4）——吞了不补，键盘就是彻底失灵。
    /// English:
    ///   Swallow it; the business application never sees the keystroke (spec §5.4).
    ///
    ///   Once swallowed, giving it back to the operator is the caller's responsibility. Spec §19
    ///   forbids swallowing normal keyboard input indefinitely, and a laptop workstation has no
    ///   spare keyboard (spec assumption A4): swallowed and not returned means a dead keyboard.
    /// </summary>
    Swallow,
}

/// <summary>
/// 中文：
///   处理一次按键并给出决定。
///
///   ★ 实现必须极快（规格 §19）：不做文件 IO、不跑正则、不碰 UI、不加锁、
///     不分配对象。超时一次，Windows 就摘掉钩子且不通知——进程还在跑、
///     界面还显示着模式，而原始条码已经直接流进业务系统（规格 §19.1）。
/// English:
///   Handles one keystroke and returns the verdict.
///
///   Implementations must be extremely fast (spec §19): no file IO, no regex, no UI, no locks, no
///   allocation. Exceed the timeout once and Windows removes the hook without notice — the process
///   still running and the UI still showing a mode while raw barcodes flow straight into the
///   business system (spec §19.1).
/// </summary>
public delegate HookDecision KeyboardEventHandler(in ObservedInputEvent observedEvent);
