// =============================================================================
// ScanResult.cs
//
// 中文：
//   一次扫描的结局：完成并带回原始码，或失败并带回原因。
//
//   与 ParseResult 同样是**封闭的判别式类型**（决策 D-7）：失败分支上根本
//   没有"扫描内容"这个属性，想在失败分支里读它，代码连编译都通不过。
//
//   ★ 失败也带原始码，但它的用途与 ParseResult 的不同，别混为一谈。
//
//     ParseResult.Failure 的原始码是给 F10 强制发送用的（规格 §10）——
//     解析失败时，工人可以选择把扫到的原码原样发出去。
//
//     ScanResult.Failed 的原始码**不是**给强制发送用的。规格 §5.4 对扫描超时
//     写得很清楚：丢弃这次不完整的扫描、不产生任何业务输入、回到 IDLE、
//     显示一个可见的扫描错误。**没有 F10 这条路**——因为一次超时的扫描
//     手里那半截码本来就不是完整的条码，把它发出去等于主动写入错误数据。
//
//     那它为什么还要带？给**错误界面和诊断日志**用。规格 §10 要求"若存在
//     原始码则展示它"，§15 要求日志记录原始码。工人看到"扫到一半就断了，
//     收到的是 DGKJRD"，才知道是枪没对准还是条码破损；只说"扫描超时"
//     等于什么都没说。
//
//   ★ 完成 ≠ 有效。
//
//     Completed 只表示"一次扫描在物理层面正常结束了"——收到了终止符，
//     没有超时。这段内容能不能用，是解析与校验层的事（规格 §9）。
//     两层分开，失败原因才能精确指向"扫描过程断了"还是"扫到的内容不对"。
//
// English:
//   How one scan ended: completed with its raw code, or failed with a reason.
//
//   Like ParseResult this is a closed discriminated type (decision D-7): the failure case
//   simply has no scan-content property, so code that reads one from the failure branch
//   does not compile.
//
//   A failure carries the raw code too, but for a different purpose than ParseResult's,
//   and the two must not be conflated. ParseResult.Failure's raw code exists for Force
//   Send (spec §10): when parsing fails, the operator may choose to emit exactly what was
//   scanned. ScanResult.Failed's does not. Spec §5.4 is explicit about a scan timeout —
//   discard the incomplete scan, emit no business input, return to IDLE, show a visible
//   error. There is no F10 path, because the half a code left after a timeout was never a
//   complete barcode and emitting it would be volunteering bad data.
//
//   So why carry it at all? For the error display and the diagnostic log. Spec §10
//   requires showing the raw code if one exists and §15 requires logging it. An operator
//   who sees "the scan broke off after DGKJRD" knows whether the gun was misaligned or
//   the label is damaged; "scan timeout" alone says nothing.
//
//   Completed does not mean valid. It means only that a scan ended normally in the
//   physical sense: a terminator arrived and nothing timed out. Whether the content is
//   usable belongs to parsing and validation (spec §9). Keeping the layers apart is what
//   lets a failure point precisely at either "the scan broke" or "what was scanned is
//   wrong".
//
// 包含的类型 / Types in this file:
//   ScanResult            抽象基类型，构造函数私有，分支集合封闭
//   ScanResult.Completed  扫描正常结束，携带原始码
//   ScanResult.Failed     扫描未能正常结束，携带原因与已收到的部分内容
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：一次扫描的结局。只可能是 <see cref="Completed"/> 或 <see cref="Failed"/> 之一。
/// English: How a scan ended — always exactly one of <see cref="Completed"/> or
///          <see cref="Failed"/>.
/// </summary>
public abstract record ScanResult
{
    /// <summary>
    /// 中文：私有构造函数，封闭分支集合。
    /// English: Private constructor sealing the set of cases.
    /// </summary>
    private ScanResult()
    {
    }

    /// <summary>
    /// 中文：扫描正常结束。
    ///       RawCode 是终止符之前收到的全部字符，**终止符本身不在其中**
    ///       （规格 §5.4、§10）。内容是否合理由解析与校验层判断。
    /// English: The scan ended normally. RawCode is every character received before the
    ///          terminator, which is not itself included (spec §5.4, §10). Whether the
    ///          content is plausible is for parsing and validation to say.
    /// </summary>
    /// <param name="RawCode">
    /// 中文：扫到的完整原始字符串。 English: The complete raw scanned string.
    /// </param>
    public sealed record Completed(string RawCode) : ScanResult;

    /// <summary>
    /// 中文：扫描未能正常结束。
    /// English: The scan did not end normally.
    /// </summary>
    /// <param name="Reason">
    /// 中文：失败原因码。UI 层负责映射为对应语言的文案（决策 D-6）。
    /// English: The reason code; the UI maps it to localized text (decision D-6).
    /// </param>
    /// <param name="PartialRawCode">
    /// 中文：失败发生时已经收到的部分内容，可能为空串。
    ///
    ///       **只用于错误展示与诊断日志，不用于强制发送**——理由见文件头。
    ///       字段名用 PartialRawCode 而非 RawCode，正是为了让调用点一眼看出
    ///       它不是一个完整的条码。
    /// English: Whatever had been received when the failure occurred; may be empty.
    ///
    ///          For the error display and the diagnostic log only, never for Force Send —
    ///          see the file header. Named PartialRawCode rather than RawCode precisely so
    ///          a call site can see at a glance that it is not a complete barcode.
    /// </param>
    public sealed record Failed(ScanFailureReason Reason, string PartialRawCode) : ScanResult;
}
