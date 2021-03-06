﻿//-----------------------------------------------------------------------
// <copyright file="ClusterClient.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Actor.Internal;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.Event;
using Akka.Remote;
using Akka.Util.Internal;

namespace Akka.Cluster.Tools.Client
{
    /// <summary>
    /// This actor is intended to be used on an external node that is not member
    /// of the cluster. It acts like a gateway for sending messages to actors
    /// somewhere in the cluster. From the initial contact points it will establish
    /// a connection to a <see cref="ClusterReceptionist"/> somewhere in the cluster. It will
    /// monitor the connection to the receptionist and establish a new connection if
    /// the link goes down. When looking for a new receptionist it uses fresh contact
    /// points retrieved from previous establishment, or periodically refreshed
    /// contacts, i.e. not necessarily the initial contact points.
    /// </summary>
    public sealed class ClusterClient : ActorBase
    {
        #region Messages

        /// <summary>
        /// The message will be delivered to one recipient with a matching path, if any such
        /// exists. If several entries match the path the message will be delivered
        /// to one random destination. The sender of the message can specify that local
        /// affinity is preferred, i.e. the message is sent to an actor in the same local actor
        /// system as the used receptionist actor, if any such exists, otherwise random to any other
        /// matching entry.
        /// </summary>
        [Serializable]
        public sealed class Send
        {
            /// <summary>
            /// TBD
            /// </summary>
            public string Path { get; }
            /// <summary>
            /// TBD
            /// </summary>
            public object Message { get; }
            /// <summary>
            /// TBD
            /// </summary>
            public bool LocalAffinity { get; }

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="path">TBD</param>
            /// <param name="message">TBD</param>
            /// <param name="localAffinity">TBD</param>
            public Send(string path, object message, bool localAffinity = false)
            {
                Path = path;
                Message = message;
                LocalAffinity = localAffinity;
            }
        }

        /// <summary>
        /// The message will be delivered to all recipients with a matching path.
        /// </summary>
        [Serializable]
        public sealed class SendToAll
        {
            /// <summary>
            /// TBD
            /// </summary>
            public string Path { get; }
            /// <summary>
            /// TBD
            /// </summary>
            public object Message { get; }

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="path">TBD</param>
            /// <param name="message">TBD</param>
            public SendToAll(string path, object message)
            {
                Path = path;
                Message = message;
            }
        }

