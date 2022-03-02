using System;
using Microsoft.Jupyter.Core.Protocol;

namespace Microsoft.Jupyter.Core
{
    public interface IShellServer
    {
        event Action<Message> KernelInfoRequest;

        event Action<Message> ShutdownRequest;

        void SendShellMessage(Message message);

        void SendIoPubMessage(Message message);

        void SendStdinMessage(Message message);

        public Message ReceiveStdinMessage();

        void Start();
    }

    public interface IShellServerSupportsInterrupt : IShellServer
    {
        event Action<Message> InterruptRequest;
    }
}
