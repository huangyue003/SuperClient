using SuperSocket.ProtoBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.SuperClient
{
    public interface IChannelCreator<TReceicePackage>
    {
        Action<IChannel<TReceicePackage>>? ChannelCreated { get; set; }
        void Dispose();
        Task RunAsync(string uri, IPipelineFilter<TReceicePackage> pipelineFilter);
    }
}
