using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class HololensMicrophoneStreamServer : MonoBehaviour
{
    private const uint PacketMagic = 0x434D584D; // MXMC
    private const ushort ProtocolVersion = 1;

    [Header("Server")]
    public bool streamOnStart = true;
    public int listenPort = 5066;
    public int maxQueuedPackets = 120;
    public bool logStatus = true;

    [Header("Microphone")]
    public string microphoneDeviceName = "";
    public int sampleRate = 48000;
    public int ringBufferSeconds = 10;
    public float packetSeconds = 0.02f;
    [Tooltip("Gain applied to the custom microphone stream before sending.")]
    public float streamGain = 16f;
    [Range(0.1f, 1f)]
    public float streamLimiterCeiling = 0.95f;

    private readonly ConcurrentQueue<byte[]> packetQueue = new ConcurrentQueue<byte[]>();
    private readonly AutoResetEvent packetReady = new AutoResetEvent(false);

    private AudioClip microphoneClip;
    private string activeDeviceName;
    private int readPosition;
    private float[] sampleBuffer;
    private byte[] pcmBuffer;
    private Thread serverThread;
    private TcpListener listener;
    private volatile bool serverRunning;
    private volatile bool clientConnected;
    private volatile string pendingServerLog;
    private int queuedPacketCount;
    private uint sequence;
    private bool isCapturing;
    private bool startRequested;
    private float nextStatusTime;
    private long sentPacketCount;
    private long droppedPacketCount;
    private float lastInputRms;
    private float lastInputPeak;

    private void Start()
    {
        if (streamOnStart)
        {
            StartStreaming();
        }
    }

    private void Update()
    {
        LogPendingServerMessage();

        if (!isCapturing || microphoneClip == null)
        {
            return;
        }

        int currentPosition = Microphone.GetPosition(activeDeviceName);
        if (currentPosition < 0)
        {
            return;
        }

        int availableFrames = GetAvailableFrames(currentPosition);
        if (availableFrames > 0)
        {
            StreamAvailableFrames(availableFrames);
            readPosition = (readPosition + availableFrames) % microphoneClip.samples;
        }

        if (logStatus && Time.unscaledTime >= nextStatusTime)
        {
            nextStatusTime = Time.unscaledTime + 5f;
            Debug.Log("HololensMicrophoneStreamServer port=" + listenPort +
                      ", clientConnected=" + clientConnected +
                      ", sentPackets=" + sentPacketCount +
                      ", queuedPackets=" + queuedPacketCount +
                      ", droppedPackets=" + droppedPacketCount +
                      ", inputRms=" + lastInputRms.ToString("F4") +
                      ", inputPeak=" + lastInputPeak.ToString("F4"));
        }
    }

    private void OnDisable()
    {
        StopStreaming();
    }

    private void OnDestroy()
    {
        StopStreaming();
        packetReady.Dispose();
    }

    public void StartStreaming()
    {
        if (!serverRunning)
        {
            StartServerThread();
        }

        if (!startRequested && !isCapturing)
        {
            StartCoroutine(StartCaptureWhenAuthorized());
        }
    }

    public void StopStreaming()
    {
        StopCapture();
        StopServerThread();
        ClearPacketQueue();
    }

    private IEnumerator StartCaptureWhenAuthorized()
    {
        startRequested = true;

        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        }

        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            Debug.LogWarning("HololensMicrophoneStreamServer cannot stream because microphone permission was not granted.");
            startRequested = false;
            yield break;
        }

        string deviceName = string.IsNullOrWhiteSpace(microphoneDeviceName) ? null : microphoneDeviceName.Trim();
        int requestedSampleRate = Mathf.Clamp(sampleRate, 8000, 48000);
        int bufferSeconds = Mathf.Clamp(ringBufferSeconds, 1, 60);

        microphoneClip = Microphone.Start(deviceName, true, bufferSeconds, requestedSampleRate);
        activeDeviceName = deviceName;
        readPosition = 0;

        if (microphoneClip == null)
        {
            Debug.LogWarning("HololensMicrophoneStreamServer could not start microphone capture.");
            startRequested = false;
            yield break;
        }

        float startWaitTime = Time.unscaledTime + 2f;
        while (Microphone.GetPosition(activeDeviceName) <= 0 && Time.unscaledTime < startWaitTime)
        {
            yield return null;
        }

        isCapturing = true;
        startRequested = false;
        Debug.Log("HololensMicrophoneStreamServer streaming on TCP port " + listenPort +
                  ", sampleRate=" + microphoneClip.frequency +
                  ", channels=" + microphoneClip.channels);
    }

    private void StopCapture()
    {
        isCapturing = false;
        startRequested = false;

        if (microphoneClip != null)
        {
            if (Microphone.IsRecording(activeDeviceName))
            {
                Microphone.End(activeDeviceName);
            }

            microphoneClip = null;
        }
    }

    private int GetAvailableFrames(int currentPosition)
    {
        if (microphoneClip == null || microphoneClip.samples <= 0)
        {
            return 0;
        }

        if (currentPosition == readPosition)
        {
            return 0;
        }

        if (currentPosition > readPosition)
        {
            return currentPosition - readPosition;
        }

        return microphoneClip.samples - readPosition + currentPosition;
    }

    private void StreamAvailableFrames(int availableFrames)
    {
        if (microphoneClip == null || availableFrames <= 0)
        {
            return;
        }

        int clipFrameCount = microphoneClip.samples;
        int cursor = readPosition;
        int remainingFrames = availableFrames;
        int maxPacketFrames = Mathf.Max(1, Mathf.RoundToInt(microphoneClip.frequency * Mathf.Clamp(packetSeconds, 0.005f, 0.25f)));

        while (remainingFrames > 0)
        {
            int contiguousFrames = Mathf.Min(remainingFrames, clipFrameCount - cursor, maxPacketFrames);
            StreamFrames(cursor, contiguousFrames);
            cursor = (cursor + contiguousFrames) % clipFrameCount;
            remainingFrames -= contiguousFrames;
        }
    }

    private void StreamFrames(int startFrame, int frameCount)
    {
        if (!clientConnected || microphoneClip == null || frameCount <= 0)
        {
            return;
        }

        int channels = Mathf.Max(1, microphoneClip.channels);
        int sampleValueCount = frameCount * channels;
        if (sampleBuffer == null || sampleBuffer.Length != sampleValueCount)
        {
            sampleBuffer = new float[sampleValueCount];
        }

        if (pcmBuffer == null || pcmBuffer.Length != sampleValueCount * 2)
        {
            pcmBuffer = new byte[sampleValueCount * 2];
        }

        microphoneClip.GetData(sampleBuffer, startFrame);
        MeasureSamples(sampleBuffer, sampleValueCount, out lastInputRms, out lastInputPeak);
        int payloadByteCount = EncodePcm16(sampleBuffer, sampleValueCount, pcmBuffer, streamGain, streamLimiterCeiling);
        EnqueuePacket(BuildPacket(microphoneClip.frequency, channels, frameCount, pcmBuffer, payloadByteCount));
        sentPacketCount++;
    }

    private byte[] BuildPacket(int packetSampleRate, int channels, int frameCount, byte[] payload, int payloadByteCount)
    {
        using (MemoryStream stream = new MemoryStream(32 + payloadByteCount))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(PacketMagic);
            writer.Write(ProtocolVersion);
            writer.Write((ushort)Mathf.Clamp(channels, 1, 8));
            writer.Write(packetSampleRate);
            writer.Write(sequence++);
            writer.Write(frameCount);
            writer.Write(payloadByteCount);
            writer.Write((double)Time.realtimeSinceStartup);
            writer.Write(payload, 0, payloadByteCount);
            return stream.ToArray();
        }
    }

    private void EnqueuePacket(byte[] packet)
    {
        if (packet == null || packet.Length == 0)
        {
            return;
        }

        packetQueue.Enqueue(packet);
        Interlocked.Increment(ref queuedPacketCount);

        int queueLimit = Mathf.Max(1, maxQueuedPackets);
        while (Volatile.Read(ref queuedPacketCount) > queueLimit && packetQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref queuedPacketCount);
            Interlocked.Increment(ref droppedPacketCount);
        }

        packetReady.Set();
    }

    private void ClearPacketQueue()
    {
        while (packetQueue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref queuedPacketCount, 0);
    }

    private void StartServerThread()
    {
        serverRunning = true;
        serverThread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "HololensMicrophoneStreamServer"
        };
        serverThread.Start();
    }

    private void StopServerThread()
    {
        serverRunning = false;
        clientConnected = false;
        packetReady.Set();

        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (serverThread != null && serverThread.IsAlive)
        {
            serverThread.Join(500);
        }

        serverThread = null;
        listener = null;
    }

    private void ServerLoop()
    {
        try
        {
            listener = new TcpListener(IPAddress.Any, listenPort);
            listener.Start(1);
            pendingServerLog = "HololensMicrophoneStreamServer listening on TCP port " + listenPort;

            while (serverRunning)
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        client.NoDelay = true;
                        client.SendBufferSize = 64 * 1024;
                        clientConnected = true;
                        ClearPacketQueue();
                        pendingServerLog = "HololensMicrophoneStreamServer client connected on port " + listenPort;

                        using (NetworkStream stream = client.GetStream())
                        {
                            while (serverRunning && client.Connected)
                            {
                                if (packetQueue.TryDequeue(out byte[] packet))
                                {
                                    Interlocked.Decrement(ref queuedPacketCount);
                                    stream.Write(packet, 0, packet.Length);
                                    continue;
                                }

                                packetReady.WaitOne(100);
                            }
                        }
                    }
                }
                catch (IOException e)
                {
                    if (serverRunning)
                    {
                        pendingServerLog = "HololensMicrophoneStreamServer client disconnected: " + e.Message;
                    }
                }
                catch (SocketException e)
                {
                    if (serverRunning)
                    {
                        pendingServerLog = "HololensMicrophoneStreamServer socket stopped: " + e.Message;
                    }
                }
                finally
                {
                    clientConnected = false;
                    ClearPacketQueue();
                }
            }
        }
        catch (SocketException e)
        {
            if (serverRunning)
            {
                pendingServerLog = "HololensMicrophoneStreamServer socket stopped: " + e.Message;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            if (serverRunning)
            {
                pendingServerLog = "HololensMicrophoneStreamServer stopped after an error: " + e.Message;
            }
        }
        finally
        {
            clientConnected = false;
            ClearPacketQueue();
        }
    }

    private void LogPendingServerMessage()
    {
        string message = pendingServerLog;
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        pendingServerLog = null;
        Debug.Log(message);
    }

    private static int EncodePcm16(float[] samples, int sampleValueCount, byte[] target, float gain, float limiterCeiling)
    {
        gain = Mathf.Max(0f, gain);
        for (int i = 0; i < sampleValueCount; i++)
        {
            float sample = ApplySoftLimiter(samples[i] * gain, limiterCeiling);
            short pcm = sample < 0f
                ? (short)Mathf.RoundToInt(sample * 32768f)
                : (short)Mathf.RoundToInt(sample * 32767f);
            int byteIndex = i * 2;
            target[byteIndex] = (byte)(pcm & 0xff);
            target[byteIndex + 1] = (byte)((pcm >> 8) & 0xff);
        }

        return sampleValueCount * 2;
    }

    private static float ApplySoftLimiter(float value, float ceiling)
    {
        ceiling = Mathf.Clamp(ceiling, 0.1f, 1f);
        float abs = Mathf.Abs(value);
        if (abs <= ceiling)
        {
            return value;
        }

        float sign = value < 0f ? -1f : 1f;
        float excess = abs - ceiling;
        float limited = ceiling + (1f - ceiling) * (excess / (excess + 1f));
        return sign * Mathf.Min(limited, 1f);
    }

    private static void MeasureSamples(float[] samples, int sampleValueCount, out float rms, out float peak)
    {
        double sumSquares = 0.0;
        peak = 0f;

        for (int i = 0; i < sampleValueCount; i++)
        {
            float sample = samples[i];
            sumSquares += sample * sample;
            float abs = Mathf.Abs(sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        rms = sampleValueCount > 0 ? Mathf.Sqrt((float)(sumSquares / sampleValueCount)) : 0f;
    }

    private void OnValidate()
    {
        listenPort = Mathf.Clamp(listenPort, 1, 65535);
        maxQueuedPackets = Mathf.Clamp(maxQueuedPackets, 1, 1000);
        sampleRate = Mathf.Clamp(sampleRate, 8000, 48000);
        ringBufferSeconds = Mathf.Clamp(ringBufferSeconds, 1, 60);
        packetSeconds = Mathf.Clamp(packetSeconds, 0.005f, 0.25f);
        streamGain = Mathf.Max(0f, streamGain);
        streamLimiterCeiling = Mathf.Clamp(streamLimiterCeiling, 0.1f, 1f);
    }
}
