namespace TecControl.Core.Comm;

/// <summary>串口的最小抽象，便于单元测试替换。</summary>
public interface ISerialTransport : IDisposable
{
    bool IsOpen { get; }

    void Open();
    void Close();

    /// <summary>清空接收缓冲区（发送新指令前丢弃残留数据）。</summary>
    void DiscardInput();

    void Write(byte[] buffer, int offset, int count);

    /// <summary>读取数据；在 timeoutMs 内无数据则返回 0。</summary>
    int Read(byte[] buffer, int offset, int count, int timeoutMs);
}
