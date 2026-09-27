using System;
using System.Net;
using System.Net.Sockets;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.Socket;

sealed class TcpSocketWrapper : SocketWrapper
{
    private TcpClient _socket;

    public override bool IsConnected => _socket?.Client?.Connected ?? false;

    public override EndPoint LocalEndPoint => _socket?.Client?.LocalEndPoint;


    public override void Connect(Uri uri)
    {
        if (IsConnected)
            return;

        _socket = new TcpClient();
        _socket.NoDelay = true;

        try
        {
            // Bounded connect: a blocking Connect to an unreachable host can hang the UI thread
            // for over a minute (iOS watchdog), and fails instantly while iOS shows its Local
            // Network permission prompt.
            var connect = _socket.ConnectAsync(uri.Host, uri.Port);

            if (!connect.Wait(TimeSpan.FromSeconds(8)))
            {
                Log.Error($"connect to {uri.Host}:{uri.Port} timed out");
                _socket.Close();
                InvokeOnError(SocketError.TimedOut);

                return;
            }

            if (!IsConnected)
            {
                InvokeOnError(SocketError.NotConnected);

                return;
            }

            InvokeOnConnected();
        }
        catch (SocketException socketEx)
        {
            Log.Error($"error while connecting {socketEx}");
            InvokeOnError(socketEx.SocketErrorCode);
        }
        catch (AggregateException agg) when (agg.InnerException is SocketException inner)
        {
            Log.Error($"error while connecting {inner}");
            InvokeOnError(inner.SocketErrorCode);
        }
        catch (Exception ex)
        {
            Log.Error($"error while connecting {ex}");
            InvokeOnError(SocketError.SocketError);
        }
    }

    public override void Send(byte[] buffer, int offset, int count)
    {
        var stream = _socket.GetStream();
        stream.Write(buffer, offset, count);
        stream.Flush();
    }

    public override int Read(byte[] buffer)
    {
        if (!IsConnected)
            return 0;

        var available = Math.Min(buffer.Length, _socket.Available);
        var done = 0;

        var stream = _socket.GetStream();

        while (done < available)
        {
            var toRead = Math.Min(buffer.Length, available - done);
            var read = stream.Read(buffer, done, toRead);

            if (read <= 0)
            {
                InvokeOnDisconnected();
                Disconnect();

                return 0;
            }

            done += read;
        }

        return done;
    }

    public override void Disconnect()
    {
        _socket?.Close();
        Dispose();
    }

    public override void Dispose()
    {
        _socket?.Dispose();
    }
}