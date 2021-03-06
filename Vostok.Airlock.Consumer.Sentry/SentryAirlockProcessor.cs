﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SharpRaven.Data;
using Vostok.Airlock.Logging;
using Vostok.Logging;
using Vostok.Metrics.Meters;

namespace Vostok.Airlock.Consumer.Sentry
{
    public class SentryAirlockProcessor : SimpleAirlockEventProcessorBase<LogEventData>
    {
        private readonly string sentryProjectId;
        private readonly SentryProcessorSettings processorSettings;
        private readonly ILog log;
        private readonly ISentryPacketSender packetSender;

        public SentryAirlockProcessor(string sentryProjectId, SentryProcessorSettings processorSettings, ILog log, ISentryPacketSender sentryPacketSender)
        {
            this.sentryProjectId = sentryProjectId;
            this.processorSettings = processorSettings;
            this.log = log;
            packetSender = sentryPacketSender;
        }

        public sealed override void Process(List<AirlockEvent<LogEventData>> events, ProcessorMetrics processorMetrics)
        {
            var errorEvents = events.Where(x => x.Payload.Level == LogLevel.Error || x.Payload.Level == LogLevel.Fatal);
            var throttledErrorEvents = ThrottleEvents(errorEvents.OrderBy(x => x.Timestamp), processorMetrics.EventIgnoredCounter);
            var packets = throttledErrorEvents.Select(
                x =>
                {
                    var logEvent = x.Payload;
                    var jsonPacket = new JsonPacket(sentryProjectId)
                    {
                        Level = logEvent.Level == LogLevel.Error ? ErrorLevel.Error : ErrorLevel.Fatal,
                        Tags = logEvent.Properties,
                        TimeStamp = logEvent.Timestamp.UtcDateTime,
                        Exceptions =  logEvent.Exceptions?.Select(ex => ex.ToSentry()).ToList(),
                        Message = logEvent.Message,
                        MessageObject = logEvent.Message
                    };
                    JsonPacketPatcher.PatchPacket(jsonPacket);
                    return jsonPacket;
                });
            Parallel.ForEach(
                packets,
                new ParallelOptions {MaxDegreeOfParallelism = processorSettings.MaxTasks},
                packet =>
                {
                    try
                    {
                        packetSender.SendPacket(packet, processorMetrics.SendingErrorCounter);
                        try
                        {
                            processorMetrics.EventProcessedCounter.Add();
                        }
                        catch (Exception e)
                        {
                            log.Error(e);
                        }
                    }
                    catch (Exception e)
                    {
                        processorMetrics.EventFailedCounter.Add();
                        log.Error(e);
                    }
                });
        }

        private IEnumerable<AirlockEvent<LogEventData>> ThrottleEvents(IEnumerable<AirlockEvent<LogEventData>> events, ICounter messageIgnoredCounter)
        {
            var lastTimestampIndex = 0L;
            var periodCounter = 0;
            foreach (var airlockEvent in events)
            {
                var normalizedTimestampIndex = airlockEvent.Timestamp.Ticks/processorSettings.ThrottlingPeriod.Ticks;
                if (normalizedTimestampIndex == lastTimestampIndex)
                {
                    periodCounter++;
                }
                else
                {
                    periodCounter = 0;
                    lastTimestampIndex = normalizedTimestampIndex;
                }
                if (periodCounter < processorSettings.ThrottlingThreshold)
                    yield return airlockEvent;
                else
                    messageIgnoredCounter.Add();
            }
        }
    }
}