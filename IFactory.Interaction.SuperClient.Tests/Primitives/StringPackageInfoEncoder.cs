using SuperSocket.ProtoBase;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.SuperClient.Tests
{
    internal class StringPackageInfoEncoder : IPackageEncoder<StringPackageInfo>
    {
        public int Encode(IBufferWriter<byte> writer, StringPackageInfo pack)
        {
            var content = pack.Key + " " + pack.Body + "\r\n";
            return writer.Write(content, Encoding.UTF8);
        }
    }
}
