using Avalonia.Metadata;

// 与 Tec.Hmi.Ui 里的同名声明合并：同一个「urn:tec:ctl」前缀下，
// 留在工作站的控件与搬去 HMI 类库的控件都解析得到。
[assembly: XmlnsDefinition("urn:tec:ctl", "Tec.App.Controls")]
