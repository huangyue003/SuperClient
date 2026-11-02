using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient
{
    public interface IChannelWithSessionIdentifier
    {
        string SessionIdentifier { get; }
    }
}
