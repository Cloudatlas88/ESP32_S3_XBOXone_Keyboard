"""静态检查：临界区里有没有"会阻塞"的调用。

为什么要有这个：`macro_toggle_or_trigger()` 里有一句 ESP_LOGI，
它被 `keymap_apply()` 在 `portENTER_CRITICAL()` 之内调用过。
串口写会阻塞，而 **FreeRTOS 不允许在临界区里阻塞** —— 断言失败、设备直接复位。
现象是"循环宏按第二下想停止时 HID 重新枚举"，查起来完全看不出和日志有关。

这类错误靠读代码很难发现（写日志的那行和关锁的那行常常隔着好几个文件），
所以做成自动检查：**谁能（直接或间接）阻塞，就不许在临界区里被调用。**

做法：
  1. 把每个 .c 里函数体切出来
  2. 找出"直接阻塞"的函数：函数体里出现 ESP_LOG* / printf / vTaskDelay / xQueueSend
     等会阻塞或可能触发调度的原语
  3. 在函数调用图上做传递闭包 —— 间接调到阻塞函数的也算阻塞
  4. 扫每个 portENTER_CRITICAL .. portEXIT_CRITICAL 之间的所有调用，
     只要被调函数属于"阻塞集合"，就报错

用法：python tools/check_critical_sections.py
退出码 0 = 干净，1 = 有问题。
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                    'firmware', 'main_fw', 'main')

# 会阻塞（或可能触发任务切换）的原语：绝对不能出现在临界区里
BLOCKING = re.compile(
    r'\b(ESP_LOG[EWIDV]|printf|vprintf|puts|vTaskDelay|vTaskDelayUntil|'
    r'xQueueSend|xQueueSendToBack|xQueueSendToFront|xQueueReceive|'
    r'xSemaphoreTake|xSemaphoreGive|xEventGroupWaitBits|vTaskSuspend|'
    r'heap_caps_malloc|malloc|calloc|free|fopen|nvs_set_|nvs_commit)\s*\(')

# 函数定义（够用的正则：返回类型 + 名字 + 参数表，行首不是空白或缩进有限）
FUNC_DEF = re.compile(
    r'^(?:static\s+)?(?:inline\s+)?(?:const\s+)?[A-Za-z_][\w\s\*]*?\b(\w+)\s*\([^;{]*\)\s*\{',
    re.M)

CALL = re.compile(r'\b([A-Za-z_]\w*)\s*\(')

# 调用时不算"被调函数"的关键字
KEYWORDS = {
    'if', 'for', 'while', 'switch', 'return', 'sizeof', 'typeof',
    'defined', 'static', 'inline', 'const', 'void', 'int', 'char', 'bool',
}


def strip_comments(src: str) -> str:
    src = re.sub(r'/\*.*?\*/', ' ', src, flags=re.S)
    src = re.sub(r'//[^\n]*', ' ', src)
    # 字符串字面量清掉，免得里面的括号干扰
    src = re.sub(r'"(?:\\.|[^"\\])*"', '""', src)
    return src


def body_of(src: str, open_idx: int) -> str:
    """从 '{' 的下标开始，配平花括号取函数体"""
    depth = 0
    i = open_idx
    while i < len(src):
        if src[i] == '{':
            depth += 1
        elif src[i] == '}':
            depth -= 1
            if depth == 0:
                return src[open_idx:i + 1]
        i += 1
    return src[open_idx:]


def analyse_src(src: str):
    funcs = {}          # name -> (start, body)
    for m in FUNC_DEF.finditer(src):
        name = m.group(1)
        if name in KEYWORDS:
            continue
        body = body_of(src, m.end() - 1)
        funcs.setdefault(name, (m.start(), body))
    return funcs


def check_source(sources: dict, label: str = '') -> list:
    """对一组 {文件名: 源码} 做检查，返回问题列表。

    抽成函数是为了能拿**构造出来的**源码做自检 ——
    一个从不报错的检查器等于没有，必须先证明它真能抓到那个 bug。
    """
    allfuncs = {}
    for fname, text in sources.items():
        for name, (_start, body) in analyse_text(text).items():
            allfuncs.setdefault(name, body)

    blocking = {n for n, b in allfuncs.items() if BLOCKING.search(b)}
    changed = True
    while changed:
        changed = False
        for n, b in allfuncs.items():
            if n in blocking:
                continue
            for c in set(CALL.findall(b)):
                if c in blocking and c != n:
                    blocking.add(n)
                    changed = True
                    break

    problems = []
    for fname, src in sources.items():
        for m in re.finditer(r'portENTER_CRITICAL\s*\(', src):
            end = src.find('portEXIT_CRITICAL', m.end())
            line = src[:m.start()].count('\n') + 1
            if end < 0:
                problems.append((fname, line, 'portENTER_CRITICAL 没有对应的 EXIT'))
                continue
            seg = src[m.end():end]

            # ★ ① 锁里**直接**用了阻塞原语（ESP_LOG* / xQueueSend / vTaskDelay …）
            #   这一步不能靠"被调函数在不在 blocking 集合里"来判断 ——
            #   ESP_LOGI 是宏、xQueueSend 是 FreeRTOS 库函数，
            #   它们根本不在我们自己的函数表里，所以永远进不了 blocking 集合。
            #   第一版漏了这一步，结果"锁里直接打日志"这种最直白的写法都不报 ——
            #   是自检把它抓出来的。
            for prim in sorted(set(BLOCKING.findall(seg))):
                problems.append((fname, line, f'临界区里直接调用了会阻塞的 {prim}()'))

            # ★ ② 锁里调用了**本地函数**，而它（直接或间接）会阻塞
            for c in sorted(set(CALL.findall(seg))):
                if c in blocking:
                    problems.append((fname, line, f'临界区里调用了会阻塞的 {c}()'))
    return problems


def analyse_text(text: str):
    return analyse_src(strip_comments(text))


def self_test() -> int:
    """自检：必须能抓到"锁里打日志"和"锁里间接调打日志的函数"两种写法。"""
    bad_direct = {
        'x.c': 'static int mux;\n'
               'void f(void)\n{\n'
               '    portENTER_CRITICAL(&mux);\n'
               '    ESP_LOGI("t", "boom");\n'
               '    portEXIT_CRITICAL(&mux);\n}\n'
    }
    # ★ 就是真实踩到的那个形状：锁里调的 helper 自己打了日志（跨函数）
    bad_indirect = {
        'x.c': 'static int mux;\n'
               'int helper(int id)\n{\n'
               '    ESP_LOGI("t", "stopping %d", id);\n'
               '    return id;\n}\n'
               'void f(void)\n{\n'
               '    portENTER_CRITICAL(&mux);\n'
               '    helper(3);\n'
               '    portEXIT_CRITICAL(&mux);\n}\n'
    }
    # 锁里同时还操作队列 —— 也该报
    bad_queue = {
        'x.c': 'static int mux;\n'
               'void f(void)\n{\n'
               '    portENTER_CRITICAL(&mux);\n'
               '    xQueueSend(q, &v, 0);\n'
               '    portEXIT_CRITICAL(&mux);\n}\n'
    }
    good = {
        'x.c': 'static int mux;\n'
               'int helper(int id)\n{\n'
               '    ESP_LOGI("t", "stopping %d", id);\n'
               '    return id;\n}\n'
               'void f(void)\n{\n'
               '    int snapshot;\n'
               '    portENTER_CRITICAL(&mux);\n'
               '    snapshot = g_state;\n'
               '    g_state = 0;\n'
               '    portEXIT_CRITICAL(&mux);\n'
               '    helper(snapshot);\n'
               '}\n'
    }

    print('── 自检：检查器本身是不是真的能抓到东西 ──')
    cases = [
        ('锁里直接打日志', bad_direct, True),
        ('★ 锁里间接调用打日志的函数（真实踩到的形状）', bad_indirect, True),
        ('锁里操作队列', bad_queue, True),
        ('只在锁里读内存、日志放在锁外（应放行）', good, False),
    ]
    fails = 0
    for name, src, want_problem in cases:
        got = check_source(src)
        ok = bool(got) == want_problem
        if not ok:
            fails += 1
        detail = got[0][2] if got else '未报问题'
        print(f'  [{"PASS" if ok else "FAIL"}] {name}  —— {detail}')
    print()
    return fails


def main() -> int:
    fails = self_test()
    if fails:
        print(f'  !! 自检失败 {fails} 项 —— 检查器本身不可信，先修它')
        return 1

    files = sorted(f for f in os.listdir(ROOT) if f.endswith('.c'))
    if not files:
        print(f'!! {ROOT} 下没有 .c 文件')
        return 1

    sources = {f: strip_comments(open(os.path.join(ROOT, f),
                                       encoding='utf-8', errors='replace').read())
               for f in files}
    problems = check_source(sources)

    nfuncs = sum(len(analyse_text(t)) for t in sources.values())

    print('═' * 62)
    print('  临界区静态检查（锁里不许有会阻塞的调用）')
    print('═' * 62)
    print(f'  扫描 {len(files)} 个 .c 文件，{nfuncs} 个函数')

    if problems:
        for f, line, msg in problems:
            print(f'  [FAIL] {f}:{line}  {msg}')
        print(f'\n  结果：{len(problems)} 处问题')
        return 1

    print('  [PASS] 所有临界区里都只有内存操作，没有任何会阻塞的调用')
    print('\n  结果：0 处问题')
    return 0


if __name__ == '__main__':
    sys.exit(main())
