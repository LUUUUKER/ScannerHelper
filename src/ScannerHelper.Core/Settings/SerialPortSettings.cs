// =============================================================================
// SerialPortSettings.cs
//
// 中文：
//   扫码枪串口的连接参数（决策 D-27）。
//
//   ★ 为什么 Core 自己定义 SerialParity / SerialStopBits，而不用 System.IO.Ports 的。
//
//     Core 目标框架是 net8.0、不引用任何东西，必须能在 macOS 上构建与单元测试
//     （规格 §16）。System.IO.Ports 是一个包，且它代表的是**某个平台上的串口
//     实现**；让配置模型依赖它，等于让「波特率是多少」这件业务配置绑在一个
//     具体的实现上。Win32 那一侧负责把这两个枚举翻译过去，翻译是它的工作。
//
//     这与整个项目的依赖方向一致：Core 定义契约，外层实现它，依赖向内。
//
//   ★ 终止符**刻意不在这里**（决策 D-27）。
//
//     读取器一律同时接受 CR、LF、CRLF。「同时接受三种」在任何一把枪上都正确，
//     而「让人从三个选项里挑一个」只是多出一个可以填错的地方——填错的表现是
//     一枪都收不到，而那看起来和「串口方案不行」一模一样。诊断页会显示实际
//     收到的是哪一种，那是信息，不是开关。
//
//   ★ 端口号会变，所以它不是身份。
//
//     换一个 USB 口，COM3 就可能变成 COM7。因此重连要靠 ScannerBindingSettings
//     里记下的设备身份（VID/PID/序列号）去找端口，而不是记住端口号
//     （见 ARCHITECTURE_CHANGE_SERIAL.md §5.2）。这里的 PortName 只是
//     「上次用的那个」，是一条线索而不是依据。
//
// English:
//   Connection parameters for the scanner's COM port (decision D-27).
//
//   Core defines its own SerialParity/SerialStopBits rather than using System.IO.Ports because
//   Core targets net8.0, references nothing, and must build and unit-test on macOS (spec §16).
//   System.IO.Ports is a package representing one platform's serial implementation, and binding a
//   configuration model to it would tie "what baud rate" — a business setting — to a particular
//   implementation. Translating the two enums is the Win32 layer's job. Dependencies point inward.
//
//   The terminator is deliberately absent (decision D-27): the reader always accepts CR, LF and
//   CRLF. Accepting all three is correct on every scanner, whereas offering a choice of three only
//   adds somewhere to be wrong — and being wrong there presents as receiving nothing at all, which
//   looks exactly like "the serial approach does not work". The diagnostics page reports which one
//   actually arrives; that is information, not a switch.
//
//   PortName is not an identity. Moving to another USB socket can turn COM3 into COM7, so
//   reconnection resolves the port from the device identity recorded in ScannerBindingSettings
//   (see ARCHITECTURE_CHANGE_SERIAL.md §5.2). The name here is only "the one used last time" — a
//   hint rather than a basis.
//
// 包含的类型 / Types in this file:
//   SerialParity
//   SerialStopBits
//   SerialPortSettings
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：串口校验位。
/// English: The serial parity setting.
/// </summary>
public enum SerialParity
{
    /// <summary>中文：无校验。扫码枪出厂几乎都是这个。 English: None, the usual factory default.</summary>
    None,

    /// <summary>中文：奇校验。 English: Odd.</summary>
    Odd,

    /// <summary>中文：偶校验。 English: Even.</summary>
    Even,

    /// <summary>中文：标记位。 English: Mark.</summary>
    Mark,

    /// <summary>中文：空格位。 English: Space.</summary>
    Space,
}

/// <summary>
/// 中文：串口停止位。
/// English: The serial stop-bit setting.
/// </summary>
public enum SerialStopBits
{
    /// <summary>中文：1 位。扫码枪出厂几乎都是这个。 English: One, the usual factory default.</summary>
    One,

    /// <summary>中文：1.5 位。 English: One and a half.</summary>
    OnePointFive,

    /// <summary>中文：2 位。 English: Two.</summary>
    Two,
}

/// <summary>
/// 中文：扫码枪串口的连接参数。默认值取自 2026-09-10 的实测（9600 / 8 / 无 / 1）。
/// English: The scanner port's connection parameters, defaulting to what was measured on
///          2026-09-10 (9600 / 8 / none / 1).
/// </summary>
public sealed class SerialPortSettings
{
    /// <summary>
    /// 中文：上次使用的端口名，例如 COM3。null 表示还没绑定过。
    ///       它**不是身份**——理由见文件头。
    /// English: The port used last time, such as COM3, or null if never bound. It is not an
    ///          identity; see the file header.
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// 中文：
    ///   波特率。默认 9600。
    ///
    ///   ★ 填错**不会报任何错**，只会收到一串看起来像随机字节的东西。这是串口
    ///     最经典的坑，也是诊断页必须把原始字节显示出来的原因：不摆出来，人会
    ///     以为是扫码枪坏了。
    /// English:
    ///   The baud rate, defaulting to 9600.
    ///
    ///   A wrong value raises no error and simply yields what look like random bytes — the classic
    ///   serial pitfall, and why the diagnostics page must show the raw bytes. Without them a
    ///   person concludes the scanner is broken.
    /// </summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>
    /// 中文：数据位，默认 8。
    /// English: Data bits, defaulting to 8.
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// 中文：校验位，默认无。
    /// English: Parity, defaulting to none.
    /// </summary>
    public SerialParity Parity { get; set; } = SerialParity.None;

    /// <summary>
    /// 中文：停止位，默认 1。
    /// English: Stop bits, defaulting to one.
    /// </summary>
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

    /// <summary>
    /// 中文：
    ///   多久收不到任何数据就认为「这条链路可疑」，默认 10 分钟。
    ///
    ///   ★ 这一条是为了检测一种新的静默失效：**有人扫一张配置码把枪切回了键盘
    ///     模式**。那之后串口好好地开着、不报任何错，只是永远没有数据；而扫码枪
    ///     开始直接往业务软件里打字，原始条码原封不动地进去了
    ///     （ARCHITECTURE_CHANGE_SERIAL.md §5.1）。
    ///
    ///     这正是规格 §19.1 说的那类失效——程序还在跑、界面还显示着模式，而它
    ///     要防的事正在发生。所以超过这个时长之后，界面**不得**再显示一个笃定的
    ///     「正常」状态。
    ///
    ///     十分钟是有意取得比较宽的：仓库里工人去搬货、开会、吃饭都很正常，
    ///     报得太勤就会变成没人看的噪声，而一个被无视的警告等于没有警告。
    /// English:
    ///   How long without any data before the link is considered suspect; ten minutes by default.
    ///
    ///   This detects a new silent failure: someone scans a configuration barcode and switches the
    ///   scanner back to keyboard mode. The port then stays open and raises nothing while never
    ///   receiving data, and the scanner types raw barcodes straight into the business application
    ///   (ARCHITECTURE_CHANGE_SERIAL.md §5.1) — spec §19.1's failure exactly: the program running
    ///   and the UI showing a mode while the thing it exists to prevent is happening. Past this
    ///   interval the UI must not present a confident "normal" state.
    ///
    ///   Ten minutes is deliberately generous: workers fetch goods, attend meetings and eat lunch,
    ///   and an alarm that cries too often becomes noise nobody reads — and an ignored warning is
    ///   no warning at all.
    /// </summary>
    public int SilenceWarningMinutes { get; set; } = 10;
}
