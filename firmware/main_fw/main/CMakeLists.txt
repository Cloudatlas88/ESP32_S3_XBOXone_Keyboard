idf_component_register(
    SRCS
        "main.c"
        "usb_descriptors.c"
        "input_scan.c"
        "hid_keyboard.c"
        "hid_config.c"
        "keymap.c"
        "macro_engine.c"
        "nvs_config.c"
        "kb_config.c"
        "crc16.c"
    INCLUDE_DIRS "."
    # ESP-IDF v6.1 严格校验组件依赖：用到的每个组件都必须显式列出
    PRIV_REQUIRES
        esp_driver_gpio
        esp_adc
        esp_timer
        nvs_flash
        esp_hw_support
    )
