using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;
using UnityEngine;
#if ENABLE_WINMD_SUPPORT
using Windows.Devices.Sensors;
#endif

/// <summary>
/// Receives Quest-side hand and drawing packets and applies them to the HoloLens scene.
/// </summary>
[DisallowMultipleComponent]
public sealed class QuestHandDataReceiver : MonoBehaviour
{
    [Header("Network")]
    public int listenPort = 5055;
    public bool startReceiverOnEnable = true;

    [Header("WebRTC audio")]
    [Tooltip("Enable bidirectional WebRTC audio between Quest and HoloLens.")]
    public bool useWebRtcAudio = true;
    public int webRtcLocalSignalingPort = 5076;
    public int webRtcRemoteSignalingPort = 5077;
    public float webRtcStartupDelaySeconds = 0.75f;
    public bool logWebRtcAudioStatus = true;

    [Header("MRTK hand rigs")]
    public Transform leftHandRoot;
    public Transform rightHandRoot;
    public bool autoFindHandRoots = true;
    public bool applyWorldPose = true;
    public bool hideWhenNotTracked = true;
    public bool showMrtkHandMesh = true;
    public bool driveMrtkBoneTransforms = true;

    [Header("Pose space")]
    public bool applyCameraRelativePoses = true;
    public bool autoFindTargetCamera = true;
    public Transform targetCamera;
    public float remotePositionScale = 1f;
    public Vector3 remotePositionOffset = Vector3.zero;
    public Vector3 remoteRotationOffsetEuler = Vector3.zero;

    [Header("Remote hand look")]
    public bool applyRemoteHandMaterial = true;
    public bool reapplyRemoteHandMaterialEveryFrame = true;
    public Color remoteHandColor = new Color(0.12f, 0.72f, 1f, 0.18f);
    [Range(0.02f, 1f)]
    public float remoteHandAlpha = 0.22f;
    [Range(0.05f, 1.5f)]
    public float remoteHandBrightness = 0.38f;
    [Tooltip("Adjust remote hand brightness and alpha from the room's ambient light.")]
    public bool adaptRemoteHandVisibilityToRoomLight = true;
    [Tooltip("How often the HoloLens ambient light sensor is sampled.")]
    public float roomLightSampleIntervalSeconds = 0.5f;
    [Tooltip("Lux value treated as a dark room.")]
    public float darkRoomLux = 30f;
    [Tooltip("Lux value treated as a bright room.")]
    public float brightRoomLux = 650f;
    [Range(0.05f, 1.5f)]
    public float darkRoomHandBrightness = 0.32f;
    [Range(0.05f, 1.5f)]
    public float brightRoomHandBrightness = 0.95f;
    [Range(0.02f, 1f)]
    public float darkRoomHandAlpha = 0.16f;
    [Range(0.02f, 1f)]
    public float brightRoomHandAlpha = 0.45f;
    public bool enableRemoteHandEmission = false;
    public bool showQuestJointVisual = false;
    public float jointSphereDiameter = 0.018f;
    public float jointLineWidth = 0.009f;

    [Header("Remote drawing")]
    public bool receiveRemoteStrokes = true;
    public Transform remoteStrokeRoot;
    public Color fallbackStrokeColor = Color.red;
    public float fallbackStrokeWidth = 0.01f;
    public bool useSenderStrokeStyle = true;

    [Header("MRTK pointer visuals")]
    public bool disableMrtkHandRays = true;
    public bool disableMrtkControllerRays = false;
    public bool disableMrtkGazePointer = false;

    [Header("Packet queue")]
    public int maxQueuedPackets = 120;
    public int maxPacketsProcessedPerFrame = 30;

    private readonly object packetLock = new object();
    private readonly Queue<string> packetQueue = new Queue<string>(64);
    private readonly Dictionary<string, Transform> leftBones = new Dictionary<string, Transform>();
    private readonly Dictionary<string, Transform> rightBones = new Dictionary<string, Transform>();
    private readonly Dictionary<string, JointWorldPose> leftWorldJoints = new Dictionary<string, JointWorldPose>(32);
    private readonly Dictionary<string, JointWorldPose> rightWorldJoints = new Dictionary<string, JointWorldPose>(32);
    private readonly Dictionary<string, int> leftJointPriorities = new Dictionary<string, int>(32);
    private readonly Dictionary<string, int> rightJointPriorities = new Dictionary<string, int>(32);
    private readonly Dictionary<string, RemoteStroke> remoteStrokes = new Dictionary<string, RemoteStroke>(32);
    private readonly Dictionary<string, string> activeRemoteStrokeKeys = new Dictionary<string, string>(8);
    private readonly HashSet<string> completedRemoteStrokeSources = new HashSet<string>();
    private readonly HandRigRetargeter leftRigRetargeter = new HandRigRetargeter();
    private readonly HandRigRetargeter rightRigRetargeter = new HandRigRetargeter();
    private readonly HandJointVisual leftJointVisual = new HandJointVisual("RemoteQuestLeftHand");
    private readonly HandJointVisual rightJointVisual = new HandJointVisual("RemoteQuestRightHand");
    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running;
    private Material remoteHandMaterial;
    private Material remoteStrokeMaterial;
    private Transform createdStrokeRoot;
    private int anonymousStrokeId = 1;
    private bool mrtkPointerSettingsApplied;
    private bool lastDisableMrtkHandRays;
    private bool lastDisableMrtkControllerRays;
    private bool lastDisableMrtkGazePointer;
    private float nextMrtkPointerApplyTime;
    private WebRtcAudioPeer webRtcAudioPeer;
    private float effectiveRemoteHandAlpha = 0.22f;
    private float effectiveRemoteHandBrightness = 0.38f;
    private float nextRoomLightSampleTime;
#if ENABLE_WINMD_SUPPORT
    private LightSensor ambientLightSensor;
#endif

    private void OnEnable()
    {
        // Bring up all runtime subsystems that need scene references: audio, hand rigs, styling, and networking.
        StartWebRtcAudio();

        if (autoFindHandRoots)
        {
            leftHandRoot = leftHandRoot != null ? leftHandRoot : FindTransformByName("L_Hand_MRTK_Rig");
            rightHandRoot = rightHandRoot != null ? rightHandRoot : FindTransformByName("R_Hand_MRTK_Rig");
        }

        RebuildBoneMaps();
        UpdateRemoteHandVisibilityForRoomLight(true);
        ApplyRemoteHandStyle();
        ResolveTargetCamera();
        mrtkPointerSettingsApplied = false;
        ApplyMrtkPointerSettingsWhenReady();

        if (startReceiverOnEnable)
        {
            StartReceiver();
        }
    }

    private void LateUpdate()
    {
        bool remoteHandVisibilityChanged = UpdateRemoteHandVisibilityForRoomLight(false);

        if (reapplyRemoteHandMaterialEveryFrame || remoteHandVisibilityChanged)
        {
            ApplyRemoteHandStyle();
        }

        ApplyMrtkPointerSettingsWhenReady();
    }

    private void OnDisable()
    {
        StopWebRtcAudio();
        StopReceiver();
    }

    private void OnDestroy()
    {
        StopWebRtcAudio();
        leftJointVisual.Destroy();
        rightJointVisual.Destroy();
        DestroyRemoteStrokes();

        if (remoteHandMaterial != null)
        {
            Destroy(remoteHandMaterial);
            remoteHandMaterial = null;
        }

        if (remoteStrokeMaterial != null)
        {
            Destroy(remoteStrokeMaterial);
            remoteStrokeMaterial = null;
        }

    }

