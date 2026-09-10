# Task 4a — 输入观测测量报告 / Input Observation Measurements

- 生成时间 / Generated: 2026-09-09 22:45:55 -07:00
- 观测开始 / Run started: 2026-09-09 22:38:38 -07:00
- 测量机器 / Machine: development laptop

## 0. 数据有效性 / Data validity

| 项 / Item | 值 / Value |
|---|---|
| 成功配对的按键事件 / Paired key events | 1049 |
| 缓冲区丢弃 / Dropped by buffer | 0 |
| 钩子看到、Raw Input 未见 / Hook only | 423 |
| Raw Input 看到、钩子未见 / Raw Input only | 422 |

⚠️ **本次数据不完整，下面的统计不能作为完整证据使用。**

- 缓冲区丢弃不为零：说明界面线程被拖住的时间超过了缓冲区容量所能覆盖的范围，分布中间存在缺口，尾部百分位尤其不可信。
- 未配对事件不为零：说明某一条通道看到了另一条完全没看到的按键。**这本身可能是比时序更重要的发现**，不要当作噪声略过——先查清楚是哪一类按键，再决定这批数据还能不能用。

⚠️ **This run is incomplete and the statistics below are not complete evidence.**

## 1. 哪一条通道先到 / Which channel arrives first

| 顺序 / Order | 次数 / Count | 占比 / Share |
|---|---|---|
| 钩子先到 / Hook first | 1049 | 100.0 % |
| Raw Input 先到 / Raw Input first | 0 | 0.0 % |

**这个数字为什么决定架构 / Why this decides the architecture**

钩子回调必须**当场**决定放行还是吞掉，而且不能阻塞等待——等待超过 `LowLevelHooksTimeout`（默认 300 毫秒），Windows 会跳过回调、通常还把钩子摘掉且不发任何通知（规格 §19.1）。

本次观测中**钩子先到占多数**。这意味着回调必须做决定时，设备身份还没到手，因此无法在回调里直接判断「这一下是不是扫码枪按的」。

按规格 §5.3，此时唯一可行的设计是**扣留后重放**：先把按键全部吞掉，等 `WM_INPUT` 说明来源之后，若是普通键盘再用 `SendInput` 补发回去。代价是所有普通打字都要绕这一圈，中文输入法的组字过程最容易在这里被打断——规格 §4.3 把 IME 单列为验收项，正是为此。

Hook-first dominates. Device identity is therefore unavailable when the callback must decide, so the callback cannot itself judge whether the keystroke came from the scanner. Per spec §5.3 the only viable design is withhold-then-replay, whose cost falls on ordinary typing and, above all, on IME composition — which is why spec §4.3 lists IME as its own acceptance item.

## 2. 两条通道的时差分布 / Inter-channel delta

时差 = Raw Input 时间戳 − 钩子时间戳。**正数表示钩子先到。** 两条通道读的是同一个 `Stopwatch` 高分辨率时钟。

| 统计量 / Statistic | 有符号 / Signed (µs) | 绝对值 / Absolute (µs) |
|---|---|---|
| 最小 / Min | 110.7 | 110.7 |
| 中位 / Median | 843.3 | 843.3 |
| p99 | 20294.4 | 20294.4 |
| 最大 / Max | 57107.3 | 57107.3 |

**看尾部，不要看中位数。** 假设一千次按键里九百九十九次时差是 50 微秒、一次是 200 毫秒，平均值大约 250 微秒，看上去毫无问题——而那唯一的一次恰恰是架构会出事的地方。4b 的等待窗口必须覆盖 p99 乃至最大值，否则每一百次按键就有一次判断错源。

Read the tail, not the median. 4b's wait window must cover p99 and beyond, or one keystroke in a hundred is attributed to the wrong device.

> 一处需要如实说明的口径：Raw Input 的时间戳是「消息循环处理到这条 `WM_INPUT` 的时刻」，而不是「Windows 产生这条原始输入的时刻」，两者之间隔着消息队列的排队时间。这不是误差——回调做决定时身份**可不可用**，取决于消息何时被取到，而不是它何时被产生。但读者不能把这个数字当成硬件时延。

