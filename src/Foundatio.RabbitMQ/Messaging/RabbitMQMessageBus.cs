﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging {
    public class RabbitMQMessageBus : MessageBusBase<RabbitMQMessageBusOptions> {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly ConnectionFactory _factory;
        private IConnection _publisherClient;
        private IConnection _subscriberClient;
        private IModel _publisherChannel;
        private IModel _subscriberChannel;
        private bool _delayedExchangePluginEnabled = true;

        public RabbitMQMessageBus(RabbitMQMessageBusOptions options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            // Initialize the connection factory. automatic recovery will allow the connections to be restored
            // in case the server is restarted or there has been any network failures
            // Topology ( queues, exchanges, bindings and consumers) recovery "TopologyRecoveryEnabled" is already enabled
            // by default so no need to initialize it. NetworkRecoveryInterval is also by default set to 5 seconds.
            // it can always be fine tuned if needed.
            _factory = new ConnectionFactory { Uri = new Uri(options.ConnectionString), AutomaticRecoveryEnabled = true };
        }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_subscriberChannel != null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

            using (await _lock.LockAsync().AnyContext()) {
                if (_subscriberChannel != null)
                    return;

                _subscriberClient = CreateConnection();
                _subscriberChannel = _subscriberClient.CreateModel();

                // If InitPublisher is called first, then we will never come in this if clause.
                if (!CreateDelayedExchange(_subscriberChannel)) {
                    _subscriberClient = CreateConnection();
                    _subscriberChannel = _subscriberClient.CreateModel();
                    CreateRegularExchange(_subscriberChannel);
                }

                CreateQueue(_subscriberChannel);
                var consumer = new EventingBasicConsumer(_subscriberChannel);
                consumer.Received += OnMessage;
                consumer.Shutdown += OnConsumerShutdown;

                _subscriberChannel.BasicConsume(_options.Topic, true, consumer);
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("The unique channel number for the subscriber is : {ChannelNumber}", _subscriberChannel.ChannelNumber);
            }
        }

        private void OnConsumerShutdown(object sender, ShutdownEventArgs e) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Consumer shutdown. Reply Code: {ReplyCode} Reason: {ReplyText}", e.ReplyCode, e.ReplyText);
        }

        private void OnMessage(object sender, BasicDeliverEventArgs e) {
            if (_subscribers.IsEmpty)
                return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("OnMessageAsync({MessageId})", e.BasicProperties?.MessageId);
            MessageBusData message;
            try {
                message = _serializer.Deserialize<MessageBusData>(e.Body);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "OnMessageAsync({MessageId}) Error deserializing messsage: {Message}", e.BasicProperties?.MessageId, ex.Message);
                return;
            }

            SendMessageToSubscribers(message, _serializer);
        }

        protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) {
            if (_publisherChannel != null)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_publisherChannel != null)
                    return;

                // Create the client connection, channel, declares the exchange, queue and binds
                // the exchange with the publisher queue. It requires the name of our exchange, exhange type, durability and autodelete.
                // For now we are using same autoDelete for both exchange and queue ( it will survive a server restart )
                _publisherClient = CreateConnection();
                _publisherChannel = _publisherClient.CreateModel();

                // We first attempt to create "x-delayed-type". For this plugin should be installed.
                // However, we plugin is not installed this will throw an exception. In that case
                // we attempt to create regular exchange. If regular exchange also throws and exception
                // then trouble shoot the problem.
                if (!CreateDelayedExchange(_publisherChannel)) {
                    // if the initial exchange creation was not successful then we must close the previous connection
                    // and establish the new client connection and model otherwise you will keep recieving failure in creation
                    // of the regular exchange too.
                    _publisherClient = CreateConnection();
                    _publisherChannel = _publisherClient.CreateModel();
                    CreateRegularExchange(_publisherChannel);
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("The unique channel number for the publisher is : {ChannelNumber}", _publisherChannel.ChannelNumber);
            }
        }

        /// <summary>
        /// Publish the message
        /// </summary>
        /// <param name="messageType"></param>
        /// <param name="message"></param>
        /// <param name="delay">Along with the delay value, _delayExchange should also be set to true</param>
        /// <param name="cancellationToken"></param>
        /// <remarks>RabbitMQ has an upper limit of 2GB for messages.BasicPublish blocking AMQP operations.
        /// The rule of thumb is: avoid sharing channels across threads.
        /// Publishers in your application that publish from separate threads should use their own channels.
        /// The same is a good idea for consumers.</remarks>
        protected override Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            var data = _serializer.SerializeToBytes(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = _serializer.SerializeToBytes(message)
            });

            // if the rabbitmq plugin is not availaible then use the base class delay mechanism
            if (!_delayedExchangePluginEnabled && delay.HasValue && delay.Value > TimeSpan.Zero) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                return AddDelayedMessageAsync(messageType, message, delay.Value);
            }

            var basicProperties = _publisherChannel.CreateBasicProperties();
            if (_options.DefaultMessageTimeToLive.HasValue)
                basicProperties.Expiration = _options.DefaultMessageTimeToLive.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);

            // RabbitMQ only supports delayed messages with a third party plugin called "rabbitmq_delayed_message_exchange"
            if (_delayedExchangePluginEnabled && delay.HasValue && delay.Value > TimeSpan.Zero) {
                // Its necessary to typecast long to int because rabbitmq on the consumer side is reading the
                // data back as signed (using BinaryReader#ReadInt64). You will see the value to be negative
                // and the data will be delievered immediately.
                basicProperties.Headers = new Dictionary<string, object> { { "x-delay", Convert.ToInt32(delay.Value.TotalMilliseconds) } };

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
            } else {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Message Publish: {MessageType}", messageType.FullName);
            }

            // The publication occurs with mandatory=false
            _publisherChannel.BasicPublish(_options.ExchangeName, String.Empty, basicProperties, data);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Connect to a broker - RabbitMQ
        /// </summary>
        /// <returns></returns>
        private IConnection CreateConnection() {
            return _factory.CreateConnection();
        }

        /// <summary>
        /// Attempts to create the delayed exchange.
        /// </summary>
        /// <param name="model"></param>
        /// <returns>true if the delayed exchange was successfully declared. Which means plugin was installed.</returns>
        private bool CreateDelayedExchange(IModel model) {
            bool success = true;
            if (!_delayedExchangePluginEnabled)
                return true;

            try {
                // This exchange is a delayed exchange (direct). You need rabbitmq_delayed_message_exchange plugin to RabbitMQ
                // Disclaimer : https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/ . Please read the *Performance
                // Impact* of the delayed exchange type.
                var args = new Dictionary<string, object> { { "x-delayed-type", ExchangeType.Fanout } };
                model.ExchangeDeclare(_options.ExchangeName, "x-delayed-message", _options.IsExchangeDurable, _options.IsExchangeExclusive, args);
            } catch (OperationInterruptedException ex) {
                if (ex.ShutdownReason.ReplyCode == 503) {
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation(ex, "Not able to create x-delayed-type exchange");
                    _delayedExchangePluginEnabled = false;
                    success = false;
                }
            }

            return success;
        }

        private void CreateRegularExchange(IModel model) {
            model.ExchangeDeclare(_options.ExchangeName, ExchangeType.Fanout, _options.IsExchangeDurable, _options.IsExchangeExclusive, null);
        }

        /// <summary>
        /// The client sends a message to an exchange and attaches a routing key to it.
        /// The message is sent to all queues with the matching routing key. Each queue has a
        /// receiver attached which will process the message. We’ll initiate a dedicated message
        /// exchange and not use the default one. Note that a queue can be dedicated to one or more routing keys.
        /// </summary>
        /// <param name="model">channel</param>
        private void CreateQueue(IModel model) {
            // Setup the queue where the messages will reside - it requires the queue name and durability.
            // Durable (the queue will survive a broker restart)
            // Exclusive (used by only one connection and the queue will be deleted when that connection closes)
            // Auto-delete (queue is deleted when last consumer unsubscribes)
            // Arguments (some brokers use it to implement additional features like message TTL)
            model.QueueDeclare(_options.Topic, _options.IsQueueDurable, _options.IsQueueExclusive, _options.IsQueueAutoDeleteEnabled, _options.Arguments);

            // bind the queue with the exchange.
            model.QueueBind(_options.Topic, _options.ExchangeName, "");
        }

        public override void Dispose() {
            base.Dispose();
            ClosePublisherConnection();
            CloseSubscriberConnection();
        }

        private void ClosePublisherConnection() {
            if (_publisherClient == null)
                return;

            using (_lock.Lock()) {
                if (_publisherClient == null)
                    return;

                if (_publisherChannel != null &&_publisherChannel.IsOpen)
                    _publisherChannel.Close();

                _publisherChannel?.Dispose();
                _publisherChannel = null;

                if (_publisherClient != null && _publisherClient.IsOpen)
                    _publisherClient.Close();

                _publisherClient?.Dispose();
                _publisherClient = null;
            }
        }

        private void CloseSubscriberConnection() {
            if (_subscriberClient == null)
                return;

            using (_lock.Lock()) {
                if (_subscriberClient == null)
                    return;

                if (_subscriberChannel != null && _subscriberChannel.IsOpen)
                    _subscriberChannel.Close();

                _subscriberChannel?.Dispose();
                _subscriberChannel = null;

                if (_subscriberClient != null && _subscriberClient.IsOpen)
                    _subscriberClient.Close();

                _subscriberClient?.Dispose();
                _subscriberClient = null;
            }
        }
    }
}