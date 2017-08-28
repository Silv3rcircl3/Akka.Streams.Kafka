﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Configuration;
using Akka.Streams.Dsl;
using Akka.Streams.Implementation;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.TestKit;
using Confluent.Kafka;
using Confluent.Kafka.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Kafka.Tests
{
    public class IntegrationTests : Akka.TestKit.Xunit2.TestKit
    {
        private const string KafkaUrl = "localhost:9092";

        private const string InitialMsg = "initial msg in topic, required to create the topic before any consumer subscribes to it";

        private readonly ActorMaterializer _materializer;

        public IntegrationTests(ITestOutputHelper output) 
            : base(ConfigurationFactory.FromResource<ConsumerSettings<object, object>>("Akka.Streams.Kafka.reference.conf"), null, output)
        {
            _materializer = Sys.Materializer();
        }

        private string Uuid { get; } = Guid.NewGuid().ToString();

        private string CreateTopic(int number) => $"topic-{number}-{Uuid}";
        private string CreateGroup(int number) => $"group-{number}-{Uuid}";

        private ProducerSettings<Null, string> ProducerSettings =>
            ProducerSettings<Null, string>.Create(Sys, null, new StringSerializer(Encoding.UTF8))
                .WithBootstrapServers(KafkaUrl);

        private async Task GivenInitializedTopic(string topic)
        {
            var producer = ProducerSettings.CreateKafkaProducer();
            await producer.ProduceAsync(topic, null, InitialMsg, 0);
            producer.Dispose();
        }

        private ConsumerSettings<Null, string> CreateConsumerSettings(string group)
        {
            return ConsumerSettings<Null, string>.Create(Sys, null, new StringDeserializer(Encoding.UTF8))
                .WithBootstrapServers(KafkaUrl)
                .WithProperty("auto.offset.reset", "earliest")
                .WithGroupId(group);
        }

        private async Task Produce(string topic, IEnumerable<int> range, ProducerSettings<Null, string> producerSettings)
        {
            await Source
                .From(range)
                .Select(elem => new ProduceRecord<Null, string>(topic, null, elem.ToString()))
                .Via(Dsl.Producer.CreateFlow(producerSettings))
                .RunWith(Sink.Ignore<Task<Result<Null, string>>>(), _materializer);
        }

        private TestSubscriber.Probe<string> CreateProbe(ConsumerSettings<Null, string> consumerSettings, string topic)
        {
            return Dsl.Consumer
                .PlainSource(consumerSettings, Subscriptions.Assignment(new TopicPartition(topic, 0)))
                .Where(c => !c.Value.Equals(InitialMsg))
                .Select(c => c.Value)
                .RunWith(this.SinkProbe<string>(), _materializer);
        }

        [Fact]
        public async Task ProduceToPlainSinkAndConsumeFromPlainSource()
        {
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, 100), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, topic1);

            probe
                .Request(100)
                .ExpectNextN(Enumerable.Range(1, 100).Select(c => c.ToString()));

            probe.Cancel();
        }
    }
}