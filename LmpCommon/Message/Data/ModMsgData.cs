using System;
using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Interface;

namespace LmpCommon.Message.Data
{
    public class ModMsgData : MessageData, IMessageDataResettable
    {
        /// <inheritdoc />
        internal ModMsgData() { }

        /// <summary>
        /// Name of the mod that creates this msg
        /// </summary>
        public string ModName;

        /// <summary>
        /// Relay the msg to all players once it arrives to the serer
        /// </summary>
        public bool Relay;

        /// <summary>
        /// Send it in reliable mode or in UDP-unreliable mode
        /// </summary>
        public bool Reliable;

        /// <summary>
        /// Number of bytes that are being sent
        /// </summary>
        public int NumBytes;

        /// <summary>
        /// Data to send
        /// </summary>
        public byte[] Data = new byte[0];

        public override string ClassName { get; } = nameof(ModMsgData);

        /// <summary>
        /// Ceiling on a single payload, so a corrupt length cannot force a huge allocation. A
        /// legitimate payload is far below this: the MTU is 1408 and this Lidgren build has no
        /// fragmentation.
        /// </summary>
        private const int MaxPayloadBytes = 4 * 1024 * 1024;

        /// <inheritdoc />
        public void Reset()
        {
            ModName = null;
            Relay = false;
            Reliable = false;
            NumBytes = 0;
            Data = new byte[0];
        }

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            //NetBuffer.Write throws past the end of the array and that reaches the player as a disconnect, so clamp.
            var count = NumBytes;
            if (count < 0)
                count = 0;
            if (count > Data.Length)
                count = Data.Length;

            lidgrenMsg.Write(ModName ?? string.Empty);
            lidgrenMsg.Write(Relay);
            lidgrenMsg.Write(Reliable);
            lidgrenMsg.Write(count);
            lidgrenMsg.Write(Data, 0, count);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            ModName = lidgrenMsg.ReadString();
            Relay = lidgrenMsg.ReadBoolean();
            Reliable = lidgrenMsg.ReadBoolean();
            NumBytes = lidgrenMsg.ReadInt32();

            //A length that could not fit in what is left of the message means a corrupt sender
            var remaining = lidgrenMsg.LengthBytes - lidgrenMsg.PositionInBytes;
            if (NumBytes < 0 || NumBytes > remaining || NumBytes > MaxPayloadBytes)
                throw new InvalidOperationException(
                    $"[LMP] Mod message declared {NumBytes} bytes with only {remaining} remaining");

            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);
        }

        internal override int InternalGetMessageSize()
        {
            var count = NumBytes;
            if (count < 0)
                count = 0;
            if (count > Data.Length)
                count = Data.Length;

            return (ModName ?? string.Empty).GetByteCount() + sizeof(bool) + sizeof(bool) + sizeof(int) + sizeof(byte) * count;
        }
    }
}