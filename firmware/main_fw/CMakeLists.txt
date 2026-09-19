# ============================================================
#  ESP32-S3 手柄改 USB HID 键盘 —— 正式工程
# ============================================================
cmake_minimum_required(VERSION 3.22)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

# 只编译 main 及其依赖，加快构建
idf_build_set_property(MINIMAL_BUILD ON)

# ★ 定稿版本号。
#   不设的话 ESP-IDF 会用 `git describe` 取（之前显示过 "flash-025"），
#   那是每次烧录的标签、不是产品版本。定稿必须固定住。
#   ★ 每次烧录的可回溯性由 Git 标签 flash-NNN + flash_archive/ + FLASH_LOG.md 保证，
#     不依赖这个字段。
set(PROJECT_VER "Ver001")

project(main_fw)
