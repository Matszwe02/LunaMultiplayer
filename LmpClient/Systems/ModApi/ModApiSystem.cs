using System;
using LmpClient.Base;
using LmpClient.Network;
using LmpCommon.Message.Data;

namespace LmpClient.Systems.ModApi
{
    public class ModApiSystem : MessageSystem<ModApiSystem, ModApiMessageSender, ModApiMessageHandler>
    {
        #region Base overrides

        public override string SystemName { get; } = nameof(ModApiSystem);

        public override int ExecutionOrder => int.MinValue + 2;

        #endregion

        #region Public methods

        /// <summary>
        /// Sends a mod Message.
        /// </summary>
        /// <param name="modName">Mod Name</param>
        /// <param name="messageData">The message payload</param>
        /// <param name="relay">If set to <c>true</c>, the server relays the Message to all other authenticated clients</param>
        /// <param name="reliable">Defaults to <c>true</c>; false sends UnreliableSequenced and may drop silently.</param>
        public void SendModMessage(string modName, byte[] messageData, bool relay, bool reliable = true)
        {
            SendModMessage(modName, messageData, messageData.Length, relay, reliable);
        }

        /// <summary>
        /// Sends a mod Message.
        /// </summary>
        /// <param name="modName">Mod Name</param>
        /// <param name="messageData">The message payload</param>
        /// <param name="numBytes">Number of bytes to take from the array</param>
        /// <param name="relay">If set to <c>true</c>, the server relays the Message to all other authenticated clients</param>
        /// <param name="reliable">See the overload above.</param>
        public void SendModMessage(string modName, byte[] messageData, int numBytes, bool relay, bool reliable = true)
        {
            if (modName == null)
                return;
            if (messageData == null)
            {
                LunaLog.LogError($"[LMP]: {modName} attemped to send a null Message");
                return;
            }

            if (numBytes < 0 || numBytes > messageData.Length)
            {
                LunaLog.LogError($"[LMP]: {modName} attemped to send {numBytes} bytes from a {messageData.Length} byte array");
                return;
            }

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<ModMsgData>();

            if (msgData.Data.Length < numBytes)
                msgData.Data = new byte[numBytes];

            Array.Copy(messageData, msgData.Data, numBytes);

            msgData.NumBytes = numBytes;
            msgData.Relay = relay;
            msgData.Reliable = reliable;
            msgData.ModName = modName;

            MessageSender.SendMessage(msgData);
        }

        #endregion
    }
}
