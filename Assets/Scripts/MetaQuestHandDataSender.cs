using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Sends Meta Quest hand skeleton poses to the HoloLens receiver over UDP.
/// </summary>
[DisallowMultipleComponent]
public sealed class MetaQuestHandDataSender : MonoBehaviour
{
    [Header("Receiver")]
    public string receiverIp = "127.0.0.1";
    public int receiverPort = 5055;
    public float sendRateHz = 60f;

    [Header("Meta hand skeletons")]
    public MonoBehaviour leftSkeleton;
    public MonoBehaviour rightSkeleton;
    public bool autoFindSkeletons = true;

    private readonly HandPacket packet = new HandPacket();
    private readonly List<JointPose> jointBuffer = new List<JointPose>(32);
    private UdpClient udpClient;
    private IPEndPoint receiverEndPoint;
    private float nextSendTime;

    private void Awake()
    {
        udpClient = new UdpClient();
        receiverEndPoint = new IPEndPoint(IPAddress.Parse(receiverIp), receiverPort);
        packet.hands = new HandFrame[2];
        packet.hands[0] = new HandFrame { handedness = "Left" };
        packet.hands[1] = new HandFrame { handedness = "Right" };
    }

    private void OnDestroy()
    {
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
    }

    private void Update()
    {
        if (sendRateHz > 0f && Time.unscaledTime < nextSendTime)
        {
            return;
        }

        nextSendTime = Time.unscaledTime + 1f / Mathf.Max(1f, sendRateHz);

        if (autoFindSkeletons)
        {
            leftSkeleton = leftSkeleton != null ? leftSkeleton : FindSkeleton(true);
            rightSkeleton = rightSkeleton != null ? rightSkeleton : FindSkeleton(false);
        }

        FillHand(leftSkeleton, packet.hands[0], true);
        FillHand(rightSkeleton, packet.hands[1], false);

        string json = JsonUtility.ToJson(packet);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        udpClient.Send(bytes, bytes.Length, receiverEndPoint);
    }

    private static MonoBehaviour FindSkeleton(bool left)
    {
        // Reflection keeps this helper independent from a hard compile-time Oculus/Meta SDK reference.
        MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour == null || behaviour.GetType().Name != "OVRSkeleton")
            {
                continue;
            }

            string objectName = behaviour.name.ToLowerInvariant();
            string typeText = ReadPropertyAsString(behaviour, "SkeletonType").ToLowerInvariant();
            if (left && (objectName.Contains("left") || typeText.Contains("left")))
            {
                return behaviour;
            }

            if (!left && (objectName.Contains("right") || typeText.Contains("right")))
            {
                return behaviour;
            }
        }

        return null;
    }

    private void FillHand(MonoBehaviour skeleton, HandFrame hand, bool left)
    {
        // Pack the wrist and all available skeleton bones into a Unity-serializable packet.
        hand.isTracked = false;
        hand.wrist = PoseData.Invalid;
        hand.joints = null;

        if (skeleton == null)
        {
            return;
        }

        object bonesObject = ReadProperty(skeleton, "Bones");
        IEnumerable bones = bonesObject as IEnumerable;
        if (bones == null)
        {
            return;
        }

        bool tracked = ReadTrackingState(skeleton);
        jointBuffer.Clear();

        foreach (object bone in bones)
        {
            if (bone == null)
            {
                continue;
            }

            string id = ReadPropertyAsString(bone, "Id");
            Transform boneTransform = ReadProperty(bone, "Transform") as Transform;
            if (boneTransform == null)
            {
                continue;
            }

            PoseData pose = new PoseData
            {
                isValid = true,
                position = boneTransform.position,
                rotation = boneTransform.rotation
            };

            if (id.IndexOf("Wrist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hand.wrist = pose;
            }

            jointBuffer.Add(new JointPose
            {
                name = id,
                pose = pose
            });
        }

        hand.isTracked = tracked || jointBuffer.Count > 0;
        hand.joints = jointBuffer.ToArray();
    }

    private static bool ReadTrackingState(MonoBehaviour skeleton)
    {
        Component[] components = skeleton.GetComponentsInParent<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i] == null || components[i].GetType().Name != "OVRHand")
            {
                continue;
            }

            object value = ReadProperty(components[i], "IsTracked");
            if (value is bool)
            {
                return (bool)value;
            }
        }

        components = skeleton.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i] == null || components[i].GetType().Name != "OVRHand")
            {
                continue;
            }

            object value = ReadProperty(components[i], "IsTracked");
            if (value is bool)
            {
                return (bool)value;
            }
        }

        return false;
    }

    private static object ReadProperty(object target, string propertyName)
    {
        if (target == null)
        {
            return null;
        }

        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
        {
            return property.GetValue(target, null);
        }

        FieldInfo field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return field != null ? field.GetValue(target) : null;
    }

    private static string ReadPropertyAsString(object target, string propertyName)
    {
        object value = ReadProperty(target, propertyName);
        return value != null ? value.ToString() : string.Empty;
    }

    [Serializable]
    private sealed class HandPacket
    {
        public HandFrame[] hands;
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
    private struct PoseData
    {
        public bool isValid;
        public Vector3 position;
        public Quaternion rotation;

        public static PoseData Invalid
        {
            get
            {
                return new PoseData
                {
                    isValid = false,
                    position = Vector3.zero,
                    rotation = Quaternion.identity
                };
            }
        }
    }
}
