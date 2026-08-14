using TecControl.Core.Protocol;

namespace TecControl.Core.Tests;

public class TecAsciiTests
{
    [Fact]
    public void BuildQuery_ChannelCommand() =>
        Assert.Equal("TC1:TG=?@\n", TecAscii.BuildQuery(1, TecCmd.Target));

    [Fact]
    public void BuildQuery_GeneralCommand() =>
        Assert.Equal("ERRORCODE=?@\n", TecAscii.BuildQuery(null, TecCmd.ErrorCode));

    [Fact]
    public void BuildSet_ChannelCommand() =>
        Assert.Equal("TC2:TG=2500000@\n", TecAscii.BuildSet(2, TecCmd.Target, 2500000));

    [Fact]
    public void ParseFields_SimpleOkReply()
    {
        // 协议 2.1.2 示例应答
        var fields = TecAscii.ParseFields("OKTC1:TG=2500000@\r\n");
        Assert.Equal(2500000, fields["TC1:TG"]);
    }

    [Fact]
    public void GetField_MissingField_Throws() =>
        Assert.Throws<TecProtocolException>(() =>
            TecAscii.GetField("OKTC1:TG=2500000@\r\n", 2, TecCmd.Target));

    [Fact]
    public void ParseFields_DataDemand2_DocExample()
    {
        // 协议 3.6.2 示例应答（原文）
        const string reply =
            "TC1:TCADJTEMP=2259187@TC1:RESISTOR=11139104486@TC1:PWM=0@" +
            "TC2:TCADJTEMP=999999999@TC2:RESISTOR=0@TC2:PWM=0@SINTERIORTEMP=23@";

        var fields = TecAscii.ParseFields(reply);

        Assert.Equal(2259187, fields["TC1:TCADJTEMP"]);
        Assert.Equal(11139104486, fields["TC1:RESISTOR"]);
        Assert.Equal(999999999, fields["TC2:TCADJTEMP"]);
        Assert.Equal(23, fields["SINTERIORTEMP"]);
    }

    [Fact]
    public void ParseFields_FullWidthColon_IsNormalized()
    {
        // 协议 3.6.2 DATADEMAND=2 示例中出现过全角冒号
        var fields = TecAscii.ParseFields("TC1：TCADJTEMP=2518788@TC1：OUTV=1000000000@");
        Assert.Equal(2518788, fields["TC1:TCADJTEMP"]);
        Assert.Equal(1000000000, fields["TC1:OUTV"]);
    }

    [Fact]
    public void ParseFields_InquireStyleReply_WithEmbeddedOk()
    {
        // 协议 3.6.1：INQUIRE 应答中 "OK" 混在字段之间
        var fields = TecAscii.ParseFields(
            "OKTC1:TG=2500000@TC2:TG=2500000@OKTC1:LIMITED=30@TC2:LIMITED=30@");
        Assert.Equal(2500000, fields["TC2:TG"]);
        Assert.Equal(30, fields["TC1:LIMITED"]);
    }

    [Fact]
    public void ParseFields_QueryEcho_IsIgnored()
    {
        // "=?" 不是数值，应被跳过而不是抛异常
        var fields = TecAscii.ParseFields("TC1:TG=?@");
        Assert.Empty(fields);
        Assert.Empty(TecAscii.ParseFieldsText("TC1:TG=?@"));
    }

    [Fact]
    public void GetTextField_RealDeviceModelReply_215L()
    {
        // 真机实测：型号查询返回型号名字符串而非协议表中的数字代码
        const string reply = "OKTEC=215L@\r\n";
        Assert.Equal("215L", TecAscii.GetTextField(reply, null, TecCmd.Model));
        // 数值解析器应跳过而不是抛异常
        Assert.Empty(TecAscii.ParseFields(reply));
    }

    [Fact]
    public void ParseFieldsText_MixedNumericAndText()
    {
        var fields = TecAscii.ParseFieldsText("OKTEC=215L@OKFPV=100@");
        Assert.Equal("215L", fields["TEC"]);
        Assert.Equal("100", fields["FPV"]);
    }
}
