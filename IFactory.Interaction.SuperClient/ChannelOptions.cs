
using System.IO.Pipelines;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Collections.Specialized;

namespace IFactory.Interaction.SuperClient
{
    public class ChannelOptions
    {
        // 1M by default
        public int MaxPackageLength { get; set; } = 1024 * 1024;
        // Close on overflow instead of growing memory without a bound.
        public int MaxPendingPackages { get; set; } = 1024;
        
        // 4k by default
        public int ReceiveBufferSize { get; set; } = 1024 * 4;

        // 4k by default
        public int SendBufferSize { get; set; } = 1024 * 4;

        // trigger the read only when the stream is being consumed
        public bool ReadAsDemand { get; set; }
        
        /// <summary>
        /// in milliseconds
        /// </summary>
        /// <value></value>
        public int ReceiveTimeout { get; set; }

        /// <summary>
        /// in milliseconds
        /// </summary>
        /// <value></value>
        public int SendTimeout { get; set; }

        /// <summary>Single TCP connect attempt in milliseconds; 0 disables the deadline.</summary>
        public int ConnectTimeout { get; set; } = 5000;
        public int ReconnectMinDelay { get; set; } = 200;
        public int ReconnectMaxDelay { get; set; } = 5000;
        public bool ReconnectJitter { get; set; } = true;
        public bool SerialReconnect { get; set; }
        /// <summary>Bounded grace for a recently closed native port to become available; 0 disables retries.</summary>
        public int SerialOpenRetryTimeout { get; set; } = 500;
        public bool NoDelay { get; set; }
        public bool KeepAlive { get; set; }

        internal ChannelOptions Copy()
        {
            var copy = (ChannelOptions)MemberwiseClone();
            copy.Values = new Dictionary<string, string>(Values);
            return copy;
        }
        internal void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPackageLength);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingPackages);
            ArgumentOutOfRangeException.ThrowIfNegative(ReceiveTimeout);
            ArgumentOutOfRangeException.ThrowIfNegative(SendTimeout);
            ArgumentOutOfRangeException.ThrowIfNegative(ConnectTimeout);
            ArgumentOutOfRangeException.ThrowIfNegative(SerialOpenRetryTimeout);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReconnectMinDelay);
            ArgumentOutOfRangeException.ThrowIfLessThan(ReconnectMaxDelay, ReconnectMinDelay);
        }

        public ILogger? Logger { get; set; }

        public Pipe? In { get; set; }

        public Pipe? Out { get; set; }

        public Dictionary<string, string> Values { get; set; } = new();
    }
}
