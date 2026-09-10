// =============================================================================
// SerialPortResolver.cs
//
// 中文：
//   把串口号和它背后的 USB 设备身份对上。
//
//   ★ 为什么非要这一层：**端口号不是身份**。
//
//     同一把扫码枪，插在笔记本左边的 USB 口是 COM3，插右边可能就是 COM7；
//     睡眠唤醒、重启、USB 集线器重新枚举，都可能让它变。而工人不会知道这件事
//     ——他只知道"昨天还好好的，今天扫码没反应了"。
//
//     所以绑定要记的是设备身份（VID/PID、序列号、设备实例路径），重连时拿身份
//     去**找**当前的端口号。这与规格 §6「优先用具体身份而不是 VID/PID」是同一个
//     道理，只是落到了串口这一层（ARCHITECTURE_CHANGE_SERIAL.md §5.2）。
//
//   ★ 为什么走注册表，而不是 WMI 或 SetupAPI。
//
//     WMI 要引入 System.Management 包，而仓库 IT 按哈希把可执行文件加进白名单
//     （规格 §22.1）——依赖越少，发布件越容易解释。SetupAPI 要写一大段 P/Invoke
//     和结构体编组，而这里需要的信息（VID/PID、序列号、端口号、友好名）恰好全部
//     以纯文本的形式躺在注册表里，读起来直白得多。
//
//     Windows 把每个设备实例记在
//       HKLM\SYSTEM\CurrentControlSet\Enum\<枚举器>\<设备>\<实例>
//     串口设备在它的 Device Parameters 子键下有一个 PortName，值就是 COM3 这种。
//
//   ★ 只走可能承载串口的那几个枚举器，不整树遍历。
//
//     整棵 Enum 树有几千个键，全走一遍要上百毫秒，而重连是会反复尝试的。
//     USB / FTDIBUS / USBSER 覆盖了所有 USB 转串口的情形，ACPI 与 PCI 覆盖
//     主板自带的老式串口，BTHENUM 覆盖蓝牙串口。
//
//     ★ 名单写死是有代价的：某种没列进来的枚举器下的串口会看不见。所以名单
//       宁可宽一点——多走几个空目录只是几毫秒，而漏掉一种设备是"这台机器上
//       就是用不了"，且现场根本无从判断原因。
//
// English:
//   Maps a COM port number to the USB device identity behind it.
//
//   This layer exists because a port number is not an identity: the same scanner is COM3 in the
//   laptop's left socket and may be COM7 in the right, and sleep, reboot or a hub re-enumerating
//   can change it. The operator knows none of this — only that "it worked yesterday and scanning
//   does nothing today". So a binding records the device identity (VID/PID, serial, device instance
//   path) and reconnection looks the current port up from it. Same reasoning as spec §6's
//   preference for specific identity over VID/PID, one layer down
//   (ARCHITECTURE_CHANGE_SERIAL.md §5.2).
//
//   The registry rather than WMI or SetupAPI: WMI would add the System.Management package, and
//   warehouse IT allow-lists the executable by hash (spec §22.1), so fewer dependencies make the
//   artifact easier to explain. SetupAPI would need a page of P/Invoke and struct marshalling,
//   while everything needed here — VID/PID, serial, port name, friendly name — already sits in the
//   registry as plain text under
//   HKLM\SYSTEM\CurrentControlSet\Enum\<enumerator>\<device>\<instance>, with a serial device's
//   Device Parameters subkey holding a PortName such as COM3.
//
//   Only enumerators that can host a COM port are walked rather than the whole tree: Enum holds
//   thousands of keys and a full walk costs hundreds of milliseconds, while reconnection retries
//   repeatedly. USB, FTDIBUS and USBSER cover every USB-to-serial case, ACPI and PCI cover
//   motherboard legacy ports, BTHENUM covers Bluetooth serial. A fixed list has a cost — a port
//   under some enumerator not listed becomes invisible — so the list errs wide: a few extra empty
//   directories cost milliseconds, whereas missing a device kind means "it just does not work on
//   this machine", with nothing on site to explain why.
//
// 包含的类型 / Types in this file:
//   SerialPortResolver
// =============================================================================

using Microsoft.Win32;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Win32.Serial;

/// <summary>
/// 中文：串口号与设备身份的对应关系。
/// English: The correspondence between a COM port number and a device identity.
/// </summary>
public static class SerialPortResolver
{
    /// <summary>
    /// 中文：设备实例的注册表根。
    /// English: The registry root holding device instances.
    /// </summary>
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>
    /// 中文：可能承载串口的枚举器。宁可宽一点，理由见文件头。
    /// English: Enumerators that can host a COM port; deliberately wide, see the file header.
    /// </summary>
    private static readonly string[] Enumerators =
        ["USB", "FTDIBUS", "USBSER", "ACPI", "PCI", "BTHENUM"];

