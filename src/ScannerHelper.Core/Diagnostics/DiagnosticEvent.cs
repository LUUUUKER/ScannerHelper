// =============================================================================
// DiagnosticEvent.cs
//
// 中文：
//   日志里的一条记录，以及它变成一行文字的样子（规格 §15）。
//
//   ★ 为什么是**纯文本一行一条**，而不是 JSON。
//
//     这份日志的读者是"现场出了问题、被叫来看一眼"的人，工具往往只有记事本。
//     一行一条的文本可以直接用眼睛扫、可以 Ctrl+F 找那个码、可以复制一段贴进
//     聊天窗口。JSON 要么每条占十几行、要么挤成一行谁也读不了，而它换来的
//     "方便程序解析"在这里没有消费者——没有任何系统会去解析这个文件。
//
//   ★ 时间戳带偏移量（例如 +08:00）。
//
//     日志会被拷到别的机器上看，而仓库、开发者、服务器可能不在同一个时区。
//     一个不带偏移的 "14:32:10" 在追查"这枪到底是几点扫的"时会引出一轮
//     没有必要的猜测。
//
//   ★ 每条都带程序版本。
//
//     现场机器上的版本未必是最新的那个。规格 §15 把它列进字段，是因为排查时
//     第一个要排除的可能就是"他装的还是上一版"。
//
//   ★ 字段之间用制表符隔开，字段内部**不允许**出现制表符或换行。
//
//     条码内容是外部数据，理论上可以包含任何字符。一个含换行的码会把一条记录
//     劈成两行，而读日志的人看到的是一条没头没尾的记录加一条孤零零的碎片——
//     那种时候他最需要的恰恰是相信"一行就是一件事"。
//
// English:
//   One record in the log and how it becomes a line of text (spec §15).
//
//   Plain text, one line per event, rather than JSON: this log is read by whoever was called over
//   when something went wrong on site, often with nothing but Notepad. One line per event can be
//   scanned by eye, searched with Ctrl+F for a code, and pasted into a chat window. JSON either
//   takes a dozen lines per entry or compresses into something unreadable, and buys machine
//   parseability that has no consumer here — no system will ever read this file.
//
//   Timestamps carry an offset (+08:00): logs get copied to other machines, and the warehouse, the
//   developer and the server may be in different time zones. A bare "14:32:10" invites a round of
//   needless guessing about when a scan actually happened.
//
//   Every line carries the application version, because the version on site is not necessarily the
//   latest; spec §15 lists it because "they are still running the previous build" is among the
//   first things an investigation must rule out.
//
//   Fields are tab-separated and may contain neither tabs nor newlines. Barcode content is external
//   data and could in principle contain anything; a code with a newline in it would split one
//   record across two lines, and the reader would see a record with no ending followed by an
//   orphaned fragment — at exactly the moment they most need to trust that one line is one event.
//
// 包含的类型 / Types in this file:
//   DiagnosticEventKind
//   DiagnosticEvent
// =============================================================================

using System.Globalization;
using System.Text;

namespace ScannerHelper.Core.Diagnostics;

/// <summary>
/// 中文：日志事件的种类。取自规格 §15 列出的那些"要记下来的事"。
/// English: The kinds of event, taken from what spec §15 lists as worth recording.
/// </summary>
public enum DiagnosticEventKind
{
    /// <summary>中文：程序启动。 English: The application started.</summary>
    Started,

    /// <summary>中文：程序退出。 English: The application exited.</summary>
    Stopped,

    /// <summary>中文：连上扫码枪。 English: The scanner connected.</summary>
    Connected,

    /// <summary>中文：与扫码枪断开。 English: The scanner disconnected.</summary>
    Disconnected,

    /// <summary>中文：自动重连成功。 English: Reconnected automatically.</summary>
    Reconnected,

    /// <summary>中文：一枪发出去了。 English: A scan was emitted.</summary>
    Emitted,

    /// <summary>中文：解析失败，等工人决定。 English: Parsing failed; awaiting the operator.</summary>
    ParseFailed,

    /// <summary>中文：校验失败，等工人决定。 English: Validation failed; awaiting the operator.</summary>
    ValidationFailed,

    /// <summary>中文：工人丢弃了这一枪。 English: The operator discarded the scan.</summary>
    Discarded,

