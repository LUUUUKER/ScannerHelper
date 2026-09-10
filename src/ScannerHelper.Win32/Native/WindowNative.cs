// =============================================================================
// WindowNative.cs
//
// 中文：
//   创建仅消息窗口并跑消息循环所需的原生声明。
//
//   ★ 为什么需要一个专用的仅消息窗口，而不是复用界面窗口。
//
//     第一轮实测暴露了这个问题。`WM_INPUT` 是**投递**到窗口消息队列里的，
//     它的时间戳只能是"消息循环处理到它的时刻"。如果那个线程还兼着刷新
//     界面，消息就得排在界面工作后面——第一轮里扫码枪的段内间隔 p99 到了
//     48 毫秒，而扫码枪根本不可能有这种停顿，那是界面线程每 100 毫秒重建
//     列表控件造成的。
//
//     低层键盘钩子的回调同样由安装线程的消息队列驱动，所以界面一忙，
//     两条通道都被拖慢，而且拖慢的程度不一样——测出来的时差于是既不是
//     硬件的性质，也不是 Windows 的性质，而是"我的界面有多卡"的性质。
//
//     把捕获整个搬到一个专用线程上，那个线程只有一个仅消息窗口、只泵消息、
//     不碰任何界面，测量才量的是真东西。
//
//   ★ 这不只是测量工具的需要，也是产品的设计要求。
//
//     产品同样要在某个线程上泵 `WM_INPUT`。若那个线程兼做界面，身份到手的
//     延迟就会被自己的界面拖大，而 4b 的扣留窗口必须覆盖那个延迟——界面越
//     卡，普通打字被扣留得越久。规格 §17 说 Win32ScannerInputSource "拥有
//     钩子、Raw Input 注册，以及两者之间的管道"，这里就是那条管道的地基。
//
//   仅消息窗口（父窗口为 HWND_MESSAGE）不会出现在任务栏、不会被枚举到、
//   收不到任何广播消息，除了接收发给它的消息之外什么都不做——正合此处所需。
//
// English:
//   Native declarations for creating a message-only window and running a message loop.
//
//   Why a dedicated message-only window rather than reusing the UI window: the first
//   measurement run exposed the problem. WM_INPUT is *posted* to a window's message
//   queue, so its timestamp can only be "when the message loop reached it". If that
//   thread also refreshes a UI, the message queues behind the UI work — in the first
//   run the scanner's within-burst interval reached a p99 of 48 ms, which no scanner
//   can produce; it was the UI thread rebuilding list controls every 100 ms.
//
//   A low-level hook's callback is likewise driven by the installing thread's message
//   queue, so a busy UI slows both channels, by differing amounts. The measured delta
//   then describes neither the hardware nor Windows but how sluggish my UI is.
//
//   Moving capture onto a dedicated thread that owns one message-only window, pumps
//   messages and touches no UI is what makes the measurement measure the real thing.
//
//   This is not only the tool's need but the product's design requirement. The product
//   must pump WM_INPUT on some thread too, and a thread that also drives a UI inflates
//   how long identity takes to arrive — which 4b's withhold window must cover, meaning
//   a laggier UI withholds ordinary typing for longer. Spec §17 has
//   Win32ScannerInputSource own "the hook, the Raw Input registration, and the plumbing
//   between them"; this is that plumbing's foundation.
//
//   A message-only window (parented to HWND_MESSAGE) never appears in the taskbar, is
//   not enumerated, and receives no broadcast messages — it does nothing but receive
//   messages addressed to it, which is exactly what is wanted.
//
// 包含的类型 / Types in this file:
//   WindowNative
//   WindowNative.WindowProcedure
//   WindowNative.WNDCLASSEXW
//   WindowNative.MSG / POINT
// =============================================================================

using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：窗口与消息循环的原生声明。
/// English: Native declarations for windows and message loops.
/// </summary>
internal static class WindowNative
{
    /// <summary>
    /// 中文：仅消息窗口的父窗口句柄。以它为父，窗口不显示、不被枚举、
    ///       不收广播消息，只接收发给自己的消息。
    /// English: The parent handle that makes a window message-only: invisible, not
    ///          enumerated, receiving no broadcasts, only messages addressed to it.
    /// </summary>
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>
    /// 中文：退出消息循环的消息号。
    /// English: The message that ends a message loop.
    /// </summary>
    internal const uint WM_QUIT = 0x0012;

    /// <summary>
    /// 中文：
    ///   窗口过程签名。
    ///   ★ 与钩子回调同理：委托实例必须由托管代码持有。窗口类里存着的是一个
    ///     函数指针，委托一旦被 GC 回收，下一条消息就会跳进已释放的内存。
    /// English:
    ///   The window procedure signature. As with the hook callback, the delegate
    ///   instance must be held by managed code: the window class stores a function
    ///   pointer, and once the delegate is collected the next message jumps into freed
    ///   memory.
    /// </summary>
    internal delegate IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 中文：窗口类。仅消息窗口只需要类名和窗口过程，其余字段留空即可。
    /// English: A window class. A message-only window needs only a name and a window
    ///          procedure; the remaining fields stay empty.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        internal uint cbSize;
        internal uint style;
        internal WindowProcedure lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int x;
        internal int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassExW")]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(
        [MarshalAs(UnmanagedType.LPWStr)] string className, IntPtr instanceHandle);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "CreateWindowExW")]
    internal static extern IntPtr CreateWindowExW(
        uint exStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string className,
        [MarshalAs(UnmanagedType.LPWStr)] string? windowName,
        uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern IntPtr DefWindowProcW(
        IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 中文：从队列取一条消息，队列空时阻塞。返回 0 表示收到 WM_QUIT，
    ///       返回 -1 表示出错。
    /// English: Retrieves one message, blocking while the queue is empty. Zero means
    ///          WM_QUIT was received; -1 indicates an error.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    internal static extern int GetMessageW(
        out MSG message, IntPtr windowHandle, uint filterMin, uint filterMax);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(
        uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
}
