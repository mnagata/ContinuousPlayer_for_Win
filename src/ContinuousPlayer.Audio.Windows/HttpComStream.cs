using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ContinuousPlayer.Core;

namespace ContinuousPlayer.Audio.Windows;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class HttpComStream(HttpAudioStream source) : IStream
{
    private readonly object gate = new();
    private long position;
    public void Read(byte[] buffer, int count, nint read)
    {
        lock (gate)
        {
            var total = 0;
            while (total < count)
            {
                var n = source.ReadAt(position, buffer.AsSpan(total, count - total));
                if (n == 0) break;
                position += n; total += n;
            }
            if (read != 0) Marshal.WriteInt32(read, total);
        }
    }
    public void Seek(long offset, int origin, nint result)
    {
        lock (gate)
        {
            var next = checked((origin == 0 ? 0 : origin == 1 ? position : origin == 2 ? source.Length : throw new ArgumentOutOfRangeException(nameof(origin))) + offset);
            if (next < 0) throw new IOException("負のシーク位置です。");
            position = next; if (result != 0) Marshal.WriteInt64(result, position);
        }
    }
    public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = source.Length, grfMode = 0 };
    public void Clone(out IStream clone) { lock (gate) clone = new HttpComStream(source) { position = position }; }
    public void Commit(int flags) { }
    public void Write(byte[] buffer, int count, nint written) => throw new UnauthorizedAccessException();
    public void SetSize(long size) => throw new UnauthorizedAccessException();
    public void CopyTo(IStream stream, long count, nint read, nint written) => throw new NotSupportedException();
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
}
