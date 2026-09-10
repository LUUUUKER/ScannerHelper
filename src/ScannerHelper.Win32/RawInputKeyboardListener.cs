// =============================================================================
// RawInputKeyboardListener.cs
//
// 中文：
//   Raw Input 键盘通道的托管封装（Task 4a）。
//
//   与低层钩子那条通道相比，这条的位置完全不同：它是**投递**到窗口消息队列
//   里的，等我们取到时按键早已在去焦点程序的路上。它拦不住任何东西，它唯一
//   能提供、而钩子完全没有的东西是 —— **是哪台物理设备按的**。
//
//   ★ 关于时间戳的一处诚实说明。
//
//     本类记录的时间戳，是"消息循环处理到这条 WM_INPUT 的时刻"，而不是
//     "Windows 产生这条原始输入的时刻"。两者之间隔着一段消息队列排队时间。
//
//     这不是测量误差，恰恰是我们要测的东西。4a 真正要回答的问题是：钩子
//     回调必须当场决定放行还是吞掉时，**设备身份是否已经可用**。可用与否
//     取决于消息何时被我们取到，而不是取决于它何时被产生。所以这里量的
//     正是那个有决策意义的时刻。
//
//     但报告里必须写清这一点，否则读者会把这个数字误解成硬件时延。
//
//   ★ 用 RIDEV_INPUTSINK 注册，否则只在本窗口位于前台时才收得到事件。
//     而本程序的全部工作前提就是焦点在业务软件那边——不加这个标志，
//     真实场景下一个事件都收不到，开发者自己点着窗口测试却一切正常。
//
// English:
//   A managed wrapper over the Raw Input keyboard channel (Task 4a).
//
//   Its position differs entirely from the hook's: it is *posted* to a window's
//   message queue, so by the time we read it the keystroke is already on its way to
//   the focused application. It can block nothing. The one thing it offers that the
//   hook lacks completely is which physical device produced the input.
//
//   An honest note about the timestamp. What this records is the moment the message
//   loop reached this WM_INPUT, not the moment Windows produced the raw input; queue
//   latency sits between the two.
//
//   That is not measurement error — it is the thing being measured. What 4a actually
//   asks is whether device identity is *available* at the instant the hook callback
//   must decide to pass or swallow. Availability depends on when the message reaches
//   us, not on when it was produced, so this is the decision-relevant moment. The
//   report must still say so, or a reader will mistake the figure for hardware latency.
//
//   Registration uses RIDEV_INPUTSINK, without which events arrive only while this
//   window is in the foreground — and this application's entire premise is that focus
//   belongs to the business application. Omitting it yields no events at all in real
//   use while working perfectly when a developer clicks on their own window.
//
// 包含的成员 / Members in this file:
//   IsRegistered  是否已注册
//   Register      向 Windows 注册接收键盘原始输入
//   HandleRawInput  处理一条 WM_INPUT
//   Dispose       释放常驻的原生缓冲区
// =============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScannerHelper.Win32.Native;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：
///   处理一条已解析出来的原始输入事件。
///
///   ★ 没有返回值，这不是省略。Raw Input 是**投递**来的通知，事件早已被
///     Windows 送达焦点程序，这里无论做什么都拦不住它。能拦的只有钩子
///     （见 <see cref="HookDecision"/>）。两者的签名不同，正是为了让这个
///     差别在类型上就看得见，而不是靠记住。
/// English:
///   Handles one decoded raw-input event.
///
///   It returns nothing, which is not an omission: raw input is a *posted* notification and
///   the event has already been delivered to the focused application, so nothing done here
///   can stop it. Only the hook can (see <see cref="HookDecision"/>). The two signatures
///   differ precisely so that the distinction is visible in the types rather than remembered.
/// </summary>
public delegate void RawInputEventHandler(in ObservedInputEvent observedEvent);

