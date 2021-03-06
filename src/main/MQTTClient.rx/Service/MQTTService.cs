﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using IMQTTClientRx.Model;
using IMQTTClientRx.Service;
using Microsoft.Extensions.DependencyInjection;
using MQTTClientRx.Extension;
using MQTTClientRx.Model;
using MQTTnet;
using MQTTnet.Core;
using MQTTnet.Core.Adapter;
using MQTTnet.Core.Client;
using MQTTnet.Core.Protocol;
using MQTTnet.Core.Serializer;

// ReSharper disable PossibleMultipleEnumeration

namespace MQTTClientRx.Service
{
    public class MQTTService : IMQTTService
    {
        private IMqttClient _client;
        private IMQTTClient _wrappedClient;

        public bool IsConnected { get; private set; }

        public (IObservable<IMQTTMessage> observableMessage, IMQTTClient client)
            CreateObservableMQTTService(
                IClientOptions options,
                IEnumerable<ITopicFilter> topicFilters = null,
                IWillMessage willMessage = null)
        {
            IsConnected = false;

            var services = new ServiceCollection()
                .AddMqttClient()
                .AddLogging()
                .BuildServiceProvider();

            _client = services.GetRequiredService<IMqttClient>();

            _wrappedClient = new MQTTClient(_client, this);

            IsConnected = false;

            var observable = Observable.Create<IMQTTMessage>(
                    async obs =>
                    {
                        var disposableConnect = Observable.FromEventPattern<MqttClientConnectedEventArgs>(
                                h => _client.Connected += h,
                                h => _client.Connected -= h)
                            .Subscribe(
                                async connectEvent =>
                                {
                                    Debug.WriteLine("Connected");
                                    if (topicFilters?.Any() ?? false)
                                    {
                                        try
                                        {
                                            await _wrappedClient.SubscribeAsync(topicFilters);
                                        }
                                        catch (Exception ex)
                                        {
                                            obs.OnError(ex);
                                        }
                                    }
                                },
                                obs.OnError,
                                obs.OnCompleted);

                        var disposableMessage = Observable.FromEventPattern<MqttApplicationMessageReceivedEventArgs>(
                                h => _client.ApplicationMessageReceived += h,
                                h => _client.ApplicationMessageReceived -= h)
                            .Subscribe(
                                msgEvent =>
                                {
                                    var message = new MQTTMessage
                                    {
                                        Payload = msgEvent.EventArgs.ApplicationMessage.Payload,
                                        Retain = msgEvent.EventArgs.ApplicationMessage.Retain,
                                        QualityOfServiceLevel =
                                            ConvertToQoSLevel(msgEvent.EventArgs.ApplicationMessage.QualityOfServiceLevel),
                                        Topic = msgEvent.EventArgs.ApplicationMessage.Topic
                                    };

                                    obs.OnNext(message);
                                },
                                obs.OnError,
                                obs.OnCompleted);

                        var disposableDisconnect = Observable.FromEventPattern<MqttClientDisconnectedEventArgs>(
                                h => _client.Disconnected += h,
                                h => _client.Disconnected -= h)
                            .Subscribe(
                                disconnectEvent =>
                                {
                                    if (!IsConnected) return;
                                    Debug.WriteLine("Disconnected");
                                    obs.OnCompleted();
                                },
                                obs.OnError,
                                obs.OnCompleted);

                        if (!IsConnected)
                        {
                            try
                            {
                                var opt = UnwrapOptions(options, willMessage);
                                await _client.ConnectAsync(opt);
                                IsConnected = true;
                            }
                            catch (Exception ex)
                            {
                                IsConnected = false;
                                obs.OnError(ex);

                            }
                        }

                        return new CompositeDisposable(
                            Disposable.Create(async () => { await CleanUp(_client); }),
                            disposableMessage,
                            disposableConnect,
                            disposableDisconnect);
                    })
                .FinallyAsync(async () => { await CleanUp(_client); })
                .Publish().RefCount();

            return (observable, _wrappedClient);
        }


        private async Task CleanUp(IMqttClient client)
        {
            if (client.IsConnected)
            {
                var disconnectTask = client.DisconnectAsync();
                var timeOutTask = Task.Delay(TimeSpan.FromSeconds(5));

                var result = await Task.WhenAny(disconnectTask, timeOutTask).ConfigureAwait(false);

                Debug.WriteLine(result == timeOutTask
                    ? "Disconnect Timed Out"
                    : "Disconnected Successfully");

                IsConnected = false;
            }
        }

