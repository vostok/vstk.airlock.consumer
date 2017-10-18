using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Confluent.Kafka;
using Vostok.Logging;

namespace Vostok.AirlockConsumer
{
    public class ProcessorHost
    {
        private const int maxBatchSize = 100*1000;
        private const int overflowLimit = maxBatchSize * 10;
        private const int resumeConsumingMinimum = overflowLimit / 2;
        private readonly string routingKey;
        private readonly CancellationToken stopSignal;
        private readonly IAirlockEventProcessor processor;
        private readonly ILog log;
        private readonly Consumer<Null, byte[]> consumer;
        private readonly TimeSpan maxDequeueTimeout = TimeSpan.FromSeconds(1);
        private readonly TimeSpan minDequeueTimeout = TimeSpan.FromMilliseconds(100);
        private readonly BlockingCollection<Message<Null, byte[]>> eventsQueue = new BlockingCollection<Message<Null, byte[]>>(new ConcurrentQueue<Message<Null, byte[]>>());
        private readonly Thread processorThread;

        public ProcessorHost(string consumerGroupHostId, string routingKey, CancellationToken stopSignal, IAirlockEventProcessor processor, ILog log, Consumer<Null, byte[]> consumer)
        {
            this.routingKey = routingKey;
            this.stopSignal = stopSignal;
            this.processor = processor;
            this.log = log;
            this.consumer = consumer;
            processorThread = new Thread(ProcessorThreadFunc)
            {
                IsBackground = true,
                Name = $"processor-{consumerGroupHostId}-{processor.ProcessorId}",
            };
        }

        public void Start()
        {
            processorThread.Start();
        }

        public bool Enqueue(Message<Null, byte[]> message)
        {
            // todo (avk, 04.10.2017): �� ������������� ��-�� ������������ ������������ (use consumer.Pause() api) https://github.com/vostok/airlock.consumer/issues/14
            eventsQueue.Add(message, CancellationToken.None);
            return eventsQueue.Count >= overflowLimit;
        }

        public void CompleteAdding()
        {
            eventsQueue.CompleteAdding();
        }

        public bool CanResumeConsuming()
        {
            return eventsQueue.Count <= resumeConsumingMinimum;
        }

        public void WaitForTermination()
        {
            processorThread.Join();
            processor.Release(routingKey);
        }

        private void ProcessorThreadFunc()
        {
            try
            {
                while (!eventsQueue.IsCompleted)
                {
                    var messageBatch = DequeueMessageBatch();
                    if (messageBatch.Count > 0)
                        ProcessMessageBatch(messageBatch);
                }
            }
            catch (Exception e)
            {
                log.Fatal("Unhandled exception on processor thread", e);
                throw;
            }
        }

        private List<Message<Null, byte[]>> DequeueMessageBatch()
        {
            var sw = Stopwatch.StartNew();
            var messageBatch = new List<Message<Null, byte[]>>();
            var dequeueTimeout = maxDequeueTimeout;
            while (true)
            {
                if (eventsQueue.TryTake(out var message, dequeueTimeout))
                {
                    messageBatch.Add(message);
                    if (messageBatch.Count >= maxBatchSize)
                        break;
                }
                else
                {
                    if (stopSignal.IsCancellationRequested)
                    {
                        if (eventsQueue.IsCompleted)
                            break;
                        dequeueTimeout = TimeSpan.Zero;
                    }
                    else
                    {
                        var elapsed = sw.Elapsed;
                        if (elapsed > maxDequeueTimeout)
                            break;
                        dequeueTimeout = maxDequeueTimeout - elapsed;
                        if (dequeueTimeout < minDequeueTimeout)
                            dequeueTimeout = minDequeueTimeout;
                    }
                }
            }
            return messageBatch;
        }

        private void ProcessMessageBatch(List<Message<Null, byte[]>> messageBatch)
        {
            var airlockEvents = new List<AirlockEvent<byte[]>>();
            var offsetsToCommit = new Dictionary<TopicPartition, long>();
            foreach (var x in messageBatch)
            {
                airlockEvents.Add(new AirlockEvent<byte[]>
                {
                    RoutingKey = routingKey, Timestamp = x.Timestamp.UtcDateTime, Payload = x.Value,
                });
                offsetsToCommit[x.TopicPartition] = x.Offset + 1;
            }
            DoProcessMessageBatch(airlockEvents);
            consumer.CommitAsync(offsetsToCommit.Select(x => new TopicPartitionOffset(x.Key, x.Value))).GetAwaiter().GetResult();
        }

        private void DoProcessMessageBatch(List<AirlockEvent<byte[]>> airlockEvents)
        {
            try
            {
                processor.Process(airlockEvents);
            }
            catch (Exception e)
            {
                log.Error($"Processor failed for routingKey: {routingKey}, processorType: {processor.GetType().Name}, processorId: {processor.ProcessorId}", e);
            }
        }
    }
}