/// <summary>
/// 中文：接收并解析键盘类原始输入。
/// English: Receives and decodes keyboard raw input.
/// </summary>
public sealed class RawInputKeyboardListener : IDisposable
{
    private readonly RawInputEventHandler _handler;

    /// <summary>
    /// 中文：
    ///   常驻的原生缓冲区，供 GetRawInputData 写入负载。
    ///
    ///   一次性分配、反复使用，而不是每条消息都分配一次：原始输入在扫码期间
    ///   来得非常密集（一枪几十个字符、每字符两个事件），每条都分配一次会
    ///   制造出完全没必要的 GC 压力。而 GC 暂停正是可能顶穿
    ///   LowLevelHooksTimeout、让 Windows 悄悄摘掉钩子的原因之一（规格 §19.1）——
    ///   本类虽然不在钩子回调里，但它与钩子跑在同一个线程上。
    /// English:
    ///   A long-lived native buffer for GetRawInputData to write into.
    ///
    ///   Allocated once and reused rather than per message: raw input arrives densely
    ///   during a scan (dozens of characters, two events each), and allocating per
    ///   message would create wholly unnecessary GC pressure. A GC pause is among the
    ///   things that can blow the LowLevelHooksTimeout budget and have Windows silently
    ///   remove the hook (spec §19.1) — and while this class is not the hook callback,
    ///   it runs on the same thread.
    /// </summary>
    private IntPtr _payloadBuffer;

    private int _payloadBufferSize;
    private bool _isDisposed;

    /// <summary>
    /// 中文：原始输入到达时的窗口消息号。宿主窗口需要在自己的窗口过程里认出它，
    ///       因此这里对外公开一份——否则调用方就得自己写一个 0x00FF 字面量，
    ///       而那正是原生常量四处散落的开始。
    /// English: The window message announcing raw input. Exposed here because the host
    ///          window must recognize it in its own window procedure; otherwise the
    ///          caller writes a bare 0x00FF literal, which is how native constants start
    ///          scattering across a codebase.
    /// </summary>
    public const int WindowMessage = NativeMethods.WM_INPUT;

    /// <summary>
    /// 中文：
    ///   构造监听器。此时并不注册，注册请调用 <see cref="Register"/>。
    ///   输入：handler 每条解析出来的事件的去处，不得为 null。
    /// English:
    ///   Creates the listener without registering; call <see cref="Register"/> for
    ///   that. handler receives every decoded event and must not be null.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：handler 为 null。 English: handler is null.
    /// </exception>
    public RawInputKeyboardListener(RawInputEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
        _payloadBufferSize = Marshal.SizeOf<RawInputNative.RAWINPUTKEYBOARD>();
        _payloadBuffer = Marshal.AllocHGlobal(_payloadBufferSize);
    }

    /// <summary>
    /// 中文：
    ///   构造一个只把事件写进观测缓冲区的监听器，供 Task 4a 的诊断工具使用。
    ///   输入：buffer 观测事件的去处，不得为 null。
    ///   输出：监听器。
    ///
    ///   与钩子那边的 <see cref="LowLevelKeyboardHook.CreateObserveOnly"/> 同理：
    ///   4a 的工具要在真实工作机上跑，它"只观测"这件事应当由结构保证。这里的
    ///   lambda 只写缓冲区、不接受任何外部输入，调用方无从让它做别的事。
    ///
    ///   （Raw Input 通道本来就吞不掉任何东西，所以这个工厂的意义不在安全，
    ///   而在于让两条通道的构造方式对称、读起来是同一个故事。）
    /// English:
    ///   Creates a listener that only records into an observation buffer, for Task 4a's
    ///   diagnostic tool.
    ///
    ///   The same idea as <see cref="LowLevelKeyboardHook.CreateObserveOnly"/> on the hook
    ///   side: 4a's tool runs on live machines and "observes only" should be structural. The
    ///   lambda here writes to the buffer and takes no outside input, leaving a caller no way
    ///   to make it do anything else.
    ///
    ///   (The Raw Input channel cannot swallow anything in any case, so this factory's value
    ///   is not safety but symmetry — the two channels then read as the same story.)
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：buffer 为 null。 English: buffer is null.
    /// </exception>
    public static RawInputKeyboardListener CreateObserveOnly(ObservationBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return new RawInputKeyboardListener((in ObservedInputEvent observedEvent) =>
            buffer.Write(observedEvent));
    }

