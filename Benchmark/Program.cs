using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DemoServer;
using SimplPipelines;
using SimplSockets;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Benchmark
{
    static class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<Benchmarks>();
            Console.WriteLine(summary);
        }
    }
    [ClrJob, CoreJob, WarmupCount(2), MemoryDiagnoser]
    public class Benchmarks
    {
        static readonly EndPoint
            v1 = new IPEndPoint(IPAddress.Loopback, 6000),
            v2 = new IPEndPoint(IPAddress.Loopback, 6001);

        byte[] _data;
        IDisposable _socketServer, _pipeServer;
        [GlobalSetup]
        public void Setup()
        {
            var socketServer = new SimplSocketServer(CreateV1Socket);
            socketServer.Listen(v1);
            _socketServer = socketServer;
            var pipeServer = SimplPipelineSocketServer.For<ReverseServer>();
            pipeServer.Listen(v2);
            _pipeServer = pipeServer;

            _data = new byte[1024];
        }
        static Socket CreateV1Socket() => new Socket(
            AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            { NoDelay = true };
        void Dispose<T>(ref T field) where T : class, IDisposable
        {
            if (field != null) try { field.Dispose(); } catch { }
        }
        [GlobalCleanup]
        public void TearDown()
        {
            Dispose(ref _socketServer);
            Dispose(ref _pipeServer);
        }
        // note in the below: GC.KeepAlive is just an opaque method
        // that ensures we don't not do "the thing"; we aren't doing
        // anything magic with GC here!

        const int Ops = 1000;
        void AssertResult(long result)
        {
            int expected = _data.Length * Ops;
            if (result != expected) throw new InvalidOperationException(
                $"Data error: expected {expected}, got {result}");
        }
        [Benchmark(OperationsPerInvoke = Ops)]
        public async Task v2_v2()
        {
            long x = 0;
            using (var client = await SimplPipelineClient.ConnectAsync(v2))
            {
                for (int i = 0; i < Ops; i++)
                {
                    using (var response = await client.SendReceiveAsync(_data))
                    {
                        x += response.Memory.Length;
                    }
                }
            }
            AssertResult(x);
        }
        [Benchmark(OperationsPerInvoke = Ops)]
        public void v1_v1()
        {
            long x = 0;
            using (var client = new SimplSocketClient(CreateV1Socket))
            {
                for (int i = 0; i < Ops; i++)
                {
                    var response = client.SendReceive(_data);
                    x += response.Length;
                }
            }
            AssertResult(x);
        }
    }
}
