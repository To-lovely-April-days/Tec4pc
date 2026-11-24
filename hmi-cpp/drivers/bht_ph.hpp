// BHT-DM100 数字高温 pH 电极一体式变送器（电极 + 温补 + 变送器一体，Modbus-RTU 直接出 pH）。
// 从 C# 的 BhtPhClient 搬过来，只有协议层；会话里「写哪个寄存器、写什么值」的那几样（标定命令、
// 恢复出厂、重启、标准液、零电位、通讯参数）也收成了具名函数，轮询 / 读回核对 / 等结果码的流程不在这里。
//
// 现场（2026-11-09 用户用串口工具实测）：工位 A 在 COM12、工位 B 在 COM13，一个工位一支、各占一条串口；
// **出厂地址是 2**（说明书缺省值一栏；例子里却写着 01——连不上时两个都试）、9600、8N1（寄存器 25 数据格式 = 1）。
// COM12 那支回帧：温度 15.8 ℃、pH 4.62、电位 136.6 mV、温度状态 0（每差 1 pH 约 59 mV，136.6 mV ≈ 7 以下 2.3 个单位，对得上）。
//
// 寄存器（说明书「ModbusRTU 通讯协议」表，地址十进制，全走 FC03 读 / FC06 写）：
//   0 温度 ×0.1 ℃、1 pH ×0.01、2 手动温度 ×0.1 ℃、3 电位 ×0.1 mV（有符号）、5 温度状态（0 正常 / 1 过高过低 / 2 未接传感器）、
//   8 地址、9 波特率、10 参数恢复（写 1996）、11 复位（写 1524）、
//   13 标定命令（1 零电位 / 2 斜率低点 / 3 斜率高点，读回标定结果码）、14 零电位 ×0.01 mV、15 斜率 %、20 标准液类型、25 数据格式。
#pragma once
#include "../io/modbus_rtu.hpp"
#include <string>

namespace tec {

// 一拍实时量（寄存器 0~5）
struct BhtLive {
    double temp_c = 0;         // 电极温度
    double ph = 0;
    double manual_temp_c = 0;  // 温度传感器没接时变送器按这个温度做温补
    double mv = 0;             // 电极电位，有符号
    int temp_status = 0;       // 0 正常 / 1 过高过低（温补不可信）/ 2 未接传感器（电极可能没插好，上层按 Bad 发）
};
// 标定区（14 零电位、15 斜率、20 标准液）
struct BhtCalibration {
    double zero_mv = 0;
    double slope_percent = 0;  // 新电极接近 100 %，低于 70 % 标定会被拒——该换电极了
    int buffer_set = 0;        // 0 = 4.00 / 7.00 / 10.00，1 = 4.00 / 6.86 / 9.18
};
// 通讯区（8 地址、9 波特率、25 数据格式）
struct BhtComm {
    int address = 0;
    int baud = 0;
    int frame = 0;             // 0 8N2 / 1 8N1 / 2 8E1 / 3 8O1
};

// 标定哪一点；数值就是写进寄存器 13 的命令字
enum class BhtCalPoint : uint16_t {
    Zero = 1,       // 零电位：电极泡在 pH 7.00（或 6.86）缓冲液里且读数稳定
    SlopeLow = 2,   // 斜率低点：泡在 4.00
    SlopeHigh = 3   // 斜率高点：泡在 10.00（或 9.18）
};

class BhtPh {
public:
    static constexpr uint16_t kRegTemp = 0, kRegPh = 1, kRegManualTemp = 2, kRegMv = 3, kRegTempStatus = 5;
    static constexpr uint16_t kRegAddress = 8, kRegBaud = 9, kRegRestore = 10, kRegReboot = 11, kRegCalibrate = 13;
    static constexpr uint16_t kRegZero = 14, kRegSlope = 15, kRegBuffer = 20, kRegFrame = 25;
    static constexpr uint16_t kRestoreMagic = 1996, kRebootMagic = 1524;
    static constexpr int kDefaultAddress = 2;      // 说明书缺省值一栏写的 2（例子里用的是 1）
    static constexpr int kDefaultBaud = 9600;
    static constexpr double kCalTempMinC = 5, kCalTempMaxC = 50;   // 变送器只允许在这个温度范围标定
    static constexpr double kZeroLimitMv = 59.16;                  // 零电位调整的范围 ±59.16 mV

    explicit BhtPh(ModbusRtu& bus) : bus_(bus) {}
    ModbusRtu& bus() { return bus_; }

    // 每拍读这个：寄存器 0~5 一问读完（FC03）
    BhtLive read_live();
    // 标定区，两问（14~15、20）
    BhtCalibration read_calibration();
    // 通讯区，两问（8~9、25）
    BhtComm read_comm();

    // 原样写一个寄存器（FC06）。下面的具名函数都走它
    void write(uint16_t reg, uint16_t value);

    // 发标定命令（寄存器 13 写 1 / 2 / 3）。它要多久把标定做完说明书没写：上层是每半秒读一次
    // read_calibration_result()、最多 3 s，读回不再是刚写的命令值才算结果到了；一直是命令值就老实说没回，
    // 拿零电位 / 斜率的前后变化说话，不编一个「成功」。发之前上层先看温度在不在 5~50 ℃
    void start_calibration(BhtCalPoint point);
    // 读标定结果码（说明书「校准反馈错误代码列表」——从标定命令寄存器 13 读回）
    int read_calibration_result();

    // 标准液类型（寄存器 20）：0 = 4.00 / 7.00 / 10.00，1 = 4.00 / 6.86 / 9.18。标定前先选对
    void set_buffer_set(int buffer_set);
    // 零电位调整（寄存器 14，×0.01 mV 有符号）；超出 ±59.16 mV 的按边界写。零电位标定成功时它自动更新，一般不用手改
    void set_zero_mv(double mv);
    // 从机地址（寄存器 8）；超出 1~247 的按边界写。写完变送器立刻只按新地址应答——按旧地址读不回来，
    // 这个 BhtPh 底下的 ModbusRtu 站号也就不对了，调用方要按新地址重建
    void set_address(int address);
    // 波特率（寄存器 9）：只认 4800 / 9600 / 19200，别的抛 std::invalid_argument。写完串口要跟着改
    void set_baud(int baud);
    // 数据格式（寄存器 25）：0 8N2 / 1 8N1 / 2 8E1 / 3 8O1，别的抛 std::invalid_argument。
    // 本程序只按 8N1 开口子——改成别的就连不上了，要改回去只能用串口工具按新格式写寄存器 25 = 1
    void set_frame(int frame);
    // 恢复出厂参数（寄存器 10 写 1996）：地址回 2、波特率回 9600、标定清掉，电极要重新标定
    void restore_factory();
    // 重启变送器（寄存器 11 写 1524），几秒后恢复应答
    void reboot();

    static std::string temp_status_text(int status);
    static std::string buffer_text(int buffer_set);
    static std::string frame_text(int frame);
    // 标定结果码 → 人话（说明书表）；0 = 成功
    static std::string calibration_result_text(int code);

private:
    ModbusRtu& bus_;
};

}  // namespace tec