    /// <summary>
    /// 中文：是否已向 Windows 注册。
    /// English: Whether registration has been performed.
    /// </summary>
    public bool IsRegistered { get; private set; }

    /// <summary>
    /// 中文：
    ///   注册接收键盘原始输入。
    ///   输入：windowHandle 接收 WM_INPUT 的窗口句柄，不得为 0。
    ///   输出：无。
    ///
    ///   调用方随后必须在该窗口的窗口过程里处理 <c>WM_INPUT</c>，把消息的
    ///   lParam 交给 <see cref="HandleRawInput"/>。
    /// English:
    ///   Registers for keyboard raw input on the given window, which then must handle
    ///   WM_INPUT in its window procedure and pass the message's lParam to
    ///   <see cref="HandleRawInput"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 中文：窗口句柄为 0。 English: The window handle is zero.
    /// </exception>
    /// <exception cref="Win32Exception">
    /// 中文：注册失败。 English: Registration failed.
    /// </exception>
    public void Register(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException(
                "使用 RIDEV_INPUTSINK 时必须提供一个有效的窗口句柄。"
                + " A valid window handle is required when using RIDEV_INPUTSINK.",
                nameof(windowHandle));
        }

        var registration = new RawInputNative.RAWINPUTDEVICE
        {
            usUsagePage = RawInputNative.HID_USAGE_PAGE_GENERIC,
            usUsage = RawInputNative.HID_USAGE_GENERIC_KEYBOARD,
            dwFlags = RawInputNative.RIDEV_INPUTSINK,
            hwndTarget = windowHandle,
        };

        var registered = RawInputNative.RegisterRawInputDevices(
            [registration], 1, (uint)Marshal.SizeOf<RawInputNative.RAWINPUTDEVICE>());

