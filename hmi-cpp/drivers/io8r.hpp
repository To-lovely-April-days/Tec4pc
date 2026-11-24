// 艾莫迅 JY-MODBUS-IO8R：8 路继电器 DO（常开触点，每点 2A、4 点合计 8A）+ 8 路光耦 DI。
// 从 C# 的 Io8rClient 搬过来，只有协议层。
// 在双工位反应主机里它管热源切换：DO 把 RD105 的输出通路从 TEC 切到电加热
// （断开 = TEC，闭合 = 电加热——断电 / 总线复位时继电器落回断开位，落回的必须是安全侧），
// 另有一路 DO 接冷却水阀；DI 接电加热接触器的辅助触点做切换反馈（现场接了才有）。
//
// 一个部署前提在代码里做不了、只能写在部署清单里（需求 §7）：
// 模块的「总线错误模式」出厂默认是【保持】——断线时继电器停在原位，电加热会一直烧。
// 必须用厂家配置工具改成【复位】。按过模块上的 Reset 键会恢复出厂参数，改完要复查。
//
// 帧格式固定 8N1 不可改；波特率 / 站号看模块上的拨码（出厂 9600 / 1 号）。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <array>

namespace tec {

class Io8r {
public:
    static constexpr int kPoints = 8;
    using Points = std::array<bool, kPoints>;

    explicit Io8r(ModbusRtu& bus) : bus_(bus) {}

    // 底下的 Modbus 主站——重试 / 帧间空当这些链路参数调它（探测时把重试关掉，不然每个不通的口都要白等几倍超时）
    ModbusRtu& bus() { return bus_; }

    // 读回 8 路继电器的当前位置（FC 01）。跟自己发过的命令核对用——
    // 模块面板上有按键，人手也能扳继电器，读回来对不上要报出来
    Points read_relays();

    // 读 8 路光耦 DI（FC 02）
    Points read_inputs();

    // 切一路继电器（FC 05）。closed = true 吸合。index 不在 0~7 抛 std::out_of_range
    void set_relay(int index, bool closed);

    // 八路按给定位置一帧写完（FC 0F）。开机复位 / 走人时冷水机还没停稳，
    // 要「别的全断、冷却水阀留着」就用它——冷水机还在转就关阀会憋泵
    // ★ 下面两个「八路一起写」的操作在这台产品里不许用：DO2 / DO3 是使能继电器，用户定的是程序绝不去关它们。
    //   控温时只用 set_relay 一路一路写（加热棒 DO0 / DO1、TEC 功率 DO7 / DO6、冷却水阀 DO4）。留着它们只为协议完整和以后别的机型。
    void write_relays(const Points& closed);

    // 八路全断，一帧完成（FC 0F）。开机复位与走人都用它——
    // 全断 = 断电落回的位置（加热棒与 TEC 功率线都不带电），
    // 且一帧比八帧少七次「中途断线切了一半」的机会
    void all_off();

private:
    ModbusRtu& bus_;
};

}  // namespace tec