    /// <summary>中文：暂停期间原样发出。 English: Emitted unchanged while paused.</summary>
    EmittedWhilePaused,

    /// <summary>中文：切换了模式。 English: The mode was toggled.</summary>
    ModeChanged,

    /// <summary>中文：进入暂停。 English: Entered PAUSED.</summary>
    Paused,

    /// <summary>中文：离开暂停。 English: Left PAUSED.</summary>
    Resumed,

    /// <summary>中文：设置被改动。 English: Settings changed.</summary>
    SettingsChanged,

    /// <summary>中文：出了异常。 English: Something threw.</summary>
    Fault,
}

/// <summary>
/// 中文：日志里的一条记录。
/// English: One record in the log.
/// </summary>
/// <param name="At">中文：发生的时刻，带时区偏移。 English: When it happened, with offset.</param>
/// <param name="Kind">中文：种类。 English: The kind.</param>
/// <param name="Mode">中文：当时的模式，无关时为 null。 English: The mode, or null when irrelevant.</param>
/// <param name="RawCode">
/// 中文：原始内容。**这里存的是原文**，遮码在写出去的那一刻做（见 ToLine）。
/// English: The raw content. Stored unmasked; masking happens when the line is produced (ToLine).
/// </param>
/// <param name="Sku">中文：解析出来的 SKU。 English: The parsed SKU.</param>
/// <param name="Detail">中文：补充说明，例如失败原因、端口名。 English: Extra detail such as a
///                     failure reason or a port name.</param>
public readonly record struct DiagnosticEvent(
    DateTimeOffset At,
    DiagnosticEventKind Kind,
    string? Mode = null,
    string? RawCode = null,
    string? Sku = null,
    string? Detail = null)
{
    private const char FieldSeparator = '\t';

    /// <summary>
    /// 中文：
    ///   变成写进文件的那一行。
    ///   输入：applicationVersion 程序版本；isMaskingEnabled 是否遮码。
    ///
    ///   ★ 遮码在**这里**做，而不是在造事件的地方。造事件的地方有十几处，
    ///     判断散在十几处迟早漏一处——而漏掉的那一处会把明文写进一个使用者
    ///     以为已经遮过的日志里，且没有任何迹象。见 BarcodeMask。
    /// English:
    ///   Produces the line written to the file.
    ///
    ///   Masking happens here rather than where events are constructed: there are a dozen such
    ///   places, and a decision repeated a dozen times is eventually missed in one — which then
    ///   writes plaintext into a log its reader believes is masked, with nothing to show for it.
    ///   See BarcodeMask.
    /// </summary>
    public string ToLine(string applicationVersion, bool isMaskingEnabled)
    {
        var line = new StringBuilder(160);

        line.Append(At.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        Append(line, applicationVersion);
        Append(line, Kind.ToString());
        Append(line, Mode);
        Append(line, BarcodeMask.Apply(RawCode, isMaskingEnabled));
        Append(line, Sku is null ? null : BarcodeMask.Apply(Sku, isMaskingEnabled));
        Append(line, Detail);

        return line.ToString();
    }

    /// <summary>
    /// 中文：
    ///   追加一个字段，并把里面的制表符与换行换成空格。
    ///
    ///   ★ 这不是防御性代码。条码内容是**外部数据**——扫码枪送来什么就是什么，
    ///     而某些条码类型确实可以携带控制字符。一个含换行的码会把一条记录劈成
    ///     两行，读日志的人看到的是一条没头没尾的记录加一条孤零零的碎片，
    ///     而那种时候他最需要的恰恰是相信"一行就是一件事"。
    /// English:
    ///   Appends one field with tabs and newlines replaced by spaces.
    ///
    ///   Not defensive coding: barcode content is external data — whatever the scanner sends — and
    ///   some symbologies genuinely carry control characters. A code containing a newline would
    ///   split one record across two lines, leaving the reader with a record that has no ending and
    ///   an orphaned fragment, at exactly the moment they most need to trust that one line is one
    ///   event.
    /// </summary>
    private static void Append(StringBuilder line, string? value)
    {
        line.Append(FieldSeparator);

        if (string.IsNullOrEmpty(value))
        {
            line.Append('—');
            return;
        }

        foreach (var character in value)
        {
            line.Append(character is '\t' or '\r' or '\n' ? ' ' : character);
        }
    }
}
