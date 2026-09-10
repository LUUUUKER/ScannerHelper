// =============================================================================
// RawInputNative.cs
//
// 中文：
//   Raw Input 的原生声明（规格 §4.1、§6）。
//
//   这条通道的性质，与低层钩子恰好互补：
//     - 它**知道是哪台物理设备**产生了这次输入（RAWINPUTHEADER.hDevice）；
//     - 但它是**投递**给窗口的消息，你排队取到时按键早已在去焦点程序的路上，
//       没有任何办法拦下来。
//
//   扫码枪绑定就建立在 hDevice 之上（规格 §6）：扫码枪一个句柄，键盘另一个
//   句柄，二者可区分。
//
//   ★ 必须用 RIDEV_INPUTSINK 注册。
//
//     默认情况下只有窗口处于前台时才收得到原始输入。而本程序的整个工作前提
//     就是**焦点在业务软件那边**——工人盯着的是仓库网页，不是我们。不加
//     这个标志，程序在真实使用场景下会一个事件都收不到，而在开发者自己点着
//     窗口测试时一切正常。这是那种"测试时好好的、上线就没反应"的典型陷阱。
//
// English:
//   Native declarations for Raw Input (spec §4.1, §6).
//
//   This channel is the exact complement of the low-level hook: it knows which
//   physical device produced the input (RAWINPUTHEADER.hDevice), but it arrives as a
//   *posted* window message, so by the time it is dequeued the keystroke is already
//   on its way to the focused application and cannot be stopped.
//
//   Scanner binding rests on hDevice (spec §6): the scanner has one handle, the
//   keyboard another, and the two are distinguishable.
//
//   Registration must use RIDEV_INPUTSINK. By default raw input reaches a window only
//   while it is in the foreground — yet this application's entire premise is that
//   focus belongs to the business application; the operator is looking at the
//   warehouse page, not at us. Without the flag the program receives nothing at all in
//   real use, while working perfectly when a developer clicks on its own window
//   during testing. A textbook "fine in testing, silent in production" trap.
//
// 包含的类型 / Types in this file:
//   RawInputNative              常量与入口点
//   RawInputNative.RAWINPUTDEVICE      注册用的设备描述
//   RawInputNative.RAWINPUTHEADER      每条原始输入的头部，含设备句柄
//   RawInputNative.RAWKEYBOARD         键盘负载
//   RawInputNative.RAWINPUTKEYBOARD    头部加键盘负载
//   RawInputNative.RAWINPUTDEVICELIST  枚举设备时的条目
// =============================================================================

using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：Raw Input 的原生声明。
/// English: Native declarations for Raw Input.
/// </summary>
internal static class RawInputNative
{
    /// <summary>
    /// 中文：HID 用途页与用途。0x01/0x06 即"通用桌面页 / 键盘"。
    ///       扫码枪工作在 HID 键盘模式，因此它也落在这一类里——这正是我们
    ///       必须靠设备句柄而不是设备类别来区分它和真键盘的原因。
    /// English: HID usage page and usage: 0x01/0x06 is "generic desktop / keyboard".
    ///          A scanner in HID keyboard mode falls into this same class, which is
    ///          exactly why it must be told apart from a real keyboard by device
    ///          handle rather than by device class.
    /// </summary>
    internal const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    internal const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    /// <summary>
    /// 中文：即使本窗口不在前台也接收原始输入。见文件头说明——这个标志是必需的，
    ///       不是优化。
    /// English: Receive raw input even when this window is not in the foreground. As
    ///          the header explains, this flag is required rather than an optimization.
    /// </summary>
    internal const uint RIDEV_INPUTSINK = 0x00000100;

    /// <summary>中文：从 WM_INPUT 的句柄中取原始输入数据。 English: Read the raw input payload.</summary>
    internal const uint RID_INPUT = 0x10000003;

    /// <summary>
    /// 中文：取设备名。返回的是设备接口路径，形如
    ///       <c>\\?\HID#VID_05E0&amp;PID_1200#7&amp;...#{guid}</c>（USB 扫码枪）
    ///       或 <c>\\?\ACPI#PNP0303#4&amp;...</c>（笔记本内置键盘，见规格假设 A4）。
    ///       这是最具体、也最可靠的身份来源。
    /// English: The device name — an interface path such as
    ///          <c>\\?\HID#VID_05E0&amp;PID_1200#7&amp;...#{guid}</c> for a USB scanner,
    ///          or <c>\\?\ACPI#PNP0303#4&amp;...</c> for a laptop's built-in keyboard
    ///          (spec assumption A4). The most specific and most reliable identity.
    /// </summary>
    internal const uint RIDI_DEVICENAME = 0x20000007;

