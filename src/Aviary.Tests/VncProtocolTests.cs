using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Aviary.Core;
using Aviary.Qemu;
using Xunit;

namespace Aviary.Tests;

public sealed class VncProtocolTests
{
    // The fake RFB server runs in-process on TCP; Aviary itself connects to QEMU over a Unix socket.
    sealed record Endpoint(int Port) : IDisplayConnection
    {
        public async Task<Stream> OpenAsync(CancellationToken token) { var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, Port, token); return tcp.GetStream(); }
    }
    static async Task<byte[]> Read(NetworkStream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }
    static byte[] Rect(int x, int y, int w, int h, int encoding)
    {
        var bytes = new byte[12]; Put16(bytes, 0, x); Put16(bytes, 2, y); Put16(bytes, 4, w); Put16(bytes, 6, h); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), encoding); return bytes;
    }
    static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
    static async Task Handshake(NetworkStream stream, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), token); await Read(stream, 12, token);
        await stream.WriteAsync(new byte[] { 1, 1 }, token); await Read(stream, 1, token);
        await stream.WriteAsync(new byte[4], token); await Read(stream, 1, token);
        var init = new byte[24]; Put16(init, 0, 2); Put16(init, 2, 2); await stream.WriteAsync(init, token);
        await Read(stream, 20, token); var header = await Read(stream, 4, token);
        int encodings = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)); var values = await Read(stream, encodings * 4, token);
        Assert.Contains(Enumerable.Range(0, encodings).Select(i => BinaryPrimitives.ReadInt32BigEndian(values.AsSpan(i * 4))), e => e == -239);
        Assert.Contains(Enumerable.Range(0, encodings).Select(i => BinaryPrimitives.ReadInt32BigEndian(values.AsSpan(i * 4))), e => e == -308);
        await Read(stream, 10, token);
    }
    [Fact]
    public async Task CursorMaskAndDesktopResizePreserveProtocolAlignment()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var cursorResult = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameResult = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token); var stream = peer.GetStream(); await Handshake(stream, timeout.Token);
                await stream.WriteAsync(new byte[] { 0, 0, 0, 3 }, timeout.Token);
                await stream.WriteAsync(Rect(1, 0, 2, 2, -239), timeout.Token);
                await stream.WriteAsync(Enumerable.Repeat((byte)42, 16).ToArray(), timeout.Token); await stream.WriteAsync(new byte[] { 128, 64 }, timeout.Token);
                await stream.WriteAsync(Rect(0, 0, 3, 2, -308), timeout.Token);
                var layout = new byte[20]; layout[0] = 1; Put16(layout, 12, 3); Put16(layout, 14, 2); await stream.WriteAsync(layout, timeout.Token);
                await stream.WriteAsync(Rect(0, 0, 3, 2, 0), timeout.Token); await stream.WriteAsync(Enumerable.Repeat((byte)99, 24).ToArray(), timeout.Token);
                var refresh = await Read(stream, 10, timeout.Token); Assert.Equal(0, refresh[1]); Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(refresh.AsSpan(6))); Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(refresh.AsSpan(8)));
                var resize = await Read(stream, 24, timeout.Token); Assert.Equal(251, resize[0]); Assert.Equal(1, resize[6]); Assert.Equal(1280, BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(2))); Assert.Equal(720, BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(4))); Assert.Equal(1280, BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(16)));
                await finish.Task.WaitAsync(timeout.Token);
            });
            await using var display = new VncDisplayClient();
            display.CursorChanged += (w, h, x, y, data) => { Assert.Equal(2, w); Assert.Equal(2, h); Assert.Equal(1, x); cursorResult.TrySetResult(data); };
            display.FrameReceived += (w, h, data) => { if (w == 3 && h == 2) frameResult.TrySetResult(data); };
            await display.ConnectAsync(new Endpoint(((IPEndPoint)listener.LocalEndpoint).Port), timeout.Token);
            var cursor = await cursorResult.Task.WaitAsync(timeout.Token); Assert.Equal(new byte[] { 255, 0, 0, 255 }, new[] { cursor[3], cursor[7], cursor[11], cursor[15] });
            var frame = await frameResult.Task.WaitAsync(timeout.Token); Assert.Equal(24, frame.Length); Assert.Equal(99, frame[0]);
            await display.ResizeDesktopAsync(1280, 720); finish.TrySetResult(); await server;
        }
        finally { listener.Stop(); }
    }
    [Theory][InlineData(100, 200)][InlineData(4097, 1080)][InlineData(800, 3000)]
    public async Task InvalidDesktopRequestsAreRejected(int width, int height)
    {
        await using var client = new VncDisplayClient(); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ResizeDesktopAsync(width, height));
    }
}
