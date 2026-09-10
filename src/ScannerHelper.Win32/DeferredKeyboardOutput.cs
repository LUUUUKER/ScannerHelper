// =============================================================================
// DeferredKeyboardOutput.cs
//
// 中文：
//   本程序发往外界的**全部**合成输入都排在这一个队列里，并且**绝不在钩子
//   回调里发出去**。
//
//   ★ 这个类是一次实测事故的直接产物，不是预防性设计。
//
//     2026-09-10 的第一次 4b 实测：钩子回调最长耗时 3379 毫秒，381 次超过
//     50 毫秒预算，处理完成的扫描 0 枪。现场表现是——焦点在本程序的输入框
//     里一切正常，焦点在别的程序里则打字和扫码都会冒出一长串重复字符。
//
//     成因是我把 SendInput 放在了钩子回调里。补发要把合成按键送进**另一个
//     进程**的输入队列，那个进程还可能挂着输入法，这一趟可以阻塞很久；而
//     钩子回调是同步的，Windows 正在等我们的答复。于是：
//
//       1. 工人按下一个键，我们的钩子说「吞掉」，但迟迟不返回；
//       2. 超时之后 **Windows 不再理会我们的返回值，把这个键照常投递出去**
//          （并且可能顺手把钩子摘掉，规格 §19.1 说的正是这件事）；
//       3. 稍后我们又补发了一次。
//
//     同一下按键于是送达两次。回调越慢积压越多，最后滚成一长串。
//     「焦点在本程序里正常」也由此解释：注入的按键落在自己的窗口里那趟路很短。
//
//     更要命的是连带后果：回调卡住时消息循环也停了，WM_INPUT 排不上队，
//     50 毫秒的关联窗口必然超时，于是**每一个按键都被判成「来源不明、按普通
//     键盘补发」**——扫码枪从头到尾没被认出来过，那 0 枪就是这么来的。
//     扫码枪的字符被当成扫描码补发进输入法，拼出了汉字。
//
//   ★ 因此规则是：钩子回调只做决定，一个字节都不往外发。
//
//     入队极便宜（几十纳秒），发送由消息循环来做。Raw Input 与定时器本来就
//     跑在消息循环上，那里可以立刻发；只有钩子回调这条路要投递一下。
//
//   ★ 为什么补发与文本输出共用**同一个**队列。
//
//     两者的先后关系是有意义的：一枪扫完要发出结果，而同一时刻可能还压着
//     几个待补发的普通按键。分成两个队列，就等于把它们的相对顺序交给两次
//     独立的排空，工人看到的字符次序会和他实际的操作对不上。
//
//     队列里连续的补发会合并成一次 SendInput——顺序只在一次调用之内才有
//     保证（见 KeystrokeReplayService）。
//
// English:
//   Every synthesized input this application sends outward queues here, and none of it is ever
//   sent from inside the hook callback.
//
//   This class comes from a measured failure rather than from caution. The first 4b run, on
//   2026-09-10, recorded a longest hook callback of 3379 ms, 381 callbacks over the 50 ms
//   budget, and zero scans processed. On screen: with focus in this program's own text box
//   everything looked fine, while with focus in another program both typing and scanning
//   produced long runs of repeated characters.
//
//   The cause was calling SendInput inside the hook callback. Replay pushes synthesized
//   keystrokes into *another process's* input queue, possibly through its IME, and that trip can
//   block for a long time — while the hook callback is synchronous and Windows is waiting for
//   the answer. So: the operator presses a key, our hook says "swallow" but does not return; on
//   timeout Windows disregards the return value and delivers the key anyway (possibly removing
//   the hook, which is exactly what spec §19.1 describes); and a moment later we replay it too.
//   The keystroke lands twice, and the slower the callback the more this compounds — into the
//   long runs that were observed. It also explains why focus inside this program looked fine:
//   injected keys landing in our own window travel a very short path.
//
//   The knock-on effect was worse: while the callback is stuck the message loop is stopped too,
//   WM_INPUT cannot be processed, the 50 ms correlation window necessarily expires, and every
//   keystroke is settled as "source unknown, replay as ordinary keyboard". The scanner was never
//   identified at all — which is where the zero scans came from — and its characters were
//   replayed as scan codes into an IME, which composed them into Chinese.
//
//   Hence the rule: the hook callback decides and sends not one byte. Enqueuing costs tens of
//   nanoseconds; sending is the message loop's job. Raw Input and the timer already run on the
//   message loop and can send immediately; only the hook callback has to post.
//
//   Replay and text output share one queue because their relative order carries meaning: a scan
//   completing emits its result while ordinary keystrokes may still be waiting to be replayed.
//   Two queues would hand that ordering to two independent drains, and the operator would see
//   characters in an order that does not match what they did. Consecutive replays in the queue
//   are merged into one SendInput, order being guaranteed only within a single call (see
//   KeystrokeReplayService).
//
// 包含的类型 / Types in this file:
//   DeferredKeyboardOutput
// =============================================================================

