// =============================================================================
// Sounds.cs
//
// 中文：
//   两种提示音：切换模式、出错（规格 §7、§13）。
//
//   ★ 声音在这里不是装饰，是**替代看屏幕**的那条通道。
//
//     工人低头搬货、双手都占着，他不会一直盯着屏幕。规格 §7 要求切换模式时出声，
//     而且"SN→SKU 与 SKU→SN 若可行应当可听地区分"——因为模式搞错的代价是接下来
//     几十枪都以错误的形式进仓库系统，而他毫不知情。
//
//     所以两个模式用两个不同的音高：切到 SKU 升调，切回 SN 降调。听一下就知道
//     现在在哪一边，不用抬头。
//
//   ★ 出错音刻意和模式音**不同类**：两声急促的低音。
//
//     模式音是"事情按你的意思变了"，错误音是"停一下，这一枪没发出去"。两者若
//     只是音高不同，忙起来会听混；节奏不同才分得开。
//
//   ★ 一律在后台线程上响，绝不阻塞界面。
//
//     Beep 是同步的——在界面线程上响 150 毫秒，界面就卡 150 毫秒。而声音响起的
//     那一刻，往往正是工人在等界面给出反馈的那一刻。
//
// English:
//   Two sounds: mode change and error (spec §7, §13).
//
//   Sound here is not decoration but the channel that replaces looking at the screen. The operator
//   is bent over goods with both hands busy and does not watch the display. Spec §7 requires a
//   sound on mode change and that SN→SKU and SKU→SN be audibly distinguishable if practical,
//   because getting the mode wrong sends the next few dozen scans into the warehouse system in the
//   wrong shape with nobody aware. So the two modes use two pitches: rising into SKU, falling back
//   into SN — one listen and you know which side you are on without looking up.
//
//   The error sound is deliberately a different kind rather than another pitch: two short low
//   tones. A mode sound says "it changed as you asked"; an error says "stop, that scan did not go
//   out". Distinguished only by pitch they blur together when things are busy; a different rhythm
//   separates them.
//
//   Everything plays on a background thread and never blocks the UI. Beep is synchronous, so
//   playing it on the UI thread freezes the UI for its duration — and the moment a sound plays is
//   usually the moment the operator is waiting for the UI to respond.
//
// 包含的成员 / Members in this file:
//   ModeChanged  切换模式
//   Error        一枪没能发出去
// =============================================================================

using System.Runtime.InteropServices;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.App;

/// <summary>
/// 中文：提示音。
/// English: The notification sounds.
/// </summary>
public static class Sounds
{
    /// <summary>
    /// 中文：
    ///   发一声。频率单位赫兹，时长单位毫秒。
    ///
    ///   ★ 用 kernel32 的 Beep 而不是 System.Media.SystemSounds：后者只有系统
    ///     方案里那几个音，没法把两个模式区分开，而区分正是规格 §7 要的。
    ///     现代机器上没有蜂鸣器，Windows 会把它转到默认声卡上放。
    /// English:
    ///   Emits one tone, in hertz and milliseconds.
    ///
    ///   kernel32's Beep rather than System.Media.SystemSounds: the latter offers only the handful
    ///   of sounds in the system scheme and cannot tell two modes apart, which is exactly what
    ///   spec §7 asks for. Machines without a beeper have Windows route it to the default audio
    ///   device.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Beep(uint frequency, uint duration);

    /// <summary>
    /// 中文：
    ///   模式变了。切到 SKU 是升调，切回 SN 是降调。
    ///   输入：mode 切换之后的模式；isEnabled 设置里是否开着提示音。
    /// English:
    ///   The mode changed: rising into SKU, falling back into SN. isEnabled is the setting.
    /// </summary>
    public static void ModeChanged(ScanMode mode, bool isEnabled)
    {
        if (!isEnabled)
        {
            return;
        }

        // SN 在下、SKU 在上，方向与"进入更细的规则"这个感觉一致。
        // SN low, SKU high — the direction matches the sense of entering a stricter rule.
        var (first, second) = mode == ScanMode.Sku ? (660u, 880u) : (880u, 660u);

        Play(() =>
        {
            Beep(first, 90);
            Beep(second, 110);
        });
    }

    /// <summary>
    /// 中文：
    ///   一枪没能发出去，在等工人决定。
    ///   两声急促的低音——和模式音的节奏不同，忙起来也分得开。
    /// English:
    ///   A scan did not go out and awaits the operator. Two short low tones, a different rhythm
    ///   from the mode sound so the two stay apart when things are busy.
    /// </summary>
    public static void Error(bool isEnabled)
    {
        if (!isEnabled)
        {
            return;
        }

        Play(() =>
        {
            Beep(420, 130);
            Beep(420, 130);
        });
    }

    /// <summary>
    /// 中文：
    ///   在后台线程上响，并且**吞掉一切异常**。
    ///
    ///   ★ 没有声卡、被静音、被组策略禁掉——这些都可能让 Beep 失败，而它们
    ///     没有一个值得打断工人。声音是辅助通道，屏幕上那块大面板才是主通道；
    ///     为一个响不出来的提示音弹一个对话框，是本末倒置。
    /// English:
    ///   Plays on a background thread and swallows every exception. No audio device, muted, blocked
    ///   by policy — any of these can make Beep fail and none of them is worth interrupting the
    ///   operator. Sound is the secondary channel; the large panel on screen is the primary one,
    ///   and a dialog about a sound that would not play inverts the two.
    /// </summary>
    private static void Play(Action tones)
        => Task.Run(() =>
        {
            try
            {
                tones();
            }
            catch (Exception)
            {
                // 见上。 See above.
            }
        });
}