        private static IMqttClientOptions UnwrapOptions(IClientOptions wrappedOptions, IWillMessage willMessage)
        {
            var optionsBuilder = new MqttClientOptionsBuilder();

            if (wrappedOptions.ConnectionType == ConnectionType.Tcp)
            {
                optionsBuilder.WithTcpServer(wrappedOptions.Uri.Host);
            }
            else
            {
                optionsBuilder.WithWebSocketServer(wrappedOptions.Uri.AbsoluteUri);
            }

            return optionsBuilder
                .WithWillMessage(WrapWillMessage(willMessage))
                .WithCleanSession(wrappedOptions.CleanSession)
                .WithClientId(wrappedOptions.ClientId ?? Guid.NewGuid().ToString().Replace("-", string.Empty))
                .WithTls(wrappedOptions.AllowUntrustedCertificates, wrappedOptions.IgnoreCertificateChainErrors,
                    wrappedOptions.IgnoreCertificateChainErrors, UnwrapCertificates(wrappedOptions.Certificates))
                .WithProtocolVersion(UnwrapProtocolVersion(wrappedOptions.ProtocolVersion))
                .WithCommunicationTimeout(wrappedOptions.DefaultCommunicationTimeout == default(TimeSpan)
                    ? TimeSpan.FromSeconds(10)
                    : wrappedOptions.DefaultCommunicationTimeout)
                .WithKeepAlivePeriod(wrappedOptions.KeepAlivePeriod == default(TimeSpan)
                    ? TimeSpan.FromSeconds(5)
                    : wrappedOptions.KeepAlivePeriod)
                .WithCredentials(wrappedOptions.UserName, wrappedOptions.Password)
                .Build();
       }

        private static MqttApplicationMessage WrapWillMessage(IWillMessage message)
        {
            if (message != null)
            {
                var applicationMessage = new MqttApplicationMessageBuilder();

                applicationMessage
                    .WithTopic(message.Topic)
                    .WithPayload(message.Payload)
                    .WithRetainFlag(message.Retain);

                ConvertToQualityOfServiceLevel(applicationMessage, message.QualityOfServiceLevel);

                return applicationMessage.Build();
            }

            return null;
        }

        private static byte[][] UnwrapCertificates(IEnumerable<byte[]> certificates)
        {
            return certificates?.ToArray();
        }

        private static MqttProtocolVersion UnwrapProtocolVersion(ProtocolVersion protocolVersion)
        {
            switch (protocolVersion)
            {
                case ProtocolVersion.ver310: return MqttProtocolVersion.V310;
                case ProtocolVersion.ver311: return MqttProtocolVersion.V311;
                default: throw new ArgumentException(protocolVersion.ToString());
            }
        }

        private static MqttClientTcpOptions UnwrapConnectionType(ConnectionType connectionType)
        {
            switch (connectionType)
            {
                default: throw new ArgumentOutOfRangeException(nameof(connectionType), connectionType, null);
            }
        }

        private static QoSLevel ConvertToQoSLevel(MqttQualityOfServiceLevel qos)
        {
            switch (qos)
            {
                case MqttQualityOfServiceLevel.AtMostOnce: return QoSLevel.AtMostOnce;
                case MqttQualityOfServiceLevel.AtLeastOnce: return QoSLevel.AtLeastOnce;
                case MqttQualityOfServiceLevel.ExactlyOnce: return QoSLevel.ExactlyOnce;
                default:
                    throw new ArgumentOutOfRangeException(nameof(qos), qos, null);
            }
        }

        private static void ConvertToQualityOfServiceLevel(MqttApplicationMessageBuilder builder, QoSLevel qos)
        {
            switch (qos)
            {
                case QoSLevel.AtMostOnce:
                    builder.WithAtMostOnceQoS();
                    break;
                case QoSLevel.AtLeastOnce:
                    builder.WithAtLeastOnceQoS();
                    break;
                case QoSLevel.ExactlyOnce:
                    builder.WithExactlyOnceQoS();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(qos), qos, null);
            }
        }
    }
}