// =============================================================================
// ObservationBuffer.cs
//
// 中文：
//   连接钩子回调与界面线程的环形缓冲区。
//
//   ★ 这个类存在的唯一理由，是让低层钩子回调**什么都不用等、什么都不用分配**。
//
//     规格 §19 禁止在回调里做文件 IO、正则、UI 操作和慢日志。这条清单还应该
//     加上两项，理由完全相同：
//
//       加锁    —— 界面线程一旦持锁，回调就得等；等超过 LowLevelHooksTimeout
//                  （默认 300 毫秒），Windows 会跳过回调、通常还把钩子摘掉，
//                  且不发任何通知。工人这时看到的是"程序还在跑、模式还显示着"，
//                  而原始条码已经直接进了业务系统（规格 §19.1）。
//       分配    —— 每次分配都在累积 GC 压力，而一次 GC 暂停同样可能顶穿那
//                  300 毫秒。用引用类型记事件，等于亲手制造出规格 §19.1 要
//                  检测的那个故障。
//
//     所以写入路径上只有一次 Interlocked.Increment 和一次数组元素赋值。
//     没有锁，没有分配，没有任何可能阻塞的调用。
//
//   ★ 满了就丢最旧的，而不是阻塞写入方。
//
//     取舍很明确：丢事件只让这次**测量**不完整，阻塞回调会让**整个系统的键盘**
//     卡住。而且丢弃是被记账的——DroppedCount 不为零时，这份测量数据就不能
//     当作完整证据使用，报告必须如实写出来。一份悄悄少了几个事件的时序分布，
//     比没有数据更危险。
//
//   一处诚实的说明：写入方绕满一圈追上读取方时，理论上可能读到一个写了一半的
//   结构体（多字段结构体的赋值不是原子的）。容量 16384、界面每 100 毫秒取一次，
//   要发生这种情况需要在 100 毫秒内产生一万六千次按键——扫码枪也远达不到。
//   这里选择记录这个前提，而不是为它加一层同步：加锁会破坏本类存在的全部理由。
//
// English:
//   The ring buffer joining the hook callback to the UI thread.
//
//   Its sole reason to exist is to let the low-level hook callback wait for nothing
//   and allocate nothing.
//
//   Spec §19 forbids file IO, regex, UI work and slow logging inside the callback.
//   Two more belong on that list for the same reason. Locking: if the UI thread holds
//   the lock the callback must wait, and waiting past LowLevelHooksTimeout (300 ms by
//   default) has Windows skip the callback and usually remove the hook, with no
//   notification — leaving the operator with a program that is still running and still
//   displaying a mode while raw codes go straight into the business system
//   (spec §19.1). Allocation: every allocation adds GC pressure, and a GC pause can
//   blow the same 300 ms budget. Recording events as reference types would manufacture
//   the very failure spec §19.1 exists to detect.
//
//   So the write path is one Interlocked.Increment and one array element assignment.
//   No locks, no allocation, nothing that can block.
//
//   When full, the oldest events are dropped rather than blocking the writer. The
//   trade is clear: dropping events makes this *measurement* incomplete, whereas
//   blocking the callback freezes the keyboard for the entire system. Drops are
//   accounted for — a non-zero DroppedCount means the data cannot be used as complete
//   evidence and the report must say so. A timing distribution quietly missing a few
//   events is more dangerous than no data at all.
//
//   One honest caveat: if the writer laps the reader, a half-written struct could in
//   principle be read, since assigning a multi-field struct is not atomic. With 16384
//   slots and the UI draining every 100 ms, that needs sixteen thousand keystrokes in
//   a tenth of a second — far beyond any scanner. The precondition is recorded rather
//   than synchronized against, because a lock would defeat this class's entire purpose.
//
// 包含的成员 / Members in this file:
//   Capacity       槽位数量
//   DroppedCount   因缓冲区写满而丢弃的事件数
//   Write          写入一个事件（钩子回调调用，必须极快）
//   Drain          取走当前累积的全部事件（界面线程调用）
// =============================================================================

namespace ScannerHelper.Win32.Observation;

/// <summary>
/// 中文：单写单读的无锁环形缓冲区，专用于把观测到的按键事件送出钩子回调。
/// English: A single-writer, single-reader lock-free ring buffer, used solely to carry
///          observed key events out of the hook callback.
/// </summary>
public sealed class ObservationBuffer
{
    private readonly ObservedInputEvent[] _slots;

    /// <summary>
    /// 中文：把序号折算成槽位下标的掩码。容量取 2 的幂，于是取模退化成一次按位与——
    ///       写入路径上连一次除法都不做。
    /// English: Masks a sequence number down to a slot index. The capacity is a power
    ///          of two, so the modulo degrades into a bitwise AND and the write path
    ///          performs not even a division.
    /// </summary>
    private readonly int _indexMask;

    /// <summary>
    /// 中文：已写入的事件总数。由写入方用 Interlocked 递增。
    /// English: Total events written; incremented by the writer via Interlocked.
    /// </summary>
    private long _writeCount;

    /// <summary>
    /// 中文：已读走的事件总数。只有读取方碰它，因此无需同步。
    /// English: Total events read. Touched by the reader alone, so it needs no
    ///          synchronization.
    /// </summary>
    private long _readCount;

