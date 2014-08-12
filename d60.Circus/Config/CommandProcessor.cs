﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using d60.Circus.Aggregates;
using d60.Circus.Commands;
using d60.Circus.Events;
using d60.Circus.Exceptions;
using d60.Circus.Extensions;
using d60.Circus.Numbers;

namespace d60.Circus.Config
{
    /// <summary>
    /// Main command processor event emitter thing - if you can successfully create this bad boy, you have a fully functioning event sourcing thing going for you
    /// </summary>
    public class CommandProcessor
    {
        const string InnerProcessMethodName = "InnerProcessCommand";

        static readonly MethodInfo CommandProcessorMethod =
            MethodBase.GetCurrentMethod().DeclaringType
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == InnerProcessMethodName && m.IsGenericMethod);

        readonly Options _options = new Options();
        readonly Retryer _retryer = new Retryer();
        readonly ICommandMapper _commandMapper = new CommandMapper();
        readonly IEventStore _eventStore;
        readonly IAggregateRootRepository _aggregateRootRepository;
        readonly IEventDispatcher _eventDispatcher;

        public CommandProcessor(IEventStore eventStore, IAggregateRootRepository aggregateRootRepository, IEventDispatcher eventDispatcher)
        {
            if (eventStore == null) throw new ArgumentNullException("eventStore");
            if (aggregateRootRepository == null) throw new ArgumentNullException("aggregateRootRepository");
            if (eventDispatcher == null) throw new ArgumentNullException("eventDispatcher");

            if (CommandProcessorMethod == null)
            {
                throw new ApplicationException(string.Format("Could not find the expected eventDispatcher method '{0}' on {1}", InnerProcessMethodName, GetType()));
            }

            _eventStore = eventStore;
            _aggregateRootRepository = aggregateRootRepository;
            _eventDispatcher = eventDispatcher;
        }

        /// <summary>
        /// Initializes the views, giving them a chance to catch up to the current state
        /// </summary>
        public void Initialize()
        {
            _eventDispatcher.Initialize(_eventStore, Options.PurgeExistingViews);
        }

        public Options Options
        {
            get { return _options; }
        }

        /// <summary>
        /// Processes the specified command by invoking the generic eventDispatcher method
        /// </summary>
        public void ProcessCommand(Command command)
        {
            var commandType = command.GetType();
            var aggregateRootType = GetAggregateRootType(commandType);

            try
            {
                CommandProcessorMethod
                    .MakeGenericMethod(aggregateRootType, commandType)
                    .Invoke(this, new object[] { command });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException;
                inner.PreserveStackTrace();
                throw inner;
            }
        }

        // ReSharper disable UnusedMember.Local
        /// <summary>
        /// This method is called via reflection!
        /// </summary>
        void InnerProcessCommand<TAggregateRoot, TCommand>(TCommand command)
            where TCommand : Command<TAggregateRoot>
            where TAggregateRoot : AggregateRoot, new()
        {
            var emittedDomainEvents = new List<DomainEvent>();

            try
            {
                var batchId = Guid.NewGuid();

                _retryer.RetryOn<ConcurrencyException>(() =>
                {
                    var eventsFromThisUnitOfWork = DoProcessCommand<TAggregateRoot, TCommand>(batchId, command);

                    emittedDomainEvents.AddRange(eventsFromThisUnitOfWork);
                }, maxRetries: Options.MaxRetries);
            }
            catch (Exception exception)
            {
                // ordinary re-throw if exception is a domain exception
                if (Options.DomainExceptionTypes.Contains(exception.GetType()))
                {
                    throw;
                }

                throw CommandProcessingException.Create(command, exception);
            }

            if (!emittedDomainEvents.Any()) return;

            try
            {
                // when we come to this place, we deliver the events to the view manager
                _eventDispatcher.Dispatch(_eventStore, emittedDomainEvents);
            }
            catch (Exception exception)
            {
                var message =
                    string.Format(
                        "An error ocurred while dispatching events with global sequence numbers {0} to event dispatcher." +
                        " The events were properly saved in the event store, but you might need to re-initialize the" +
                        " event dispatcher",
                        string.Join(", ", emittedDomainEvents.Select(e => e.GetGlobalSequenceNumber())));

                throw new ApplicationException(message, exception);
            }
        }
        // ReSharper restore UnusedMember.Local

        IEnumerable<DomainEvent> DoProcessCommand<TAggregateRoot, TCommand>(Guid batchId, TCommand command)
            where TCommand : Command<TAggregateRoot>
            where TAggregateRoot : AggregateRoot, new()
        {
            var unitOfWork = new RealUnitOfWork();
            var handler = _commandMapper.GetHandlerFor<TCommand, TAggregateRoot>();
            var aggregateRootInfo = _aggregateRootRepository.Get<TAggregateRoot>(command.AggregateRootId, unitOfWork: unitOfWork);
            var aggregateRoot = aggregateRootInfo.AggregateRoot;

            aggregateRoot.SequenceNumberGenerator = new CachingSequenceNumberGenerator(aggregateRootInfo.LastSeqNo + 1);

            unitOfWork.AddToCache(aggregateRoot, aggregateRootInfo.LastGlobalSeqNo);

            handler(command, aggregateRoot);

            var emittedEvents = unitOfWork.EmittedEvents.ToList();

            if (!emittedEvents.Any()) return emittedEvents;

            foreach (var e in emittedEvents)
            {
                e.Meta.Merge(command.Meta);
            }

            // first: save the events
            _eventStore.Save(batchId, emittedEvents);

            return emittedEvents;
        }

        static Type GetAggregateRootType(Type commandType)
        {
            var baseCommandType = commandType;

            do
            {
                if (baseCommandType.IsGenericType && baseCommandType.GetGenericTypeDefinition() == typeof(Command<>))
                {
                    return baseCommandType.GetGenericArguments().Single();
                }
                baseCommandType = baseCommandType.BaseType;
            } while (baseCommandType != null);

            throw new ArgumentException(string.Format("Could not find the generic Command<> base type from which {0} should have been derived - please derive commands off of the generic Command<> type, closing it with the type of the aggregate root that the command targets, e.g. Command<SomeAggregateRoot>", commandType));
        }
    }
}