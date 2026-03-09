using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Ensures HTTP transports resume after domain reloads similar to the legacy stdio bridge.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpBridgeReloadHandler
    {
        private static readonly TimeSpan[] ResumeRetrySchedule =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
        };

        static HttpBridgeReloadHandler()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            // Auto-connect on editor startup: if the resume flag is set (session was active
            // before the editor was closed/crashed), try to reconnect automatically.
            // Use delayCall to ensure all editor subsystems are initialized first.
            EditorApplication.delayCall += OnEditorStartup;
        }

        private static void OnEditorStartup()
        {
            bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
            bool hasFlag = EditorPrefs.GetBool(EditorPrefKeys.ResumeHttpAfterReload, false);

            if (!useHttp || !hasFlag)
                return;

            McpLog.Info("[HTTP AutoConnect] Resume flag found on editor startup, attempting auto-connect...");
            _ = ResumeHttpWithRetriesAsync();
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                var transport = MCPServiceLocator.TransportManager;
                bool isRunning = transport.IsRunning(TransportMode.Http);

                // The resume flag may already be set from session start (see SetResumeFlag).
                // Only update it if the transport is actively running; never clear a flag
                // that was set earlier — the WebSocket often disconnects before this callback
                // fires (e.g., server ping timeout during compilation), so IsRunning() may
                // return false even though we should resume.
                if (isRunning)
                {
                    EditorPrefs.SetBool(EditorPrefKeys.ResumeHttpAfterReload, true);
                }

                // Force synchronous teardown to avoid orphaned sockets.
                var client = transport.GetClient(TransportMode.Http);
                if (client != null)
                {
                    transport.ForceStop(TransportMode.Http);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to evaluate HTTP bridge reload state: {ex.Message}");
            }
        }

        /// <summary>
        /// Call this when an HTTP session starts successfully, so domain reload can resume it.
        /// </summary>
        public static void SetResumeFlag()
        {
            if (EditorConfigurationCache.Instance.UseHttpTransport)
            {
                EditorPrefs.SetBool(EditorPrefKeys.ResumeHttpAfterReload, true);
            }
        }

        /// <summary>
        /// Call this when the user explicitly ends the session, so domain reload won't resume.
        /// </summary>
        public static void ClearResumeFlag()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload);
        }

        private static void OnAfterAssemblyReload()
        {
            bool resume = false;
            try
            {
                // Only resume HTTP if it is still the selected transport.
                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                resume = useHttp && EditorPrefs.GetBool(EditorPrefKeys.ResumeHttpAfterReload, false);
                // Don't clear the flag here — keep it so editor restart can also auto-connect.
                // The flag is only cleared when:
                // 1. User explicitly clicks "End Session" (ClearResumeFlag)
                // 2. ResumeHttpWithRetriesAsync succeeds (connection restored)
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to read HTTP bridge reload flag: {ex.Message}");
                resume = false;
            }

            if (!resume)
            {
                return;
            }

            // If the editor is not compiling, attempt an immediate restart without relying on editor focus.
            bool isCompiling = EditorApplication.isCompiling;
            try
            {
                var pipeline = Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                var prop = pipeline?.GetProperty("isCompiling", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null) isCompiling |= (bool)prop.GetValue(null);
            }
            catch { }

            if (!isCompiling)
            {
                _ = ResumeHttpWithRetriesAsync();
                return;
            }

            // Fallback when compiling: schedule on the editor loop
            EditorApplication.delayCall += () =>
            {
                _ = ResumeHttpWithRetriesAsync();
            };
        }

        private static async Task ResumeHttpWithRetriesAsync()
        {
            Exception lastException = null;

            for (int i = 0; i < ResumeRetrySchedule.Length; i++)
            {
                int attempt = i + 1;
                McpLog.Debug($"[HTTP Reload] Resume attempt {attempt}/{ResumeRetrySchedule.Length}");

                TimeSpan delay = ResumeRetrySchedule[i];
                if (delay > TimeSpan.Zero)
                {
                    McpLog.Debug($"[HTTP Reload] Waiting {delay.TotalSeconds:0.#}s before resume attempt {attempt}");
                    try { await Task.Delay(delay); }
                    catch { return; }
                }

                // Abort retries if the user switched transports while we were waiting.
                if (!EditorConfigurationCache.Instance.UseHttpTransport)
                {
                    return;
                }

                try
                {
                    bool started = await MCPServiceLocator.TransportManager.StartAsync(TransportMode.Http);
                    if (started)
                    {
                        McpLog.Debug($"[HTTP Reload] Resume succeeded on attempt {attempt}");
                        MCPForUnityEditorWindow.RequestHealthVerification();
                        return;
                    }

                    var state = MCPServiceLocator.TransportManager.GetState(TransportMode.Http);
                    string reason = string.IsNullOrWhiteSpace(state?.Error) ? "no error detail" : state.Error;
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attempt} failed: {reason}");
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attempt} threw: {ex.Message}");
                }
            }

            if (lastException != null)
            {
                McpLog.Warn($"Failed to resume HTTP MCP bridge after domain reload: {lastException.Message}");
            }
            else
            {
                McpLog.Warn("Failed to resume HTTP MCP bridge after domain reload");
            }
        }
    }
}
