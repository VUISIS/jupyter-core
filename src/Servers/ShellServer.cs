// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Microsoft.Jupyter.Core.Protocol;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.Jupyter.Core
{
    public class ShellServer : IDisposable, IShellServerSupportsInterrupt
    {

        // A shell server works by using a request-reply pattern, as documented
        // at:
        //
        //     https://jupyter-client.readthedocs.io/en/stable/messaging.html#wire-protocol
        // In particular:
        //
        //     • The client sends a <action>_request message on its shell socket.
        //     • The kernel responds with a "status": "busy" message on its IOPub.
        //     • When the message has been fully acted upon, a <action>_reply
        //       message is sent out across the shell socket.
        //     • A final "status": "idle" is sent out on the IOPub socket.
        //
        // This sequence ensures that the shell socket can handle requests from
        // multiple frontends at once, while maintaining proper sequences.
        // Meanwhile, the IOPub socket makes sure that all frontends know about
        // the current status, stdout printing, etc.
        //
        // Note that for "quick" reply/request patterns, we need not advertise
        // that we are busy over the IOPub socket. For example, we skip that
        // step when responding to kernel_info_requests.
        private bool alive = false;
        private bool once = true;
        private RouterSocket shellSocket;
        private Thread shellThread;

        private RouterSocket controlSocket;
        private Thread controlThread;

        private PublisherSocket ioPubSocket;

        private RouterSocket stdinSocket;

        private Thread stdinThread;

        private ILogger<ShellServer> logger;
        private KernelContext context;
        private IServiceProvider provider;
        private IShellRouter router;

        private string session;

        public ShellServer(
            ILogger<ShellServer> logger,
            IOptions<KernelContext> context,
            IServiceProvider provider,
            IShellRouter router
        )
        {
            this.logger = logger;
            this.context = context.Value;
            this.provider = provider;
            this.router = router;

            router.RegisterHandler("kernel_info_request", async message => OnKernelInfoRequest(message));
            router.RegisterHandler("interrupt_request", async message => InterruptRequest?.Invoke(message));
            router.RegisterHandler("shutdown_request", async message => OnShutdownRequest(message));
        }

        public void Start()
        {
            shellSocket = new RouterSocket();
            shellSocket.Bind(context.ConnectionInfo.ShellZmqAddress);
            controlSocket = new RouterSocket();
            controlSocket.Bind(context.ConnectionInfo.ControlZmqAddress);
            ioPubSocket = new PublisherSocket();
            ioPubSocket.Bind(context.ConnectionInfo.IoPubZmqAddress);
            stdinSocket = new RouterSocket();
            stdinSocket.Bind(context.ConnectionInfo.StdinZmqAddress);

            alive = true;
            controlThread = new Thread(() => EventLoop(controlSocket))
            {
                Name = "Control server"
            };
            controlThread.Start();
            shellThread = new Thread(() => EventLoop(shellSocket))
            {
                Name = "Shell server"
            };
            shellThread.Start();
            /*stdinThread = new Thread(() => EventLoop(stdinSocket))
            {
                Name = "Stdin server"
            };
            stdinThread.Start();*/
        }

        public void SendShellMessage(Message message) =>
            SendMessage(shellSocket, "shell", message);

        public void SendControlMessage(Message message) =>
            SendMessage(controlSocket, "control", message);

        public void SendIoPubMessage(Message message) =>
            SendMessage(ioPubSocket, "iopub", message);

        public void SendStdinMessage(Message message) =>
            SendMessage(stdinSocket, "stdin", message);

        public Message ReceiveStdinMessage()
        {
            return stdinSocket.ReceiveMessage(context);
        }

        private void SendMessage(NetMQSocket socket, string socketKind, Message message)
        {
            // Add metadata for the current session if needed.
            if (message.Header.Session == null)
            {
                message.Header.Session = session;
            }
            logger.LogDebug($"Sending {socketKind} message:\n\t{JsonConvert.SerializeObject(message)}");
            lock (socket)
            {
                socket.SendMessage(context, message);
            }
        }

        private void EventLoop(NetMQSocket socket)
        {
            this.logger.LogDebug("Starting shell server event loop at {Address}.", socket);
            while (alive)
            {
                try
                {
                    // Start by pulling off the next <action>_request message
                    // from the client.
                    var nextMessage = socket.ReceiveMessage(context);

                    session ??= nextMessage.Header.Session;
                    // Get a service that can handle the message type and
                    // dispatch.
                    router.Handle(nextMessage);
                }
                catch (ProtocolViolationException ex)
                {
                    logger.LogCritical(ex, $"Protocol violation when trying to receive next ZeroMQ message.");
                }
                catch (ThreadInterruptedException)
                {
                    if (alive) continue; else return;
                }
            }
        }

        /// <summary>
        ///       Called by shell servers to report kernel information to the
        ///       client. By default, this method responds by converting
        ///       the kernel properties stored in this engine's context to a
        ///       <c>kernel_info</c> Jupyter message.
        /// </summary>
        /// <param name="message">The original request from the client.</param>
        public virtual void OnKernelInfoRequest(Message message)
        {
            // Before handling the kernel info request, make sure to call any
            // events for custom handling.
            KernelInfoRequest?.Invoke(message);

            try
            {
                // Tell the client we're about to handle their kernel_info_request.
                this.SendIoPubMessage(
                    new Message
                    {
                        Header = new MessageHeader
                        {
                            MessageType = "status"
                        },
                        Content = new KernelStatusContent
                        {
                            ExecutionState = ExecutionState.Busy
                        }
                    }
                    .AsReplyTo(message)
                );

                this.SendShellMessage(
                    new Message
                    {
                        ZmqIdentities = message.ZmqIdentities,
                        ParentHeader = message.Header,
                        Metadata = null,
                        Content = this.context.Properties.AsKernelInfoReply(),
                        Header = new MessageHeader
                        {
                            MessageType = "kernel_info_reply",
                            Id = Guid.NewGuid().ToString(),
                            ProtocolVersion = "5.2.0"
                        }
                    }
                );

                // Once finished, have the shell server report that we are
                // idle.
                this.SendIoPubMessage(
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
                    }
                    .AsReplyTo(message)
                );
            }
            catch (Exception e)
            {
                this.logger?.LogError(e, "Unable to process KernelInfoRequest");
            }
        }

        public virtual void OnShutdownRequest(Message message)
        {
            ShutdownRequest?.Invoke(message);
            System.Environment.Exit(0);
        }

        #region IDisposable Support
        private bool disposedValue = false; 

        public event Action<Message> KernelInfoRequest;
        public event Action<Message> InterruptRequest;
        public event Action<Message> ShutdownRequest;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    shellSocket?.Dispose();
                    controlSocket?.Dispose();
                    ioPubSocket?.Dispose();
                    stdinSocket?.Dispose();
                }
                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion
    }
}