        /// <summary>
        /// The message will be delivered to all recipients Actors that have been registered as subscribers to
        /// to the named topic.
        /// </summary>
        [Serializable]
        public sealed class Publish
        {
            /// <summary>
            /// TBD
            /// </summary>
            public string Topic { get; }
            /// <summary>
            /// TBD
            /// </summary>
            public object Message { get; }

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="topic">TBD</param>
            /// <param name="message">TBD</param>
            public Publish(string topic, object message)
            {
                Topic = topic;
                Message = message;
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        [Serializable]
        public sealed class SetUnhandledMessagesMediator
        {
            public IActorRef ActorRef { get; }
            public SetUnhandledMessagesMediator(IActorRef actorRef)
            {
                ActorRef = actorRef;
            }
        }

        /// <summary> 
        /// </summary>
        [Serializable]
        public sealed class Subscribe
        {
            public string Topic { get; }
            public string Group { get; }

            public Subscribe(string topic, string group = null)
            {
                Topic = topic;
                Group = group;
            }
        }

        [Serializable]
        public sealed class Unsubscribe
        {
            public Subscribe Subscribe { get; }
            public Unsubscribe(Subscribe subscribe)
            {
                Subscribe = subscribe;
            }
        }

        [Serializable]
        internal sealed class RefreshContactsTick
        {
            /// <summary>
            /// TBD
            /// </summary>
            public static RefreshContactsTick Instance { get; } = new RefreshContactsTick();
            private RefreshContactsTick() { }
        }

        /// <summary>
        /// TBD
        /// </summary>
        [Serializable]
        internal sealed class HeartbeatTick
        {
            /// <summary>
            /// TBD
            /// </summary>
            public static HeartbeatTick Instance { get; } = new HeartbeatTick();
            private HeartbeatTick() { }
        }

        /// <summary>
        /// TBD
        /// </summary>
        [Serializable]
        internal sealed class ReconnectTimeout
        {
            /// <summary>
            /// TBD
            /// </summary>
            public static ReconnectTimeout Instance { get; } = new ReconnectTimeout();
            private ReconnectTimeout() { }
        }

        #endregion

        /// <summary>
        /// Factory method for <see cref="ClusterClient"/> <see cref="Actor.Props"/>.
        /// </summary>
        /// <param name="settings">TBD</param>
        /// <exception cref="ArgumentNullException">TBD</exception>
        /// <returns>TBD</returns>
        public static Props Props(ClusterClientSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            return Actor.Props.Create(() => new ClusterClient(settings)).WithDeploy(Deploy.Local);
        }

        private ILoggingAdapter _log = Context.GetLogger();
        private readonly ClusterClientSettings _settings;
        private readonly DeadlineFailureDetector _failureDetector;
        private ImmutableHashSet<ActorPath> _contactPaths;
        private readonly ActorSelection[] _initialContactsSelections;
        private ActorSelection[] _contacts;
        private ImmutableHashSet<ActorPath> _contactPathsPublished;
        private ImmutableList<IActorRef> _subscribers;
        private readonly ICancelable _heartbeatTask;
        private ICancelable _refreshContactsCancelable;
        private readonly Queue<Tuple<object, IActorRef>> _buffer;
        private IActorRef _unhandledMessagesMediator = ActorRefs.Nobody;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="settings">TBD</param>
        /// <exception cref="ArgumentException">TBD</exception>
        /// <returns>TBD</returns>
        public ClusterClient(ClusterClientSettings settings)
        {
            if (settings.InitialContacts.Count == 0)
            {
                throw new ArgumentException("Initial contacts for cluster client cannot be empty");
            }

            _settings = settings;
            _failureDetector = new DeadlineFailureDetector(_settings.AcceptableHeartbeatPause, _settings.HeartbeatInterval);

            _contactPaths = settings.InitialContacts.ToImmutableHashSet();
            _initialContactsSelections = _contactPaths.Select(Context.ActorSelection).ToArray();
            _contacts = _initialContactsSelections;

            SendGetContacts();

            _contactPathsPublished = ImmutableHashSet<ActorPath>.Empty;
            _subscribers = ImmutableList<IActorRef>.Empty;

            _heartbeatTask = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
                settings.HeartbeatInterval,
                settings.HeartbeatInterval,
                Self,
                HeartbeatTick.Instance,
                Self);

            _refreshContactsCancelable = null;
            ScheduleRefreshContactsTick(settings.EstablishingGetContactsInterval);
            Self.Tell(RefreshContactsTick.Instance);

            _buffer = new Queue<Tuple<object, IActorRef>>();
        }