    private void Update()
    {
        int processedCount = 0;
        int perFrameLimit = Mathf.Max(1, maxPacketsProcessedPerFrame);
        string json;
        while (processedCount < perFrameLimit && TryDequeuePacket(out json))
        {
            ProcessPacket(json);
            processedCount++;
        }
    }

    private void StartWebRtcAudio()
    {
        if (!useWebRtcAudio)
        {
            return;
        }

        if (webRtcAudioPeer == null)
        {
            webRtcAudioPeer = GetComponent<WebRtcAudioPeer>();
            if (webRtcAudioPeer == null)
            {
                webRtcAudioPeer = gameObject.AddComponent<WebRtcAudioPeer>();
            }
        }

        webRtcAudioPeer.startupDelaySeconds = Mathf.Max(0f, webRtcStartupDelaySeconds);
        webRtcAudioPeer.logStatus = logWebRtcAudioStatus;
        webRtcAudioPeer.ConfigureAndStart(
            false,
            "",
            Mathf.Clamp(webRtcLocalSignalingPort, 1, 65535),
            Mathf.Clamp(webRtcRemoteSignalingPort, 1, 65535));
    }

    private void StopWebRtcAudio()
    {
        if (webRtcAudioPeer != null)
        {
            webRtcAudioPeer.StopPeer();
        }
    }

    private bool TryDequeuePacket(out string json)
    {
        lock (packetLock)
        {
            if (packetQueue.Count > 0)
            {
                json = packetQueue.Dequeue();
                return true;
            }
        }

        json = null;
        return false;
    }

    private void ProcessPacket(string json)
    {
        // Each UDP message is a full snapshot: hand poses plus optional stroke payloads.
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        HandFramePacket packet;
        try
        {
            packet = JsonUtility.FromJson<HandFramePacket>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("QuestHandDataReceiver could not parse packet: " + e.Message);
            return;
        }

        if (packet == null)
        {
            return;
        }

        bool posesAreCameraRelative = applyCameraRelativePoses &&
                                      string.Equals(packet.poseSpace, "CameraLocal", StringComparison.OrdinalIgnoreCase);

        if (packet.hands != null)
        {
            for (int i = 0; i < packet.hands.Length; i++)
            {
                ApplyHand(packet.hands[i], posesAreCameraRelative);
            }
        }

        ApplyStrokeEvents(packet.strokeEvents, posesAreCameraRelative);
    }

    public void StartReceiver()
    {
        // UDP receive runs on a background thread and queues JSON for Unity's main thread.
        if (running)
        {
            return;
        }

        try
        {
            ClearQueuedPackets();
            udpClient = new UdpClient(listenPort);
            running = true;
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();
        }
        catch (Exception e)
        {
            running = false;
            Debug.LogError("QuestHandDataReceiver could not listen on UDP port " + listenPort + ": " + e.Message);
        }
    }

    public void StopReceiver()
    {
        running = false;

        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        receiveThread = null;
        ClearQueuedPackets();
    }

