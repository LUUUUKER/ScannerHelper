# =============================================================================
# 检查扫码枪.ps1 / probe-scanner.ps1
#
# 中文:
#   在工位那台电脑上量一件事:**这把枪(或它的 2.4G 接收器)在 Windows 眼里到底
#   是个什么设备**。
#
#   ★ 为什么必须量,不能猜。
#
#     2.4G 接收器插上之后可能是三种东西之一,而这三种对应三条完全不同的路:
#
#       1. 虚拟串口(会多出一个 COM 口)      —— 本程序现在就能用,改个端口号而已
#       2. HID POS / HID 条码扫描器          —— 要写一条新的输入通道,但"枪不打字"
#                                               这条结构性保证仍然成立
#       3. 普通 USB 键盘                     —— 这条路已经在 2026-09-10 被证伪
#                                               (见 TASK_4B_FINDING_20260910.md)
#
#     猜错的代价是几天的工作量做在错误的方向上。这个脚本三十秒给出答案。
#
#   ★ 只读。它不改任何设置、不装任何东西、不需要管理员权限。
#
# English:
#   Measures one thing on the workstation: what this scanner (or its 2.4G receiver) actually is in
#   Windows' eyes.
#
#   It has to be measured rather than guessed, because a 2.4G receiver enumerates as one of three
#   things and each leads somewhere completely different: a virtual COM port (works today, just pick
#   the new port), a HID POS / HID barcode device (needs a new input channel, but keeps the
#   structural guarantee that the scanner types nothing), or a plain USB keyboard (the path proven
#   impossible on 2026-09-10 — see TASK_4B_FINDING_20260910.md). Guessing wrong costs days of work
#   aimed in the wrong direction; this answers in thirty seconds.
#
#   Read-only: it changes no settings, installs nothing, and needs no administrator rights.
# =============================================================================

$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot '扫码枪检查结果.txt'
$lines = New-Object System.Collections.Generic.List[string]

function Add-Line { param($t = '') $script:lines.Add([string]$t) }

Add-Line "扫码枪设备检查结果"
Add-Line ("生成时间: " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add-Line ("电脑名称: " + $env:COMPUTERNAME)
Add-Line ("系统版本: " + [System.Environment]::OSVersion.VersionString)
Add-Line ("=" * 72)
Add-Line

# ---------------------------------------------------------------- 一、串口
Add-Line "【一】串口 (COM)  —— 有的话就是最好的情况"
Add-Line ("-" * 72)

$ports = @()
try { $ports = [System.IO.Ports.SerialPort]::GetPortNames() } catch { }

if ($ports.Count -eq 0) {
    Add-Line "  没有任何串口。"
    Add-Line "  如果此刻 2.4G 接收器是插着的,说明它没有走虚拟串口模式。"
} else {
    Add-Line ("  找到 " + $ports.Count + " 个: " + ($ports -join ', '))
}
Add-Line

Add-Line "  串口设备的详细身份 (含 VID/PID):"
$comDevices = @()
try {
    $comDevices = Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.Name -match '\(COM\d+\)' }
} catch { }

if ($comDevices.Count -eq 0) {
    Add-Line "    (无)"
} else {
    foreach ($d in $comDevices) {
        Add-Line ("    名称    : " + $d.Name)
        Add-Line ("    设备 ID : " + $d.DeviceID)
        Add-Line ("    厂商    : " + $d.Manufacturer)
        Add-Line ("    状态    : " + $d.Status)
        Add-Line
    }
}
Add-Line

# ------------------------------------------------------- 二、键盘类设备
Add-Line "【二】键盘类设备  —— 枪如果在这里出现,就是最坏的情况"
Add-Line ("-" * 72)
Add-Line "  (内置键盘、普通 USB 键盘也会列在这里,看 VID/PID 认哪个是枪)"
Add-Line

$keyboards = @()
try {
    $keyboards = Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.PNPClass -eq 'Keyboard' }
} catch { }

if ($keyboards.Count -eq 0) {
    Add-Line "    (无)"
} else {
    foreach ($d in $keyboards) {
        Add-Line ("    名称    : " + $d.Name)
        Add-Line ("    设备 ID : " + $d.DeviceID)
        Add-Line
    }
}
Add-Line

# ---------------------------------------------------------- 三、HID 设备
Add-Line "【三】HID 设备  —— 枪如果以 HID POS / 条码扫描器身份出现,在这里"
Add-Line ("-" * 72)

$hids = @()
try {
    $hids = Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.PNPClass -eq 'HIDClass' -or $_.DeviceID -like 'HID\*' }
} catch { }

if ($hids.Count -eq 0) {
    Add-Line "    (无)"
} else {
    foreach ($d in $hids) {
        Add-Line ("    名称    : " + $d.Name)
        Add-Line ("    设备 ID : " + $d.DeviceID)
        Add-Line
    }
}
Add-Line

# ----------------------------------------------------- 四、所有 USB 设备
Add-Line "【四】全部 USB 设备  —— 兜底,前三节都没认出来时从这里找"
Add-Line ("-" * 72)

$usb = @()
try {
    $usb = Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.DeviceID -like 'USB\*' } | Sort-Object Name
} catch { }

if ($usb.Count -eq 0) {
    Add-Line "    (无)"
} else {
    foreach ($d in $usb) {
        Add-Line ("    " + $d.Name)
        Add-Line ("        " + $d.DeviceID)
    }
}
Add-Line
Add-Line ("=" * 72)
Add-Line "怎么读这份结果:"
Add-Line
Add-Line "  第一节里多出一个 COM 口, 且拔掉接收器它就消失"
Add-Line "      → 最好的情况。本程序现在就能用,在设置里选那个端口即可。"
Add-Line
Add-Line "  第三节里有一个名字带 POS / Barcode / 扫描 字样的 HID 设备"
Add-Line "      → 可行。需要给程序加一条 HID 输入通道,但枪仍然不打字。"
Add-Line
Add-Line "  枪只出现在第二节(键盘)里"
Add-Line "      → 最坏的情况。需要换接收器的工作模式,或换一把支持虚拟串口的枪。"
Add-Line
Add-Line "★ 建议做两次: 先拔掉接收器跑一次, 再插上跑一次, 对比多出来的是什么。"
Add-Line "  (第二次跑之前先把上一份结果改个名字, 否则会被覆盖)"

[System.IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding $true))

Write-Host ""
Write-Host "检查完成。结果已写入:" -ForegroundColor Green
Write-Host "  $out"
Write-Host ""
Write-Host "串口: " -NoNewline
if ($ports.Count -eq 0) {
    Write-Host "没有找到任何串口" -ForegroundColor Yellow
} else {
    Write-Host ($ports -join ', ') -ForegroundColor Green
}
Write-Host ""
