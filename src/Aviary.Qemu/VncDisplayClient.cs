using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Aviary.Core;
namespace Aviary.Qemu;
// Deliberately small RFB 3.8 client: raw rectangles, desktop resizing, absolute pointer and key events.
public sealed class VncDisplayClient : IAsyncDisposable
{
    readonly TcpClient socket = new(); readonly SemaphoreSlim sendGate = new(1); readonly CancellationTokenSource lifetime = new();
    NetworkStream? stream; Task? loop; byte[] pixels = []; int width, height;
    public event Action<int, int, byte[]>? FrameReceived;
    public event Action<string>? Disconnected;
    public event Action<int, int, int, int, byte[]>? CursorChanged;
    public event Action<int>? ResizeReply;
    async Task<byte[]> ReadAsync(int count, CancellationToken token) { var bytes = new byte[count]; await stream!.ReadExactlyAsync(bytes, token); return bytes; }
    static ushort U16(byte[] b, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset));
    static uint U32(byte[] b, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));
    static void Put16(byte[] b, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset), checked((ushort)value));
    public async Task ConnectAsync(IDisplayConnection connection, CancellationToken token = default)
    {
        await socket.ConnectAsync("127.0.0.1", connection.Port, token); stream = socket.GetStream();
        var version = Encoding.ASCII.GetString(await ReadAsync(12, token)); if (version != "RFB 003.008\n") throw new IOException("This display requires RFB 3.8.");
        await SendAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), token);
        var count = (await ReadAsync(1, token))[0]; if (count == 0) throw new IOException("VNC rejected the connection."); var types = await ReadAsync(count, token);
        if (!types.Contains((byte)1)) throw new IOException("Only local unauthenticated VNC is supported."); await SendAsync([1], token);
        if (U32(await ReadAsync(4, token)) != 0) throw new IOException("VNC security negotiation failed."); await SendAsync([1], token);
        var init = await ReadAsync(24, token); Resize(U16(init), U16(init, 2)); var nameLength = U32(init, 20); if (nameLength > 65536) throw new IOException("Invalid display name length."); await ReadAsync((int)nameLength, token);
        // 32-bit little-endian true color: B,G,R,padding.
        await SendAsync([0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0], token);
        await SendAsync([2, 0, 0, 4, 0, 0, 0, 0, 255, 255, 255, 33, 255, 255, 255, 17, 255, 255, 254, 204], token); // Raw, DesktopSize, RichCursor, ExtendedDesktopSize
        await RequestAsync(false, token); loop = ReceiveAsync();
    }
    void Resize(int w, int h) { if (w < 1 || h < 1 || w > 8192 || h > 8192 || (long)w * h > 16777216) throw new IOException("Unsupported framebuffer size."); width = w; height = h; pixels = new byte[w * h * 4]; }
    async Task SendAsync(byte[] bytes, CancellationToken token = default) { await sendGate.WaitAsync(token); try { await stream!.WriteAsync(bytes, token); } finally { sendGate.Release(); } }
    Task RequestAsync(bool incremental, CancellationToken token) { var b = new byte[10]; b[0] = 3; b[1] = incremental ? (byte)1 : (byte)0; Put16(b, 6, width); Put16(b, 8, height); return SendAsync(b, token); }
    async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var type = (await ReadAsync(1, lifetime.Token))[0];
                if (type == 0)
                {
                    var update = await ReadAsync(3, lifetime.Token); var rectangles = U16(update, 1);
                    bool resized = false;
                    for (int i = 0; i < rectangles; i++)
                    {
                        var r = await ReadAsync(12, lifetime.Token); int x = U16(r), y = U16(r, 2), w = U16(r, 4), h = U16(r, 6); var encoding = unchecked((int)U32(r, 8));
                        if (encoding == -223) { Resize(w, h); resized = true; continue; }
                        if (encoding == -308)
                        {
                            var layout = await ReadAsync(4, lifetime.Token);
                            await ReadAsync(layout[0] * 16, lifetime.Token);
                            ResizeReply?.Invoke(y);
                            if (y == 0 && (w != width || h != height)) { Resize(w, h); resized = true; }
                            continue;
                        }
                        if (encoding == -239)
                        {
                            if (w > 512 || h > 512) throw new IOException("Guest cursor exceeds size limit.");
                            var cursor = await ReadAsync(w * h * 4, lifetime.Token);
                            var mask = await ReadAsync(((w + 7) / 8) * h, lifetime.Token);
                            for (int cy = 0; cy < h; cy++)
                                for (int cx = 0; cx < w; cx++)
                                {
                                    int offset = (cy * w + cx) * 4;
                                    bool visible = (mask[cy * ((w + 7) / 8) + cx / 8] & (128 >> (cx % 8))) != 0;
                                    cursor[offset + 3] = visible ? (byte)255 : (byte)0;
                                    if (!visible) cursor[offset] = cursor[offset + 1] = cursor[offset + 2] = 0;
                                }
                            CursorChanged?.Invoke(w, h, x, y, cursor);
                            continue;
                        }
                        if (encoding != 0 || x + w > width || y + h > height) throw new IOException("Invalid VNC rectangle.");
                        var data = await ReadAsync(checked(w * h * 4), lifetime.Token);
                        for (int row = 0; row < h; row++) Buffer.BlockCopy(data, row * w * 4, pixels, ((y + row) * width + x) * 4, w * 4);
                    }
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    FrameReceived?.Invoke(width, height, (byte[])pixels.Clone()); await RequestAsync(!resized, lifetime.Token);
                }
                else if (type == 2) { /* Bell */ }
                else if (type == 3) { var header = await ReadAsync(7, lifetime.Token); var length = U32(header, 3); if (length > 1048576) throw new IOException("Clipboard payload exceeds limit."); await ReadAsync((int)length, lifetime.Token); }
                else throw new IOException("Unsupported VNC server message.");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { if (!lifetime.IsCancellationRequested) Disconnected?.Invoke(ex.Message); }
    }
    public Task KeyAsync(uint key, bool down) { var b = new byte[8]; b[0] = 4; b[1] = down ? (byte)1 : (byte)0; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), key); return SendAsync(b, lifetime.Token); }
    public Task ResizeDesktopAsync(int requestedWidth, int requestedHeight)
    {
        if (requestedWidth < 320 || requestedWidth > 4096 || requestedHeight < 200 || requestedHeight > 2160) throw new ArgumentOutOfRangeException(nameof(requestedWidth));
        var message = new byte[24]; message[0] = 251; message[6] = 1;
        Put16(message, 2, requestedWidth); Put16(message, 4, requestedHeight);
        Put16(message, 16, requestedWidth); Put16(message, 18, requestedHeight);
        return SendAsync(message, lifetime.Token);
    }
    public Task PointerAsync(int x, int y, byte buttons) { var b = new byte[6]; b[0] = 5; b[1] = buttons; Put16(b, 2, Math.Clamp(x, 0, width - 1)); Put16(b, 4, Math.Clamp(y, 0, height - 1)); return SendAsync(b, lifetime.Token); }
    public async Task SendCtrlAltDeleteAsync() { await KeyAsync(0xffe3, true); await KeyAsync(0xffe9, true); await KeyAsync(0xffff, true); await KeyAsync(0xffff, false); await KeyAsync(0xffe9, false); await KeyAsync(0xffe3, false); }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); socket.Dispose(); if (loop is not null) await loop; lifetime.Dispose(); sendGate.Dispose(); }
}
