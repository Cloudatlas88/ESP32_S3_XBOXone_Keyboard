/**
 * @file kb_config.c
 * @brief 配置结构的默认值 / 封包（CRC）/ 校验
 */
#include <string.h>

#include "kb_config.h"
#include "crc16.h"

/**
 * 结构体 CRC 的覆盖范围：从 crc16 字段【之后】开始。
 *
 * ⚠️ 这里踩过坑：写成 `(uint8_t *)cfg + sizeof(cfg->crc16)` 是错的 ——
 *    sizeof(cfg->crc16) 得到的是「字段长度 2」，而 crc16 字段实际在偏移 6，
 *    结果 CRC 把自己也算进去了，导致校验永远不可能通过。
 *    正确起点是结构体头部 8 字节（4 个 uint16）之后。
 */
#define KB_CFG_CRC_OFFSET  8

void kb_config_seal(kb_config_t *cfg)
{
    cfg->magic   = KB_CFG_MAGIC;
    cfg->version = KB_CFG_VERSION;
    cfg->size    = sizeof(kb_config_t);
    cfg->crc16   = 0;
    cfg->crc16 = crc16_ccitt((const uint8_t *)cfg + KB_CFG_CRC_OFFSET,
                             sizeof(*cfg) - KB_CFG_CRC_OFFSET);
}

const char *kb_config_validate_reason(const kb_config_t *cfg)
{
    if (cfg->magic   != KB_CFG_MAGIC)        return "magic 不符";
    if (cfg->version != KB_CFG_VERSION)      return "版本不符（上位机需同步升级）";
    if (cfg->size    != sizeof(kb_config_t)) return "结构体大小不符";

    /* 两个摇杆各自检查（0x0004 起摇杆各有独立死区）*/
    for (int s = 0; s < STICK_COUNT; s++) {
        if (cfg->stick[s].deadzone_pct > STICK_DEADZONE_MAX) return "死区超出 0~60%";
    }

    if (cfg->macro_count > MACRO_MAX)        return "宏数量超限";
    for (int i = 0; i < cfg->macro_count; i++) {
        if (cfg->macros[i].step_count > MACRO_STEP_MAX) return "宏步数超限";
    }

    /* 宏总开关按键 / 上电默认值：这两个字段已废弃（保留字节），
       固件不再读取，所以也【不做校验】—— 老配置里存着旧值也能照常加载。 */

    const uint16_t calc = crc16_ccitt((const uint8_t *)cfg + KB_CFG_CRC_OFFSET,
                                      sizeof(*cfg) - KB_CFG_CRC_OFFSET);
    if (calc != cfg->crc16)                  return "CRC 不符";

    return NULL;    /* NULL = 通过 */
}

bool kb_config_validate(const kb_config_t *cfg)
{
    return kb_config_validate_reason(cfg) == NULL;
}

void kb_config_default(kb_config_t *cfg)
{
    memset(cfg, 0, sizeof(*cfg));

    /* ── 摇杆轴向行为：两个摇杆各自独立 ── */
    for (int s = 0; s < STICK_COUNT; s++) {
        cfg->stick[s].deadzone_pct = STICK_DEADZONE_PCT;
        cfg->stick[s].invert_x     = 0;
        cfg->stick[s].invert_y     = 0;
    }

    /* ── 24 个槽位的默认键映射 ──
     * 面键用同名字母；肩键/菜单键取键盘上顺手的位置；
     * ★ 两个摇杆若都用方向键会**互相打架**（同一个键被两处映射，
     *   一边按下另一边就被覆盖），所以左摇杆用 WASD、右摇杆用 IJKL。
     * Xbox / Share / L3 / R3 默认不映射（Windows 上 Xbox 键被系统占用，
     * 硬映射不一定生效，留着让用户自己决定）。
     * ★ LT / RT 也默认不映射 —— 扳机该映射成哪个键完全取决于游戏，
     *   猜一个默认值只会让人莫名其妙地按出字来。
     */
    static const uint8_t defkey[SLOT_COUNT] = {
        /*  0- 3  A    B    X    Y      */ 0x04, 0x05, 0x1B, 0x1C,
        /*  4- 5  LB   RB               */ 0x14, 0x08,             /* Q, E */
        /*  6- 9  View Menu Xbox Share */ 0x2B, 0x29, 0x00, 0x00, /* Tab, Esc, —, — */
        /* 10-11  L3   R3               */ 0x00, 0x00,
        /* 12-15  十字键 ↑ ↓ ← →        */ 0x52, 0x51, 0x50, 0x4F,
        /* 16-19  左摇杆 ↑ ↓ ← →        */ 0x1A, 0x16, 0x04, 0x07, /* W, S, A, D */
        /* 20-23  右摇杆 ↑ ↓ ← →        */ 0x17, 0x0D, 0x0C, 0x0E, /* I, J, K, L */
        /* 24-25  LT   RT               */ 0x00, 0x00,             /* 不映射，由用户自己定 */
    };

    for (int i = 0; i < SLOT_COUNT; i++) {
        cfg->buttons[i].keycode   = defkey[i];
        cfg->buttons[i].modifiers = 0;
        cfg->buttons[i].trigger   = 0;         /* 已废弃字段（原"触发方式"），占位 */
        /* 没有默认键的槽位直接标成"未启用"，免得它悄悄发一个 0x00 出去 */
        cfg->buttons[i].flags     = defkey[i] != 0 ? KB_BTNFLAG_ENABLED : 0;
        cfg->buttons[i].macro_id  = 0xFF;      /* 0xFF = 不使用宏 */
    }

    cfg->macro_count = 0;                       /* 默认没有宏 */

    /* reserved2 是保留字节（原宏总开关/上电默认，功能已移除）。写 0 即可。 */
    memset(cfg->reserved2, 0, sizeof(cfg->reserved2));

    kb_config_seal(cfg);
}