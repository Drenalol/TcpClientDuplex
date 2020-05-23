using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Drenalol.Client;
using Drenalol.Stuff;
using NUnit.Framework;

namespace Drenalol
{
    [Parallelizable]
    [TestFixture(TestOf = typeof(TcpClientIo<,>))]
    public class TcpClientIoTests
    {
        public static ImmutableList<Mock> Mocks;

        [OneTimeSetUp]
        public void Load()
        {
            Mocks = JsonExt.Deserialize<List<Mock>>(File.ReadAllText("MOCK_DATA_1000")).Select(mock => mock.Build()).ToImmutableList();
        }

        [TestCase(1000000, 1, 5)]
        [TestCase(1000000, 2, 5)]
        [TestCase(1000000, 3,5)]
        [TestCase(1000000, 4, 5)]
        public void MultipleConsumersAsyncTest(int requests, int consumers, double timeout)
        {
            var requestsPerConsumer = requests / consumers;
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(timeout));
            var consumersList = Enumerable.Range(0, consumers).Select(i => new TcpClientIo<Mock, Mock>(IPAddress.Any, 10000, new TcpClientIoOptions
            {
                StreamPipeReaderOptions = new StreamPipeReaderOptions(bufferSize: 131072),
                StreamPipeWriterOptions = new StreamPipeWriterOptions(minimumBufferSize: 131072)
            })).ToList();
            var requestQueue = 0;
            var waitersQueue = 0;
            var sendMs = new ConcurrentBag<long>();
            var receiveMs = new ConcurrentBag<long>();
            var bytesWrite = 0L;
            var bytesRead = 0L;

            Task.WaitAll(consumersList.Select(io => Task.Run(() => DoWork(io), cts.Token)).ToArray());

