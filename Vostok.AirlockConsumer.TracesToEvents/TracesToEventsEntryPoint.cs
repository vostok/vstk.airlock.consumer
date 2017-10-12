﻿using System.Collections.Generic;
using Vostok.Airlock;
using Vostok.Logging;
using Vostok.Metrics;
using Vostok.Tracing;

namespace Vostok.AirlockConsumer.TracesToEvents
{
    public class TracesToEventsEntryPoint : ConsumerApplication
    {
        public static void Main()
        {
            new ConsumerApplicationHost<TracesToEventsEntryPoint>().Run();
        }

        protected sealed override void DoInitialize(ILog log, Dictionary<string, string> environmentVariables, out IRoutingKeyFilter routingKeyFilter, out IAirlockEventProcessorProvider processorProvider)
        {
            routingKeyFilter = new DefaultRoutingKeyFilter(RoutingKey.TracesSuffix);
            AirlockSerializerRegistry.Register(new MetricEventSerializer());
            var airlockConfig = GetAirlockConfig(log, environmentVariables);
            var airlockClient = new AirlockClient(airlockConfig, log);
            processorProvider = new DefaultAirlockEventProcessorProvider<Span, SpanAirlockSerializer>(project => new TracesToEventsProcessor(airlockClient, log));
            // todo (avk, 11.10.2017): wait for airlock gate to start https://github.com/vostok/airlock.consumer/issues/11
        }
    }
}