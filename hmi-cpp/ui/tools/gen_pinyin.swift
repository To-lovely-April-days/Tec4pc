// 汉字转拼音（不带声调），用系统自带的转换。只在生成拼音字库 / 词库时用，产品里不带它。
//   不带参数：标准输入里每个字输出一行「字 拼音」（生成 ui/src/pinyin_dict.inc 用，每字一个读音）
//   --words ：标准输入里一行一个词，输出「词<TAB>拼 音」（音节之间空格；整词一起转，多音字按词来定读音）
import Foundation
let data = FileHandle.standardInput.readDataToEndOfFile()
let text = String(data: data, encoding: .utf8) ?? ""
func pinyin(_ s: String) -> String {
    let m = NSMutableString(string: s)
    CFStringTransform(m, nil, kCFStringTransformMandarinLatin, false)
    CFStringTransform(m, nil, kCFStringTransformStripDiacritics, false)
    return m as String
}
if CommandLine.arguments.contains("--words") {
    for line in text.split(separator: "\n") where !line.isEmpty { print("\(line)\t\(pinyin(String(line)))") }
} else {
    for ch in text where !ch.isNewline { print("\(ch) \(pinyin(String(ch)))") }
}
