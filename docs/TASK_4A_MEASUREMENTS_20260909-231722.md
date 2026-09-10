# Task 4a — 输入观测测量报告 / Input Observation Measurements

- 生成时间 / Generated: 2026-09-09 23:17:22 -07:00
- 观测开始 / Run started: 2026-09-09 23:13:33 -07:00
- 测量机器 / Machine: development laptop

## 0. 数据有效性 / Data validity

| 项 / Item | 值 / Value |
|---|---|
| 成功配对的按键事件 / Paired key events | 3799 |
| 缓冲区丢弃 / Dropped by buffer | 0 |
| 钩子看到、Raw Input 未见 / Hook only | 1 |
| Raw Input 看到、钩子未见 / Raw Input only | 40 |

⚠️ **本次数据不完整，下面的统计不能作为完整证据使用。**

- 缓冲区丢弃不为零：说明界面线程被拖住的时间超过了缓冲区容量所能覆盖的范围，分布中间存在缺口，尾部百分位尤其不可信。
- 未配对事件不为零：说明某一条通道看到了另一条完全没看到的按键。**这本身可能是比时序更重要的发现**，不要当作噪声略过——先查清楚是哪一类按键，再决定这批数据还能不能用。

⚠️ **This run is incomplete and the statistics below are not complete evidence.**

## 1. 哪一条通道先到 / Which channel arrives first

| 顺序 / Order | 次数 / Count | 占比 / Share |
|---|---|---|
| 钩子先到 / Hook first | 3775 | 99.4 % |
| Raw Input 先到 / Raw Input first | 24 | 0.6 % |

**这个数字为什么决定架构 / Why this decides the architecture**

钩子回调必须**当场**决定放行还是吞掉，而且不能阻塞等待——等待超过 `LowLevelHooksTimeout`（默认 300 毫秒），Windows 会跳过回调、通常还把钩子摘掉且不发任何通知（规格 §19.1）。

本次观测中**钩子先到占多数**。这意味着回调必须做决定时，设备身份还没到手，因此无法在回调里直接判断「这一下是不是扫码枪按的」。

按规格 §5.3，此时唯一可行的设计是**扣留后重放**：先把按键全部吞掉，等 `WM_INPUT` 说明来源之后，若是普通键盘再用 `SendInput` 补发回去。代价是所有普通打字都要绕这一圈，中文输入法的组字过程最容易在这里被打断——规格 §4.3 把 IME 单列为验收项，正是为此。

Hook-first dominates. Device identity is therefore unavailable when the callback must decide, so the callback cannot itself judge whether the keystroke came from the scanner. Per spec §5.3 the only viable design is withhold-then-replay, whose cost falls on ordinary typing and, above all, on IME composition — which is why spec §4.3 lists IME as its own acceptance item.

⚠️ **两种顺序都出现过。** 这说明顺序不是恒定的，任何「总是先到」的假设都不成立，设计必须同时处理两种情况。

## 2. 两条通道的虚拟键码是否一致 / Virtual-key agreement

**观测到分歧。同一次物理按键，两条通道报出的虚拟键码不同。**

| 钩子报告 / Hook | Raw Input 报告 / Raw Input |
|---|---|
| `0xA0` VK_LSHIFT | `0x10` VK_SHIFT |
| `0xA2` VK_LCONTROL | `0x11` VK_CONTROL |

**这意味着什么 / What this means**

钩子会区分左右修饰键（`VK_LSHIFT` / `VK_RSHIFT`），Raw Input 只给通用码（`VK_SHIFT`）。两者的**扫描码始终相同**。

因此 `InputEventCorrelator` 必须按 **(扫描码, 扩展位, 按下/弹起)** 来把两条通道的事件对应起来，**绝不能用虚拟键码**。扩展位不能省——扫描码本身会重复，右 Ctrl 与左 Ctrl 的扫描码相同，只有它能区分左右。

