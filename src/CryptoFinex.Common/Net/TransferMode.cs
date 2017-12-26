using System;

namespace CryptoFinex.Common.Net
{
    [Flags]
    public enum TransferMode
    {
        Binary = 0x01,
        Text = 0x02
    }
}
