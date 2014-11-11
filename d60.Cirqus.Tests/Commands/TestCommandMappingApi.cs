﻿using d60.Cirqus.Aggregates;
using d60.Cirqus.Commands;
using d60.Cirqus.Config;
using d60.Cirqus.Events;
using d60.Cirqus.Serialization;
using d60.Cirqus.Testing.Internals;
using NUnit.Framework;
using TestContext = d60.Cirqus.Testing.TestContext;

namespace d60.Cirqus.Tests.Commands
{
    [TestFixture]
    public class TestCommandMappingApi : FixtureBase
    {
        readonly JsonDomainEventSerializer _serializer = new JsonDomainEventSerializer();
        ICommandProcessor _realCommandProcessor;
        TestContext _fakeCommandProcessor;

        protected override void DoSetUp()
        {
            var commandMappings = new CommandMappings()
                .Map<RawRootCommand>((context, command) =>
                {
                    var instance = context.Load<Root>(command.AggregateRootId, createIfNotExists: true);
                    instance.DoStuff();
                });

            _realCommandProcessor = CommandProcessor.With()
                .EventStore(e => e.Registrar.Register<IEventStore>(c => new InMemoryEventStore(_serializer)))
                .Options(o => o.AddCommandMappings(commandMappings))
                .Create();

            RegisterForDisposal(_realCommandProcessor);

            _fakeCommandProcessor = new TestContext()
                .AddCommandMappings(commandMappings);
        }

        [Test]
        public void CanExecuteRawBaseCommandWithRealCommandProcessor()
        {
            _realCommandProcessor.ProcessCommand(new RawRootCommand
            {
                AggregateRootId = "hej"
            });
        }

        [Test]
        public void CanExecuteRawBaseCommandWithFakeCommandProcessor()
        {
            _fakeCommandProcessor.ProcessCommand(new RawRootCommand
            {
                AggregateRootId = "hej"
            });
        }

        public class RawRootCommand : Command
        {
            public string AggregateRootId { get; set; }
        }

        public class Root : AggregateRoot, IEmit<Event>
        {
            public void DoStuff()
            {
                Emit(new Event());
            }

            public void Apply(Event e)
            {

            }
        }

        public class Event : DomainEvent<Root>
        {
        }
    }
}