> 这条是踩出来的，不是想出来的。本工具第一版按虚拟键码配对，结果所有修饰键在两条队列里各自堆积、永远配不上，报出 400 多个「未配对」事件。若这个坑留到 Task 4b 才踩，现场表现会是「扫码枪扫含大写字母的条码时，识别不出来源」——而那时要同时面对未验证的拦截逻辑和这个隐藏的配对错误，正是规格 §4.3 拆分4a 与 4b 所要避免的局面。

The hook distinguishes left and right modifiers while Raw Input reports the generic code; scan codes always match. InputEventCorrelator must therefore key on (scan code, extended flag, direction) and never on the virtual key. This was discovered by hitting it: the tool's first version paired on virtual keys and reported 400-odd unpaired modifier events.

## 3. 两条通道的时差分布 / Inter-channel delta

时差 = Raw Input 时间戳 − 钩子时间戳。**正数表示钩子先到。** 两条通道读的是同一个 `Stopwatch` 高分辨率时钟。

| 统计量 / Statistic | 有符号 / Signed (µs) | 绝对值 / Absolute (µs) |
|---|---|---|
| 最小 / Min | -14883744.4 | 108.1 |
| 中位 / Median | 751.0 | 761.0 |
| p99 | 5783.8 | 7933.7 |
| 最大 / Max | 24530142.9 | 24530142.9 |

**看尾部，不要看中位数。** 假设一千次按键里九百九十九次时差是 50 微秒、一次是 200 毫秒，平均值大约 250 微秒，看上去毫无问题——而那唯一的一次恰恰是架构会出事的地方。4b 的等待窗口必须覆盖 p99 乃至最大值，否则每一百次按键就有一次判断错源。

Read the tail, not the median. 4b's wait window must cover p99 and beyond, or one keystroke in a hundred is attributed to the wrong device.

> 一处需要如实说明的口径：Raw Input 的时间戳是「消息循环处理到这条 `WM_INPUT` 的时刻」，而不是「Windows 产生这条原始输入的时刻」，两者之间隔着消息队列的排队时间。这不是误差——回调做决定时身份**可不可用**，取决于消息何时被取到，而不是它何时被产生。但读者不能把这个数字当成硬件时延。

> **测量环境**：钩子与 `WM_INPUT` 都跑在一条专用线程上，那条线程只有一个仅消息窗口，只泵消息、不碰任何界面。这一点是必须的：第一轮把捕获放在界面线程上，界面每 100 毫秒重建一次列表控件，消息就排在那些工作后面，量到的时差于是变成了「界面有多卡」而不是通道本身的性质。

> 这同时是一条**产品设计要求**：产品也必须在一条专用的、什么别的事都不干的线程上泵 `WM_INPUT`。若那条线程兼做界面，身份到手的延迟会被自己的界面拖大——而 4b 的扣留窗口必须覆盖那个延迟，界面越卡，普通打字被扣留得越久。

> Measurement environment: both channels run on a dedicated thread owning a message-only window that pumps messages and touches no UI. This is required — with capture on the UI thread the first run measured how sluggish the UI was rather than a property of the channels. It is also a product design requirement: a thread that also drives a UI inflates how long identity takes to arrive, and 4b's withhold window must cover that.

## 4. 各设备的输入特征 / Per-device input characteristics

| 设备 / Device | VID/PID | 字符数 / Chars | 连发段 / Bursts | 最大段 / Largest | 段内间隔最小 / Min (ms) | 中位 / Median | p99 | 最大 / Max |
|---|---|---|---|---|---|---|---|---|
| HID Keyboard Device | VID_0581 PID_0115 | 1872 | 72 | 26 | 0.1 | 1.1 | 7.3 | 16.5 |
| Standard PS/2 Keyboard | — | 27 | 13 | 5 | -24047.0 | 122.0 | 186.7 | 186.7 |

「连发段」按大于 200 毫秒的空隙切分，只统计**段内**间隔。不切分的话，两枪扫描之间几秒钟的空闲会被算成一个字符间隔，中位数随即失去意义，而结果看上去仍然像个正常数字。

**这组数字直接决定 Task 6 的扫描无活动超时。** 取小了会把一枪正常的扫描从中间截断，取大了则每次扫描后都要多等那么久才认定结束。选值应当明显大于扫码枪段内间隔的最大值，同时明显小于人两次有意识按键的间隔。