using System.ComponentModel;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Output;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：把全部合成输出排队，交给消息循环发送。
/// English: Queues all synthesized output for the message loop to send.
/// </summary>
public sealed class DeferredKeyboardOutput : IKeyboardOutputService
{
    /// <summary>
    /// 中文：
    ///   队列里的一项。
    ///
    ///   用一个带类型标记的结构体，而不是三个队列或者 Action 委托：
    ///   委托要分配对象，而入队会发生在钩子回调路径上——那里的对象分配会
    ///   带来 GC 暂停，而 GC 暂停正是能顶穿 LowLevelHooksTimeout 的东西
    ///   （规格 §19.1）。这个类存在的全部意义就是别再顶穿它。
    /// English:
    ///   One queued item. A tagged struct rather than three queues or an Action delegate:
    ///   delegates allocate, and enqueuing happens on the hook callback path where an allocation
    ///   can trigger the GC pause that blows the LowLevelHooksTimeout budget (spec §19.1) — the
    ///   very thing this class exists to stop.
    /// </summary>
    private readonly record struct OutputItem(OutputKind Kind, KeyEvent KeyEvent, string? Text);

    private enum OutputKind
    {
        Replay,
        Text,
        Enter,
    }

    private readonly Queue<OutputItem> _queue = new();
    private readonly List<KeyEvent> _replayBatch = [];
    private readonly KeystrokeReplayService _replay = new();
    private readonly SendInputKeyboardOutputService _output = new();

    private long _replayedCount;
    private long _emittedTextCount;
    private long _faultCount;
    private Exception? _lastFault;

    /// <summary>
    /// 中文：
    ///   队列里是否还有东西。
    ///
    ///   ★ 这个属性只在**捕获线程**上读写。整个类没有一把锁，也不该有——
    ///     锁是钩子回调路径上最不能出现的东西（规格 §19）。串行性来自
    ///     结构：入队要么在钩子回调里、要么在消息循环里，而两者都是捕获
    ///     线程，不会并发（见 ICaptureThreadWork）。
    /// English:
    ///   Whether anything is queued.
    ///
    ///   Read and written on the capture thread only. The class holds no lock and should not: a
    ///   lock is the one thing that must never appear on the hook callback path (spec §19).
    ///   Serialization is structural — enqueuing happens either in the hook callback or on the
    ///   message loop, both of which are the capture thread and never concurrent (see
    ///   ICaptureThreadWork).
    /// </summary>
    public bool HasPending => _queue.Count > 0;

    /// <summary>
    /// 中文：已补发的按键数。
    /// English: How many keystrokes were replayed.
    /// </summary>
    public long ReplayedCount => Interlocked.Read(ref _replayedCount);

    /// <summary>
    /// 中文：已发出的文本次数。
    /// English: How many text emissions were sent.
    /// </summary>
    public long EmittedTextCount => Interlocked.Read(ref _emittedTextCount);

    /// <summary>
    /// 中文：发送时出错的次数。
    /// English: How many sends failed.
    /// </summary>
    public long FaultCount => Interlocked.Read(ref _faultCount);

    /// <summary>
    /// 中文：最近一次发送异常。
    /// English: The most recent send exception.
    /// </summary>
    public Exception? LastFault => Volatile.Read(ref _lastFault);

