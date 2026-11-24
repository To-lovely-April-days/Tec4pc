# 触摸屏程序（C++ 重写）。两个程序：
#   tec_core  控制核心：连硬件、控温、跑序列、记数据、对外开 TCP。没有界面
#   tec_hmi   界面：LVGL 画，经本机 TCP 连控制核心（和 PC 工作站同一套协议）
# 界面崩了重启界面就行，控温不受影响——所以拆成两个程序。
cmake_minimum_required(VERSION 3.20)
project(tec_hmi_cpp LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(MSVC)
  add_compile_options(/W4 /utf-8)      # 源码里有中文注释和中文字符串，按 UTF-8 读
  # NOMINMAX：Windows 的头文件会把 min / max 定义成宏，把 std::min / std::max 弄坏（LVGL 的窗口驱动会把 windows.h 带进来）
  # _CRT_SECURE_NO_WARNINGS：不要为 fopen、getenv 这些标准函数报「不安全」的警告
  add_compile_definitions(NOMINMAX _CRT_SECURE_NO_WARNINGS)
else()
  add_compile_options(-Wall -Wextra)
endif()

# 核心库：不碰硬件、不碰界面的纯逻辑，在哪个系统上都能编、都能测
add_library(tec_core_lib STATIC
  core/control/outer_loop.cpp
  core/control/gain_table.cpp
  core/control/inner_loop.cpp
  core/control/heat_source_switch.cpp
  core/station/channel_controller.cpp
  core/station/reactor_hardware.cpp
  core/station/station.cpp
  core/sequence/sequence.cpp
  core/station/aux_runner.cpp
  core/station/aux_hardware.cpp
  core/data/run_recorder.cpp
  core/data/store.cpp
  core/data/drives.cpp
  core/sim/sim_hardware.cpp
  core/app/core_app.cpp
  net/tcp_server.cpp
  core/app/real_devices.cpp
  io/modbus_rtu.cpp
  drivers/rd105.cpp
  drivers/io8r.cpp
  drivers/chiller.cpp
  drivers/yudian.cpp
  drivers/bht_ph.cpp
  drivers/aqmd.cpp
  drivers/feed.cpp
)
target_include_directories(tec_core_lib PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
if(WIN32)
  target_sources(tec_core_lib PRIVATE io/win_serial.cpp)   # 真串口只有 Windows 版
  target_link_libraries(tec_core_lib PUBLIC ws2_32)
endif()

# 控制核心主程序
add_executable(tec_core apps/tec_core/main.cpp)
# 出厂增益表拷到程序旁边：PC 工作站启动本机控制核心时，第一次从这里拷一份到数据目录里用
add_custom_command(TARGET tec_core POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy ${CMAKE_CURRENT_SOURCE_DIR}/testdata/gain-table-run1110.csv $<TARGET_FILE_DIR:tec_core>/gain-table.csv)
target_link_libraries(tec_core PRIVATE tec_core_lib)

# 界面程序。LVGL 整个源码目录编成一个库（用到哪些部件由 ui/lv_conf.h 定）；
# 页面代码 ui/gen/pages_1.inc 是从设计稿生成好的、和图片字体一起在仓库里，这里不用再生成。
# Windows 上会真的开窗口、收触摸；别的系统上编出来的是不开窗口、只出图的版本（开发自查用）。
set(LVGL_DIR ${CMAKE_CURRENT_SOURCE_DIR}/third_party/lvgl-9.2.2)
if(EXISTS ${LVGL_DIR}/lvgl.h)
  file(GLOB_RECURSE LVGL_SRC ${LVGL_DIR}/src/*.c)
  add_library(lvgl STATIC ${LVGL_SRC})
  target_compile_definitions(lvgl PUBLIC LV_CONF_INCLUDE_SIMPLE)
  target_include_directories(lvgl PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/ui ${LVGL_DIR})
  if(MSVC)
    target_compile_options(lvgl PRIVATE /W0)
  else()
    target_compile_options(lvgl PRIVATE -w)
  endif()
  # 界面程序编两份：
  #   tec_hmi      按 1280×720 画，一个像素对一个像素。触摸屏（1280×720）和普通显示器用这个
  #   tec_hmi_2x   同一套界面按 2 倍画（2560×1440），字和线都是按大尺寸现画的、不是放大出来的。4K 这类高分屏上要清楚就用它
  foreach(sc 1 2)
    if(sc EQUAL 1)
      set(t tec_hmi)
    else()
      set(t tec_hmi_2x)
    endif()
    add_executable(${t} ui/src/main.cpp ui/src/ui_kit.cpp net/tcp_server.cpp)
    target_compile_definitions(${t} PRIVATE UI_SCALE=${sc} PAGES_INC="../gen/pages_${sc}.inc")
    target_include_directories(${t} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/ui)
    target_link_libraries(${t} PRIVATE lvgl)
    if(WIN32)
      target_link_libraries(${t} PRIVATE ws2_32)
    endif()
    if(MSVC)
      target_compile_options(${t} PRIVATE /bigobj)      # 生成的页面代码很长，一个文件里的段数会超过默认上限
      # 它是带窗口的程序：不要另外弹一个黑的命令行窗口出来（入口还是普通的 main）
      target_link_options(${t} PRIVATE /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup)
    else()
      target_compile_options(${t} PRIVATE -Wno-unused-function -Wno-unused-parameter)
    endif()
  endforeach()
  # 字体和图片拷到程序旁边：程序启动时按「自己旁边的 assets」找
  add_custom_command(TARGET tec_hmi POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_CURRENT_SOURCE_DIR}/ui/assets $<TARGET_FILE_DIR:tec_hmi>/assets)
else()
  message(STATUS "没找到 third_party/lvgl-9.2.2，这次不编界面程序 tec_hmi（下载方法见 README）")
endif()

enable_testing()
add_executable(golden_outer tests/golden_outer.cpp)
target_link_libraries(golden_outer PRIVATE tec_core_lib)
file(GLOB GOLDEN_OUTER_CSV ${CMAKE_CURRENT_SOURCE_DIR}/testdata/golden_outer/*.csv)
add_test(NAME golden_outer COMMAND golden_outer ${GOLDEN_OUTER_CSV})

add_executable(golden_fast tests/golden_fast.cpp)
target_link_libraries(golden_fast PRIVATE tec_core_lib)
file(GLOB GOLDEN_FAST_CSV ${CMAKE_CURRENT_SOURCE_DIR}/testdata/golden_fast/*.csv)
add_test(NAME golden_fast COMMAND golden_fast ${CMAKE_CURRENT_SOURCE_DIR}/testdata/gain-table-run1110.csv ${GOLDEN_FAST_CSV})

add_executable(golden_inner tests/golden_inner.cpp)
target_link_libraries(golden_inner PRIVATE tec_core_lib)
file(GLOB GOLDEN_INNER_CSV ${CMAKE_CURRENT_SOURCE_DIR}/testdata/golden_inner/*.csv)
add_test(NAME golden_inner COMMAND golden_inner ${CMAKE_CURRENT_SOURCE_DIR}/testdata/gain-table-run1110.csv ${GOLDEN_INNER_CSV})

find_package(Threads REQUIRED)
target_link_libraries(tec_core_lib PUBLIC Threads::Threads)
add_executable(test_modbus tests/test_modbus.cpp)
target_link_libraries(test_modbus PRIVATE tec_core_lib)
add_test(NAME test_modbus COMMAND test_modbus)

add_executable(golden_switch tests/golden_switch.cpp)
target_link_libraries(golden_switch PRIVATE tec_core_lib)
file(GLOB GOLDEN_SWITCH_CSV ${CMAKE_CURRENT_SOURCE_DIR}/testdata/golden_switch/*.csv)
add_test(NAME golden_switch COMMAND golden_switch ${GOLDEN_SWITCH_CSV})

# 各设备协议层的测试：用假串口扮设备，核对发出去的帧和解析
foreach(t test_rd105 test_duo_devices test_aqmd test_feed test_station test_channel test_sequence test_aux test_recorder test_store)
  add_executable(${t} tests/${t}.cpp)
  target_link_libraries(${t} PRIVATE tec_core_lib)
  add_test(NAME ${t} COMMAND ${t})
endforeach()

add_executable(golden_channel tests/golden_channel.cpp)
target_link_libraries(golden_channel PRIVATE tec_core_lib)
file(GLOB GOLDEN_CHANNEL_CSV ${CMAKE_CURRENT_SOURCE_DIR}/testdata/golden_channel/*.csv)
add_test(NAME golden_channel COMMAND golden_channel ${CMAKE_CURRENT_SOURCE_DIR}/testdata/gain-table-run1110.csv ${GOLDEN_CHANNEL_CSV})

add_executable(test_core_app tests/test_core_app.cpp)
target_link_libraries(test_core_app PRIVATE tec_core_lib)
add_test(NAME test_core_app COMMAND test_core_app ${CMAKE_CURRENT_SOURCE_DIR}/testdata/gain-table-run1110.csv)
