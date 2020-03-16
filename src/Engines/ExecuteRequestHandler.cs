// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Microsoft.Jupyter.Core.Protocol;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

namespace Microsoft.Jupyter.Core
{
    internal class ExecuteRequestHandler : OrderedShellHandler<ExecutionResult>
    {

        private IExecutionEngine engine;
        private IShellServer shellServer;

        private Task<bool>? previousTask = null;

        /// <summary>
        ///      The number of cells that have been executed since the start of
        ///      this engine. Used by clients to typeset cell numbers, e.g.:
        ///      <c>In[12]:</c>.
        /// </summary>
        public int ExecutionCount { get; protected set; } = 0;

        public ExecuteRequestHandler(IExecutionEngine engine, IShellServer server)
        {
            this.engine = engine;
            this.shellServer = shellServer;
        }

        public string MessageType => "execute_request";
        
        protected async virtual Task<ExecutionResult> ExecutionTaskForMessage(Message message, int executionCount)
        {
            var engineResponse = await Execute(
                ((ExecuteRequestContent)message.Content).Code,
                new ExecutionChannel(this, message)
            );

            
            // Send the engine's output as an execution result.
            if (engineResponse.Output != null)
            {
                var serialized = EncodeForDisplay(engineResponse.Output);
                this.shellServer.SendIoPubMessage(
                    new Message
                    {
                        ZmqIdentities = message.ZmqIdentities,
                        ParentHeader = message.Header,
                        Metadata = null,
                        Content = new ExecuteResultContent
                        {
                            ExecutionCount = executionCount,
                            Data = serialized.Data,
                            Metadata = serialized.Metadata
                        },
                        Header = new MessageHeader
                        {
                            MessageType = "execute_result"
                        }
                    }
                );
            }

            // Handle the message.
            this.shellServer.SendShellMessage(
                new Message
                {
                    ZmqIdentities = message.ZmqIdentities,
                    ParentHeader = message.Header,
                    Metadata = null,
                    Content = new ExecuteReplyContent
                    {
                        ExecuteStatus = engineResponse.Status,
                        ExecutionCount = executionCount
                    },
                    Header = new MessageHeader
                    {
                        MessageType = "execute_reply"
                    }
                }
            );

            return engineResponse;
        }

        protected async Task SendAbortMessage(Message message)
        {
            // The previous call failed, so abort here and let the
            // shell server know.
            this.shellServer.SendShellMessage(
                new Message
                {
                    ZmqIdentities = message.ZmqIdentities,
                    ParentHeader = message.Header,
                    Metadata = null,
                    Content = new ExecuteReplyContent
                    {
                        ExecuteStatus = ExecuteStatus.Abort,
                        ExecutionCount = null
                    },
                    Header = new MessageHeader
                    {
                        MessageType = "execute_reply"
                    }
                }
            );

            // Finish by telling the client that we're free again.
            this.shellServer.SendIoPubMessage(
                new Message
                {
                    Header = new MessageHeader
                    {
                        MessageType = "status"
                    },
                    Content = new KernelStatusContent
                    {
                        ExecutionState = ExecutionState.Idle
                    }
                }.AsReplyTo(message)
            );
        }

        private void NotifyBusyStatus(Message message, ExecutionState state)
        {
            // Begin by sending that we're busy.
            this.shellServer.SendIoPubMessage(
                new Message
                {
                    Header = new MessageHeader
                    {
                        MessageType = "status"
                    },
                    Content = new KernelStatusContent
                    {
                        ExecutionState = state
                    }
                }.AsReplyTo(message)
            );
        }

        private int IncrementExecutionCount()
        {
            lock (this)
            {
                return ++this.ExecutionCount;
            }
        }

        public override async Task<ExecutionResult> HandleAsync(Message message, ExecutionResult? previousResult)
        {
            this.Logger.LogDebug($"Asked to execute code:\n{((ExecuteRequestContent)message.Content).Code}");

            if (previousResult != null && previousResult.ExecuteStatus != ExecuteStatus.Ok)
            {
                SendAbortMessage(message);
                return ExecutionResult.Aborted;
            }
            
            executionCount = IncrementExecutionCount();
            NotifyBusyStatus(message, ExecutionState.Busy);

            try
            {
                var result = await ExecutionTaskForMessage(message, executionCount.Value);
                return result;
            }
            catch (Exception e)
            {
                this.Logger?.LogError(e, "Unable to process ExecuteRequest");
                return new ExecutionResult
                {
                    Output = e,
                    Status = ExecuteStatus.Error
                };
            }
        }

    }

}
