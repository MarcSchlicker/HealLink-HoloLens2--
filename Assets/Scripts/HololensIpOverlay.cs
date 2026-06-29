using System;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
#if ENABLE_WINMD_SUPPORT
using Windows.Networking;
using Windows.Networking.Connectivity;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class HololensIpOverlay : MonoBehaviour
{
    private const string OverlayRootName = "HololensIpOverlay";
    private const string EditorPreviewText = "Editor Preview";
    private const string NoIpAddressText = "No IP address";

    [Header("State")]
    public bool showOverlay = true;
    public float refreshSeconds = 1f;

    [Header("World overlay")]
    public Transform targetCamera;
    public bool useWorldSpaceOverlay = true;
    [Tooltip("Creates a visible overlay object if none is assigned or found below the target camera.")]
    public bool createWorldOverlayIfMissing = true;
    [Tooltip("Use this only when you want the inspector values below to overwrite the scene objects once. It turns itself off after applying.")]
    public bool applyInspectorLayoutOnce = false;
    public Transform overlayRootTransform;
    public TextMeshPro textField;
    public MeshRenderer backgroundRenderer;
    public Vector3 localPosition = new Vector3(-0.28f, 0.18f, 0.75f);
    public Vector2 panelSize = new Vector2(0.86f, 0.18f);
    public float worldFontSize = 0.07f;
    [Range(0.05f, 1f)]
    public float backgroundAlpha = 0.6f;
    [Range(0.1f, 1f)]
    public float textAlpha = 0.95f;

    [Header("GUI fallback")]
    public bool showGuiFallback = true;
    public int fontSize = 24;
    public Vector2 margin = new Vector2(12f, 12f);

    private string currentIp = NoIpAddressText;
    private float nextRefreshTime;
    private Material backgroundMaterial;
    private GUIStyle labelStyle;
    private Texture2D guiBackgroundTexture;

    private void OnEnable()
    {
        currentIp = ReadCurrentIpAddress();
        EnsureWorldOverlay();
        UpdateOverlayText();
    }

    private void Start()
    {
        currentIp = ReadCurrentIpAddress();
        EnsureWorldOverlay();
        UpdateOverlayText();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextRefreshTime)
        {
            nextRefreshTime = Time.unscaledTime + Mathf.Max(0.25f, refreshSeconds);
            currentIp = ReadCurrentIpAddress();
        }

        if (showOverlay && useWorldSpaceOverlay)
        {
            EnsureWorldOverlay();
            UpdateOverlayText();
        }

        if (overlayRootTransform != null)
        {
            overlayRootTransform.gameObject.SetActive(showOverlay && useWorldSpaceOverlay);
        }
    }

    private void OnGUI()
    {
        if (!showOverlay || !showGuiFallback)
        {
            return;
        }

        EnsureGuiResources();

        string text = BuildOverlayText();
        Vector2 size = labelStyle.CalcSize(new GUIContent(text));
        Rect rect = new Rect(margin.x, margin.y, size.x + 24f, size.y + 14f);

        Color previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, backgroundAlpha);
        GUI.DrawTexture(rect, guiBackgroundTexture);

        GUI.color = new Color(1f, 1f, 1f, textAlpha);
        GUI.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 24f, rect.height - 14f), text, labelStyle);
        GUI.color = previousColor;
    }

    private void OnDestroy()
    {
        if (Application.isPlaying && overlayRootTransform != null)
        {
            DestroyUnityObject(overlayRootTransform.gameObject);
            overlayRootTransform = null;
        }

        if (Application.isPlaying && backgroundMaterial != null)
        {
            DestroyUnityObject(backgroundMaterial);
            backgroundMaterial = null;
        }

        if (guiBackgroundTexture != null)
        {
            DestroyUnityObject(guiBackgroundTexture);
            guiBackgroundTexture = null;
        }
    }

    private void OnValidate()
    {
        refreshSeconds = Mathf.Max(0.25f, refreshSeconds);
        panelSize = new Vector2(Mathf.Max(0.1f, panelSize.x), Mathf.Max(0.05f, panelSize.y));
        worldFontSize = Mathf.Max(0.01f, worldFontSize);
        fontSize = Mathf.Max(10, fontSize);

        if (!isActiveAndEnabled)
        {
            return;
        }

        currentIp = ReadCurrentIpAddress();
        EnsureWorldOverlay();
        if (applyInspectorLayoutOnce)
        {
            ApplyInspectorLayout();
            applyInspectorLayoutOnce = false;
        }

        UpdateOverlayText();
    }

    private void EnsureWorldOverlay()
    {
        Transform parent = ResolveTargetCamera();
        if (parent == null)
        {
            return;
        }

        bool createdOverlay = false;
        if (overlayRootTransform == null)
        {
            Transform existingOverlay = parent.Find(OverlayRootName);
            if (existingOverlay != null)
            {
                overlayRootTransform = existingOverlay;
            }
            else if (createWorldOverlayIfMissing)
            {
                overlayRootTransform = new GameObject(OverlayRootName).transform;
                overlayRootTransform.SetParent(parent, false);
                createdOverlay = true;
            }
            else
            {
                return;
            }
        }

        if (overlayRootTransform.parent == null)
        {
            overlayRootTransform.SetParent(parent, false);
        }

        bool createdBackground = false;
        if (backgroundRenderer == null)
        {
            Transform existingBackground = overlayRootTransform.Find("Background");
            GameObject background;
            if (existingBackground != null)
            {
                background = existingBackground.gameObject;
            }
            else
            {
                background = GameObject.CreatePrimitive(PrimitiveType.Quad);
                createdBackground = true;
            }

            background.name = "Background";
            background.transform.SetParent(overlayRootTransform, false);
            backgroundRenderer = background.GetComponent<MeshRenderer>();
            Collider collider = background.GetComponent<Collider>();
            if (collider != null)
            {
                DestroyUnityObject(collider);
            }

            if (backgroundRenderer != null)
            {
                backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                backgroundRenderer.receiveShadows = false;
                if (backgroundRenderer.sharedMaterial == null)
                {
                    backgroundMaterial = CreateTransparentMaterial();
                    if (backgroundMaterial != null)
                    {
                        backgroundRenderer.sharedMaterial = backgroundMaterial;
                    }
                }
            }
        }

        bool createdText = false;
        if (textField == null)
        {
            Transform existingText = overlayRootTransform.Find("Text");
            GameObject textObject;
            if (existingText != null)
            {
                textObject = existingText.gameObject;
            }
            else
            {
                textObject = new GameObject("Text");
                createdText = true;
            }

            textObject.transform.SetParent(overlayRootTransform, false);
            textField = textObject.GetComponent<TextMeshPro>();
            if (textField == null)
            {
                textField = textObject.AddComponent<TextMeshPro>();
            }
        }

        if (createdOverlay || createdBackground || createdText || applyInspectorLayoutOnce)
        {
            ApplyInspectorLayout();
        }

        if (textField != null)
        {
            textField.text = BuildOverlayText();
        }
    }

    private void ApplyInspectorLayout()
    {
        if (overlayRootTransform != null)
        {
            overlayRootTransform.localPosition = localPosition;
            overlayRootTransform.localRotation = Quaternion.identity;
            overlayRootTransform.localScale = Vector3.one;
        }

        if (backgroundRenderer != null)
        {
            backgroundRenderer.transform.localPosition = new Vector3(panelSize.x * 0.5f, -panelSize.y * 0.5f, 0.01f);
            backgroundRenderer.transform.localRotation = Quaternion.identity;
            backgroundRenderer.transform.localScale = new Vector3(panelSize.x, panelSize.y, 1f);
        }

        Material material = backgroundMaterial != null ? backgroundMaterial : backgroundRenderer != null ? backgroundRenderer.sharedMaterial : null;
        if (material != null)
        {
            SetMaterialColor(material, new Color(0f, 0f, 0f, backgroundAlpha));
        }

        if (textField != null)
        {
            RectTransform rectTransform = textField.rectTransform;
            rectTransform.sizeDelta = panelSize;
            rectTransform.localPosition = new Vector3(0.035f, -panelSize.y * 0.5f, -0.01f);
            rectTransform.localRotation = Quaternion.identity;
            rectTransform.localScale = Vector3.one;
            textField.enableWordWrapping = false;
            textField.alignment = TextAlignmentOptions.Left;
            textField.verticalAlignment = VerticalAlignmentOptions.Middle;
            textField.fontStyle = FontStyles.Bold;
            textField.fontSize = Mathf.Max(0.01f, worldFontSize);
            textField.color = new Color(1f, 1f, 1f, textAlpha);
        }
    }

    private Transform ResolveTargetCamera()
    {
        if (targetCamera != null)
        {
            return targetCamera;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            targetCamera = mainCamera.transform;
            return targetCamera;
        }

        return transform;
    }

    private void UpdateOverlayText()
    {
        if (textField != null)
        {
            textField.text = BuildOverlayText();
        }
    }

    private string BuildOverlayText()
    {
        if (!Application.isPlaying && string.IsNullOrEmpty(currentIp))
        {
            return "IP: " + EditorPreviewText;
        }

        return "IP: " + currentIp;
    }

    private void EnsureGuiResources()
    {
        if (guiBackgroundTexture == null)
        {
            guiBackgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            guiBackgroundTexture.SetPixel(0, 0, Color.white);
            guiBackgroundTexture.Apply();
        }

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
        }

        labelStyle.fontSize = Mathf.Max(10, fontSize);
    }

    private static Material CreateTransparentMaterial()
    {
        Shader shader = Shader.Find("Unlit/Color");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            return null;
        }

        Material material = new Material(shader);
        ConfigureTransparentMaterial(material);
        return material;
    }

    private static void ConfigureTransparentMaterial(Material material)
    {
        material.SetOverrideTag("RenderType", "Transparent");

        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetInt("_ZWrite", 0);
        }

        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private static void SetMaterialColor(Material material, Color color)
    {
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
    }

    private static string ReadCurrentIpAddress()
    {
        if (!Application.isPlaying)
        {
            return EditorPreviewText;
        }

#if ENABLE_WINMD_SUPPORT
        string uwpIp = TryReadUwpIpv4Address();
        return string.IsNullOrEmpty(uwpIp) ? NoIpAddressText : uwpIp;
#else
        string dotNetIp = TryReadDotNetIpv4Address();
        return string.IsNullOrEmpty(dotNetIp) ? NoIpAddressText : dotNetIp;
#endif
    }

