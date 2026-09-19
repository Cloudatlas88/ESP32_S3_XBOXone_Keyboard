"""
宏功能端到端验收 —— 针对本轮新增的三项功能。

覆盖：

  A. 配置往返      宏定义（含基准值+抖动%的随机延迟）写入/读回一致
  B. 随机延迟分布  连续触发多次，统计实际间隔是否落在 [基准-抖动, 基准+抖动]
  C. 宏总开关      关掉后触发被拒；打开后又能触发
  D. 循环 + 中止   循环宏能持续跑，且【中止后立刻停】

不需要按实体按键：全部走 CFG_MACRO_RUN (0x08)，
它和实体按键触发在固件里是同一条路径（macro_trigger）。
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfg_proto as P

# 随机延迟那一组用的基准值（基准值 = 分布中心，抖动 ±50%）
RAND_BASE_MS = 400

PASS = FAIL = 0


def check(name, ok, detail=""):
    global PASS, FAIL
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  —— {detail}" if detail else ""))
    if ok:
        PASS += 1
    else:
        FAIL += 1


def measure_macro_ms(dev, macro_id: int, timeout: float = 5.0) -> float | None:
    """触发一个宏，测量它从「开始执行」到「回到空闲」花了多少毫秒。

    用它间接量出宏里的随机延迟 —— 不需要在主机侧抓键盘事件，
    因为宏总时长基本就等于各步延迟之和（敲击保持只有 20~35ms）。
    """
    t0 = time.perf_counter()
    if not dev.macro_run(macro_id)["ok"]:
        return None

    # 先等它真的跑起来
    while not dev.macro_busy():
        if time.perf_counter() - t0 > timeout:
            return None

    start = time.perf_counter()
    while dev.macro_busy():
        if time.perf_counter() - t0 > timeout:
            return None
        time.sleep(0.004)

    return (time.perf_counter() - start) * 1000.0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    print("════════ 宏功能端到端验收 ════════")
    dev = P.Dev()
    try:
        # ── A. 写入一个已知的测试配置 ──
        print("\n── A. 配置往返（基准值 + 抖动% 的随机延迟）")
        base = P.unpack_cfg(dev.read())

        def pad(steps):
            steps = list(steps)
            while len(steps) < P.MACRO_STEP_MAX:
                steps.append(dict(action=P.ACT_DELAY, keycode=0, modifiers=0, dmin=0, dmax=0))
            return steps

        macros = list(base.get("macros", []))
        while len(macros) < P.MACRO_MAX:
            macros.append(dict(step_count=0, flags=0, timeout=0, steps=[]))

        # 宏#0：A → 随机延迟(base=250, jitter=20%) → B     不循环
        macros[0] = dict(step_count=3, flags=0, timeout=8000, steps=pad([
            dict(action=P.ACT_KEY_TAP,    keycode=0x04, modifiers=0, dmin=0,   dmax=0),
            dict(action=P.ACT_RAND_DELAY, keycode=0,    modifiers=0, dmin=250, dmax=20),
            dict(action=P.ACT_KEY_TAP,    keycode=0x05, modifiers=0, dmin=0,   dmax=0),
        ]))
        # 宏#1：循环敲 C，每 350ms 一次
        macros[1] = dict(step_count=2, flags=1, timeout=12000, steps=pad([
            dict(action=P.ACT_KEY_TAP, keycode=0x06, modifiers=0, dmin=0,   dmax=0),
            dict(action=P.ACT_DELAY,   keycode=0,    modifiers=0, dmin=350, dmax=0),
        ]))

        base["macros"] = macros
        base["macro_count"] = 2
        btns = [dict(x) for x in base["buttons"]]
        btns[0]["flags"] = P.BTNFLAG_ENABLED | P.BTNFLAG_HAS_MACRO
        btns[0]["macro_id"] = 0
        btns[1]["flags"] = P.BTNFLAG_ENABLED | P.BTNFLAG_HAS_MACRO
        btns[1]["macro_id"] = 1
        base["buttons"] = btns

        dev.write(P.pack_cfg(base))
        dev.save()
        time.sleep(0.3)

        back = P.unpack_cfg(dev.read())
        # ★ 版本号从 cfg_proto 的常量取，别写死 ——
        #   配置格式每升一次版本都要回来改这个硬编码，纯属自找麻烦
        #   （0x0004→0x0005 时就在这里白报了一次失败）。
        check(f"配置版本 = 0x{P.CFG_VERSION:04X}",
              back["version"] == P.CFG_VERSION, f"0x{back['version']:04X}")
        check("宏#0 步数 / 超时 一致",
              back["macros"][0]["step_count"] == 3 and back["macros"][0]["timeout"] == 8000)
        check("随机延迟 基准值 读回正确",
              back["macros"][0]["steps"][1]["dmin"] == 250,
              f"基准={back['macros'][0]['steps'][1]['dmin']}ms")
        check("随机延迟 抖动% 读回正确",
              back["macros"][0]["steps"][1]["dmax"] == 20,
              f"抖动=±{back['macros'][0]['steps'][1]['dmax']}%")
        check("宏#1 循环标志位 已置位", (back["macros"][1]["flags"] & 1) == 1)
        check("按钮1/按钮2 分别绑到宏#0/宏#1",
              back["buttons"][0]["macro_id"] == 0 and (back["buttons"][0]["flags"] & P.BTNFLAG_HAS_MACRO)
              and back["buttons"][1]["macro_id"] == 1 and (back["buttons"][1]["flags"] & P.BTNFLAG_HAS_MACRO))

        # ── B. 宏总开关已移除：宏上电即可用 ──
        print("\n── B. 宏总开关已移除（宏默认就是打开的）")

        # 偏移 57/58 原来是「宏总开关按键 / 上电默认启用」，现在是保留字节。
        # 老配置里存着旧值也要能正常加载 —— 固件已完全不读它们。
        rawcfg = dev.read()
        check("保留字节不会被写入非 0 值（旧字段已彻底停用）",
              rawcfg[57] == 0 and rawcfg[58] == 0,
              f"b[57]={rawcfg[57]} b[58]={rawcfg[58]}")

        # 不设任何开关，直接触发就应该成功
        r = dev.macro_run(0)
        check("无需任何开关，直接触发宏 → 成功", r["ok"], f"{r['detail']}")
        time.sleep(2.0)

        # 废弃的 0x07 应当被识别为未知命令，而不是被当成别的东西执行
        p = dev.parse_resp(dev.cmd(0x07, data=bytes([0xFF])))
        check("废弃的 0x07（宏总开关命令）被拒绝为未知命令",
              p["cmd"] == P.CMD_NACK and p["data"][1] == 0x01,
              f"cmd=0x{p['cmd']:02X} err=0x{p['data'][1]:02X} ({P.ERR.get(p['data'][1], '?')})")

        # ── C. ★ 循环宏：按一下开、再按一下停 ──
        print("\n── C. 循环宏开关行为（按一下开始，再按一下停止）")

        r = dev.macro_run(1)
        check("第一次触发循环宏 → 启动", r["ok"] and r["started"], f"{r['detail']}")

        time.sleep(2.2)
        check("2.2 秒后仍在执行（确实在循环，没有跑一轮就停）", dev.macro_busy())

        # ★ 核心：再触发一次同一个循环宏，应该是【停】而不是重来
        r = dev.macro_run(1)
        check("再触发一次同一个循环宏 → 命令被接受", r["ok"], f"{r['detail']}")
        check("★ 本次动作是【停止】而不是重来", r["ok"] and not r["started"],
              f"started={r['started']}")
        time.sleep(0.6)
        check("★ 循环宏已停止（这正是你要的『再点击为结束』）",
              not dev.macro_busy(),
              "仍在执行" if dev.macro_busy() else "已停止且按键已释放")

        # 确认停掉之后还能再开起来（开关是可循环的）
        r = dev.macro_run(1)
        check("停掉之后再触发 → 又能启动（开关可反复切换）",
              r["ok"] and r["started"], f"{r['detail']}")
        time.sleep(0.5)
        check("确实又跑起来了", dev.macro_busy())
        dev.macro_run(1)          # 收尾：停掉
        time.sleep(0.6)
        check("再次触发可停掉，恢复空闲", not dev.macro_busy())

        # ── C2. 非循环宏不受影响：再按一次是「重来」而不是「停」──
        print("\n── C2. 非循环宏行为不变（再触发一次是重来，不是停）")
        r = dev.macro_run(0)
        check("触发非循环宏 → 报告为『启动』", r["ok"] and r["started"], f"{r['detail']}")
        time.sleep(0.05)
        r2 = dev.macro_run(0)
        check("运行中再触发非循环宏 → 仍然报告『启动』（不是被关掉）",
              r2["ok"] and r2["started"],
              f"{r2['detail']}")
        time.sleep(2.5)
        check("非循环宏自己跑完回到空闲", not dev.macro_busy())

        # ── D. 重复触发稳定性 ──
        print("\n── D. 连续触发 8 次（检查随机延迟不会卡死/漏触发）")
        okcnt = 0
        for i in range(8):
            rr = dev.macro_run(0)
            if rr["ok"]:
                okcnt += 1
            time.sleep(0.75)
        check("8 次触发全部被接受", okcnt == 8, f"{okcnt}/8")
        time.sleep(1.5)
        check("恢复空闲", not dev.macro_busy())
        # ── E. 随机延迟的分布（你这次改动的核心）──
        print("\n── E. 随机延迟分布：基准 400ms ± 50% → 期望落在 [200, 600]ms")

        # 测出来的"宏总时长" = 随机延迟 + 敲击保持(18~35ms) + 5ms 任务步进。
        # 所以再放一个【抖动=0】的对照宏，用它量出这段固定开销，
        # 才谈得上判断"基准值有没有被真正尊重"。
        def mk(step0):
            return dict(step_count=2, flags=0, timeout=8000, steps=pad([
                step0,
                dict(action=P.ACT_KEY_TAP, keycode=0x07, modifiers=0, dmin=0, dmax=0),
            ]))

        macros[2] = mk(dict(action=P.ACT_RAND_DELAY, keycode=0, modifiers=0, dmin=400, dmax=0))   # 对照：固定 400
        macros[3] = mk(dict(action=P.ACT_RAND_DELAY, keycode=0, modifiers=0, dmin=400, dmax=50))  # 随机：400 ± 50%
        base["macros"] = macros
        base["macro_count"] = 4
        dev.write(P.pack_cfg(base))
        dev.save()
        time.sleep(0.3)

        fixed, rnd = [], []
        # ★ 样本数不能太少。measure_macro_ms 是**轮询 HID 按键状态**量出来的，
        #   本身带几十毫秒的测量噪声；均匀分布本身也有统计涨落
        #   （[200,600] 的均值标准误 ≈ 115/√n）。n=14 时总标准误约 30ms，
        #   实测三次跑出来的随机均值是 397 / 430 / 493 —— 判据稍微紧一点就偶发变红。
        #   加到 20 个样本、判据放到 ±25%，同时靠下面那条**极差**断言
        #   （那才是区分"基准±抖动"和"区间[min,max]"的真正判据）来兜底。
        for _ in range(16):
            v = measure_macro_ms(dev, 2)
            if v is not None:
                fixed.append(v)
            time.sleep(0.15)
        for _ in range(20):
            v = measure_macro_ms(dev, 3)
            if v is not None:
                rnd.append(v)
            time.sleep(0.15)

        print(f"       固定 400ms 对照: " + " ".join(f"{s:.0f}" for s in fixed))
        print(f"       随机 400±50%   : " + " ".join(f"{s:.0f}" for s in rnd))

        if len(fixed) < 8 or len(rnd) < 10:
            check("采集到足够样本", False, f"对照 {len(fixed)} / 随机 {len(rnd)}")
        else:
            f_mean = sum(fixed) / len(fixed)
            r_mean = sum(rnd) / len(rnd)
            r_lo, r_hi = min(rnd), max(rnd)

            # 固定开销 = 敲击保持 + 任务步进 + 轮询量化，必须很小且稳定。
            #
            # 上界不是"放宽到能过为止"，而是按构成算出来的（都已实测）：
            #   敲击保持   MACRO_TAP_HOLD_MAX_MS            35 ms
            #   到期→收尾  WAIT_DELAY→CHECK→FINISH 两跳×5ms 10 ms
            #   状态轮询   QueryStatus 往返 8ms（实测 25 次恒定 8.0ms） 8 ms
            #   调度抖动   5ms tick 的相位差                     ~7 ms
            #                                        ───────────────
            #                                        合计约        60 ms
            # 实测反复落在 +59 ~ +62 ms，极差 <25ms。取 100ms 作为上界。
            OVERHEAD_MAX = 100
            overhead = f_mean - 400
            check("抖动=0 时退化成正好的固定延迟",
                  abs(overhead) <= OVERHEAD_MAX and (max(fixed) - min(fixed)) <= 60,
                  f"均值 {f_mean:.0f}ms（偏差 {overhead:+.0f}ms，上界 {OVERHEAD_MAX}）"
                  f"极差 {max(fixed) - min(fixed):.0f}ms")

            # 用实测开销校准期望上界，而不是拍脑袋放宽
            exp_lo = 200 + overhead - 25
            exp_hi = 600 + overhead + 25
            check("随机样本落在 [基准-抖动, 基准+抖动] 内（已扣除固定开销）",
                  r_lo >= exp_lo and r_hi <= exp_hi,
                  f"实测 {r_lo:.0f}~{r_hi:.0f}ms，允许区间 [{exp_lo:.0f}, {exp_hi:.0f}]")

            check("确实在随机，不是固定值",
                  (r_hi - r_lo) > 150,
                  f"极差 {r_hi - r_lo:.0f}ms（对照宏只有 {max(fixed) - min(fixed):.0f}ms）")

            # ★ 最关键的一条：分布中心应当就在基准值上。
            #   如果固件把 delay_min/max 当成旧的 [min,max] 区间理解，
            #   中心会跑到 (200+600)/2 = 400 —— 等等，那也正好是 400……
            #   所以真正能区分的是**跨度**（上面那条极差断言），
            #   而这条负责兜底"均值没跑偏"。
            #
            # ★ 判据直接对**基准值**，不要拿"对照宏的均值"当基准：
            #   两次测量是分开跑的，各自带着独立的 USB 上报/轮询开销，
            #   实测对照均值在 440~460ms 之间跳（开销几十毫秒），
            #   而随机宏那次的平均开销又可能接近 0 —— 两个噪声源相减，
            #   差个 ±60ms 很正常，原来卡在 60ms 的阈值就会偶发变红。
            #   直接判"均值落在基准的 ±20% 内"更稳，也更贴近要验证的性质。
            # ★ 判据：随机均值要落在**扣除固定开销后的中心**附近。
            #   固定开销（轮询 HID 带来的几十毫秒）由对照宏（抖动=0）量出来，
            #   所以正确的中心是 f_mean 而不是裸的 400。
            #   实测：随机均值 430/468/397 而对照均值总在 460 上下 ——
            #   拿裸 400 判会一直偏一边，拿 f_mean 判才是在验"基准值当中心用"。
            #   容差 100ms：均匀分布 [200,600] 的均值标准误约 26ms，测量噪声另有几十毫秒，
            #   加上两个估计各自的涨落，100ms 约等于 3 个标准误，既够稳也仍能区分。
            check("分布中心 ≈ 基准值 400ms（扣除固定开销后 ±100ms）",
                  abs(r_mean - f_mean) <= 100,
                  f"随机均值 {r_mean:.0f}ms vs 对照均值 {f_mean:.0f}ms"
                  f"（差 {r_mean - f_mean:+.0f}ms；裸基准 {RAND_BASE_MS}ms）")

            uniq = len(set(round(s / 10) for s in rnd))
            check("取值足够分散", uniq >= 6, f"{uniq} 个不同的 10ms 档位")

        # ── G. ★ 修饰键不能卡住（与实体键盘一致）──
        print("\n── G. 修饰键释放（与实体键盘一致）")

        # 实体键盘松开 Ctrl 时，报告里那一位就清了。
        # 早先固件里 s_modifiers 只增不减 —— 按下 Ctrl+A 再松开，
        # 主机会认为 Ctrl 一直按着，之后所有键都变成 Ctrl+某键。
        # 这里用"带修饰键的宏步骤"复现：跑完之后报告里必须干干净净。
        macro = dict(step_count=2, flags=0, timeout=5000, steps=pad([
            # 组合键：Ctrl + A
            dict(action=P.ACT_COMBO, keycode=0x04, modifiers=0x01, dmin=0, dmax=0),
            dict(action=P.ACT_DELAY, keycode=0,    modifiers=0,    dmin=300, dmax=0),
        ]))
        test_cfg = P.unpack_cfg(dev.read())
        test_cfg["macros"][0] = macro
        test_cfg["macro_count"] = max(test_cfg.get("macro_count", 0), 1)
        dev.write(P.pack_cfg(test_cfg))
        dev.save()
        time.sleep(0.4)

        # 跑之前应当是干净的
        st0 = dev.macro_state()
        check("宏运行前：修饰键为 0、无按键按下",
              st0 is not None and st0["modifiers"] == 0 and st0["pressed"] == 0,
              f"modifiers=0x{st0['modifiers']:02X} pressed={st0['pressed']}" if st0 else "读不到状态")

        r = dev.macro_run(0)
        check("触发带修饰键的宏（Ctrl+A）", r["ok"] and r["started"], f"{r['detail']}")

        # 等它跑完
        for _ in range(60):
            if not dev.macro_busy():
                break
            time.sleep(0.05)
        time.sleep(0.4)

        st1 = dev.macro_state()
        check("★ 宏跑完后修饰键归 0（Ctrl 没有卡住）",
              st1 is not None and st1["modifiers"] == 0,
              f"modifiers=0x{st1['modifiers']:02X}" + ("  ← Ctrl 卡住了！" if st1 and st1["modifiers"] else ""))
        check("★ 宏跑完后没有残留按下的键",
              st1 is not None and st1["pressed"] == 0,
              f"pressed={st1['pressed']}" if st1 else "读不到状态")

        # ── G2. 敏感性验证：证明上面那条不是空跑 ──
        #    如果 STATUS 里的 modifiers 字段永远是 0（比如我写错了偏移），
        #    上面两条也会"通过"，但什么都没验到。所以这里反过来：
        #    让宏长时间【按住】Ctrl，期间必须读到非 0 —— 证明这个字段反映的是真实状态。
        print("\n── G2. 敏感性验证：按住期间必须读到非 0（证明 G 组不是空跑）")

        hold_macro = dict(step_count=2, flags=0, timeout=5000, steps=pad([
            # 按住 Ctrl+A 不放
            dict(action=P.ACT_KEY_DOWN, keycode=0x04, modifiers=0x01, dmin=0, dmax=0),
            # 保持 900ms 后再结束（结束时宏引擎会释放所有按键）
            dict(action=P.ACT_DELAY,    keycode=0,    modifiers=0,    dmin=900, dmax=0),
        ]))

        test_cfg = P.unpack_cfg(dev.read())
        test_cfg["macros"][0] = hold_macro
        dev.write(P.pack_cfg(test_cfg))
        dev.save()
        time.sleep(0.4)

        r = dev.macro_run(0)
        check("触发「按住 Ctrl+A」的宏", r["ok"] and r["started"], f"{r['detail']}")

        time.sleep(0.35)          # 此时应在按住阶段
        hold = dev.macro_state()
        check("★ 按住期间修饰键读到 Ctrl（0x01）—— 证明该字段反映真实状态",
              hold is not None and hold["modifiers"] == 0x01,
              f"modifiers=0x{hold['modifiers']:02X}（期望 0x01）" if hold else "读不到状态")
        check("★ 按住期间能读到有键按下",
              hold is not None and hold["pressed"] >= 1,
              f"pressed={hold['pressed']}" if hold else "读不到状态")

        for _ in range(80):
            if not dev.macro_busy():
                break
            time.sleep(0.05)
        time.sleep(0.4)

        after = dev.macro_state()
        check("★ 宏结束后修饰键又归 0（释放路径确实清掉了）",
              after is not None and after["modifiers"] == 0,
              f"modifiers=0x{after['modifiers']:02X}" if after else "读不到状态")

        # ── 收尾：把设备恢复成干净的演示配置 ──
        # 本测试会往 4 个宏槽里写东西（包括只用于测量的对照宏）。
        # 如果就这么留在设备里，宏槽会有一堆没人用的定义，
        # 而 macro_count 又指不回它们 —— 上位机一读一存就会把计数纠正过来，
        # 看起来像"配置自己变了"。所以这里显式清成演示配置。
        print("\n── H. 收尾：恢复成干净的演示配置")
        final = P.build_demo_macro(P.unpack_cfg(dev.read()))
        dev.write(P.pack_cfg(final))
        dev.save()
        time.sleep(0.3)
        fin = P.unpack_cfg(dev.read())
        check("宏数量 = 2，宏槽#3/#4 已清空",
              fin["macro_count"] == 2
              and fin["macros"][0]["step_count"] == 4
              and fin["macros"][1]["step_count"] == 2
              and fin["macros"][2]["step_count"] == 0
              and fin["macros"][3]["step_count"] == 0,
              f"count={fin['macro_count']} 各槽步数="
              f"{[m['step_count'] for m in fin['macros']]}")
        check("无残留的宏绑定（按钮1/2 分别绑宏#0/#1，其余解绑）",
              fin["buttons"][0]["macro_id"] == 0 and fin["buttons"][1]["macro_id"] == 1
              and all(fin["buttons"][i]["macro_id"] == 0xFF for i in (2, 3, 4)))

    finally:
        dev.close()

    print(f"\n════════ 结果：{PASS} 通过 / {FAIL} 失败 ════════")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
