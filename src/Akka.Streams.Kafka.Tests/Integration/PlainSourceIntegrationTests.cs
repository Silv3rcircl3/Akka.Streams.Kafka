﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Configuration;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.TestKit;
using Confluent.Kafka;
using Confluent.Kafka.Serialization;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Kafka.Tests.Integration
{
    public class PlainSourceIntegrationTests : Akka.TestKit.Xunit2.TestKit
    {
        private const string KafkaUrl = "localhost:9092";

        private const string InitialMsg = "initial msg in topic, required to create the topic before any consumer subscribes to it";

        private readonly ActorMaterializer _materializer;

        public PlainSourceIntegrationTests(ITestOutputHelper output) 
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
                .RunWith(Sink.Ignore<Message<Null, string>>(), _materializer);
        }

        private TestSubscriber.Probe<string> CreateProbe(ConsumerSettings<Null, string> consumerSettings, string topic, ISubscription sub)
        {
            return Dsl.Consumer
                .PlainSource(consumerSettings, sub)
                .Where(c => !c.Value.Equals(InitialMsg))
                .Select(c =>
                {
                    Output.WriteLine($"Consumed: {c.Value}");
                    return c.Value;
                })
                .RunWith(this.SinkProbe<string>(), _materializer);
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_topicPartition_assignment()
        {
            int elementsCount = 100;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, topic1, Subscriptions.Assignment(new TopicPartition(topic1, 0)));
            
            probe.Request(elementsCount);
            foreach (var i in Enumerable.Range(1, elementsCount).Select(c => c.ToString()))
                probe.ExpectNext(i, TimeSpan.FromSeconds(10));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_topicPartitionOffset_assignment()
        {
            int elementsCount = 100;
            int offset = 50;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, topic1, Subscriptions.AssignmentWithOffset(new TopicPartitionOffset(topic1, 0, new Offset(offset))));

            probe
                .Request(elementsCount)
                .ExpectNextN(Enumerable.Range(offset, elementsCount - offset).Select(c => c.ToString()));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_subscribe_to_topic()
        {
            int elementsCount = 100;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, topic1, Subscriptions.Topics(topic1));

            probe.Request(elementsCount);
            foreach (var i in Enumerable.Range(1, elementsCount).Select(c => c.ToString()))
                probe.ExpectNext(i, TimeSpan.FromSeconds(10));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_should_fail_stage_if_broker_unavailable()
        {
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            var config = ConsumerSettings<Null, string>.Create(Sys, null, new StringDeserializer(Encoding.UTF8))
                .WithBootstrapServers("localhost:10092")
                .WithGroupId(group1);

            var probe = CreateProbe(config, topic1, Subscriptions.Assignment(new TopicPartition(topic1, 0)));
            probe.Request(1).ExpectError().Should().BeOfType<Exception>();
        }
    }
}