    private void ClearQueuedPackets()
    {
        lock (packetLock)
        {
            packetQueue.Clear();
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] bytes = udpClient.Receive(ref any);
                string json = Encoding.UTF8.GetString(bytes);
                lock (packetLock)
                {
                    int queueLimit = maxQueuedPackets > 0 ? maxQueuedPackets : 1;
                    while (packetQueue.Count >= queueLimit)
                    {
                        packetQueue.Dequeue();
                    }

                    packetQueue.Enqueue(json);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning("QuestHandDataReceiver receive error: " + e.Message);
            }
        }
    }

    private void ApplyHand(HandFrame hand, bool posesAreCameraRelative)
    {
        // Convert sender joint names into normalized keys used by the MRTK retargeter.
        if (hand == null)
        {
            return;
        }

        bool isLeft = string.Equals(hand.handedness, "Left", StringComparison.OrdinalIgnoreCase);
        Transform root = isLeft ? leftHandRoot : rightHandRoot;
        Dictionary<string, Transform> bones = isLeft ? leftBones : rightBones;
        Dictionary<string, JointWorldPose> worldJoints = isLeft ? leftWorldJoints : rightWorldJoints;
        Dictionary<string, int> jointPriorities = isLeft ? leftJointPriorities : rightJointPriorities;
        HandJointVisual jointVisual = isLeft ? leftJointVisual : rightJointVisual;

        worldJoints.Clear();
        jointPriorities.Clear();

        if (hideWhenNotTracked && root != null)
        {
            root.gameObject.SetActive(hand.isTracked);
        }

        if (!hand.isTracked)
        {
            if (showQuestJointVisual && hideWhenNotTracked)
            {
                jointVisual.SetActive(false);
            }

            return;
        }

        AddWorldJoint(worldJoints, jointPriorities, "wrist", hand.wrist, posesAreCameraRelative, 10);

        if (hand.joints != null)
        {
            bool useOvrHandBoneOrder = LooksLikeOvrHandBoneOrder(hand.joints);
            for (int i = 0; i < hand.joints.Length; i++)
            {
                JointPose joint = hand.joints[i];
                int priority;
                string key = NormalizeIncomingJointName(joint.name, i, useOvrHandBoneOrder, out priority);
                AddWorldJoint(worldJoints, jointPriorities, key, joint.pose, posesAreCameraRelative, priority);
            }
        }

        if (showQuestJointVisual)
        {
            jointVisual.Update(transform, worldJoints, GetOrCreateRemoteHandMaterial(), ResolveRemoteHandColor(), jointSphereDiameter, jointLineWidth);
        }

        if (root == null || worldJoints.Count == 0)
        {
            return;
        }

        HandRigRetargeter rigRetargeter = isLeft ? leftRigRetargeter : rightRigRetargeter;

        if (!driveMrtkBoneTransforms || !rigRetargeter.ApplyRoot(root, worldJoints))
        {
            ApplyRootFallback(root, worldJoints, hand.wrist, posesAreCameraRelative);
        }

        if (!driveMrtkBoneTransforms)
        {
            return;
        }

        rigRetargeter.Apply(bones, worldJoints);
    }

    private bool ApplyRootFallback(Transform root, Dictionary<string, JointWorldPose> worldJoints, PoseData wristPose, bool poseIsCameraRelative)
    {
        if (root == null)
        {
            return false;
        }

        JointWorldPose worldPose;
        if (worldJoints != null &&
            (worldJoints.TryGetValue("wrist", out worldPose) || worldJoints.TryGetValue("palm", out worldPose)))
        {
            ApplyWorldPose(root, worldPose);
            return true;
        }

        if (wristPose.isValid)
        {
            ApplyPose(root, wristPose, poseIsCameraRelative);
            return true;
        }

        return false;
    }

    private void AddWorldJoint(Dictionary<string, JointWorldPose> worldJoints, string key, PoseData pose, bool poseIsCameraRelative)
    {
        AddWorldJoint(worldJoints, null, key, pose, poseIsCameraRelative, 0);
    }

    private void AddWorldJoint(Dictionary<string, JointWorldPose> worldJoints, Dictionary<string, int> jointPriorities, string key, PoseData pose, bool poseIsCameraRelative, int priority)
    {
        if (string.IsNullOrEmpty(key) || !pose.isValid)
        {
            return;
        }

        JointWorldPose worldPose;
        if (TryGetWorldPose(pose, poseIsCameraRelative, out worldPose))
        {
            if (jointPriorities != null)
            {
                int existingPriority;
                if (jointPriorities.TryGetValue(key, out existingPriority) && existingPriority > priority)
                {
                    return;
                }

                jointPriorities[key] = priority;
            }

            worldJoints[key] = worldPose;
        }
    }

    private void ApplyPose(Transform target, PoseData pose, bool poseIsCameraRelative)
    {
        if (target == null || !pose.isValid)
        {
            return;
        }

        JointWorldPose worldPose;
        if (TryGetWorldPose(pose, poseIsCameraRelative, out worldPose))
        {
            ApplyWorldPose(target, worldPose);
        }
    }

    private static void ApplyWorldPose(Transform target, JointWorldPose pose)
    {
        target.SetPositionAndRotation(pose.position, pose.rotation);
    }

    private bool TryGetWorldPose(PoseData pose, bool poseIsCameraRelative, out JointWorldPose worldPose)
    {
        // Quest packets usually arrive in camera-local space, so convert them through the HoloLens camera.
        worldPose = new JointWorldPose
        {
            position = Vector3.zero,
            rotation = Quaternion.identity
        };

        if (!pose.isValid)
        {
            return false;
        }

        if (poseIsCameraRelative)
        {
            Transform cameraTransform = ResolveTargetCamera();
            if (cameraTransform == null)
            {
                return false;
            }

            Vector3 localPosition = pose.position * remotePositionScale + remotePositionOffset;
            Quaternion localRotation = Quaternion.Euler(remoteRotationOffsetEuler) * pose.rotation;
            worldPose.position = cameraTransform.TransformPoint(localPosition);
            worldPose.rotation = cameraTransform.rotation * localRotation;
            return true;
        }

        if (applyWorldPose)
        {
            worldPose.position = pose.position;
            worldPose.rotation = pose.rotation;
            return true;
        }

        worldPose.position = transform.TransformPoint(pose.position);
        worldPose.rotation = transform.rotation * pose.rotation;
        return true;
    }

    private bool TryGetWorldPosition(PoseData pose, bool poseIsCameraRelative, out Vector3 worldPosition)
    {
        JointWorldPose worldPose;
        if (TryGetWorldPose(pose, poseIsCameraRelative, out worldPose))
        {
            worldPosition = worldPose.position;
            return true;
        }

        worldPosition = Vector3.zero;
        return false;
    }

    private void ApplyStrokeEvents(StrokeEvent[] strokeEvents, bool posesAreCameraRelative)
    {
        // Remote drawing is represented as keyed stroke lifetimes: begin, append points, then complete.
        if (!receiveRemoteStrokes || strokeEvents == null)
        {
            return;
        }

        for (int i = 0; i < strokeEvents.Length; i++)
        {
            StrokeEvent strokeEvent = strokeEvents[i];
            if (strokeEvent == null || !strokeEvent.point.isValid)
            {
                continue;
            }

            Vector3 worldPoint;
            if (!TryGetWorldPosition(strokeEvent.point, posesAreCameraRelative, out worldPoint))
            {
                continue;
            }

            string sourceId = GetStrokeSourceId(strokeEvent);
            string action = NormalizeStrokeAction(strokeEvent.action);
            if (action == "begin")
            {
                BeginRemoteStroke(sourceId, strokeEvent, worldPoint);
            }
            else if (action == "end")
            {
                AppendRemoteStroke(sourceId, strokeEvent, worldPoint);
                FinishRemoteStroke(sourceId);
            }
            else
            {
                AppendRemoteStroke(sourceId, strokeEvent, worldPoint);
            }
        }
    }

    private void BeginRemoteStroke(string sourceId, StrokeEvent strokeEvent, Vector3 worldPoint)
    {
        if (string.IsNullOrEmpty(sourceId))
        {
            return;
        }

        completedRemoteStrokeSources.Remove(sourceId);

        string strokeKey = CreateUniqueStrokeKey(sourceId);
        RemoteStroke stroke = CreateRemoteStroke(strokeKey, strokeEvent);
        remoteStrokes[strokeKey] = stroke;
        activeRemoteStrokeKeys[sourceId] = strokeKey;
        stroke.Append(worldPoint);
    }

    private void AppendRemoteStroke(string sourceId, StrokeEvent strokeEvent, Vector3 worldPoint)
    {
        RemoteStroke stroke = GetOrCreateActiveRemoteStroke(sourceId, strokeEvent);
        if (stroke == null)
        {
            return;
        }

        stroke.Append(worldPoint);
    }

    private void FinishRemoteStroke(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
        {
            return;
        }

        string strokeKey;
        RemoteStroke stroke;
        if (activeRemoteStrokeKeys.TryGetValue(sourceId, out strokeKey) &&
            remoteStrokes.TryGetValue(strokeKey, out stroke) &&
            stroke != null)
        {
            stroke.Complete();
        }

        activeRemoteStrokeKeys.Remove(sourceId);
        completedRemoteStrokeSources.Add(sourceId);
    }

    private RemoteStroke GetOrCreateActiveRemoteStroke(string sourceId, StrokeEvent strokeEvent)
    {
        if (string.IsNullOrEmpty(sourceId) || completedRemoteStrokeSources.Contains(sourceId))
        {
            return null;
        }

        string strokeKey;
        RemoteStroke stroke;
        if (activeRemoteStrokeKeys.TryGetValue(sourceId, out strokeKey) &&
            remoteStrokes.TryGetValue(strokeKey, out stroke) &&
            stroke != null &&
            !stroke.IsComplete)
        {
            return stroke;
        }

        if (remoteStrokes.TryGetValue(sourceId, out stroke) && stroke != null)
        {
            if (stroke.IsComplete)
            {
                return null;
            }

            activeRemoteStrokeKeys[sourceId] = sourceId;
            return stroke;
        }

        strokeKey = CreateUniqueStrokeKey(sourceId);
        stroke = CreateRemoteStroke(strokeKey, strokeEvent);
        remoteStrokes[strokeKey] = stroke;
        activeRemoteStrokeKeys[sourceId] = strokeKey;
        return stroke;
    }

    private RemoteStroke CreateRemoteStroke(string strokeKey, StrokeEvent strokeEvent)
    {
        Transform strokeParent = GetRemoteStrokeRoot();
        GameObject strokeObject = new GameObject("RemoteQuestStroke_" + strokeKey);
        if (strokeParent != null)
        {
            strokeObject.transform.SetParent(strokeParent, false);
        }

        LineRenderer line = strokeObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.numCapVertices = 6;
        line.numCornerVertices = 6;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = GetOrCreateRemoteStrokeMaterial();

        Color color = ResolveStrokeColor(strokeEvent);
        float width = ResolveStrokeWidth(strokeEvent);
        return new RemoteStroke(strokeObject, line, color, width);
    }

    private Transform GetRemoteStrokeRoot()
    {
        if (remoteStrokeRoot != null)
        {
            return remoteStrokeRoot;
        }

        if (createdStrokeRoot == null)
        {
            GameObject root = new GameObject("RemoteQuestPersistentStrokes");
            root.transform.SetParent(transform, false);
            createdStrokeRoot = root.transform;
        }

        return createdStrokeRoot;
    }

    private string CreateUniqueStrokeKey(string sourceId)
    {
        string baseKey = !string.IsNullOrEmpty(sourceId) ? sourceId : "remote-stroke-" + anonymousStrokeId++;
        string key = baseKey;
        int suffix = 2;
        while (remoteStrokes.ContainsKey(key))
        {
            key = baseKey + "-" + suffix;
            suffix++;
        }

        return key;
    }

    private string GetStrokeSourceId(StrokeEvent strokeEvent)
    {
        if (strokeEvent != null && !string.IsNullOrEmpty(strokeEvent.id))
        {
            return strokeEvent.id;
        }

        return "anonymous-stroke-" + anonymousStrokeId++;
    }

    private static string NormalizeStrokeAction(string action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return "point";
        }

        return action.Trim().ToLowerInvariant();
    }

    private Color ResolveStrokeColor(StrokeEvent strokeEvent)
    {
        if (useSenderStrokeStyle && strokeEvent != null && strokeEvent.color.a > 0.001f)
        {
            return strokeEvent.color;
        }

        return fallbackStrokeColor;
    }

    private float ResolveStrokeWidth(StrokeEvent strokeEvent)
    {
        if (useSenderStrokeStyle && strokeEvent != null && strokeEvent.width > 0.0001f)
        {
            return strokeEvent.width;
        }

        return fallbackStrokeWidth;
    }

    private Material GetOrCreateRemoteStrokeMaterial()
    {
        if (remoteStrokeMaterial != null)
        {
            return remoteStrokeMaterial;
        }

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            return null;
        }

        remoteStrokeMaterial = new Material(shader);
        if (remoteStrokeMaterial.HasProperty("_Color"))
        {
            remoteStrokeMaterial.SetColor("_Color", Color.white);
        }

        if (remoteStrokeMaterial.HasProperty("_BaseColor"))
        {
            remoteStrokeMaterial.SetColor("_BaseColor", Color.white);
        }

        return remoteStrokeMaterial;
    }

    private void DestroyRemoteStrokes()
    {
        foreach (RemoteStroke stroke in remoteStrokes.Values)
        {
            if (stroke != null)
            {
                stroke.Destroy();
            }
        }

        remoteStrokes.Clear();
        activeRemoteStrokeKeys.Clear();
        completedRemoteStrokeSources.Clear();

        if (createdStrokeRoot != null)
        {
            Destroy(createdStrokeRoot.gameObject);
            createdStrokeRoot = null;
        }
    }

    private void ApplyMrtkPointerSettingsWhenReady()
    {
        // MRTK creates pointers lazily, so retry for a short time after startup and when inspector values change.
        bool valuesUnchanged = mrtkPointerSettingsApplied &&
                               lastDisableMrtkHandRays == disableMrtkHandRays &&
                               lastDisableMrtkControllerRays == disableMrtkControllerRays &&
                               lastDisableMrtkGazePointer == disableMrtkGazePointer;
        if (valuesUnchanged)
        {
            return;
        }

        if (Time.unscaledTime < nextMrtkPointerApplyTime)
        {
            return;
        }

        nextMrtkPointerApplyTime = Time.unscaledTime + 0.25f;

        if (!(CoreServices.InputSystem?.FocusProvider is IPointerPreferences))
        {
            return;
        }

        PointerUtils.SetHandRayPointerBehavior(disableMrtkHandRays ? PointerBehavior.AlwaysOff : PointerBehavior.Default, Handedness.Any);
        PointerUtils.SetMotionControllerRayPointerBehavior(disableMrtkControllerRays ? PointerBehavior.AlwaysOff : PointerBehavior.Default, Handedness.Any);
        PointerUtils.SetGazePointerBehavior(disableMrtkGazePointer ? PointerBehavior.AlwaysOff : PointerBehavior.Default);

        lastDisableMrtkHandRays = disableMrtkHandRays;
        lastDisableMrtkControllerRays = disableMrtkControllerRays;
        lastDisableMrtkGazePointer = disableMrtkGazePointer;
        mrtkPointerSettingsApplied = true;
    }

    private Transform ResolveTargetCamera()
    {
        if (!autoFindTargetCamera && targetCamera != null)
        {
            return targetCamera;
        }

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

        Camera[] cameras = Camera.allCameras;
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null && cameras[i].enabled)
            {
                targetCamera = cameras[i].transform;
                return targetCamera;
            }
        }

        return null;
    }

    private void ApplyRemoteHandStyle()
    {
        Material material = GetOrCreateRemoteHandMaterial();

        SetHandMeshVisible(leftHandRoot, showMrtkHandMesh);
        SetHandMeshVisible(rightHandRoot, showMrtkHandMesh);

        if (!applyRemoteHandMaterial || material == null)
        {
            return;
        }

        ApplyMaterialToHand(leftHandRoot, material);
        ApplyMaterialToHand(rightHandRoot, material);
    }

    private Material GetOrCreateRemoteHandMaterial()
    {
        if (remoteHandMaterial == null)
        {
            remoteHandMaterial = CreateRemoteHandMaterial();
        }

        ConfigureTransparentMaterial(remoteHandMaterial, ResolveRemoteHandColor(), enableRemoteHandEmission);
        return remoteHandMaterial;
    }

    private Color ResolveRemoteHandColor()
    {
        float brightness = Mathf.Max(0f, effectiveRemoteHandBrightness);
        return new Color(
            Mathf.Clamp01(remoteHandColor.r * brightness),
            Mathf.Clamp01(remoteHandColor.g * brightness),
            Mathf.Clamp01(remoteHandColor.b * brightness),
            Mathf.Clamp01(remoteHandColor.a * effectiveRemoteHandAlpha));
    }

    private bool UpdateRemoteHandVisibilityForRoomLight(bool force)
    {
        float previousAlpha = effectiveRemoteHandAlpha;
        float previousBrightness = effectiveRemoteHandBrightness;

        if (!adaptRemoteHandVisibilityToRoomLight)
        {
            effectiveRemoteHandAlpha = remoteHandAlpha;
            effectiveRemoteHandBrightness = remoteHandBrightness;
            return HasRemoteHandVisibilityChanged(previousAlpha, previousBrightness);
        }

        float now = Application.isPlaying ? Time.unscaledTime : 0f;
        if (!force && Application.isPlaying && now < nextRoomLightSampleTime)
        {
            return false;
        }

        nextRoomLightSampleTime = now + Mathf.Max(0.1f, roomLightSampleIntervalSeconds);

        float roomLux = ReadRoomLightLux();
        float lightFactor = Mathf.InverseLerp(darkRoomLux, brightRoomLux, roomLux);
        effectiveRemoteHandBrightness = Mathf.Clamp(
            Mathf.Lerp(darkRoomHandBrightness, brightRoomHandBrightness, lightFactor),
            0.05f,
            1.5f);
        effectiveRemoteHandAlpha = Mathf.Clamp01(
            Mathf.Lerp(darkRoomHandAlpha, brightRoomHandAlpha, lightFactor));

        return force || HasRemoteHandVisibilityChanged(previousAlpha, previousBrightness);
    }

    private float ReadRoomLightLux()
    {
#if ENABLE_WINMD_SUPPORT
        try
        {
            if (ambientLightSensor == null)
            {
                ambientLightSensor = LightSensor.GetDefault();
            }

            if (ambientLightSensor != null)
            {
                LightSensorReading reading = ambientLightSensor.GetCurrentReading();
                if (reading != null)
                {
                    return Mathf.Max(0f, (float)reading.IlluminanceInLux);
                }
            }
        }
        catch (Exception)
        {
        }
#endif

        // Editor and unsupported devices do not expose the HoloLens light sensor, so use scene ambient light as a stable preview value.
        float ambientFactor = Mathf.Clamp01(RenderSettings.ambientIntensity);
        if (ambientFactor <= 0f)
        {
            ambientFactor = Mathf.Clamp01(RenderSettings.ambientLight.maxColorComponent);
        }

        return Mathf.Lerp(darkRoomLux, brightRoomLux, ambientFactor > 0f ? ambientFactor : 0.5f);
    }

    private bool HasRemoteHandVisibilityChanged(float previousAlpha, float previousBrightness)
    {
        return !Mathf.Approximately(previousAlpha, effectiveRemoteHandAlpha) ||
               !Mathf.Approximately(previousBrightness, effectiveRemoteHandBrightness);
    }

    private static void SetHandMeshVisible(Transform root, bool visible)
    {
        if (root == null)
        {
            return;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                renderers[i].enabled = visible;
            }
        }
    }

    private static void ApplyMaterialToHand(Transform root, Material material)
    {
        if (root == null || material == null)
        {
            return;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
            {
                continue;
            }

            renderers[i].sharedMaterial = material;
            renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderers[i].receiveShadows = false;
        }
    }

    private static Material CreateRemoteHandMaterial()
    {
        Shader shader = FindRemoteHandShader();
        if (shader == null)
        {
            return null;
        }

        return new Material(shader);
    }

    private static Shader FindRemoteHandShader()
    {
        string[] shaderNames =
        {
            "Sprites/Default",
            "Unlit/Color",
            "Universal Render Pipeline/Unlit",
            "Mixed Reality Toolkit/Standard",
            "Standard"
        };

        for (int i = 0; i < shaderNames.Length; i++)
        {
            Shader shader = Shader.Find(shaderNames[i]);
            if (shader != null)
            {
                return shader;
            }
        }

        return null;
    }

    private static void ConfigureTransparentMaterial(Material material, Color color, bool enableEmission)
    {
        if (material == null)
        {
            return;
        }

        material.SetOverrideTag("RenderType", "Transparent");

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_TintColor"))
        {
            material.SetColor("_TintColor", color);
        }

        if (material.HasProperty("_EmissionColor"))
        {
            Color emission = enableEmission ? new Color(color.r, color.g, color.b, 1f) : Color.black;
            material.SetColor("_EmissionColor", emission);
            if (enableEmission)
            {
                material.EnableKeyword("_EMISSION");
            }
            else
            {
                material.DisableKeyword("_EMISSION");
            }
        }

        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", 0f);
        }

        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", 0f);
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

    private void RebuildBoneMaps()
    {
        leftBones.Clear();
        rightBones.Clear();
        BuildBoneMap(leftHandRoot, true, leftBones);
        BuildBoneMap(rightHandRoot, false, rightBones);
        leftRigRetargeter.Rebuild(leftHandRoot, leftBones);
        rightRigRetargeter.Rebuild(rightHandRoot, rightBones);
    }

    private static void BuildBoneMap(Transform root, bool isLeft, Dictionary<string, Transform> map)
    {
        if (root == null)
        {
            return;
        }

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            string key = NormalizeTargetBoneName(transforms[i].name, isLeft);
            if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
            {
                map.Add(key, transforms[i]);
            }
        }
    }

    private static Transform FindTransformByName(string targetName)
    {
        Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] != null && transforms[i].name == targetName)
            {
                return transforms[i];
            }
        }

        return null;
    }

    private static string NormalizeTargetBoneName(string source, bool isLeft)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        string value = source.ToLowerInvariant()
            .Replace("-", "_")
            .Replace(" ", "_");

        value = StripPrefix(value, isLeft ? "l_" : "r_");
        value = StripPrefix(value, isLeft ? "left_" : "right_");

        if (value.Contains("wrist"))
        {
            return "wrist";
        }

        string finger = ReadFingerName(value);
        if (finger == null)
        {
            return null;
        }

        if (value.Contains("end") || value.Contains("tip"))
        {
            return finger + "_end";
        }

        if (value.EndsWith("_1", StringComparison.Ordinal) || value.EndsWith("1", StringComparison.Ordinal))
        {
            return finger + "_1";
        }

        if (value.EndsWith("_2", StringComparison.Ordinal) || value.EndsWith("2", StringComparison.Ordinal))
        {
            return finger + "_2";
        }

        if (value.EndsWith("_3", StringComparison.Ordinal) || value.EndsWith("3", StringComparison.Ordinal))
        {
            return finger + "_3";
        }

        return null;
    }

    private static bool LooksLikeOvrHandBoneOrder(JointPose[] joints)
    {
        if (joints == null || joints.Length < 24)
        {
            return false;
        }

        string firstName = joints[0].name != null ? joints[0].name.ToLowerInvariant() : string.Empty;
        if (firstName.Contains("body_start") || firstName.Contains("hand_wristroot") || firstName.Contains("xrhand_wrist"))
        {
            return true;
        }

        int ovrAliasCount = 0;
        for (int i = 0; i < joints.Length; i++)
        {
            string name = joints[i].name != null ? joints[i].name.ToLowerInvariant() : string.Empty;
            if (name.Contains("body_") || name.Contains("fullbody_") || name.Contains("hand_") || name.Contains("xrhand_"))
            {
                ovrAliasCount++;
            }
        }

        return ovrAliasCount >= 20;
    }

    private static string NormalizeIncomingJointName(string source, int jointIndex, bool useOvrHandBoneOrder, out int priority)
    {
        if (useOvrHandBoneOrder)
        {
            string orderedKey = NormalizeOvrHandBoneByIndex(jointIndex);
            if (!string.IsNullOrEmpty(orderedKey))
            {
                priority = 100;
                return orderedKey;
            }
        }

        return NormalizeIncomingJointName(source, out priority);
    }

    private static string NormalizeOvrHandBoneByIndex(int index)
    {
        switch (index)
        {
            case 0:
                return "wrist";
            case 2:
                return "thumb_1";
            case 3:
                return "thumb_2";
            case 4:
                return "thumb_3";
            case 5:
                return "thumb_end";
            case 6:
                return "pointer_metacarpal";
            case 7:
                return "pointer_1";
            case 8:
                return "pointer_2";
            case 9:
                return "pointer_3";
            case 10:
                return "pointer_end";
            case 11:
                return "middle_metacarpal";
            case 12:
                return "middle_1";
            case 13:
                return "middle_2";
            case 14:
                return "middle_3";
            case 15:
                return "middle_end";
            case 16:
                return "ring_metacarpal";
            case 17:
                return "ring_1";
            case 18:
                return "ring_2";
            case 19:
                return "ring_3";
            case 20:
                return "ring_end";
            case 21:
                return "pinky_metacarpal";
            case 22:
                return "pinky_1";
            case 23:
                return "pinky_2";
            case 24:
                return "pinky_3";
            case 25:
                return "pinky_end";
            default:
                return null;
        }
    }

    private static string NormalizeIncomingJointName(string source, out int priority)
    {
        priority = 50;

        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        string originalValue = source.ToLowerInvariant();
        bool exactXrHandName = originalValue.Contains("xrhand_");
        bool exactLegacyHandName = originalValue.Contains("hand_") &&
                                   !originalValue.Contains("body_") &&
                                   !originalValue.Contains("fullbody_");
        if (originalValue.Contains("body_") || originalValue.Contains("fullbody_"))
        {
            priority = 0;
            return null;
        }

        priority = exactXrHandName ? 90 : exactLegacyHandName ? 80 : 50;

        string value = source.ToLowerInvariant()
            .Replace("ovrskeleton.boneid.", string.Empty)
            .Replace("ovrplugin.boneid.", string.Empty)
            .Replace("fullbody_left", string.Empty)
            .Replace("fullbody_right", string.Empty)
            .Replace("-", "_")
            .Replace(" ", "_");

        value = StripPrefix(value, "left_");
        value = StripPrefix(value, "right_");
        value = StripPrefix(value, "l_");
        value = StripPrefix(value, "r_");

        if (value.Contains("forearm"))
        {
            return null;
        }

        if (value.Contains("wrist"))
        {
            return "wrist";
        }

        if (value.Contains("palm"))
        {
            return "palm";
        }

        string finger = ReadFingerName(value);
        if (finger == null)
        {
            return null;
        }

        if (value.Contains("tip") || value.Contains("end"))
        {
            return finger + "_end";
        }

        if (finger == "thumb")
        {
            if (value.Contains("thumb0"))
            {
                return "thumb_metacarpal";
            }

            if (value.Contains("metacarpal") || value.Contains("thumb1"))
            {
                return "thumb_1";
            }

            if (value.Contains("proximal") || value.Contains("thumb2"))
            {
                return "thumb_2";
            }

            if (value.Contains("distal") || value.Contains("thumb3"))
            {
                return "thumb_3";
            }
        }

        if (value.Contains("metacarpal") || value.Contains("pinky0") || value.Contains("little0"))
        {
            return finger + "_metacarpal";
        }

        if (value.Contains("proximal") || value.Contains("index1") || value.Contains("pointer1") ||
            value.Contains("middle1") || value.Contains("ring1") || value.Contains("pinky1") ||
            value.Contains("little1"))
        {
            return finger + "_1";
        }

        if (value.Contains("intermediate") || value.Contains("index2") || value.Contains("pointer2") ||
            value.Contains("middle2") || value.Contains("ring2") || value.Contains("pinky2") ||
            value.Contains("little2"))
        {
            return finger + "_2";
        }

        if (value.Contains("distal") || value.Contains("index3") || value.Contains("pointer3") ||
            value.Contains("middle3") || value.Contains("ring3") || value.Contains("pinky3") ||
            value.Contains("little3"))
        {
            return finger + "_3";
        }

        return null;
    }

    private static string StripPrefix(string value, string prefix)
    {
        if (!string.IsNullOrEmpty(value) &&
            !string.IsNullOrEmpty(prefix) &&
            value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return value.Substring(prefix.Length);
        }

        return value;
    }

    private static string ReadFingerName(string value)
    {
        if (value.Contains("thumb"))
        {
            return "thumb";
        }

        if (value.Contains("index") || value.Contains("pointer"))
        {
            return "pointer";
        }

        if (value.Contains("middle"))
        {
            return "middle";
        }

        if (value.Contains("ring"))
        {
            return "ring";
        }

        if (value.Contains("pinky") || value.Contains("little"))
        {
            return "pinky";
        }

        return null;
    }

    private struct JointWorldPose
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    private sealed class RemoteStroke
    {
        private readonly GameObject gameObject;
        private readonly LineRenderer line;
        private readonly List<Vector3> points = new List<Vector3>(64);
        private readonly Color color;
        private readonly float width;

        public bool IsComplete { get; private set; }

        public RemoteStroke(GameObject gameObject, LineRenderer line, Color color, float width)
        {
            this.gameObject = gameObject;
            this.line = line;
            this.color = color;
            this.width = Mathf.Max(0.0001f, width);
            ConfigureLine();
        }

        public void Append(Vector3 worldPoint)
        {
            if (IsComplete)
            {
                return;
            }

            if (points.Count > 0 && Vector3.Distance(points[points.Count - 1], worldPoint) < 0.0001f)
            {
                return;
            }

            points.Add(worldPoint);
            UpdateLine();
        }

        public void Complete()
        {
            IsComplete = true;
            UpdateLine();
        }

        public void Destroy()
        {
            if (gameObject != null)
            {
                UnityEngine.Object.Destroy(gameObject);
            }
        }

        private void ConfigureLine()
        {
            if (line == null)
            {
                return;
            }

            line.startColor = color;
            line.endColor = color;
            line.startWidth = width;
            line.endWidth = width;
        }

        private void UpdateLine()
        {
            if (line == null || points.Count == 0)
            {
                return;
            }

            ConfigureLine();

            if (points.Count == 1)
            {
                line.positionCount = 2;
                line.SetPosition(0, points[0]);
                line.SetPosition(1, points[0]);
                return;
            }

            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                line.SetPosition(i, points[i]);
            }
        }
    }

    // Maps normalized Quest joint poses onto the existing MRTK hand rig transforms.
    private sealed class HandRigRetargeter
    {
        private static readonly string[] RetargetOrder =
        {
            "thumb_1", "thumb_2", "thumb_3",
            "pointer_1", "pointer_2", "pointer_3",
            "middle_1", "middle_2", "middle_3",
            "ring_1", "ring_2", "ring_3",
            "pinky_1", "pinky_2", "pinky_3"
        };

        private static readonly string[] PalmFingers =
        {
            "pointer", "middle", "ring", "pinky"
        };

        private readonly Dictionary<string, BoneBinding> bindings = new Dictionary<string, BoneBinding>(20);
        private Transform boundRoot;
        private Vector3 bindOriginInRootSpace;
        private Quaternion bindRootRotation = Quaternion.identity;
        private Quaternion bindHandFrameRotation = Quaternion.identity;
        private bool hasRootBinding;

        public void Rebuild(Transform root, Dictionary<string, Transform> bones)
        {
            bindings.Clear();
            boundRoot = root;
            hasRootBinding = false;

            if (root != null && bones != null)
            {
                Vector3 bindOrigin;
                Quaternion bindFrameRotation;
                if (TryBuildHandFrame(bones, out bindOrigin, out bindFrameRotation))
                {
                    bindOriginInRootSpace = root.InverseTransformPoint(bindOrigin);
                    bindRootRotation = root.rotation;
                    bindHandFrameRotation = bindFrameRotation;
                    hasRootBinding = true;
                }
            }

            if (bones == null)
            {
                return;
            }

            for (int i = 0; i < RetargetOrder.Length; i++)
            {
                string key = RetargetOrder[i];
                string childKey = GetChildKey(key);

                Transform bone;
                Transform child;
                if (string.IsNullOrEmpty(childKey) ||
                    !bones.TryGetValue(key, out bone) ||
                    !bones.TryGetValue(childKey, out child) ||
                    bone == null ||
                    child == null)
                {
                    continue;
                }

                Vector3 bindDirection = child.position - bone.position;
                if (bindDirection.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                bindings[key] = new BoneBinding
                {
                    bone = bone,
                    childKey = childKey,
                    bindLocalRotation = bone.localRotation,
                    bindDirectionInBoneSpace = bone.InverseTransformDirection(bindDirection).normalized
                };
            }
        }

        public bool ApplyRoot(Transform root, Dictionary<string, JointWorldPose> joints)
        {
            if (root == null ||
                joints == null ||
                joints.Count == 0 ||
                !hasRootBinding ||
                boundRoot != root)
            {
                return false;
            }

            Vector3 targetOrigin;
            Quaternion targetFrameRotation;
            if (!TryBuildHandFrame(joints, out targetOrigin, out targetFrameRotation))
            {
                return false;
            }

            Quaternion rootRotation = targetFrameRotation * Quaternion.Inverse(bindHandFrameRotation) * bindRootRotation;
            Vector3 rootPosition = targetOrigin - (rootRotation * bindOriginInRootSpace);
            root.SetPositionAndRotation(rootPosition, rootRotation);
            return true;
        }

        public void Apply(Dictionary<string, Transform> bones, Dictionary<string, JointWorldPose> joints)
        {
            if (joints == null || joints.Count == 0)
            {
                return;
            }

            if (bindings.Count == 0)
            {
                Rebuild(boundRoot, bones);
            }

            for (int i = 0; i < RetargetOrder.Length; i++)
            {
                BoneBinding binding;
                if (!bindings.TryGetValue(RetargetOrder[i], out binding) || binding.bone == null)
                {
                    continue;
                }

                JointWorldPose startPose;
                JointWorldPose endPose;
                if (!joints.TryGetValue(RetargetOrder[i], out startPose) ||
                    !joints.TryGetValue(binding.childKey, out endPose))
                {
                    continue;
                }

                Vector3 desiredDirection = endPose.position - startPose.position;
                if (desiredDirection.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                Quaternion parentRotation = binding.bone.parent != null ? binding.bone.parent.rotation : Quaternion.identity;
                Quaternion bindWorldRotation = parentRotation * binding.bindLocalRotation;
                Vector3 bindWorldDirection = bindWorldRotation * binding.bindDirectionInBoneSpace;
                if (bindWorldDirection.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                Quaternion directionCorrection = Quaternion.FromToRotation(bindWorldDirection, desiredDirection.normalized);
                binding.bone.rotation = directionCorrection * bindWorldRotation;
            }
        }

        private static string GetChildKey(string key)
        {
            if (key.EndsWith("_1", StringComparison.Ordinal))
            {
                return key.Substring(0, key.Length - 1) + "2";
            }

            if (key.EndsWith("_2", StringComparison.Ordinal))
            {
                return key.Substring(0, key.Length - 1) + "3";
            }

            if (key.EndsWith("_3", StringComparison.Ordinal))
            {
                return key.Substring(0, key.Length - 1) + "end";
            }

            return null;
        }

        private static bool TryBuildHandFrame(Dictionary<string, Transform> bones, out Vector3 origin, out Quaternion rotation)
        {
            origin = Vector3.zero;
            rotation = Quaternion.identity;

            if (bones == null || !TryGetPosition(bones, "wrist", out origin))
            {
                return false;
            }

            Vector3 fingerBaseCenter;
            Vector3 across;
            if (!TryGetAverageFingerBasePosition(bones, out fingerBaseCenter) ||
                !TryGetAcrossDirection(bones, out across))
            {
                return false;
            }

            return TryBuildHandFrame(origin, fingerBaseCenter, across, out rotation);
        }

        private static bool TryBuildHandFrame(Dictionary<string, JointWorldPose> joints, out Vector3 origin, out Quaternion rotation)
        {
            origin = Vector3.zero;
            rotation = Quaternion.identity;

            if (joints == null ||
                (!TryGetPosition(joints, "wrist", out origin) && !TryGetPosition(joints, "palm", out origin)))
            {
                return false;
            }

            Vector3 fingerBaseCenter;
            if (!TryGetAverageFingerBasePosition(joints, out fingerBaseCenter) &&
                !TryGetPosition(joints, "palm", out fingerBaseCenter))
            {
                return false;
            }

            Vector3 across;
            if (!TryGetAcrossDirection(joints, out across))
            {
                return false;
            }

            return TryBuildHandFrame(origin, fingerBaseCenter, across, out rotation);
        }

        private static bool TryBuildHandFrame(Vector3 origin, Vector3 fingerBaseCenter, Vector3 across, out Quaternion rotation)
        {
            rotation = Quaternion.identity;

            Vector3 forward = fingerBaseCenter - origin;
            if (forward.sqrMagnitude < 0.000001f || across.sqrMagnitude < 0.000001f)
            {
                return false;
            }

            forward.Normalize();
            across = across - Vector3.Project(across, forward);
            if (across.sqrMagnitude < 0.000001f)
            {
                return false;
            }

            across.Normalize();
            Vector3 up = Vector3.Cross(across, forward);
            if (up.sqrMagnitude < 0.000001f)
            {
                return false;
            }

            rotation = Quaternion.LookRotation(forward, up.normalized);
            return true;
        }

        private static bool TryGetAverageFingerBasePosition(Dictionary<string, Transform> bones, out Vector3 average)
        {
            average = Vector3.zero;
            int count = 0;

            for (int i = 0; i < PalmFingers.Length; i++)
            {
                Vector3 position;
                if (TryGetFingerBasePosition(bones, PalmFingers[i], out position))
                {
                    average += position;
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            average /= count;
            return true;
        }

        private static bool TryGetAverageFingerBasePosition(Dictionary<string, JointWorldPose> joints, out Vector3 average)
        {
            average = Vector3.zero;
            int count = 0;

            for (int i = 0; i < PalmFingers.Length; i++)
            {
                Vector3 position;
                if (TryGetFingerBasePosition(joints, PalmFingers[i], out position))
                {
                    average += position;
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            average /= count;
            return true;
        }

        private static bool TryGetAcrossDirection(Dictionary<string, Transform> bones, out Vector3 across)
        {
            Vector3 a;
            Vector3 b;
            if (TryGetFingerBasePosition(bones, "pointer", out a) && TryGetFingerBasePosition(bones, "pinky", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            if (TryGetFingerBasePosition(bones, "pointer", out a) && TryGetFingerBasePosition(bones, "ring", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            if (TryGetFingerBasePosition(bones, "middle", out a) && TryGetFingerBasePosition(bones, "pinky", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            across = Vector3.zero;
            return false;
        }

        private static bool TryGetAcrossDirection(Dictionary<string, JointWorldPose> joints, out Vector3 across)
        {
            Vector3 a;
            Vector3 b;
            if (TryGetFingerBasePosition(joints, "pointer", out a) && TryGetFingerBasePosition(joints, "pinky", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            if (TryGetFingerBasePosition(joints, "pointer", out a) && TryGetFingerBasePosition(joints, "ring", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            if (TryGetFingerBasePosition(joints, "middle", out a) && TryGetFingerBasePosition(joints, "pinky", out b))
            {
                across = a - b;
                return across.sqrMagnitude >= 0.000001f;
            }

            across = Vector3.zero;
            return false;
        }

        private static bool TryGetFingerBasePosition(Dictionary<string, Transform> bones, string finger, out Vector3 position)
        {
            return TryGetPosition(bones, finger + "_1", out position) ||
                   TryGetPosition(bones, finger + "_metacarpal", out position);
        }

        private static bool TryGetFingerBasePosition(Dictionary<string, JointWorldPose> joints, string finger, out Vector3 position)
        {
            return TryGetPosition(joints, finger + "_1", out position) ||
                   TryGetPosition(joints, finger + "_metacarpal", out position);
        }

        private static bool TryGetPosition(Dictionary<string, Transform> bones, string key, out Vector3 position)
        {
            position = Vector3.zero;

            Transform bone;
            if (bones == null || !bones.TryGetValue(key, out bone) || bone == null)
            {
                return false;
            }

            position = bone.position;
            return true;
        }

        private static bool TryGetPosition(Dictionary<string, JointWorldPose> joints, string key, out Vector3 position)
        {
            position = Vector3.zero;

            JointWorldPose pose;
            if (joints == null || !joints.TryGetValue(key, out pose))
            {
                return false;
            }

            position = pose.position;
            return true;
        }

        private struct BoneBinding
        {
            public Transform bone;
            public string childKey;
            public Quaternion bindLocalRotation;
            public Vector3 bindDirectionInBoneSpace;
        }
    }

    private sealed class HandJointVisual
    {
        private readonly string name;
        private readonly Dictionary<string, Transform> joints = new Dictionary<string, Transform>(32);
        private readonly List<LineRenderer> lines = new List<LineRenderer>(32);
        private GameObject root;

        public HandJointVisual(string name)
        {
            this.name = name;
        }

        public void Update(Transform parent, Dictionary<string, JointWorldPose> poses, Material material, Color color, float sphereDiameter, float lineWidth)
        {
            EnsureRoot(parent);

            if (poses == null || poses.Count == 0)
            {
                SetActive(false);
                return;
            }

            root.SetActive(true);
            HideJoints();
            UpdateJoints(poses, material, sphereDiameter);

            int lineIndex = 0;
            lineIndex = DrawPalm(poses, lineIndex, material, color, lineWidth);
            lineIndex = DrawFinger(poses, lineIndex, "thumb", material, color, lineWidth);
            lineIndex = DrawFinger(poses, lineIndex, "pointer", material, color, lineWidth);
            lineIndex = DrawFinger(poses, lineIndex, "middle", material, color, lineWidth);
            lineIndex = DrawFinger(poses, lineIndex, "ring", material, color, lineWidth);
            lineIndex = DrawFinger(poses, lineIndex, "pinky", material, color, lineWidth);
            HideLinesFrom(lineIndex);
        }

        public void SetActive(bool active)
        {
            if (root != null)
            {
                root.SetActive(active);
            }
        }

        public void Destroy()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }

            joints.Clear();
            lines.Clear();
        }

        private void EnsureRoot(Transform parent)
        {
            if (root != null)
            {
                return;
            }

            root = new GameObject(name);
            if (parent != null)
            {
                root.transform.SetParent(parent, false);
            }
        }

        private void HideJoints()
        {
            foreach (Transform joint in joints.Values)
            {
                if (joint != null)
                {
                    joint.gameObject.SetActive(false);
                }
            }
        }

        private void UpdateJoints(Dictionary<string, JointWorldPose> poses, Material material, float sphereDiameter)
        {
            foreach (KeyValuePair<string, JointWorldPose> pair in poses)
            {
                Transform joint = GetJoint(pair.Key, material);
                joint.position = pair.Value.position;
                joint.rotation = pair.Value.rotation;
                joint.localScale = Vector3.one * Mathf.Max(0.001f, sphereDiameter);
                joint.gameObject.SetActive(true);
            }
        }

        private Transform GetJoint(string key, Material material)
        {
            Transform joint;
            if (joints.TryGetValue(key, out joint) && joint != null)
            {
                ApplyMaterial(joint.gameObject, material);
                return joint;
            }

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = key;
            sphere.transform.SetParent(root.transform, false);

            Collider collider = sphere.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            ApplyMaterial(sphere, material);
            joints[key] = sphere.transform;
            return sphere.transform;
        }

        private int DrawPalm(Dictionary<string, JointWorldPose> poses, int lineIndex, Material material, Color color, float width)
        {
            lineIndex = DrawSegment(poses, lineIndex, "wrist", "palm", material, color, width);
            return lineIndex;
        }

        private int DrawFinger(Dictionary<string, JointWorldPose> poses, int lineIndex, string finger, Material material, Color color, float width)
        {
            string start = poses.ContainsKey("palm") ? "palm" : "wrist";
            string first = finger + "_1";

            if (finger != "thumb")
            {
                string metacarpal = finger + "_metacarpal";
                if (poses.ContainsKey(metacarpal))
                {
                    lineIndex = DrawSegment(poses, lineIndex, start, metacarpal, material, color, width);
                    lineIndex = DrawSegment(poses, lineIndex, metacarpal, first, material, color, width);
                }
                else
                {
                    lineIndex = DrawSegment(poses, lineIndex, start, first, material, color, width);
                }
            }
            else
            {
                string metacarpal = "thumb_metacarpal";
                if (poses.ContainsKey(metacarpal))
                {
                    lineIndex = DrawSegment(poses, lineIndex, start, metacarpal, material, color, width);
                    lineIndex = DrawSegment(poses, lineIndex, metacarpal, first, material, color, width);
                }
                else
                {
                    lineIndex = DrawSegment(poses, lineIndex, start, first, material, color, width);
                }
            }

            lineIndex = DrawSegment(poses, lineIndex, first, finger + "_2", material, color, width);
            lineIndex = DrawSegment(poses, lineIndex, finger + "_2", finger + "_3", material, color, width);
            lineIndex = DrawSegment(poses, lineIndex, finger + "_3", finger + "_end", material, color, width);
            return lineIndex;
        }

        private int DrawSegment(Dictionary<string, JointWorldPose> poses, int lineIndex, string start, string end, Material material, Color color, float width)
        {
            JointWorldPose startPose;
            JointWorldPose endPose;
            if (!poses.TryGetValue(start, out startPose) || !poses.TryGetValue(end, out endPose))
            {
                return lineIndex;
            }

            LineRenderer line = GetLine(lineIndex, material);
            ConfigureLine(line, material, color, width);
            line.SetPosition(0, startPose.position);
            line.SetPosition(1, endPose.position);
            line.gameObject.SetActive(true);
            return lineIndex + 1;
        }

        private LineRenderer GetLine(int index, Material material)
        {
            while (lines.Count <= index)
            {
                GameObject lineObject = new GameObject("JointLink");
                lineObject.transform.SetParent(root.transform, false);

                LineRenderer line = lineObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.numCapVertices = 4;
                line.numCornerVertices = 4;
                line.alignment = LineAlignment.View;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.sharedMaterial = material;
                lines.Add(line);
            }

            return lines[index];
        }

        private static void ConfigureLine(LineRenderer line, Material material, Color color, float width)
        {
            line.sharedMaterial = material;
            line.startColor = color;
            line.endColor = color;
            line.startWidth = Mathf.Max(0.001f, width);
            line.endWidth = Mathf.Max(0.001f, width);
        }

        private void HideLinesFrom(int firstHiddenIndex)
        {
            for (int i = firstHiddenIndex; i < lines.Count; i++)
            {
                if (lines[i] != null)
                {
                    lines[i].gameObject.SetActive(false);
                }
            }
        }

        private static void ApplyMaterial(GameObject gameObject, Material material)
        {
            Renderer renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null || material == null)
            {
                return;
            }

            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private void OnValidate()
    {
        webRtcLocalSignalingPort = Mathf.Clamp(webRtcLocalSignalingPort, 1, 65535);
        webRtcRemoteSignalingPort = Mathf.Clamp(webRtcRemoteSignalingPort, 1, 65535);
        webRtcStartupDelaySeconds = Mathf.Max(0f, webRtcStartupDelaySeconds);
        remoteHandAlpha = Mathf.Clamp(remoteHandAlpha, 0.02f, 1f);
        remoteHandBrightness = Mathf.Clamp(remoteHandBrightness, 0.05f, 1.5f);
        roomLightSampleIntervalSeconds = Mathf.Max(0.1f, roomLightSampleIntervalSeconds);
        darkRoomLux = Mathf.Max(0f, darkRoomLux);
        brightRoomLux = Mathf.Max(darkRoomLux + 1f, brightRoomLux);
        darkRoomHandBrightness = Mathf.Clamp(darkRoomHandBrightness, 0.05f, 1.5f);
        brightRoomHandBrightness = Mathf.Clamp(brightRoomHandBrightness, 0.05f, 1.5f);
        darkRoomHandAlpha = Mathf.Clamp(darkRoomHandAlpha, 0.02f, 1f);
        brightRoomHandAlpha = Mathf.Clamp(brightRoomHandAlpha, 0.02f, 1f);
        UpdateRemoteHandVisibilityForRoomLight(true);
    }

    [Serializable]
    private sealed class HandFramePacket
    {
        public string packetType;
        public string poseSpace;
        public HandFrame[] hands;
        public StrokeEvent[] strokeEvents;
    }

    [Serializable]
    private sealed class HandFrame
    {
        public string handedness;
        public bool isTracked;
        public PoseData wrist;
        public JointPose[] joints;
    }

    [Serializable]
    private struct JointPose
    {
        public string name;
        public PoseData pose;
    }

    [Serializable]
    private sealed class StrokeEvent
    {
        public string id;
        public string action;
        public PoseData point;
        public float width;
        public Color color;
        public float timestamp;
    }

    [Serializable]
    private struct PoseData
    {
        public bool isValid;
        public Vector3 position;
        public Quaternion rotation;
    }
}
