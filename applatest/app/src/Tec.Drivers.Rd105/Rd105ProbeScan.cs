namespace Tec.Drivers.Rd105;

/// <summary>
/// 「连接」时配置的协议 / 波特率没应答，就换着试一遍再回话——现场站在机器前的人
/// 最需要的不是「无应答」三个字，而是「换成什么就通了」。
/// 只试协议 §1 出厂那两档波特率和两种格式，别的档是现场自己改过的，那就该现场自己知道。
/// </summary>
public static class Rd105ProbeScan
{
    /// <param name="make">按（协议, 波特率）开一条链路；返回链路和它的归还句柄（真串口是同一个对象，
    /// 双工位主机那边是整套 DuoLinks）。串口必须已经让出来——调用方先把配置那条收掉再来。</param>
    /// <param name="protoLabel">连接表单里那个字段叫什么，话里指着它说。</param>
    public static async Task<string> RunAsync(Func<string, int, (Rd105Link Link, IDisposable Owner)> make,
                                              string? protocol, int baud, int station, string protoLabel,
                                              CancellationToken ct)
    {
        var tried = new List<string>();
        foreach (var (p, b) in Rd105Protocol.Alternatives(protocol, baud))
        {
            var label = $"{Rd105Protocol.Short(p, station)} @ {b}";
            IDisposable? owner = null;
            try
            {
                var (link, o) = make(p, b);
                owner = o;
                link.Open();
                var (model, _, _) = await link.Controller.ReadDeviceInfoAsync(ct).ConfigureAwait(false);
                return $"换着试了一遍：{label} 有应答（型号 {model}）——把「{protoLabel}」改成「{p}」、波特率改成 {b} 再点「连接」";
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { tried.Add(label); }
            catch (Exception ex) { tried.Add($"{label}（{ex.Message}）"); }
            finally { owner?.Dispose(); }
        }
        return $"换着试了 {string.Join("、", tried)} 也都没应答——多半不是接 RD105 的那一路串口，或者接线有问题";
    }
}
