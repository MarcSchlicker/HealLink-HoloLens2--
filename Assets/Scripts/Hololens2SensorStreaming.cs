
using UnityEngine;

public class Hololens2SensorStreaming : MonoBehaviour
{
    [Tooltip("Enable Research Mode streams.")]
    public bool enableRM = true;

    [Tooltip("Enable Front Camera stream.")]
    public bool enablePV = true;

    [Tooltip("Enable Microphone stream.")]
    public bool enableMC = true;

    [Tooltip("Enable Spatial Input stream.")]
    public bool enableSI = true;

    [Tooltip("Enable Remote Configuration interface.")]
    public bool enableRC = true;

    [Tooltip("Enable Spatial Mapping interface.")]
    public bool enableSM = true;

    [Tooltip("Enable Scene Understanding interface.")]
    public bool enableSU = true;

    [Tooltip("Enable Voice Input interface.")]
    public bool enableVI = true;

    [Tooltip("Enable Message Queue interface.")]
    public bool enableMQ = true;

    [Tooltip("Enable Extended Eye Tracking Interface.")]
    public bool enableEET = true;

    [Tooltip("Enable Extended Audio Interface.")]
    public bool enableEA = true;

    [Tooltip("Enable Extended Video Interface.")]
    public bool enableEV = true;

    [Tooltip("Enable Guest Message Queue interface.")]
    public bool enableMQX = true;

    void Start()
    {
        Debug.Log("Hololens2SensorStreaming starting. Microphone=" + enableMC +
                  ", ExtendedAudio=" + enableEA +
                  ", PV=" + enablePV +
                  ", RM=" + enableRM +
                  ", RemoteConfiguration=" + enableRC);
        hl2ss.RegisterNamedMutex(hl2ss.Device.PERSONAL_VIDEO, hl2ss.MUTEX_NAME_PV);
        hl2ss.RegisterNamedMutex(hl2ss.Device.EXTENDED_VIDEO, hl2ss.MUTEX_NAME_EV);
        hl2ss.UpdateCoordinateSystem();
        hl2ss.Initialize(enableRM, enablePV, enableMC, enableSI, enableRC, enableSM, enableSU, enableVI, enableMQ, enableEET, enableEA, enableEV, enableMQX);
        Debug.Log("Hololens2SensorStreaming initialized HL2SS. If microphone packets contain only zeros, check that this deployed UWP app is the active HL2SS server and that no Remoting/system app is exclusively using the microphone.");
    }

    void Update()
    {
        hl2ss.CheckForErrors();        
    }
}