            void DoWork(TcpClientIo<Mock, Mock> tcpClient)
            {
                try
                {
                    var list = new List<Task>();
                    list.AddRange(Enumerable.Range(0, requestsPerConsumer).Select(i => (long) i).Select(SendAsync));
                    list.AddRange(Enumerable.Range(0, requestsPerConsumer).Select(i => (long) i).Select(ReceiveAsync));
                    
                    Task.WaitAll(list.ToArray());

                    async Task SendAsync(long id)
                    {
                        var sw = Stopwatch.StartNew();
                        var mock = Mock.Create(id);
                        await tcpClient.SendAsync(mock, cts.Token);
                        sw.Stop();
                        sendMs.Add(sw.ElapsedMilliseconds);
                    }

                    async Task ReceiveAsync(long id)
                    {
                        var sw = Stopwatch.StartNew();
                        var batch = await tcpClient.ReceiveAsync(id, cts.Token);
                        batch.TryDequeue(out var mock);
                        Debug.Assert(mock.Size == mock.Body.Length);
                        sw.Stop();
                        receiveMs.Add(sw.ElapsedMilliseconds);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
                finally
                {
                    Interlocked.Add(ref bytesWrite, (long) tcpClient.BytesWrite);
                    Interlocked.Add(ref bytesRead, (long) tcpClient.BytesRead);
                    Interlocked.Add(ref requestQueue, tcpClient.Requests);
                    Interlocked.Add(ref waitersQueue, tcpClient.Waiters);
                }
            }
            
            TestContext.WriteLine($"Send Min Avg Max ms: {sendMs.Min().ToString()} {sendMs.Average().ToString(CultureInfo.CurrentCulture)} {sendMs.Max().ToString()}");
            TestContext.WriteLine($"Receive Min Avg Max ms: {receiveMs.Min().ToString()} {receiveMs.Average().ToString(CultureInfo.CurrentCulture)} {receiveMs.Max().ToString()}");
            TestContext.WriteLine($"Receive > 1 sec: {receiveMs.Count(l => l > 1000).ToString()}");
            TestContext.WriteLine($"Receive > 2 sec: {receiveMs.Count(l => l > 2000).ToString()}");
            TestContext.WriteLine($"Receive > 5 sec: {receiveMs.Count(l => l > 5000).ToString()}");
            TestContext.WriteLine($"Receive > 10 sec: {receiveMs.Count(l => l > 10000).ToString()}");
            TestContext.WriteLine($"Receive > 30 sec: {receiveMs.Count(l => l > 30000).ToString()}");
            TestContext.WriteLine($"Requests: {requestQueue.ToString()}");
            TestContext.WriteLine($"Waiters: {waitersQueue.ToString()}");
            TestContext.WriteLine($"Sended: {sendMs.Count.ToString()}");
            TestContext.WriteLine($"Received: {receiveMs.Count.ToString()}");
            TestContext.WriteLine($"BytesWrite: {Math.Round(bytesWrite / 1024000.0, 2).ToString(CultureInfo.CurrentCulture)} MegaBytes");
            TestContext.WriteLine($"BytesRead: {Math.Round(bytesRead / 1024000.0, 2).ToString(CultureInfo.CurrentCulture)} MegaBytes");
        }

        [Test]
        public async Task SameIdTest()
        {
            const int requests = 500;
            var list = new List<int>();
            var count = 0;
            var tcpClient = new TcpClientIo<Mock, Mock>(IPAddress.Any, 10000);

            _ = Task.Run(() => Parallel.For(0, requests, i =>
            {
                var mock = Mock.Create(0);
                tcpClient.SendAsync(mock).GetAwaiter().GetResult();
            }));

            while (count < requests)
            {
                var delay = TestContext.CurrentContext.Random.Next(1, 200);
                await Task.Delay(delay);
                var packageResult = await tcpClient.ReceiveAsync((long) 0);
                Assert.NotNull(packageResult);
                var queue = packageResult.QueueCount;
                count += queue;

                while (packageResult.TryDequeue(out var package))
                {
                    list.Add(package.Size);
                }

                TestContext.WriteLine($"({count.ToString()}/{requests.ToString()}) +{queue.ToString()}, by {delay.ToString()} ms, SendQueue: {tcpClient.Requests.ToString()}, ReadCount: {tcpClient.Waiters.ToString()}");
            }

            var havingCount = list.GroupBy(u => u).Where(p => p.Count() > 1).Select(ig => ig.Key.ToString()).Aggregate((acc, next) => $"{acc}, {next}");
            TestContext.WriteLine($"Non-UNIQ Sizes: {havingCount}");
            await tcpClient.DisposeAsync();
        }

        [Test]
        public async Task DisposeTest()
        {
            var tcpClient = new TcpClientIo<Mock, Mock>(IPAddress.Any, 10000);
            using var timer = new System.Timers.Timer {Interval = 3000};
            timer.Start();
            timer.Elapsed += (sender, args) =>
            {
                ((System.Timers.Timer) sender).Stop();
                tcpClient.DisposeAsync().GetAwaiter().GetResult();
            };
            var mock = Mocks[666];
            while (true)
            {
                try
                {
                    await tcpClient.SendAsync(mock);
                    await tcpClient.ReceiveAsync(mock.Id);
                }
                catch (Exception e)
                {
                    var exType = e.GetType();
                    Console.WriteLine($"Got Exception: {exType}: {e}");
                    Assert.That(exType == typeof(OperationCanceledException) || exType == typeof(TaskCanceledException));
                    break;
                }
            }
        }

        [Test]
        public async Task CancelSendReceiveTest()
        {
            await using var tcpClient = new TcpClientIo<Mock, Mock>(IPAddress.Any, 10000);
            var mock = Mocks[666];
            var attempts = 0;
            while (attempts < 3)
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                while (true)
                {
                    try
                    {
                        await tcpClient.SendAsync(mock, cts.Token);
                        await tcpClient.ReceiveAsync(mock.Id, cts.Token);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Got Exception: {e.GetType()}: {e}");
                        Assert.That(e.GetType() == typeof(OperationCanceledException));
                        attempts++;
                        break;
                    }
                }
            }
        }
    }
}