namespace LmpClient.ClientPlugins
{
    /// <summary>
    /// Contract for a client-side LMP plugin, mirroring the server's <c>Server.Plugin.ILmpPlugin</c>.
    /// Must be public with a public parameterless constructor, and must not let an exception escape
    /// a hook: the handler catches and logs, but a plugin throwing every frame would flood the log.
    /// </summary>
    public interface ILmpClientPlugin
    {
        string PluginName { get; }

        /// <summary>
        /// Called once on the Unity thread during LMP startup, after LMP's base events exist and
        /// before its Harmony patches are installed, so a plugin can subscribe and patch in turn.
        /// </summary>
        void OnAwake(ILmpClientPluginContext context);

        /// <summary>Called when LMP reaches ClientState.Running and on every reconnect.</summary>
        void OnEnabled();

        /// <summary>
        /// Called on disconnect and before OnEnabled on a reconnect. Drop per-session state here;
        /// anything cached across sessions must be revalidated in OnEnabled rather than assumed.
        /// </summary>
        void OnDisabled();

        /// <summary>Called once per Unity frame while enabled, after LMP's systems. Keep it cheap.</summary>
        void OnUpdate();

        void OnDestroy();
    }
}
