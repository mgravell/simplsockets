using SimplPipelines;
using SimplSockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DemoClient
{
    class Program
    {
        static Task Main(string[] args)
        {
            string option;
            TryAgain:
            if (args == null || args.Length == 0)
            {
                Console.WriteLine("1: run client via SimplPipelines");
                Console.WriteLine("2: run client via SimplSockets");
                option = Console.ReadLine();
            }
            else
            {
                option = args[0];
                args = null;
            }
            switch (option)
            {
                case "1": return RunViaPipelines();
                case "2": return RunViaSockets();
                default: goto TryAgain;

            }
        }
        static async Task RunViaPipelines()
        {
            using (var client = await SimplPipelineClient.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, 5000)))
            {
                await Console.Out.WriteLineAsync(
                    "Client connected; type 'q' to quit, anything else to send");

                // subscribe to broadcasts
                client.MessageReceived += async msg => await WriteLineAsync('*', msg);

                string line;
                while ((line = await Console.In.ReadLineAsync()) != null)
                {
                    if (line == "q") break;

                    Leased<byte> response;
                    using (var leased = line.Encode())
                    {
                        response = await client.SendReciveAsync(leased.Memory);
                    }
                    await WriteLineAsync('<', response);
                }
            }
        }

        static async Task RunViaSockets()
        {
            using (var client = new SimplSocketClient(() => new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            { NoDelay = true }))
            {
                client.Connect(new IPEndPoint(IPAddress.Loopback, 5000));
                await Console.Out.WriteLineAsync(
                    "Client connected; type 'q' to quit, anything else to send");

                // subscribe to broadcasts
                client.MessageReceived += async (s, e) => await WriteLineAsync('*', e.ReceivedMessage.Message);

                string line;
                while ((line = await Console.In.ReadLineAsync()) != null)
                {
                    if (line == "q") break;

                    var request = Encoding.UTF8.GetBytes(line);
                    var response = client.SendReceive(request);
                    await WriteLineAsync('<', response);
                }
            }
        }
        static async ValueTask WriteLineAsync(char prefix, Leased<byte> encoded)
        {
            using (encoded) { await WriteLineAsync(prefix, encoded.Memory); }
        }
        static async ValueTask WriteLineAsync(char prefix, ReadOnlyMemory<byte> encoded)
        {
            using (var leased = encoded.Decode())
            {
                await Console.Out.WriteAsync(prefix);
                await Console.Out.WriteAsync(' ');
                await Console.Out.WriteLineAsync(leased.Memory);
            }
        }
    }
}
