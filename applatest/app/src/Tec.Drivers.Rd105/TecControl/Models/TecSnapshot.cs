namespace TecControl.Core.Models;

/// <summary>一次 DATADEMAND=2 轮询得到的全量实时数据。温度为 NaN 表示该通道未接传感器。</summary>
public sealed record TecSnapshot(
    DateTime Timestamp,
    double Temp1C,
    double Resistor1Ohms,
    double OutVolts1,
    double Temp2C,
    double Resistor2Ohms,
    double OutVolts2,
    double InternalTempC);
