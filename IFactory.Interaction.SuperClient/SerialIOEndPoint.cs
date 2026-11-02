using System;
using System.IO.Ports;
using System.Linq;

namespace IFactory.Interaction.SuperClient
{
    public interface ISerialIOEndPoint
    {
        string PortName { get; set; }

        byte Databits { get; set; }

        int BaudRate { set; get; }

        Parity Parity { set; get; }

        StopBits StopBits { set; get; }
    }

    public class SerialIOEndPoint : ISerialIOEndPoint
    {
        public string PortName { get; set; } = string.Empty;

        public byte Databits { get; set; } = 8;

        public int BaudRate { set; get; } = 9600;

        public Parity Parity { set; get; }

        public StopBits StopBits { set; get; } = StopBits.One;

        public SerialIOEndPoint()
        {

        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri">sio://COM2/?BaudRate=9600&Parity=None&StopBits=1&DataBits=8</param>
        public SerialIOEndPoint(string uri)
            : this(ChannelUri.Parse(uri))
        {
            
        }

        public SerialIOEndPoint(Uri uri)
        {
            if (uri.Scheme != "sio") throw new ArgumentException("Serial URI scheme must be sio.");
            var query = ChannelUri.ParseQuery(uri);

            PortName = uri.Host;

            if (string.IsNullOrEmpty(PortName))
                throw new ArgumentException("PortName is required in the ListenOptions.");

            if (!query.TryGetValue("baudRate", out var baudRate) || string.IsNullOrEmpty(baudRate))
                throw new ArgumentException("BaudRate is required in the ListenOptions.");

            if (!int.TryParse(baudRate, out int intBaudRate) || intBaudRate <= 0)
                throw new ArgumentException("BaudRate is invalid in the ListenOptions.");

            BaudRate = intBaudRate;

            var parityValue = Parity.None;

            if (query.TryGetValue("parity", out var parity) && !string.IsNullOrEmpty(parity))
            {
                if (!Enum.TryParse(typeof(Parity), parity, true, out var parityParseValue))
                    throw new ArgumentException("Parity must be None, Odd, Even, Mark or Space (or a defined numeric value).");
                
                parityValue = (Parity)parityParseValue;
            }

            if (!Enum.IsDefined(parityValue)) throw new ArgumentException("Parity must be None, Odd, Even, Mark or Space (or a defined numeric value).");
            Parity = parityValue;

            var dataBitsValue = 8;

            if (query.TryGetValue("databits", out var databits) && !string.IsNullOrEmpty(databits))
            {
                if (!int.TryParse(databits, out int databitsParseValue))
                    throw new ArgumentException("Databits is invalid in the ListenOptions.");

                dataBitsValue = databitsParseValue;
            }

            if (dataBitsValue < 5 || dataBitsValue > 8) throw new ArgumentException("DataBits must be between 5 and 8.");
            Databits = (byte)dataBitsValue;


            var stopBitsValue = StopBits.One;

            if (query.TryGetValue("stopbits", out var stopbits) && !string.IsNullOrEmpty(stopbits))
            {
                if (!Enum.TryParse(typeof(StopBits), stopbits, true, out var stopbitsParseValue))
                    throw new ArgumentException("StopBits must be One, Two or OnePointFive (or a defined numeric value).");
                
                stopBitsValue = (StopBits)stopbitsParseValue;
            }

            if (!Enum.IsDefined(stopBitsValue) || stopBitsValue == StopBits.None) throw new ArgumentException("StopBits must be One, Two or OnePointFive (or a defined numeric value).");
            StopBits = stopBitsValue;
        }
    }
}




