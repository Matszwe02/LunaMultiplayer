using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LmpClient.Events;
using LmpClient.Systems.ModApi;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;

namespace LmpClient.ClientPlugins
{
    /// <summary>
    /// Discovers, loads and drives client-side LMP plugins in
    /// <c>GameData/LunaMultiplayer/ClientPlugins</c>. Mirrors the server's <c>LmpPluginHandler</c>.
    /// </summary>
    public static class LmpClientPluginHandler
    {
        private sealed class LoadedPlugin
        {
            public ILmpClientPlugin Instance;
            public PluginContext Context;
            public string Directory;
            public bool Enabled;
        }

        /// <summary>Per-plugin view of the context, so a plugin only sees its own channels.</summary>
        private sealed class PluginContext : ILmpClientPluginContext
        {
            private readonly LoadedPlugin _owner;
            private readonly Action<string, byte[], bool> _send;
            private readonly Dictionary<string, Action<byte[]>> _modHandlers = new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal);

            public PluginContext(LoadedPlugin owner, Action<string, byte[], bool> send)
            {
                _owner = owner;
                _send = send;
            }

            public string PluginName => _owner.Instance?.PluginName ?? "unknown";
            public string KspPath => MainSystem.KspPath;
            public string PluginDirectory => _owner.Directory;
            public bool IsNetworkRunning => MainSystem.NetworkState >= ClientState.Running;
            public string PlayerName => SettingsSystem.CurrentSettings?.PlayerName;

            public void SendModMessage(string modName, byte[] payload, bool reliable = true)
            {
                if (string.IsNullOrEmpty(modName) || payload == null || payload.Length == 0)
                    return;

                _send(modName, payload, reliable);
            }

            public void RegisterModHandler(string modName, Action<byte[]> handler)
            {
                if (string.IsNullOrEmpty(modName) || handler == null)
                    return;

                _modHandlers[modName] = handler;
            }

            public void Dispatch(string modName, byte[] payload)
            {
                if (string.IsNullOrEmpty(modName) || payload == null)
                    return;

                if (_modHandlers.TryGetValue(modName, out var handler))
                {
                    try
                    {
                        handler(payload);
                    }
                    catch (Exception e)
                    {
                        LunaLog.LogError($"[LMP] Client plugin '{PluginName}' threw handling a '{modName}' message: {e}");
                    }
                }
            }
        }

        private static readonly List<LoadedPlugin> Plugins = new List<LoadedPlugin>();
        private static bool _modEventHooked;
        private static bool _resolveHooked;

        /// <summary>Names of the plugins that loaded successfully. Used by the status window.</summary>
        public static string[] LoadedPluginNames =>
            Plugins.Select(p => p.Instance?.PluginName ?? "unknown").ToArray();

        /// <summary>
        /// Loads and awakens every client plugin. Call from <see cref="MainSystem.Awake"/> after
        /// LMP's base events exist and before Harmony patches are installed.
        /// </summary>
        public static void Awake()
        {
            //ModSystem.GetModFiles() skips GameData/LunaMultiplayer, so a plugin here can never
            //invalidate a server's LMPModControl.xml; KSP does not scan it either, so we load it.
            var pluginDirectory = Path.Combine(MainSystem.KspPath, "GameData", "LunaMultiplayer", "ClientPlugins");
            if (!Directory.Exists(pluginDirectory))
                return;

            HookAssemblyResolve(pluginDirectory);
            HookModEvent();

            var loadedAssemblies = new List<Assembly>();
            foreach (var file in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    //UnsafeLoadFrom tolerates the "blocked, downloaded from the internet" flag
                    loadedAssemblies.Add(Assembly.UnsafeLoadFrom(file));
                }
                catch (Exception e)
                {
                    LunaLog.LogWarning($"[LMP] Could not load client plugin assembly {Path.GetFileName(file)}: {e.Message}");
                }
            }

            foreach (var assembly in loadedAssemblies)
            {
                foreach (var type in GetLoadableTypes(assembly)
                             .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
                             .Where(t => t.GetInterfaces().Any(i => i == typeof(ILmpClientPlugin))))
                {
                    TryActivate(type, pluginDirectory);
                }
            }