    /// <summary>中文：设备类型。 English: Device types.</summary>
    internal const uint RIM_TYPEKEYBOARD = 1;

    /// <summary>
    /// 中文：RAWKEYBOARD.Flags 的位。
    ///       RI_KEY_BREAK 表示弹起（没有这个位就是按下）。
    ///       RI_KEY_E0 表示这是一个扫描码带 E0 前缀的扩展键，对应钩子那边的
    ///       LLKHF_EXTENDED。跨通道配对必须比对它——扫描码本身会重复
    ///       （右 Ctrl 与左 Ctrl 相同），只有加上这一位才能区分。
    /// English: Bits in RAWKEYBOARD.Flags. RI_KEY_BREAK means key up, its absence key
    ///          down. RI_KEY_E0 marks an extended key whose scan code carries an E0
    ///          prefix, corresponding to the hook's LLKHF_EXTENDED. Cross-channel
    ///          pairing must compare it: scan codes repeat — right Ctrl shares left
    ///          Ctrl's — and only this bit separates them.
    /// </summary>
    internal const ushort RI_KEY_BREAK = 0x01;
    internal const ushort RI_KEY_E0 = 0x02;

    /// <summary>
    /// 中文：注册要接收哪一类设备的原始输入。
    /// English: Describes which device class to receive raw input from.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        internal ushort usUsagePage;
        internal ushort usUsage;
        internal uint dwFlags;

        /// <summary>
        /// 中文：接收 WM_INPUT 的窗口。用 RIDEV_INPUTSINK 时**必须**非空。
        /// English: The window receiving WM_INPUT. Must be non-null with
        ///          RIDEV_INPUTSINK.
        /// </summary>
        internal IntPtr hwndTarget;
    }

    /// <summary>
    /// 中文：每条原始输入的头部。
    /// English: The header of every raw input payload.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        internal uint dwType;
        internal uint dwSize;

        /// <summary>
        /// 中文：★ 产生这次输入的物理设备句柄。这是整条通道唯一不可替代的东西，
        ///       也是低层钩子完全没有的东西。扫码枪绑定就靠它（规格 §6）。
        /// English: The physical device that produced this input. The one thing this
        ///          channel offers that the hook does not have at all, and what scanner
        ///          binding rests on (spec §6).
        /// </summary>
        internal IntPtr hDevice;

        internal IntPtr wParam;
    }

    /// <summary>
    /// 中文：键盘类原始输入的负载。
    /// English: The keyboard payload of a raw input event.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWKEYBOARD
    {
        /// <summary>中文：硬件扫描码。 English: The hardware scan code.</summary>
        internal ushort MakeCode;

        /// <summary>中文：标志位，见 RI_KEY_*。 English: Flags; see RI_KEY_*.</summary>
        internal ushort Flags;

        internal ushort Reserved;

        /// <summary>中文：虚拟键码。 English: The virtual key code.</summary>
        internal ushort VKey;

        /// <summary>中文：对应的窗口消息（WM_KEYDOWN 等）。 English: The corresponding window message.</summary>
        internal uint Message;

        internal uint ExtraInformation;
    }

    /// <summary>
    /// 中文：头部加键盘负载。原生的 RAWINPUT 是一个联合体，这里只声明键盘那一支——
    ///       本项目只注册键盘设备，其他分支永远不会到达。
    /// English: Header plus keyboard payload. The native RAWINPUT is a union; only the
    ///          keyboard arm is declared, since this project registers keyboards only
    ///          and no other arm can arrive.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTKEYBOARD
    {
        internal RAWINPUTHEADER header;
        internal RAWKEYBOARD keyboard;
    }

    /// <summary>
    /// 中文：枚举系统中已连接输入设备时的条目。
    /// English: One entry when enumerating the system's connected input devices.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICELIST
    {
        internal IntPtr hDevice;
        internal uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    internal static extern uint GetRawInputDeviceInfoW(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList(
        IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
}