> 该切分阈值仅用于**本报告的分析**，与 Task 6 里那个待定的扫描超时是两回事，不要互相推导。规格也提醒过：约 300 毫秒的扫描超时与`LowLevelHooksTimeout` 的 300 毫秒数字相同但毫无关系。

> **本表的时刻取自钩子那一侧，不是 Raw Input。** 设备身份只有 Raw Input 知道，「这次按键什么时候发生」却只有钩子问得准——钩子回调是在输入派发路径上被同步调用的，而 `WM_INPUT` 要先排队再被消息循环取出。配对正好把两半凑齐：设备取自 Raw Input，时刻取自钩子。第一轮直接用 Raw Input 的时间戳，量到扫码枪段内间隔 p99 48 毫秒——扫码枪不可能有这种停顿，那是消息排队的时间。

> Times here come from the hook side, not Raw Input. Only Raw Input knows the device and only the hook answers "when did this happen" accurately, so pairing supplies both halves. The first run used Raw Input timestamps and measured a 48 ms within-burst p99 for the scanner — in fact queue latency.

## 5. 当前连接的键盘类设备 / Attached keyboard-class devices

**用法**：重新插拔扫码枪、以及重启机器之后，各导出一份报告，对比本表中的「设备路径」是否逐字相同。相同则说明设备路径可以作为跨会话的绑定依据（规格 §6）；不同则绑定必须换一种身份。

| 友好名 / Friendly name | VID/PID | 序列号 / Serial | 设备路径 / Device path |
|---|---|---|---|
| HID Keyboard Device | VID_0581 PID_0115 | — | `\\?\HID#VID_0581&PID_0115&MI_00#7&242c9e3&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |
| Standard PS/2 Keyboard | — | — | `\\?\ACPI#MSFT0001#4&b6e66aa&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |
| HID Keyboard Device | — | — | `\\?\HID#ConvertedDevice&Col01#5&34bce411&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}` |

笔记本内置键盘通常走 ACPI 或 PS/2 而不是 USB HID，因此设备路径形如 `\\?\ACPI#PNP0303#...`，且**没有 VID/PID**（规格假设 A4）。若本表中内置键盘那一行的 VID/PID 是「—」，那不是缺陷，正是预期。反过来，这也说明「靠 VID/PID 区分设备」的做法在笔记本上对内置键盘直接失效，设备路径才是唯一对两类设备都成立的身份。

## 6. 人工观察记录 / Operator notes

_（空）本节需要人工填写：一次完整扫描里出现了哪些修饰键、CapsLock 状态如何变化、终止用的回车是什么形式。这些只有观察者能判断，工具不会替你下结论。_

## 7. 仍未回答 / Still open

- **这些数字尚未在试点机器上复现。** 事件时序是机器的性质，不是代码的性质；开发机与现场机不是同一台硬件。在现场机器上重跑一轮之前，本报告的每一个数字都只是暂定值。
- **设备身份跨重启是否稳定**，需要重启后再导出一份报告对比第 5 节。
- **同一台物理设备是否会被枚举成多个 Raw Input 设备。** 第 5 节里出现`HID#ConvertedDevice` 这类条目时尤其要注意——那是 Windows 为 PS/2 设备生成的 HID 映射。若扫码枪也被枚举成不止一项（例如带 `&MI_01`、`&Col02` 后缀的兄弟条目），只绑定其中一个句柄就可能漏掉另一部分事件，规格 §6 的绑定模型需要相应调整。判断方法：只用扫码枪扫码，看第 4 节里出现几台设备。
- **拦截与重放是否可行**，属于 Task 4b。本次观测**没有**拦截任何事件，因此对此不提供任何证据。规格 §4.3 把两段分开，正是为了避免在未验证的逻辑与未知的硬件行为之间同时排查——那是时序启发式被悄悄引入的典型路径。

- These figures have not been reproduced on pilot hardware; every one is provisional until they are.
- Whether device identity survives a reboot needs a second report to compare section 4 against.
- Whether interception and replay work is Task 4b. This run intercepted nothing and offers no evidence either way.

