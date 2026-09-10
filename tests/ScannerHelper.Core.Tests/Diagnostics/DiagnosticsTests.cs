// =============================================================================
// DiagnosticsTests.cs
//
// 中文：
//   诊断日志的纯逻辑（规格 §15，用例编号 DG1~DG8）。
//
//   ★ 这一组测的两件事，都是"错了不会有人发现"的那一类。
//
//     遮码错了，表现是日志里静静地写着明文，而打开开关的人以为已经保护了数据
//     ——没有任何报错、没有任何异样，直到那份日志被拷去了不该去的地方。
//
//     一行拆成两行错了，表现是读日志的人看到一条没头没尾的记录加一条孤零零的
//     碎片，而那往往正是他在追查某一枪时最需要相信"一行就是一件事"的时刻。
//
//     两者都不会让程序崩溃、不会让测试变红——除非专门写用例钉住它们。
//
// English:
//   The diagnostic log's pure logic (spec §15, cases DG1–DG8).
//
//   Both things covered here fail invisibly. A masking bug writes plaintext quietly into the log
//   while whoever turned the switch on believes the data is protected — no error, nothing out of
//   place, until that log is copied somewhere it should not go. A field-separator bug splits one
//   record across two lines, leaving the reader with a record that has no ending and an orphaned
//   fragment, usually at the moment they most need to trust that one line is one event.
//
//   Neither crashes anything or turns a test red, unless a case is written to hold them.
//
// 包含的测试 / Tests in this file:
//   Masking_disabled_returns_the_code_unchanged        DG1
//   Masking_keeps_both_ends_and_the_length             DG2
//   Short_codes_are_masked_entirely                    DG3
//   Empty_code_renders_as_a_placeholder                DG4
//   A_line_carries_every_field_in_order                DG5
//   Tabs_and_newlines_inside_a_field_become_spaces     DG6
//   The_line_honours_the_masking_setting               DG7
//   Absent_fields_render_as_a_placeholder              DG8
// =============================================================================

using ScannerHelper.Core.Diagnostics;

namespace ScannerHelper.Core.Tests.Diagnostics;

public class DiagnosticsTests
{
    private const string RawCode = "ABCDEFG123456789";

    private static readonly DateTimeOffset At =
        new(2026, 9, 10, 14, 32, 10, 123, TimeSpan.FromHours(8));

    [Fact] // DG1
    public void Masking_disabled_returns_the_code_unchanged()
        => Assert.Equal(RawCode, BarcodeMask.Apply(RawCode, isMaskingEnabled: false));

    [Fact] // DG2
    public void Masking_keeps_both_ends_and_the_length()
    {
        var masked = BarcodeMask.Apply(RawCode, isMaskingEnabled: true);

        // ★ 长度必须保留。"SKU 太短"这类失败，日志里若连长度都看不出来，那条
        //   记录就等于没写——而长度本身几乎不泄露什么，同一类条码长度本来就都一样。
        // The length must survive: for a "SKU too short" failure, an entry that does not even show
        // the length may as well not exist — and the length itself reveals almost nothing, since
        // barcodes of one kind all share it.
        Assert.Equal(RawCode.Length, masked.Length);
        Assert.Equal("AB************89", masked);
    }

    [Theory] // DG3
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("ABC")]
    [InlineData("ABCD")]
    public void Short_codes_are_masked_entirely(string shortCode)
    {
        var masked = BarcodeMask.Apply(shortCode, isMaskingEnabled: true);

        // ★ 四位以内留两头就等于原文，"遮了"只是一种错觉——而错觉比不遮更坏：
        //   打开开关的人会以为自己已经保护了数据。
        // Keeping both ends of a four-character code returns the code itself, and "masked" would be
        // an illusion — worse than not masking, because whoever turned the switch on believes the
        // data is protected.
        Assert.Equal(new string('*', shortCode.Length), masked);
        Assert.DoesNotContain(shortCode, masked, StringComparison.Ordinal);
    }

    [Theory] // DG4
    [InlineData(null)]
    [InlineData("")]
    public void Empty_code_renders_as_a_placeholder(string? code)
        => Assert.Equal("—", BarcodeMask.Apply(code, isMaskingEnabled: true));

    [Fact] // DG5
    public void A_line_carries_every_field_in_order()
    {
        var line = new DiagnosticEvent(
            At, DiagnosticEventKind.Emitted, "SKU", RawCode, "ABCDEFG", "detail")
            .ToLine("1.2.3", isMaskingEnabled: false);

        var fields = line.Split('\t');

        Assert.Equal(7, fields.Length);
        Assert.StartsWith("2026-09-10 14:32:10.123", fields[0], StringComparison.Ordinal);
        Assert.Contains("+08:00", fields[0], StringComparison.Ordinal);
        Assert.Equal("1.2.3", fields[1]);
        Assert.Equal("Emitted", fields[2]);
        Assert.Equal("SKU", fields[3]);
        Assert.Equal(RawCode, fields[4]);
        Assert.Equal("ABCDEFG", fields[5]);
        Assert.Equal("detail", fields[6]);
    }

    [Fact] // DG6
    public void Tabs_and_newlines_inside_a_field_become_spaces()
    {
        // 条码内容是外部数据，某些码制确实可以携带控制字符。
        // Barcode content is external data and some symbologies genuinely carry control characters.
        var line = new DiagnosticEvent(
            At, DiagnosticEventKind.Emitted, "SN", "AB\tCD\r\nEF")
            .ToLine("1.0.0", isMaskingEnabled: false);

        // ★ 一行必须始终是一件事。含换行的码若原样写进去，读日志的人看到的是
        //   一条没头没尾的记录加一条孤零零的碎片。
        // One line must always be one event. Written through unchanged, a code containing a newline
        // leaves the reader with a record that has no ending and an orphaned fragment.
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Equal(7, line.Split('\t').Length);
        Assert.Contains("AB CD  EF", line, StringComparison.Ordinal);
    }

    [Fact] // DG7
    public void The_line_honours_the_masking_setting()
    {
        var line = new DiagnosticEvent(
            At, DiagnosticEventKind.Emitted, "SKU", RawCode, "ABCDEFG")
            .ToLine("1.0.0", isMaskingEnabled: true);

        // ★ 原始码与解析出来的 SKU **都要**遮。只遮原始码的话，SKU 那一列就是
        //   一条明文——而 SKU 恰恰是货品的身份，泄露它和泄露原始码没有区别。
        // Both the raw code and the parsed SKU are masked. Masking only the raw code would leave
        // the SKU column in plaintext — and the SKU is the item's identity, so leaking it is no
        // different from leaking the raw code.
        Assert.DoesNotContain(RawCode, line, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG\t", line, StringComparison.Ordinal);
        Assert.Contains("AB************89", line, StringComparison.Ordinal);
    }

    [Fact] // DG8
    public void Absent_fields_render_as_a_placeholder()
    {
        var line = new DiagnosticEvent(At, DiagnosticEventKind.Started)
            .ToLine("1.0.0", isMaskingEnabled: false);

        var fields = line.Split('\t');

        // 空字段写成占位符而不是留空：连着两个制表符会让人以为是格式坏了，
        // 而列数对不上正是读日志时最先让人怀疑记录不完整的东西。
        // Absent fields render as a placeholder rather than nothing: two adjacent tabs read as a
        // broken format, and a wrong column count is the first thing that makes a reader doubt the
        // record is complete.
        Assert.Equal(7, fields.Length);
        Assert.Equal("—", fields[3]);
        Assert.Equal("—", fields[4]);
        Assert.Equal("—", fields[5]);
        Assert.Equal("—", fields[6]);
    }
}
