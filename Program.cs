using Serilog;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace TcpProxy
{
    class Program
    {
        static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
            awaitingClientWrite = new ConcurrentQueue<byte[]>();
            awaitingServerWrite = new ConcurrentQueue<byte[]>();

            var runningTask = Task.Run(Proxy);

            while (true)
            {
                var x = Console.ReadLine();
                if (x == "q")
                {
                    Environment.FailFast("Manual quit");
                }
            }
        }

        static ConcurrentQueue<byte[]> awaitingServerWrite;
        static ConcurrentQueue<byte[]> awaitingClientWrite;

        static async Task Proxy()
        {
            Log.Information("Starting listener");
            var tcpServer = new TcpListener(IPAddress.Any, 1200);
            tcpServer.Start();

            var client = await tcpServer.AcceptTcpClientAsync();
            Log.Information("Accepted client");

            using var clientStream = client.GetStream();
            using var sslStream = new SslStream(clientStream);

            await sslStream.AuthenticateAsServerAsync(new X509Certificate("localhost.pfx", "passwordthatisREALLYsecure"));
            Log.Information("Client authenticated with server");

            var proxyClient = new TcpClient();
            Log.Information("Attempting connect to localhost");
            await proxyClient.ConnectAsync("localhost", 9405);
            Log.Information("Connected");
            var proxyStream = proxyClient.GetStream();
            var proxySsl = new SslStream(proxyStream);
            Log.Information("Attempting auth as client to localhost");
            await proxySsl.AuthenticateAsClientAsync("localhost");
            Log.Information("Success");

            var clientReadTask = Task.Run(() => ReadFromClient(sslStream));
            var serverWriteTask = Task.Run(() => WriteToServer(proxySsl));

            var serverReadTask = Task.Run(() => ReadFromServer(proxySsl));
            var clientWriteTask = Task.Run(() => WriteToClient(sslStream));

            await Task.WhenAll(clientReadTask, serverWriteTask, serverReadTask, clientWriteTask);
        }

        static async Task ReadFromClient(Stream clientStream)
        {
            while (true)
            {
                byte[] buffer = new byte[1024];

                var bytesRead = await clientStream.ReadAsync(buffer, 0, 1024);
                Log.Information($"Read {bytesRead} from client");
                var toSend = new byte[bytesRead];
                Array.Copy(buffer, toSend, bytesRead);

                awaitingServerWrite.Enqueue(toSend);
            }
        }

        static async Task ReadFromServer(Stream serverStream)
        {
            while (true)
            {
                byte[] buffer = new byte[1024];

                var bytesRead = await serverStream.ReadAsync(buffer, 0, 1024);
                Log.Information($"Read {bytesRead} from server");
                var toSend = new byte[bytesRead];
                Array.Copy(buffer, toSend, bytesRead);

                awaitingClientWrite.Enqueue(toSend);
            }
        }

        static async Task WriteToClient(Stream clientStream)
        {
            while (true)
            {
                byte[] buffer;
                var hasData = awaitingClientWrite.TryDequeue(out buffer);

                if (!hasData)
                {
                    await Task.Delay(1);
                    continue;
                }
                Log.Information($"Writing {buffer.Length} bytes to client");
                await clientStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }

        static async Task WriteToServer(Stream serverStream)
        {
            while (true)
            {
                byte[] buffer;
                var hasData = awaitingServerWrite.TryDequeue(out buffer);

                if (!hasData)
                {
                    await Task.Delay(1);
                    continue;
                }
                Log.Information($"Writing {buffer.Length} bytes to server");

                await serverStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
    }
}
