// =============================================================================
// Sounds.cs
//
// 中文：
//   两种提示音：切换模式、出错（规格 §7、§13）。
//
//   ★ 声音在这里不是装饰，是**替代看屏幕**的那条通道。
//
//     工人低头搬货、双手都占着，他不会一直盯着屏幕。模式搞错的代价是接下来几十枪
//     都以错误的形式进仓库系统，而他毫不知情。
//
//   ★ 但两种声音都**默认关闭**（决策 D-28，取代规格 §7 的"默认出声"）。
//
//     仓库本来就不安静，一个没人要求就每次切模式都响的程序，发出的是噪声。而被
//     无视的提示音不只是没用——它会让人对这个程序发出的**所有**声音都不敏感，
//     包括真正要紧的那一声。默认安静、要用的人自己打开，才能让"响了"保持分量。
//
//   ★ 模式音只有**一声**：SN 低音，SKU 高音，相差一个八度（决策 D-28）。
//
//     原来做成升调/降调两声，是想让方向本身可听。实际用下来更差：两声占掉两百多
//     毫秒，工人还没听完就在扫下一枪了，而两组两声忙起来听着差不多。单声的音高是
//     瞬间可辨的，不需要听完一个序列再比较。
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
//   Sound here is not decoration but the channel that replaces looking at the screen: the operator
//   is bent over goods with both hands busy, and getting the mode wrong sends the next few dozen
//   scans into the warehouse system in the wrong shape with nobody aware.
//
//   Both sounds are nevertheless off by default (decision D-28, superseding spec §7's "by
//   default"). A warehouse is never quiet, and a program that beeps on every mode change without
//   being asked is producing noise — and an ignored sound is worse than useless, because it dulls
//   the operator to every sound this program makes, including the one that matters. Silent by
//   default, with the people who want it turning it on, is what keeps "it beeped" meaningful.
//
//   The mode sound is a single tone, low for SN and high for SKU, an octave apart (D-28). The
//   original rising/falling pair tried to make the direction itself audible and was worse in use:
//   two tones cost over two hundred milliseconds, by which time the next item is already being
//   scanned, and two pairs sound much alike when things are busy. A single pitch is recognizable
//   instantly, with no sequence to hear out and compare.
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
    ///   模式变了，响一声：SN 低音，SKU 高音。
    ///   输入：mode 切换之后的模式；isEnabled 设置里是否开着提示音（默认关闭）。
    /// English:
    ///   The mode changed; one tone, low for SN and high for SKU. isEnabled is the setting, off by
    ///   default.
    /// </summary>
    public static void ModeChanged(ScanMode mode, bool isEnabled)
    {
        if (!isEnabled)
        {
            return;
        }

        // ★ 一声，不是两声（决策 D-28）。
        //
        //   原来做成"升调 / 降调"两声，是想让方向本身可听。实际用下来那反而更差：
        //   两声要占掉两百多毫秒，工人还没听完就已经在扫下一枪了，而两组两声
        //   在忙起来的时候听着差不多。单声的音高是**瞬间**可辨的，不需要听完
        //   一个序列再比较。
        //
        //   SN 低、SKU 高，相差一个八度——这个间隔在任何嘈杂环境里都分得开，
        //   而且不需要有音乐训练。
        // One tone rather than two (decision D-28). The original rising/falling pair tried to make
        // the direction itself audible, and in use that was worse: two tones cost over two hundred
        // milliseconds, by which time the operator is already scanning the next item, and two pairs
        // sound much alike when things are busy. A single tone's pitch is recognizable instantly,
        // with no sequence to hear out and compare. SN low, SKU high, an octave apart — an interval
        // that survives any amount of noise and needs no musical training.
        var frequency = mode == ScanMode.Sku ? 1046u : 523u;

        Play(() => Beep(frequency, 120));
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