#if ENABLE_WINMD_SUPPORT
    private static string TryReadUwpIpv4Address()
    {
        try
        {
            string fallbackAddress = string.Empty;
            ConnectionProfile connectionProfile = NetworkInformation.GetInternetConnectionProfile();
            Guid? activeAdapterId = null;
            if (connectionProfile != null && connectionProfile.NetworkAdapter != null)
            {
                activeAdapterId = connectionProfile.NetworkAdapter.NetworkAdapterId;
            }

            foreach (HostName hostName in NetworkInformation.GetHostNames())
            {
                if (hostName == null ||
                    hostName.Type != HostNameType.Ipv4 ||
                    hostName.IPInformation == null ||
                    !IsUsableIpv4Address(hostName.CanonicalName))
                {
                    continue;
                }

                if (activeAdapterId.HasValue &&
                    hostName.IPInformation.NetworkAdapter != null &&
                    hostName.IPInformation.NetworkAdapter.NetworkAdapterId == activeAdapterId.Value)
                {
                    return hostName.CanonicalName;
                }

                if (string.IsNullOrEmpty(fallbackAddress))
                {
                    fallbackAddress = hostName.CanonicalName;
                }
            }

            return fallbackAddress;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
#endif

#if !ENABLE_WINMD_SUPPORT
    private static string TryReadDotNetIpv4Address()
    {
        try
        {
            IPAddress[] addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
            for (int i = 0; i < addresses.Length; i++)
            {
                IPAddress address = addresses[i];
                if (address != null && IsUsableIpv4Address(address.ToString()))
                {
                    return address.ToString();
                }
            }
        }
        catch (Exception)
        {
        }

        return string.Empty;
    }
#endif

    private static bool IsUsableIpv4Address(string value)
    {
        IPAddress address;
        return !string.IsNullOrWhiteSpace(value) &&
               IPAddress.TryParse(value, out address) &&
               address.AddressFamily == AddressFamily.InterNetwork &&
               !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any);
    }

    private static void DestroyUnityObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }
}