## 3. 各设备的输入特征 / Per-device input characteristics

| 设备 / Device | VID/PID | 字符数 / Chars | 连发段 / Bursts | 最大段 / Largest | 段内间隔最小 / Min (ms) | 中位 / Median | p99 | 最大 / Max |
|---|---|---|---|---|---|---|---|---|
| HID Keyboard Device | VID_0581 PID_0115 | 682 | 31 | 26 | 0.0 | 1.1 | 48.0 | 58.4 |
| Standard PS/2 Keyboard | — | 53 | 16 | 14 | 17.0 | 94.7 | 187.6 | 187.6 |

「连发段」按大于 200 毫秒的空隙切分，只统计**段内**间隔。不切分的话，两枪扫描之间几秒钟的空闲会被算成一个字符间隔，中位数随即失去意义，而结果看上去仍然像个正常数字。

**这组数字直接决定 Task 6 的扫描无活动超时。** 取小了会把一枪正常的扫描从中间截断，取大了则每次扫描后都要多等那么久才认定结束。选值应当明显大于扫码枪段内间隔的最大值，同时明显小于人两次有意识按键的间隔。

> 该切分阈值仅用于**本报告的分析**，与 Task 6 里那个待定的扫描超时是两回事，不要互相推导。规格也提醒过：约 300 毫秒的扫描超时与`LowLevelHooksTimeout` 的 300 毫秒数字相同但毫无关系。

## 4. 当前连接的键盘类设备 / Attached keyboard-class devices

**用法**：重新插拔扫码枪、以及重启机器之后，各导出一份报告，对比本表中的「设备路径」是否逐字相同。相同则说明设备路径可以作为跨会话的绑定依据（规格 §6）；不同则绑定必须换一种身份。

| 友好名 / Friendly name | VID/PID | 序列号 / Serial | 设备路径 / Device path |
|---|---|---|---|
| HID Keyboard Device | VID_0581 PID_0115 | — | `\\?\HID#VID_0581&PID_0115&MI_00#7&242c9e3&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |
| Standard PS/2 Keyboard | — | — | `\\?\ACPI#MSFT0001#4&b6e66aa&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |
| HID Keyboard Device | — | — | `\\?\HID#ConvertedDevice&Col01#5&34bce411&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |

笔记本内置键盘通常走 ACPI 或 PS/2 而不是 USB HID，因此设备路径形如 `\\?\ACPI#PNP0303#...`，且**没有 VID/PID**（规格假设 A4）。若本表中内置键盘那一行的 VID/PID 是「—」，那不是缺陷，正是预期。反过来，这也说明「靠 VID/PID 区分设备」的做法在笔记本上对内置键盘直接失效，设备路径才是唯一对两类设备都成立的身份。

## 5. 人工观察记录 / Operator notes

_（空）本节需要人工填写：一次完整扫描里出现了哪些修饰键、CapsLock 状态如何变化、终止用的回车是什么形式。这些只有观察者能判断，工具不会替你下结论。_

## 6. 仍未回答 / Still open

- **这些数字尚未在试点机器上复现。** 事件时序是机器的性质，不是代码的性质；开发机与现场机不是同一台硬件。在现场机器上重跑一轮之前，本报告的每一个数字都只是暂定值。
- **设备身份跨重启是否稳定**，需要重启后再导出一份报告对比第 4 节。
- **拦截与重放是否可行**，属于 Task 4b。本次观测**没有**拦截任何事件，因此对此不提供任何证据。规格 §4.3 把两段分开，正是为了避免在未验证的逻辑与未知的硬件行为之间同时排查——那是时序启发式被悄悄引入的典型路径。

- These figures have not been reproduced on pilot hardware; every one is provisional until they are.
- Whether device identity survives a reboot needs a second report to compare section 4 against.
- Whether interception and replay work is Task 4b. This run intercepted nothing and offers no evidence either way.