    /// <summary>
    /// 中文：
    ///   把一次文本输出排进队列。**不会立刻发送。**
    ///
    ///   协调器在一枪处理完时调用它，而那一刻很可能正在钩子回调里
    ///   （终止符是从钩子通道来的）。所以这里只入队。
    /// English:
    ///   Queues one text emission; nothing is sent yet. The coordinator calls this when a scan
    ///   completes, and that moment is very likely inside the hook callback — the terminator
    ///   arrives on the hook channel — so this only enqueues.
    /// </summary>
    public void EmitText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        _queue.Enqueue(new OutputItem(OutputKind.Text, default, text));
    }

    /// <summary>
    /// 中文：把一次回车排进队列。**不会立刻发送。**
    /// English: Queues one Enter; nothing is sent yet.
    /// </summary>
    public void EmitEnter() => _queue.Enqueue(new OutputItem(OutputKind.Enter, default, null));

    /// <summary>
    /// 中文：把一次补发排进队列。**不会立刻发送。**
    /// English: Queues one replay; nothing is sent yet.
    /// </summary>
    public void EnqueueReplay(in KeyEvent keyEvent)
        => _queue.Enqueue(new OutputItem(OutputKind.Replay, keyEvent, null));

    /// <summary>
    /// 中文：
    ///   把队列排空，真正发出去。**只能在消息循环上调用，绝不能在钩子回调里。**
    ///   步骤：
    ///     1. 取出队头；
    ///     2. 是补发就把后面连续的补发一起收进批次，合成一次 SendInput；
    ///     3. 是文本或回车就直接发。
    ///
    ///   ★ 步骤 2 的合并不是省开销：SendInput 只保证**一次调用之内**的事件
    ///     不被别的输入插进来。拆成多次，工人恰好此刻按下的键就可能落在
    ///     我们发出的两个字符中间。
    ///
    ///   ★ 单项失败不中断整个队列。补发失败意味着工人的按键真的丢了，
    ///     但那不是把后面几项也一起丢掉的理由——记下来，接着发。
    /// English:
    ///   Drains the queue and actually sends. Callable only on the message loop, never from the
    ///   hook callback.
    ///   Steps: (1) take the head; (2) for a replay, gather the consecutive replays behind it
    ///   into one batch and one SendInput; (3) send text or Enter directly.
    ///
    ///   The merge in step 2 is not about overhead: SendInput only guarantees that the events
    ///   within a single call are not interleaved with other input. Split across calls, a
    ///   keystroke the operator happens to make could land between two of our characters.
    ///
    ///   A failing item does not abort the queue. A failed replay means the operator's keystroke
    ///   really was lost, but that is no reason to drop the items behind it as well: record it
    ///   and carry on.
    /// </summary>
    public void Drain()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();

            switch (item.Kind)
            {
                case OutputKind.Replay:
                    // 步骤 2 / Step 2
                    _replayBatch.Clear();
                    _replayBatch.Add(item.KeyEvent);

                    while (_queue.Count > 0 && _queue.Peek().Kind == OutputKind.Replay)
                    {
                        _replayBatch.Add(_queue.Dequeue().KeyEvent);
                    }

                    Send(() =>
                    {
                        _replay.Replay(_replayBatch);
                        Interlocked.Add(ref _replayedCount, _replayBatch.Count);
                    });

                    break;

                case OutputKind.Text:
                    Send(() =>
                    {
                        _output.EmitText(item.Text!);
                        Interlocked.Increment(ref _emittedTextCount);
                    });

                    break;

                default:
                    Send(_output.EmitEnter);
                    break;
            }
        }
    }

    /// <summary>
    /// 中文：执行一次发送并兜住异常。Win32Exception 是预期内的失败
    ///       （最常见是 UIPI，规格 §2.1 假设 A2），别的异常同样不能让
    ///       消息循环倒下——循环一停，钩子还挂着却再也没人处理 WM_INPUT。
    /// English: Performs one send and contains its exception. A Win32Exception is the expected
    ///          failure (usually UIPI, spec §2.1's assumption A2), and no other exception may
    ///          bring down the message loop either: with the loop stopped the hook is still
    ///          installed and nothing processes WM_INPUT any more.
    /// </summary>
    private void Send(Action send)
    {
        try
        {
            send();
        }
        catch (Win32Exception win32Exception)
        {
            RecordFault(win32Exception);
        }
        catch (Exception sendException)
        {
            RecordFault(sendException);
        }
    }

    private void RecordFault(Exception fault)
    {
        Interlocked.Increment(ref _faultCount);
        Volatile.Write(ref _lastFault, fault);
    }
}
