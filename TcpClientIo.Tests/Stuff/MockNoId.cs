using Drenalol.Attributes;

namespace Drenalol.Stuff
{
    public class MockNoId
    {
        [TcpData(0, 4, TcpDataType.BodyLength)]
        public int Size { get; set; }

        [TcpData(4, TcpDataType = TcpDataType.Body)]
        public string Body { get; set; }
    }
}