# Task 4b 硬性关卡：失败 / Hard gate: FAILED

**结论 / Verdict:** 第 2 条不可达成。规格 §5.3 的「扣留—关联—决定」在用户态无法实现。
**Item 2 is not achievable. Spec §5.3's withhold-correlate-decide cannot be implemented in user mode.**

- 日期 / Date: 2026-09-10
- 机器 / Machine: LAPTOP-AI4MH002, Windows 11 (10.0.26200)
- 扫码枪 / Scanner: HID Keyboard Device, VID_0581 & PID_0115
- 条码 / Barcode: `ABCDEFG123456789`，每组扫 10 枪 / ten scans per run

---

## 1. 实测结果 / What was measured

同一台机器、同一把扫码枪、同一份代码。**唯一的变量是钩子吞不吞按键。**
Same machine, same scanner, same build. **The only variable is whether the hook swallows.**

| 计数 / Counter | A 组：吞 / swallowing | B 组：不吞 / observe-only |
|---|---:|---:|
| 吞掉 / Swallowed | 486 | 0 |
| 放行 / Passed | 486 | 482 |
| 补发 / Replayed | 486 | 0 |
| Raw Input 总数 / total | 485 | 487 |
| **其中来自绑定扫码枪 / from the bound scanner** | **0** | **≈487** |
| 其中设备句柄为 0（合成）/ handle zero | 485 | 0 |
| **处理完成的扫描 / Scans processed** | **0** | **10（全部正确）** |
| 超时未关联 / Unresolved | 468 | **0** |
| 丢弃的 Raw Input / Discarded | 464 | 5 |
| 钩子回调最长 / Longest callback | 11.6 ms | 1.2 ms |
| 超预算次数 / Over budget | 0 | 0 |
| 异常 / Faults | 0 | 0 |

B 组是干净的对照组：不吞的时候，配对**完美**——超时未关联为 0，十枪全部被正确
归属到扫码枪并发出。所以设备句柄是对的、身份定义是对的、关联器是对的、
时间基准是对的。

B is a clean control: with nothing swallowed, pairing is **flawless** — zero unresolved, all ten
scans attributed to the scanner and emitted. So the device handle, the identity definition, the
correlator and the time base are all correct.

---

## 2. 两条结论 / Two findings

### 发现一：吞掉按键会切断它的 Raw Input

> **按键一旦被 `WH_KEYBOARD_LL` 的回调吞掉（返回非零），Windows 就不再为它
> 产生 `WM_INPUT`。**
>
> **Once a keystroke is swallowed by a `WH_KEYBOARD_LL` callback (a non-zero return),
> Windows produces no `WM_INPUT` for it.**

A 组扫了 10 枪，来自绑定扫码枪的 Raw Input 是 **0 条**；B 组同样 10 枪是 **约 487 条**。

这直接否定了规格 §5.3 的前提。那套设计是：

1. 钩子先把按键吞掉（此时还不知道是谁按的）；
2. 等它的 `WM_INPUT` 到来，从中读出设备；
3. 按设备决定：是扫码枪就留着，不是就补发。

**第 2 步永远不会发生**，因为第 1 步已经把它取消了。扣留销毁了做决定所需要的
证据。于是每一个按键都走超时路径被原样重放——A 组的 468 次「超时未关联」，
以及记事本里那些大小写飘忽、被回车劈开、夹着乱码的输出，全都是这一件事。

Step 2 never happens, because step 1 cancelled it. Withholding destroys the very evidence the
decision needs, so every keystroke takes the expiry path and is replayed verbatim — which is what
A's 468 unresolved events are, and what the garbled, case-unstable, Enter-split output in Notepad
was all along.

**这不是可以修的缺陷，是这条路走不通。** 用户态里，Raw Input 知道是谁按的但拦不住，
低层钩子拦得住但不知道是谁按的，而两者**不能合用**——合用的那一刻，前者就没了。

This is not a defect that can be fixed. In user mode Raw Input knows the device but cannot block,
the low-level hook can block but does not know the device, and the two **cannot be combined**:
the moment you use the second, the first is gone.

