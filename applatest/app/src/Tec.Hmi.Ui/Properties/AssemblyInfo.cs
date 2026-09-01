using Avalonia.Metadata;

// 「urn:tec:ctl」把两个程序集的控件收进同一个 XAML 前缀：
// TecIcon/SvgArt/Hmi* 搬进本类库之后，工作站的十几个视图不用逐个改
// ctl: 引用——xmlns 指到这个 URI，两边的类型都在。
[assembly: XmlnsDefinition("urn:tec:ctl", "Tec.Hmi.Ui.Controls")]