        private void ScheduleRefreshContactsTick(TimeSpan interval)
        {
            if (_refreshContactsCancelable != null)
            {
                _refreshContactsCancelable.Cancel();
                _refreshContactsCancelable = null;
            }

            _refreshContactsCancelable = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
                interval,
                interval,
                Self,
                RefreshContactsTick.Instance,
                Self);
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected override void PostStop()
        {
            base.PostStop();
            _heartbeatTask.Cancel();

            if (_refreshContactsCancelable != null)
            {
                _refreshContactsCancelable.Cancel();
                _refreshContactsCancelable = null;
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="message">TBD</param>
        /// <returns>TBD</returns>
        protected override bool Receive(object message)
        {
            return Establishing(message);
        }

        private bool Establishing(object message)
        {
            ICancelable connectTimerCancelable = null;
            if (_settings.ReconnectTimeout.HasValue)
            {
                connectTimerCancelable = Context.System.Scheduler.ScheduleTellOnceCancelable(
                    _settings.ReconnectTimeout.Value,
                    Self,
                    ReconnectTimeout.Instance,
                    Self);
            }

            if (message is ClusterReceptionist.Contacts)
            {
                var contacts = (ClusterReceptionist.Contacts)message;

                if (contacts.ContactPoints.Count > 0)
                {
                    _contactPaths = contacts.ContactPoints.Select(ActorPath.Parse).ToImmutableHashSet();
                    _contacts = _contactPaths.Select(Context.ActorSelection).ToArray();
                    _contacts.ForEach(c => c.Tell(new Identify(null)));
                }

                PublishContactPoints();
            }
            else if (message is ActorIdentity)
            {
                var actorIdentify = (ActorIdentity)message;
                var receptionist = actorIdentify.Subject;

                if (receptionist != null)
                {
                    _log.Info("Connected to [{0}]", receptionist.Path);
                    ScheduleRefreshContactsTick(_settings.RefreshContactsInterval);
                    SendBuffered(receptionist);
                    Context.Become(Active(receptionist));
                    connectTimerCancelable?.Cancel();
                    _failureDetector.HeartBeat();
                }
                else
                {
                    // ok, use another instead
                }
            }
            else if (message is HeartbeatTick)
            {
                _failureDetector.HeartBeat();
            }
            else if (message is RefreshContactsTick)
            {
                SendGetContacts();
            }
            else if (message is Send)
            {
                var send = (Send)message;
                Buffer(new PublishSubscribe.Send(send.Path, send.Message, send.LocalAffinity));
            }
            else if (message is SendToAll)
            {
                var sendToAll = (SendToAll)message;
                Buffer(new PublishSubscribe.SendToAll(sendToAll.Path, sendToAll.Message));
            }
            else if (message is Publish)
            {
                var publish = (Publish)message;
                Buffer(new PublishSubscribe.Publish(publish.Topic, publish.Message));
            }
            else if (message is SetUnhandledMessagesMediator)
            {
                var mediator = (SetUnhandledMessagesMediator)message;
                SetAndWatchUnhandledMessagesMediator(mediator);
            }
            else if (message is Terminated)
            {
                var terminated = (Terminated)message;
                OnTerminated(terminated);
            }
            else if (message is Subscribe)
            {
                var subscribe = (Subscribe)message;
                Buffer(new PublishSubscribe.Subscribe(subscribe.Topic, Self, subscribe.Group), Self);
            }
            else if (message is Unsubscribe)
            {
                var unsubscribe = (Unsubscribe)message;
                Buffer(new PublishSubscribe.Unsubscribe(unsubscribe.Subscribe.Topic, Self, unsubscribe.Subscribe.Group), Self);
            }
            else if (message is ReconnectTimeout)
            {
                _log.Warning("Receptionist reconnect not successful within {0} stopping cluster client", _settings.ReconnectTimeout);
                Context.Stop(Self);
            }
            else
            {
                return ContactPointMessages(message);
            }

            return true;
        }

        private Receive Active(IActorRef receptionist)
        {
            return message =>
            {
                if (message is Send)
                {
                    var send = (Send)message;
                    receptionist.Forward(new PublishSubscribe.Send(send.Path, send.Message, send.LocalAffinity));
                }
                else if (message is SendToAll)
                {
                    var sendToAll = (SendToAll)message;
                    receptionist.Forward(new PublishSubscribe.SendToAll(sendToAll.Path, sendToAll.Message));
                }
                else if (message is Publish)
                {
                    var publish = (Publish)message;
                    receptionist.Forward(new PublishSubscribe.Publish(publish.Topic, publish.Message));
                }
                else if (message is SetUnhandledMessagesMediator)
                {
                    var mediator = (SetUnhandledMessagesMediator)message;
                    SetAndWatchUnhandledMessagesMediator(mediator);
                }
                else if (message is Terminated)
                {
                    var terminated = (Terminated)message;
                    OnTerminated(terminated);
                }
                else if (message is Subscribe)
                {
                    var subscribe = (Subscribe)message;
                    receptionist.Tell(new PublishSubscribe.Subscribe(subscribe.Topic, Self, subscribe.Group));
                }
                else if (message is Unsubscribe)
                {
                    var unsubscribe = (Unsubscribe)message;
                    receptionist.Tell(new PublishSubscribe.Unsubscribe(unsubscribe.Subscribe.Topic, Self, unsubscribe.Subscribe.Group));
                }
                else if (message is SubscribeAck)
                {
                    var ack = (SubscribeAck)message;
                    _log.Debug(ack.ToString());
                }
                else if (message is UnsubscribeAck)
                {
                    var ack = (UnsubscribeAck)message;
                    _log.Debug(ack.ToString());
                }
                else if (message is HeartbeatTick)
                {
                    if (!_failureDetector.IsAvailable)
                    {
                        _log.Info("Lost contact with [{0}], reestablishing connection", receptionist);
                        SendGetContacts();
                        ScheduleRefreshContactsTick(_settings.EstablishingGetContactsInterval);
                        Context.Become(Establishing);
                        _failureDetector.HeartBeat();
                    }
                    else
                    {
                        receptionist.Tell(ClusterReceptionist.Heartbeat.Instance);
                    }
                }
                else if (message is ClusterReceptionist.HeartbeatRsp)
                {
                    _failureDetector.HeartBeat();
                }
                else if (message is RefreshContactsTick)
                {
                    receptionist.Tell(ClusterReceptionist.GetContacts.Instance);
                }
                else if (message is ClusterReceptionist.Contacts)
                {
                    var contacts = (ClusterReceptionist.Contacts)message;

                    // refresh of contacts
                    if (contacts.ContactPoints.Count > 0)
                    {
                        _contactPaths = contacts.ContactPoints.Select(ActorPath.Parse).ToImmutableHashSet();
                        _contacts = _contactPaths.Select(Context.ActorSelection).ToArray();
                    }
                    PublishContactPoints();
                }
                else if (message is ActorIdentity)
                {
                    // ok, from previous establish, already handled
                }
                else
                {
                    return ContactPointMessages(message) || TryForwardUnhandledMessage(message);
                }

                return true;
            };
        }

        private bool ContactPointMessages(object message)
        {
            if (message is SubscribeContactPoints)
            {
                var subscriber = Sender;
                subscriber.Tell(new ContactPoints(_contactPaths));
                _subscribers = _subscribers.Add(subscriber);
                Context.Watch(subscriber);
            }
            else if (message is UnsubscribeContactPoints)
            {
                var subscriber = Sender;
                _subscribers = _subscribers.Where(c => !c.Equals(subscriber)).ToImmutableList();
            }
            else if (message is Terminated)
            {
                var terminated = (Terminated)message;
                Self.Tell(UnsubscribeContactPoints.Instance, terminated.ActorRef);
            }
            else if (message is GetContactPoints)
            {
                Sender.Tell(new ContactPoints(_contactPaths));
            }
            else return false;

            return true;
        }

        private void SendGetContacts()
        {
            ActorSelection[] sendTo;
            if (_contacts.Length == 0)
                sendTo = _initialContactsSelections;
            else if (_contacts.Length == 1)
                sendTo = _initialContactsSelections.Union(_contacts).ToArray();
            else
                sendTo = _contacts;

            if (_log.IsDebugEnabled)
                _log.Debug("Sending GetContacts to [{0}]", string.Join(", ", sendTo.AsEnumerable()));

            sendTo.ForEach(c => c.Tell(ClusterReceptionist.GetContacts.Instance));
        }

        private void Buffer(object message, IActorRef sender = null)
        {
            if (_settings.BufferSize == 0)
            {
                _log.Debug("Receptionist not available and buffering is disabled, dropping message [{0}]", message.GetType().Name);
            }
            else if (_buffer.Count == _settings.BufferSize)
            {
                var m = _buffer.Dequeue();
                _log.Debug("Receptionist not available, buffer is full, dropping first message [{0}]", m.Item1.GetType().Name);
                _buffer.Enqueue(Tuple.Create(message, sender ?? Sender));
            }
            else
            {
                _log.Debug("Receptionist not available, buffering message type [{0}]", message.GetType().Name);
                _buffer.Enqueue(Tuple.Create(message, sender ?? Sender));
            }
        }

        private void SendBuffered(IActorRef receptionist)
        {
            _log.Debug("Sending buffered messages to receptionist");
            while (_buffer.Count != 0)
            {
                var t = _buffer.Dequeue();
                receptionist.Tell(t.Item1, t.Item2);
            }
        }

        private void PublishContactPoints()
        {
            foreach (var cp in _contactPaths)
            {
                if (!_contactPathsPublished.Contains(cp))
                {
                    var contactPointAdded = new ContactPointAdded(cp);
                    _subscribers.ForEach(s => s.Tell(contactPointAdded));
                }
            }

            foreach (var cp in _contactPathsPublished)
            {
                if (!_contactPaths.Contains(cp))
                {
                    var contactPointRemoved = new ContactPointRemoved(cp);
                    _subscribers.ForEach(s => s.Tell(contactPointRemoved));
                }
            }

            _contactPathsPublished = _contactPaths;
        }

        private void SetAndWatchUnhandledMessagesMediator(SetUnhandledMessagesMediator mediator)
        {
            if (!_unhandledMessagesMediator.IsNobody())
                Context.Unwatch(_unhandledMessagesMediator);

            _unhandledMessagesMediator = mediator.ActorRef;
            if (!_unhandledMessagesMediator.IsNobody())
                Context.Watch(_unhandledMessagesMediator);
        }

        private void OnTerminated(Terminated terminated)
        {
            if (terminated.ActorRef.Equals(_unhandledMessagesMediator))
                _unhandledMessagesMediator = ActorRefs.Nobody;
            //TODO: do I need to supervise it fully and try to restart or recreate by using factory, or leave it for whoever created it?
        }

        private bool TryForwardUnhandledMessage(object message)
        {
            if (_unhandledMessagesMediator.IsNobody())
                return false;

            _unhandledMessagesMediator.Tell(message);
            return true;
        }
    }

    /// <summary>
    /// Declares a super type for all events emitted by the `ClusterClient`
    /// in relation to contact points being added or removed.
    /// </summary>
    public interface IContactPointChange
    {
        /// <summary>
        /// TBD
        /// </summary>
        ActorPath ContactPoint { get; }
    }

    /// <summary>
    /// Emitted to a subscriber when contact points have been
    /// received by the <see cref="ClusterClient"/> and a new one has been added.
    /// </summary>
    public sealed class ContactPointAdded : IContactPointChange
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="contactPoint">TBD</param>
        public ContactPointAdded(ActorPath contactPoint)
        {
            ContactPoint = contactPoint;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public ActorPath ContactPoint { get; }
    }

    /// <summary>
    /// Emitted to a subscriber when contact points have been
    /// received by the <see cref="ClusterClient"/> and a new one has been added.
    /// </summary>
    public sealed class ContactPointRemoved : IContactPointChange
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="contactPoint">TBD</param>
        public ContactPointRemoved(ActorPath contactPoint)
        {
            ContactPoint = contactPoint;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public ActorPath ContactPoint { get; }
    }

    /// <summary>
    /// TBD
    /// </summary>
    public interface ISubscribeContactPoints
    {
    }

    /// <summary>
    /// Subscribe to a cluster client's contact point changes where
    /// it is guaranteed that a sender receives the initial state
    /// of contact points prior to any events in relation to them
    /// changing.
    /// The sender will automatically become unsubscribed when it
    /// terminates.
    /// </summary>
    public sealed class SubscribeContactPoints : ISubscribeContactPoints
    {
        /// <summary>
        /// TBD
        /// </summary>
        public static readonly SubscribeContactPoints Instance = new SubscribeContactPoints();
        private SubscribeContactPoints() { }
    }

    /// <summary>
    /// TBD
    /// </summary>
    public interface IUnsubscribeContactPoints
    {
    }

    /// <summary>
    /// Explicitly unsubscribe from contact point change events.
    /// </summary>
    public sealed class UnsubscribeContactPoints : IUnsubscribeContactPoints
    {
        /// <summary>
        /// TBD
        /// </summary>
        public static readonly UnsubscribeContactPoints Instance = new UnsubscribeContactPoints();
        private UnsubscribeContactPoints() { }
    }

    /// <summary>
    /// TBD
    /// </summary>
    public interface IGetContactPoints
    {
    }

    /// <summary>
    /// Get the contact points known to this client. A <see cref="ContactPoints"/> message
    /// will be replied.
    /// </summary>
    public sealed class GetContactPoints : IGetContactPoints
    {
        /// <summary>
        /// TBD
        /// </summary>
        public static readonly GetContactPoints Instance = new GetContactPoints();
        private GetContactPoints() { }
    }

    /// <summary>
    /// The reply to <see cref="GetContactPoints"/>.
    /// </summary>
    public sealed class ContactPoints
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="contactPoints">TBD</param>
        public ContactPoints(IImmutableSet<ActorPath> contactPoints)
        {
            ContactPointsList = contactPoints;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public IImmutableSet<ActorPath> ContactPointsList { get; }
    }
}