### 发现二：我们自己合成的按键**会**产生 Raw Input

代码里原本写着「合成事件（SendInput）不经过 Raw Input，因此这条通道上恒为
false」，旁边标着「这本身是 4a 值得确认的一条事实」。**从没确认过，而且它是错的。**

A 组 485 条 Raw Input 全部来自设备句柄为 0 的合成事件，数量与 486 次补发吻合。

The code assumed synthesized input never travels through Raw Input, in a comment marked "worth
confirming in 4a" that was never confirmed. **It is wrong.** All 485 of A's Raw Input events came
from synthesized input with a zero device handle, matching the 486 replays.

后果：任何一个既注入按键、又监听 Raw Input 的设计，都必须显式滤掉设备句柄为 0
的事件，否则自己的输出会当作外部输入被重新吃进去。本次它一直在污染关联器。

---

## 3. 按计划应当怎么做 / What the plan requires

实施计划 Task 4b 原文：

> If any of 2–6 is not reliably achievable, do **not** continue while pretending timing
> heuristics are sufficient. Document the observed event ordering and failure modes,
> and revisit the architecture.

因此：**不继续建完整应用，先定架构。** 本文件即所要求的记录。

---

## 4. 可选的出路 / The options

### 选项 A —— 让扫码枪不再当键盘（推荐）

绝大多数 HID 扫码枪都能用配置条码切换到 **USB 虚拟串口（VCP / CDC）** 或
**USB HID POS（IBM SurePOS）** 模式。切过去之后：

- 扫码枪**根本不再打字**，业务软件收不到任何原始字符——泄漏问题从源头消失；
- 不需要钩子、不需要 Raw Input 关联、不需要扣留与补发；
- 工人的键盘从头到尾没被碰过，不存在「键盘失灵」这一整类风险（假设 A4）；
- 中文输入法、按住重复、CapsLock、UIPI、`LowLevelHooksTimeout`、静默摘钩——
  这些问题一次性全部消失；
- 输出仍然走 `SendInput` + `KEYEVENTF_UNICODE`，那一半代码原样保留。

**它很可能还顺带解决另一个问题**：现场早已发现，**本程序完全不启动时**，
扫码进记事本也会出错（规格 §22.5，字符间隔中位数 1.1 毫秒）。那是键盘模拟
本身的毛病，串口模式没有这个失效模式。

代价：需要找到这把枪的配置条码手册；可能需要装厂商的 VCP 驱动；每台新枪
要扫一次配置码。规格 §2 的假设与 §4 的架构要改。

### 选项 B —— 内核过滤驱动

技术上正确的做法，也是「按设备拦截键盘」在 Windows 上唯一干净的做法。
但规格明确把它排除在 V1 之外：要代码签名、要 IT 部署、装错了会让机器起不来。
仓库 IT 按哈希白名单发可执行文件（§22.1），驱动是完全另一回事。

### 选项 C —— 接受首字符泄漏或改用时序判据

即：先放行、按「一串字符来得极快」判定是扫码枪，再想办法收拾前面漏出去的。
**规格禁止**（§4.3 的硬性关卡、以及"不得用时序启发式掩盖症状"），而且第 6 条
「第一个字符不泄漏」会直接失败。不推荐，此处仅为完整性列出。

### 选项 D —— 改变工作流：扫进本程序自己的窗口

工人把焦点放在 Scanner Helper 的小窗口上扫码，本程序处理后再发给业务软件。
不需要拦截任何东西。代价是工作流变了，且与规格「对业务软件透明」的目标相悖。

---

## 5. 建议 / Recommendation

**选项 A。** 它把整个产品最难、风险最高的一块直接删掉，而不是绕过去；
并且很可能连带修掉那个「程序没开也会扫错」的老问题。

需要先确认一件事：**手上这把 VID_0581 & PID_0115 的扫码枪支持哪些工作模式。**
这要看它的说明书或配置码手册。

**Option A.** It deletes the product's hardest and riskiest part rather than working around it,
and it is likely to fix the long-standing corruption that appears even with this program not
running. The prerequisite is knowing which modes this particular scanner supports.