        if (!registered)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "注册键盘原始输入失败。 Failed to register for keyboard raw input.");
        }

        IsRegistered = true;
    }

    /// <summary>
    /// 中文：
    ///   处理一条 WM_INPUT。
    ///   输入：rawInputHandle 消息的 lParam；timestamp 调用方在窗口过程**最开头**
    ///         取得的 Stopwatch.GetTimestamp 读数。
    ///   输出：成功交出一个键盘事件返回 true；不是键盘输入或读取失败返回 false。
    ///   步骤：
    ///     1. 取出原始输入负载；失败则放弃这一条；
    ///     2. 不是键盘类型就忽略；
    ///     3. 过滤掉设备用来占位的伪键（扫描码或虚拟键为 0xFF）；
    ///     4. 交给 handler。
    ///
    ///   ★ 时间戳由**调用方**传入，而不是在本方法里取。
    ///     它必须是窗口过程处理这条消息时做的第一件事——在本方法里取的话，
    ///     P/Invoke 的开销就被算进了两条通道的时差里，而那个时差正是 4a
    ///     要测量的对象，本身可能只有几十微秒。
    ///
    ///   步骤 3 过滤的是某些键盘（尤其是笔记本内置键盘和部分扫码枪）会发出的
    ///   占位事件：扫描码 0xFF 表示"按键溢出"，虚拟键 0xFF 表示无对应按键。
    ///   它们在钩子通道里不出现，若不滤掉，两条通道的事件数量对不上，配对就会
    ///   整体错位——而错位之后测出来的时差是纯粹的噪声，却看起来像真数据。
    ///
    /// English:
    ///   Handles one WM_INPUT. timestamp is the caller's Stopwatch.GetTimestamp
    ///   reading, taken at the very top of the window procedure. Returns true when a
    ///   keyboard event was handed on.
    ///   Steps: (1) read the payload, abandoning the message on failure; (2) ignore
    ///   non-keyboard input; (3) filter out placeholder keys (scan code or virtual key
    ///   0xFF); (4) hand it to the handler.
    ///
    ///   The timestamp comes from the caller rather than being taken here. It has to be
    ///   the first thing the window procedure does: taken inside this method, the
    ///   P/Invoke overhead would be folded into the inter-channel delta that 4a exists
    ///   to measure and that may itself be only tens of microseconds.
    ///
    ///   Step 3 filters the placeholder events some keyboards emit — laptop internal
    ///   keyboards and certain scanners especially. Scan code 0xFF means "key
    ///   overrun" and virtual key 0xFF means no corresponding key. They do not appear
    ///   on the hook channel, and leaving them in makes the two channels' event counts
    ///   disagree, which shifts the whole pairing out of step. A delta measured from
    ///   mis-paired events is pure noise that looks exactly like real data.
    /// </summary>
    public bool HandleRawInput(IntPtr rawInputHandle, long timestamp)
    {
        // 步骤 1 / Step 1
        var payloadSize = (uint)_payloadBufferSize;
        var bytesWritten = RawInputNative.GetRawInputData(
            rawInputHandle,
            RawInputNative.RID_INPUT,
            _payloadBuffer,
            ref payloadSize,
            (uint)Marshal.SizeOf<RawInputNative.RAWINPUTHEADER>());

        if (bytesWritten == unchecked((uint)-1) || bytesWritten == 0)
        {
            return false;
        }

        var rawInput = Marshal.PtrToStructure<RawInputNative.RAWINPUTKEYBOARD>(_payloadBuffer);

        // 步骤 2 / Step 2
        if (rawInput.header.dwType != RawInputNative.RIM_TYPEKEYBOARD)
        {
            return false;
        }

        // 步骤 3 / Step 3
        if (rawInput.keyboard.MakeCode == 0xFF || rawInput.keyboard.VKey == 0xFF)
        {
            return false;
        }

        // 步骤 4 / Step 4
        _handler(new ObservedInputEvent(
            Channel: InputChannel.RawInput,
            Timestamp: timestamp,
            VirtualKey: rawInput.keyboard.VKey,
            ScanCode: rawInput.keyboard.MakeCode,
            IsExtended: (rawInput.keyboard.Flags & RawInputNative.RI_KEY_E0) != 0,
            IsKeyUp: (rawInput.keyboard.Flags & RawInputNative.RI_KEY_BREAK) != 0,

            // ★ 这里恒为 false，但**不是**因为合成事件不经过 Raw Input——那句话曾经
            //   写在这里、标着「值得在 4a 确认」，而 2026-09-10 的实测证明它是错的：
            //   我们自己 SendInput 补发的 486 个按键，产生了 485 条 Raw Input。
            //
            //   恒为 false 的真正原因是这条通道**没有**任何标志位能表达"合成"。
            //   要分辨合成事件只能看设备句柄：真实硬件有句柄，合成的是 0。
            //   任何既注入按键又监听 Raw Input 的代码都必须显式滤掉句柄为 0 的事件，
            //   否则自己的输出会被当成外部输入重新吃进来（见 TASK_4B_FINDING_20260910.md）。
            //
            // Always false here, but not because synthesized events bypass Raw Input — that
            // sentence used to sit here marked "worth confirming in 4a", and the 2026-09-10
            // measurement proved it wrong: 486 replayed keystrokes produced 485 Raw Input events.
            //
            // It is false because this channel carries no flag capable of expressing "synthesized"
            // at all. The only way to tell is the device handle: real hardware has one, synthesized
            // input has zero. Any code that both injects keystrokes and listens to Raw Input must
            // filter zero-handle events explicitly, or it eats its own output as external input
            // (see TASK_4B_FINDING_20260910.md).
            IsInjected: false,

            DeviceHandle: rawInput.header.hDevice));

        return true;
    }


    /// <summary>
    /// 中文：
    ///   把队列里**已经到达**的 WM_INPUT 全部取出来处理掉，队列空则立刻返回。
    ///   输入：windowHandle 本监听器注册时用的窗口句柄。
    ///   输出：处理掉的条数。
    ///
    ///   ★ 这个方法专门给钩子回调用，解决一个先后顺序问题。
    ///
    ///     钩子回调必须当场决定这一下按键是吞还是放，而这个决定依赖 Raw Input
    ///     揭示的来源。可 Raw Input 是**投递**到消息队列里的，取它要靠消息循环
    ///     ——而钩子回调恰恰打断了消息循环。需要答案的那一刻，答案正躺在队列里
    ///     没人去取；等消息循环重新跑起来，50 毫秒的关联窗口可能已经过了，
    ///     那个按键就被判成「来源不明」按普通键盘重放出去（决策 D-13）。
    ///
    ///     扫码枪一枪几十个字符、字符间隔约 1.1 毫秒（Task 4a 实测），这条路径
    ///     上积压几条消息是常态。积压一旦顶穿关联窗口，漏出去的就是条码的原始
    ///     字符——正是规格禁止的那件事。
    ///
    ///   ★ 这不是时序上的经验值。它不猜任何东西，只是把我们自己的调度造成的
    ///     延迟去掉，让关联窗口去衡量真实的通道时差。
    ///
    ///   只取 WM_INPUT，不碰别的消息：其它消息该由消息循环按自己的顺序处理。
    ///   也不调 DispatchMessage —— 消息在这里就地处理完了，因此按 Raw Input 的
    ///   约定补一次 DefWindowProc，让系统做它的清理。
    /// English:
    ///   Consumes every WM_INPUT already sitting in the queue, returning immediately when there
    ///   is none. windowHandle is the one this listener registered with; the return value is how
    ///   many were handled.
    ///
    ///   It exists for the hook callback, to fix an ordering problem. The callback must decide on
    ///   the spot whether to swallow, and that decision depends on the source Raw Input reveals —
    ///   but Raw Input is *posted*, retrieved by the message loop, and the hook callback is
    ///   precisely what interrupts that loop. At the moment the answer is needed it sits in the
    ///   queue untouched, and by the time the loop runs again the 50 ms correlation window may
    ///   have passed, leaving the keystroke settled as "source unknown" and replayed as ordinary
    ///   typing (decision D-13). A scan is dozens of characters about 1.1 ms apart (Task 4a), so
    ///   a backlog here is routine — and a backlog that outlives the correlation window leaks the
    ///   barcode's raw characters, which is exactly what the spec forbids.
    ///
    ///   This is not a timing heuristic: it guesses nothing and merely removes a delay of our own
    ///   scheduler's making, so the correlation window measures the real inter-channel delta.
    ///
    ///   Only WM_INPUT is taken; other messages stay for the loop to handle in its own order.
    ///   DispatchMessage is not called since the message is handled here, so Raw Input's
    ///   convention is honored with a DefWindowProc call to let the system clean up.
    /// </summary>
    public int DrainQueued(IntPtr windowHandle)
    {
        var handled = 0;

        while (WindowNative.PeekMessageW(
                   out var message,
                   windowHandle,
                   NativeMethods.WM_INPUT,
                   NativeMethods.WM_INPUT,
                   WindowNative.PM_REMOVE))
        {
            var timestamp = Stopwatch.GetTimestamp();
            HandleRawInput(message.lParam, timestamp);
            WindowNative.DefWindowProcW(
                message.hwnd, message.message, message.wParam, message.lParam);
            handled++;
        }

        return handled;
    }

    /// <summary>
    /// 中文：释放常驻的原生缓冲区。
    /// English: Releases the long-lived native buffer.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_payloadBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_payloadBuffer);
            _payloadBuffer = IntPtr.Zero;
            _payloadBufferSize = 0;
        }
    }
}