    private long _droppedCount;

    /// <summary>
    /// 中文：
    ///   构造缓冲区。
    ///   输入：capacity 槽位数量，必须是大于 0 的 2 的幂。
    ///   输出：缓冲区实例。
    ///
    ///   默认 16384 个槽位。按每次按键产生按下与弹起两个事件、两条通道各一份
    ///   计算，一个字符约 4 个事件；即便界面卡住整整一秒，也要每秒一万六千
    ///   多个事件才会开始丢弃。这个余量是刻意留大的：缓冲区太小导致的丢弃会
    ///   让测量数据失真，而失真的数据比没有数据更糟。
    /// English:
    ///   Creates the buffer. capacity must be a positive power of two.
    ///
    ///   The default is 16384 slots. At two events per keypress (down and up) across
    ///   two channels, one character is roughly four events, so even a UI stalled for
    ///   a full second would need over sixteen thousand events per second before
    ///   dropping begins. The headroom is deliberately generous: drops caused by an
    ///   undersized buffer distort the measurement, and distorted data is worse than
    ///   none.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：容量不是正的 2 的幂。 English: The capacity is not a positive power of two.
    /// </exception>
    public ObservationBuffer(int capacity = 16384)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity,
                "容量必须是大于 0 的 2 的幂，否则无法用按位与取代取模。"
                + " The capacity must be a positive power of two so the modulo can be"
                + " replaced by a bitwise AND.");
        }

        _slots = new ObservedInputEvent[capacity];
        _indexMask = capacity - 1;
    }

    /// <summary>
    /// 中文：槽位数量。
    /// English: The number of slots.
    /// </summary>
    public int Capacity => _slots.Length;

    /// <summary>
    /// 中文：因写满而被丢弃的事件数。**不为零时这批测量数据就不完整**，
    ///       报告必须如实标注，不能当作完整证据使用。
    /// English: Events dropped because the buffer was full. A non-zero value means the
    ///          measurement is incomplete and the report must say so rather than
    ///          presenting it as complete evidence.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// 中文：
    ///   写入一个观测事件。
    ///   输入：observedEvent 要记录的事件。输出：无。
    ///
    ///   ★ 本方法运行在低层钩子回调里，是全程序时间预算最紧的一段代码。
    ///     整个方法体只有一次 Interlocked.Increment 和一次数组赋值：没有锁、
    ///     没有分配、没有异常路径、没有任何可能阻塞的调用。
    ///
    ///     往这里加任何东西之前，先读一遍规格 §19.1。
    /// English:
    ///   Records one observed event.
    ///
    ///   This runs inside the low-level hook callback, the tightest time budget in the
    ///   program. The whole body is one Interlocked.Increment and one array
    ///   assignment: no lock, no allocation, no exception path, nothing that can
    ///   block. Read spec §19.1 before adding anything to it.
    /// </summary>
    public void Write(in ObservedInputEvent observedEvent)
    {
        var sequence = Interlocked.Increment(ref _writeCount) - 1;
        _slots[(int)(sequence & _indexMask)] = observedEvent;
    }

    /// <summary>
    /// 中文：
    ///   取走自上次调用以来累积的事件。
    ///   输入：destination 接收数组，本次最多取走它能装下的数量。
    ///   输出：实际取走的事件数。
    ///   步骤：
    ///     1. 读出当前已写入总数；
    ///     2. 若未读数量超过容量，说明写入方已经绕圈覆盖了旧数据——把超出的
    ///        部分计入丢弃数，并把读取位置推进到最旧的仍然有效的事件；
    ///     3. 按 destination 的容量取走事件；
    ///     4. 推进读取位置。
    ///
    ///   由界面线程调用，因此可以从容一些——但也不必加锁：写入方只递增
    ///   _writeCount，读取方只碰 _readCount，两者不冲突。
    /// English:
    ///   Takes the events accumulated since the last call, up to what destination
    ///   holds, and returns how many were taken.
    ///   Steps: (1) read the current write count; (2) if more is unread than the buffer
    ///   holds, the writer has wrapped over old data — account for the overrun as drops
    ///   and advance the read position to the oldest still-valid event; (3) copy out;
    ///   (4) advance the read position.
    ///
    ///   Called from the UI thread, so it can afford to be leisurely — but still needs
    ///   no lock: the writer only increments _writeCount and the reader only touches
    ///   _readCount.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：destination 为 null。 English: destination is null.
    /// </exception>
    public int Drain(ObservedInputEvent[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // 步骤 1 / Step 1
        var written = Interlocked.Read(ref _writeCount);
        var unread = written - _readCount;

        // 步骤 2 / Step 2
        if (unread > _slots.Length)
        {
            var lost = unread - _slots.Length;
            Interlocked.Add(ref _droppedCount, lost);
            _readCount = written - _slots.Length;
            unread = _slots.Length;
        }

        // 步骤 3 / Step 3
        var count = (int)Math.Min(unread, destination.Length);
        for (var offset = 0; offset < count; offset++)
        {
            destination[offset] = _slots[(int)((_readCount + offset) & _indexMask)];
        }

        // 步骤 4 / Step 4
        _readCount += count;
        return count;
    }
}
