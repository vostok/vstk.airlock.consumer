﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Vostok.Logging.Airlock;

namespace Vostok.AirlockConsumer.Logs
{
    public static class ElasticLogsIndexerEntryPoint
    {
        public static void Main(string[] args)
        {
            var settingsFromFile = Configuration.TryGetSettingsFromFile(args);
            var log = Logging.Configure((string)settingsFromFile?["airlock.consumer.log.file.pattern"] ?? "..\\log\\actions-{Date}.txt");
            var kafkaBootstrapEndpoints = (string)settingsFromFile?["bootstrap.servers"] ?? "devops-kafka1.dev.kontur.ru:9092";
            var elasticUris = ((List<object>)settingsFromFile?["airlock.consumer.elastic.endpoints"] ?? new List<object> {"http://devops-consul1.dev.kontur.ru:9200/"}).Cast<string>().Select(x => new Uri(x)).ToArray();
            const string consumerGroupId = nameof(ElasticLogsIndexerEntryPoint);
            var clientId = (string)settingsFromFile?["client.id"] ?? Dns.GetHostName();
            var processor = new LogAirlockEventProcessor(elasticUris, log);
            var processorProvider = new DefaultAirlockEventProcessorProvider<LogEventData, LogEventDataSerializer>(":logs", processor);
            var consumer = new ConsumerGroupHost(kafkaBootstrapEndpoints, consumerGroupId, clientId, true, log, processorProvider);
            consumer.Start();
            Console.ReadLine();
            consumer.Stop();
        }
    }
}