            if (Plugins.Count > 0)
                LunaLog.Log($"[LMP] Loaded {Plugins.Count} client plugin(s): {string.Join(", ", LoadedPluginNames)}");
        }

        private static void TryActivate(Type type, string pluginDirectory)
        {
            try
            {
                var instance = Activator.CreateInstance(type) as ILmpClientPlugin;
                if (instance == null)
                    return;

                var loaded = new LoadedPlugin
                {
                    Instance = instance,
                    Directory = Path.GetDirectoryName(type.Assembly.Location) ?? pluginDirectory,
                    Enabled = false
                };
                loaded.Context = new PluginContext(loaded, SendModMessage);

                Plugins.Add(loaded);

                instance.OnAwake(loaded.Context);
                LunaLog.Log($"[LMP] Client plugin '{SafeName(instance)}' awakened");
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP] Failed to activate client plugin {type.FullName}: {e}");
            }
        }

        private static string SafeName(ILmpClientPlugin plugin)
        {
            try
            {
                return plugin.PluginName ?? plugin.GetType().Name;
            }
            catch
            {
                return plugin.GetType().Name;
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                //A single unloadable type in a plugin assembly must not stop the rest
                return e.Types.Where(t => t != null);
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP] Could not enumerate types of {assembly.GetName().Name}: {e.Message}");
                return Enumerable.Empty<Type>();
            }
        }

        private static void HookAssemblyResolve(string pluginDirectory)
        {
            if (_resolveHooked)
                return;

            _resolveHooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                //Already loaded under that exact identity?
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.FullName == args.Name)
                        return assembly;
                }

                //Otherwise fall back to a loose dll next to the plugins
                var simpleName = new AssemblyName(args.Name).Name;
                var candidate = Path.Combine(pluginDirectory, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.UnsafeLoadFrom(candidate) : null;
            };
        }

        private static void HookModEvent()
        {
            if (_modEventHooked)
                return;

            _modEventHooked = true;
            ModApiEvent.onModMessageReceived.Add((modName, data) =>
            {
                foreach (var plugin in Plugins)
                    plugin.Context.Dispatch(modName, data);
            });
        }

        private static void SendModMessage(string modName, byte[] payload, bool reliable)
        {
            ModApiSystem.Singleton.SendModMessage(modName, payload, reliable);
        }

        /// <summary>Fires OnUpdate on every enabled plugin. Called from MainSystem.Update.</summary>
        public static void Update()
        {
            for (var i = 0; i < Plugins.Count; i++)
            {
                var plugin = Plugins[i];
                if (!plugin.Enabled)
                    continue;

                try
                {
                    plugin.Instance.OnUpdate();
                }
                catch (Exception e)
                {
                    //Disabled rather than reported: a plugin throwing every frame would otherwise
                    //produce thousands of identical lines
                    plugin.Enabled = false;
                    LunaLog.LogError($"[LMP] Client plugin '{SafeName(plugin.Instance)}' threw in OnUpdate and was disabled: {e}");
                }
            }
        }

        /// <summary>Called by MainSystem when the network state changes.</summary>
        public static void OnNetworkStateChanged(ClientState state)
        {
            var shouldBeEnabled = state >= ClientState.Running;
            for (var i = 0; i < Plugins.Count; i++)
            {
                var plugin = Plugins[i];
                if (plugin.Enabled == shouldBeEnabled)
                    continue;

                plugin.Enabled = shouldBeEnabled;
                try
                {
                    if (shouldBeEnabled)
                        plugin.Instance.OnEnabled();
                    else
                        plugin.Instance.OnDisabled();
                }
                catch (Exception e)
                {
                    LunaLog.LogError($"[LMP] Client plugin '{SafeName(plugin.Instance)}' threw in {(shouldBeEnabled ? "OnEnabled" : "OnDisabled")}: {e}");
                }
            }
        }

        /// <summary>Fires OnDestroy and clears the plugin list. Called from MainSystem.OnExit.</summary>
        public static void Shutdown()
        {
            for (var i = 0; i < Plugins.Count; i++)
            {
                try
                {
                    Plugins[i].Instance.OnDestroy();
                }
                catch (Exception e)
                {
                    LunaLog.LogError($"[LMP] Client plugin '{SafeName(Plugins[i].Instance)}' threw in OnDestroy: {e}");
                }
            }

            Plugins.Clear();
        }
    }
}