    /// <summary>
    /// 中文：
    ///   列出本机所有串口，连同它们背后的设备身份。
    ///   输出：端口名与身份的配对；读不到的设备直接跳过。
    ///
    ///   ★ 读不到就跳过，不抛异常。注册表里总有一些键因为权限或时机读不到
    ///     （设备正在插拔的那一瞬间尤其如此），而"少列出一个设备"远好过
    ///     "整个枚举失败、程序连不上任何端口"。
    /// English:
    ///   Lists every COM port on this machine with the device identity behind it, skipping what
    ///   cannot be read rather than throwing. Some keys are always unreadable for reasons of
    ///   permission or timing — especially in the instant a device is being plugged or unplugged —
    ///   and listing one device fewer is far better than the whole enumeration failing and the
    ///   program connecting to nothing.
    /// </summary>
    public static IReadOnlyList<(string PortName, ScannerDeviceIdentity Identity)> Enumerate()
    {
        var found = new List<(string, ScannerDeviceIdentity)>();

        using var enumRoot = OpenSubKey(Registry.LocalMachine, EnumRoot);
        if (enumRoot is null)
        {
            return found;
        }

        foreach (var enumerator in Enumerators)
        {
            using var enumeratorKey = OpenSubKey(enumRoot, enumerator);
            if (enumeratorKey is null)
            {
                continue;
            }

            foreach (var deviceName in GetSubKeyNames(enumeratorKey))
            {
                using var deviceKey = OpenSubKey(enumeratorKey, deviceName);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (var instanceName in GetSubKeyNames(deviceKey))
                {
                    using var instanceKey = OpenSubKey(deviceKey, instanceName);

                    if (instanceKey is null || ReadPortName(instanceKey) is not { } portName)
                    {
                        continue;
                    }

                    found.Add((portName, BuildIdentity(
                        enumerator, deviceName, instanceName, instanceKey)));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 中文：
    ///   按绑定的身份找出它现在在哪个端口。
    ///   输入：binding 记下来的设备身份。
    ///   输出：端口名与匹配结果；没找到时端口名为 null。
    ///
    ///   ★ 匹配用的是 Core 的 DeviceIdentityMatcher（用例 DM1~DM15）——它是
    ///     Phase A 写好并测过的纯逻辑，这次架构变更之后依然成立：要判断的还是
    ///     "记下来的那台设备和眼前这些是不是同一台"，只是"眼前这些"从 Raw Input
    ///     设备换成了串口设备。
    ///
    ///   ★ 匹配结果里的 CanReconnectAutomatically 才是能不能自动重连的依据。
    ///     只靠 VID/PID 匹配上是**低置信度**：同型号的第二把枪 VID/PID 完全一样，
    ///     自动连上去的话，工人扫的是 A 枪而程序在听 B 枪——两把枪都在同一张桌子
    ///     上时，这种事一点都不罕见。
    /// English:
    ///   Finds which port the bound device currently occupies, returning a null port name when it
    ///   is not present.
    ///
    ///   Matching uses Core's DeviceIdentityMatcher (cases DM1–DM15), the pure logic written and
    ///   tested in Phase A, which survives the architecture change intact: the question is still
    ///   whether the recorded device is one of the devices present, with "present" now meaning
    ///   serial rather than Raw Input.
    ///
    ///   CanReconnectAutomatically on the result is what decides automatic reconnection. A VID/PID
    ///   match alone is low confidence: a second scanner of the same model has identical VID and
    ///   PID, and connecting automatically would leave the operator scanning gun A while the
    ///   program listens to gun B — not at all rare when both sit on one desk.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：binding 为 null。 English: binding is null.
    /// </exception>
    public static (string? PortName, DeviceMatchResult Match) FindBoundPort(
        ScannerBindingSettings binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var ports = Enumerate();

        if (ports.Count == 0)
        {
            return (null, DeviceMatchResult.NoMatch);
        }

        var candidates = new ScannerDeviceIdentity[ports.Count];
        for (var index = 0; index < ports.Count; index++)
        {
            candidates[index] = ports[index].Identity;
        }

        var match = DeviceIdentityMatcher.FindBestMatch(candidates, binding);

        if (match.Device is not { } matched)
        {
            return (null, match);
        }

        foreach (var (portName, identity) in ports)
        {
            if (identity == matched)
            {
                return (portName, match);
            }
        }

        return (null, match);
    }

    /// <summary>
    /// 中文：
    ///   按端口名取出它的设备身份，供**绑定的那一刻**记录下来。
    ///   输出：找到返回身份；这个端口不是我们能识别的设备则返回 null。
    /// English:
    ///   Reads the identity behind a port name, for recording at the moment of binding. Returns
    ///   null when the port is not a device we can identify.
    /// </summary>
    public static ScannerDeviceIdentity? IdentifyPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        foreach (var (candidatePort, identity) in Enumerate())
        {
            if (string.Equals(candidatePort, portName, StringComparison.OrdinalIgnoreCase))
            {
                return identity;
            }
        }

        return null;
    }

    /// <summary>
    /// 中文：
    ///   把一个设备实例拼成 ScannerDeviceIdentity。
    ///
    ///   DevicePath 用设备实例路径（形如 <c>USB\VID_0581&amp;PID_0115\5&amp;1a2b3c&amp;0&amp;2</c>）。
    ///   它是这台机器上这个设备的**唯一**标识，也是匹配器眼里置信度最高的那一项。
    ///
    ///   ★ 序列号取实例名，但只在它**看起来像序列号**时才用。
    ///     实例名有两种：设备自己报的序列号（一串字母数字），或者 Windows 按
    ///     插在哪个口生成的位置串（以 & 开头、含 &amp; 号）。后者换个 USB 口就变，
    ///     当序列号用等于把"换个口"误判成"换了一把枪"——那正是这个类要解决的
    ///     问题本身。
    /// English:
    ///   Assembles one device instance into a ScannerDeviceIdentity.
    ///
    ///   DevicePath is the device instance path (like USB\VID_0581&amp;PID_0115\5&amp;1a2b3c&amp;0&amp;2), the
    ///   unique identifier for this device on this machine and the matcher's highest-confidence
    ///   field.
    ///
    ///   The serial number comes from the instance name, but only when it looks like one. Instance
    ///   names come in two kinds: a serial the device reports (alphanumeric), or a location string
    ///   Windows generates from which socket it is in (contains &amp;). The latter changes with the
    ///   socket, and treating it as a serial would read "moved to another port" as "a different
    ///   scanner" — the very problem this class exists to solve.
    /// </summary>
    private static ScannerDeviceIdentity BuildIdentity(
        string enumerator, string deviceName, string instanceName, RegistryKey instanceKey)
        => new(
            DevicePath: $@"{enumerator}\{deviceName}\{instanceName}",
            VendorId: ExtractId(deviceName, "VID_"),
            ProductId: ExtractId(deviceName, "PID_"),
            FriendlyName: ReadString(instanceKey, "FriendlyName")
                ?? ReadString(instanceKey, "DeviceDesc"),
            SerialNumber: LooksLikeSerialNumber(instanceName) ? instanceName : null);

    /// <summary>
    /// 中文：
    ///   实例名看起来像序列号吗。
    ///   含 &amp; 的是 Windows 按插槽位置生成的，不是序列号。
    /// English:
    ///   Whether an instance name looks like a serial number. Names containing &amp; are generated by
    ///   Windows from the socket position and are not serials.
    /// </summary>
    private static bool LooksLikeSerialNumber(string instanceName)
        => instanceName.Length > 0 && !instanceName.Contains('&', StringComparison.Ordinal);

    /// <summary>
    /// 中文：从 <c>VID_0581&amp;PID_0115</c> 这样的键名里取出四位十六进制。
    /// English: Extracts the four hex digits from a key name like VID_0581&amp;PID_0115.
    /// </summary>
    private static string? ExtractId(string deviceName, string marker)
    {
        var start = deviceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        start += marker.Length;

        return deviceName.Length - start >= 4
            ? deviceName.Substring(start, 4).ToUpperInvariant()
            : null;
    }

    /// <summary>
    /// 中文：读出这个设备实例的串口号；不是串口设备就返回 null。
    /// English: Reads the instance's COM port name, or null when it is not a serial device.
    /// </summary>
    private static string? ReadPortName(RegistryKey instanceKey)
    {
        using var parameters = OpenSubKey(instanceKey, "Device Parameters");
        return parameters is null ? null : ReadString(parameters, "PortName");
    }

    private static string? ReadString(RegistryKey key, string valueName)
    {
        try
        {
            return key.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 中文：打开子键，失败一律当作"没有"。见 <see cref="Enumerate"/> 的说明。
    /// English: Opens a subkey, treating any failure as absence; see <see cref="Enumerate"/>.
    /// </summary>
    private static RegistryKey? OpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] GetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
