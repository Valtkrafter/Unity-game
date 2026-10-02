using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;

[InitializeOnLoad]
public class MCPAutoBridgeStarter
{
    static MCPAutoBridgeStarter()
    {
        EditorApplication.delayCall += InitializeBridge;
    }

    private static void InitializeBridge()
    {
        EditorApplication.delayCall -= InitializeBridge;

        Type mcpServerType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            mcpServerType = assembly.GetType("McpUnity.Server");
            if (mcpServerType != null)
                break;
        }

        if (mcpServerType != null)
        {
            try
            {
                MethodInfo startMethod = mcpServerType.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) 
                                         ?? mcpServerType.GetMethod("StartServer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                         ?? mcpServerType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (startMethod != null)
                {
                    startMethod.Invoke(null, null);
                }

                Debug.Log("[MCP AutoBridge] Connected & listening for Antigravity requests.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP AutoBridge] Error during server start: {ex.Message}");
            }
        }
    }
}
