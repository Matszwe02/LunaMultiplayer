using LmpCommon.Message.Data;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public class ModDataSystemSender
    {
        public static void SendLmpModMessageToAll(ClientStructure excludeClient, string modName, byte[] messageData)
        {
            SendLmpModMessageToAll(excludeClient, modName, messageData, true);
        }

        public static void SendLmpModMessageToAll(ClientStructure excludeClient, string modName, byte[] messageData, bool reliable)
        {
            if (modName == null || messageData == null)
            {
                LunaLog.Debug("Attemped to send a null mod message");
                return;
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ModMsgData>();
            msgData.Data = messageData;
            msgData.ModName = modName;
            //Neither was ever set, so a server to client mod payload serialised zero bytes, unreliably.
            msgData.NumBytes = messageData.Length;
            msgData.Reliable = reliable;

            MessageQueuer.RelayMessage<ModSrvMsg>(excludeClient, msgData);
        }

        public static void SendLmpModMessageToClient(ClientStructure client, string modName, byte[] messageData)
        {
            SendLmpModMessageToClient(client, modName, messageData, true);
        }

        public static void SendLmpModMessageToClient(ClientStructure client, string modName, byte[] messageData, bool reliable)
        {
            if (modName == null || messageData == null)
            {
                LunaLog.Debug("Attemped to send a null mod message");
                return;
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ModMsgData>();
            msgData.Data = messageData;
            msgData.ModName = modName;
            msgData.NumBytes = messageData.Length;
            msgData.Reliable = reliable;

            MessageQueuer.SendToClient<ModSrvMsg>(client, msgData);
        }
    }
}