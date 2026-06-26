using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps HoloLens builds compatible with the UWP ARM native library shipped by MixedReality-WebRTC 2.0.2.
/// </summary>
[InitializeOnLoad]
public sealed class HoloLensWebRtcBuildArchitecture : IPreprocessBuildWithReport
{
    private const string RequiredArchitecture = "ARM";

    public int callbackOrder => -1000;

    static HoloLensWebRtcBuildArchitecture()
    {
        EditorApplication.delayCall += EnsureArchitectureForActiveTarget;
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.WSAPlayer)
        {
            EnsureArmArchitecture(true);
        }
    }

    [MenuItem("MedXR/Prepare HoloLens WebRTC Build")]
    public static void PrepareBuild()
    {
        EnsureArmArchitecture(true);
    }

    private static void EnsureArchitectureForActiveTarget()
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WSAPlayer)
        {
            EnsureArmArchitecture(false);
        }
    }

    private static void EnsureArmArchitecture(bool logWhenAlreadyCorrect)
    {
        string currentArchitecture = EditorUserBuildSettings.wsaArchitecture;
        if (string.Equals(currentArchitecture, RequiredArchitecture, System.StringComparison.OrdinalIgnoreCase))
        {
            if (logWhenAlreadyCorrect)
            {
                Debug.Log("HoloLens WebRTC build architecture is ARM.");
            }

            return;
        }

        EditorUserBuildSettings.wsaArchitecture = RequiredArchitecture;
        Debug.LogWarning(
            "HoloLens UWP architecture changed from '" + currentArchitecture + "' to ARM. " +
            "MixedReality-WebRTC 2.0.2 does not include an ARM64 UWP native plugin.");
    }
}
