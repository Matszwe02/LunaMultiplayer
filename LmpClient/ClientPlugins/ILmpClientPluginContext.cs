namespace LmpClient.ClientPlugins
{
    /// <summary>
    /// The services LMP offers to a client-side plugin. Deliberately small and mod-agnostic:
    /// nothing here knows about any particular mod.
    /// </summary>
    public interface ILmpClientPluginContext
    {
        string PluginName { get; }
        string KspPath { get; }
        string PluginDirectory { get; }

        /// <summary>True while LMP is connected and past ClientState.Running.</summary>
        bool IsNetworkRunning { get; }

        string PlayerName { get; }

        /// <summary>
        /// Sends an opaque payload on a named channel. The server dispatches it to whichever plugin
        /// registered that name, and may relay it. Pass <c>reliable: false</c> only for lossy data.
        /// </summary>
        void SendModMessage(string modName, byte[] payload, bool reliable = true);

        /// <summary>Receives payloads the server sends on a channel. Runs on the Unity thread.</summary>
        void RegisterModHandler(string modName, System.Action<byte[]> handler);
    }
}
