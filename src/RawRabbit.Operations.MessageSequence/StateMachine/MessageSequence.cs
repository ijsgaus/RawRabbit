﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RawRabbit.Operations.MessageSequence.Configuration;
using RawRabbit.Operations.MessageSequence.Configuration.Abstraction;
using RawRabbit.Operations.MessageSequence.Model;
using RawRabbit.Operations.MessageSequence.Trigger;
using RawRabbit.Operations.StateMachine;
using RawRabbit.Operations.StateMachine.Trigger;
using RawRabbit.Pipe;
using RawRabbit.Pipe.Middleware;
using Stateless;

namespace RawRabbit.Operations.MessageSequence.StateMachine
{
	public class MessageSequence : StateMachineBase<SequenceState, Type, SequenceModel>,
			IMessageChainPublisher, IMessageSequenceBuilder
	{
		private readonly IBusClient _client;
		private Action _fireAction;
		private readonly TriggerConfigurer _triggerConfigurer;
		private readonly Queue<StepDefinition> _stepDefinitions;

		public MessageSequence(IBusClient client, SequenceModel model = null) : base(model)
		{
			_client = client;
			_triggerConfigurer = new TriggerConfigurer();
			_stepDefinitions = new Queue<StepDefinition>();
		}

		protected override void ConfigureState(StateMachine<SequenceState, Type> machine)
		{
			machine
				.Configure(SequenceState.Active)
				.Permit(typeof(CancelSequence), SequenceState.Canceled);
		}

		public override SequenceModel Initialize()
		{
			return new SequenceModel
			{
				State = SequenceState.Created,
				Id = Guid.NewGuid(),
				Completed = new List<ExecutionResult>(),
				Skipped = new List<ExecutionResult>()
			};
		}

		public IMessageSequenceBuilder PublishAsync<TMessage>(TMessage message = default(TMessage), Guid globalMessageId = new Guid()) where TMessage : new()
		{
			if (globalMessageId != Guid.Empty)
			{
				Model.Id = globalMessageId;
			}
			return PublishAsync(message, context => { });
		}

		public IMessageSequenceBuilder PublishAsync<TMessage>(TMessage message, Action<IPipeContext> context, CancellationToken ct = new CancellationToken())
			where TMessage : new()
		{
			var entryTrigger = StateMachine.SetTriggerParameters<TMessage>(typeof(TMessage));

			StateMachine
				.Configure(SequenceState.Created)
				.Permit(typeof(TMessage), SequenceState.Active);

			StateMachine
				.Configure(SequenceState.Active)
				.OnEntryFromAsync(entryTrigger, msg => _client.PublishAsync(msg, c =>
				{
					c.Properties.Add(Enrichers.GlobalExecutionId.PipeKey.GlobalExecutionId, Model.Id.ToString());
					context?.Invoke(c);
				}, ct));

			_fireAction = () => StateMachine.FireAsync(entryTrigger, message);
			return this;
		}

		public IMessageSequenceBuilder When<TMessage, TMessageContext>(Func<TMessage, TMessageContext, Task> func, Action<IStepOptionBuilder> options = null)
		{
			var optionBuilder = new StepOptionBuilder();
			options?.Invoke(optionBuilder);
			_stepDefinitions.Enqueue(new StepDefinition
			{
				Type = typeof(TMessage),
				AbortsExecution = optionBuilder.Configuration.AbortsExecution,
				Optional =  optionBuilder.Configuration.Optional
			});

			var trigger = StateMachine.SetTriggerParameters<MessageAndContext<TMessage, TMessageContext>>(typeof(TMessage));

			StateMachine
				.Configure(SequenceState.Active)
				.InternalTransitionAsync(trigger, async (message, transition) =>
				{
					var matchFound = false;
					do
					{
						if (_stepDefinitions.Peek() == null)
						{
							return;
						}
						var step = _stepDefinitions.Dequeue();
						if (step.Type != typeof(TMessage))
						{
							if (step.Optional)
							{
								Model.Skipped.Add(new ExecutionResult
								{
									Type = step.Type,
									Time = DateTime.Now
								});
							}
							else
							{
								return;
							}
						}
						else
						{
							matchFound = true;
						}
					} while (!matchFound);

					await func(message.Message, message.Context);
					Model.Completed.Add(new ExecutionResult
					{
						Type = typeof(TMessage),
						Time = DateTime.Now
					});
					if (optionBuilder.Configuration.AbortsExecution)
					{
						Model.Aborted = true;
						StateMachine.Fire(typeof(CancelSequence));
					}
				});

			_triggerConfigurer
				.FromMessage<MessageSequence,TMessage, TMessageContext>(
					(msg, ctx) => Model.Id,
					(sequence, message, ctx) => StateMachine.FireAsync(trigger, new MessageAndContext<TMessage, TMessageContext> {Context = ctx, Message = message}),
					cfg => cfg
						.FromDeclaredQueue(q => q
							.WithName($"state_machine_{Model.Id}")
							.WithExclusivity()
							.WithAutoDelete())
						.Consume(c => c.WithRoutingKey($"{typeof(TMessage).Name.ToLower()}.{Model.Id}")
					)
				);
			return this;
		}

		MessageSequence<TMessage> IMessageSequenceBuilder.Complete<TMessage>()
		{
			var tsc = new TaskCompletionSource<TMessage>();
			var sequence = new MessageSequence<TMessage>
			{
				Task = tsc.Task
			};

			StateMachine
				.Configure(SequenceState.Active)
				.Permit(typeof(TMessage), SequenceState.Completed);

			var trigger = StateMachine.SetTriggerParameters<TMessage>(typeof(TMessage));
			StateMachine
				.Configure(SequenceState.Completed)
				.OnEntryFrom(trigger, message =>
				{
					sequence.Completed = Model.Completed;
					sequence.Skipped = Model.Skipped;
					_client
						.InvokeAsync(p => p
							.Use<TransientChannelMiddleware>()
							.Use<QueueDeleteMiddleware>(new QueueDeleteOptions
							{
								QueueNameFunc = context => $"state_machine_{Model.Id}"
							}))
						.ContinueWith(t =>tsc.TrySetResult(message));
				});

			StateMachine
				.Configure(SequenceState.Canceled)
				.OnEntry(() =>
				{
					_client
						.InvokeAsync(p => p
							.Use<TransientChannelMiddleware>()
							.Use<QueueDeleteMiddleware>(new QueueDeleteOptions
							{
								QueueNameFunc = context => $"state_machine_{Model.Id}"
							}));
					sequence.Completed = Model.Completed;
					sequence.Skipped = Model.Skipped;
					sequence.Aborted = true;
					tsc.TrySetResult(default(TMessage));
				});

			_triggerConfigurer
				.FromMessage<MessageSequence, TMessage>(
					message => Model.Id,
					(s, message) => StateMachine.FireAsync(trigger, message),
					cfg => cfg
						.FromDeclaredQueue(q => q
							.WithName($"state_machine_{Model.Id}")
							.WithExclusivity()
							.WithAutoDelete())
						.Consume(c => c.WithRoutingKey($"{typeof(TMessage).Name.ToLower()}.{Model.Id}")
						)
				);

			foreach (var triggerCfg in _triggerConfigurer.TriggerConfiguration)
			{
				triggerCfg.Context += context =>
				{
					context.Properties.Add(StateMachineKey.ModelId, Model.Id);
					context.Properties.Add(StateMachineKey.Machine, this);
				};
				_client.InvokeAsync(triggerCfg.Pipe, triggerCfg.Context).GetAwaiter().GetResult();
			}

			var requestTimeout = _client
				.InvokeAsync(builder => { })
				.ContinueWith(tContext => tContext.Result.GetClientConfiguration().RequestTimeout)
				.GetAwaiter()
				.GetResult();

			Timer requestTimer = null;
			requestTimer = new Timer(state =>
			{
				requestTimer?.Dispose();
				tsc.TrySetException(new TimeoutException(
					$"Unable to complete sequence {Model.Id} in {requestTimeout:g}. Operation Timed out."));
			}, null, requestTimeout, new TimeSpan(-1));

			_fireAction();

			return sequence;
		}

		private class CancelSequence { }

		// Temp class until Stateless supports multiple trigger args
		private class MessageAndContext<TMessage, TContext>
		{
			public TMessage Message { get; set; }
			public TContext Context { get; set; }
		}
	}

	public enum SequenceState
	{
		Created,
		Active,
		Completed,
		Canceled
	}
}