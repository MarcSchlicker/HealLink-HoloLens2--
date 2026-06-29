using System;
using System.Collections;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class HololensMicrophoneWavRecorder : MonoBehaviour
{
    [Header("Recording")]
    public bool recordOnStart = false;
    public string microphoneDeviceName = "";
    public int sampleRate = 48000;
    public int ringBufferSeconds = 10;
    public float segmentSeconds = 30f;
    [Tooltip("Gain applied only to the debug WAV file. WebRTC audio playback is not changed by this recorder.")]
    public float wavGain = 16f;
    [Range(0.1f, 1f)]
    public float wavLimiterCeiling = 0.95f;
    public bool logStatus = true;

    [Header("Output")]
    public string folderName = "HololensMicrophoneCaptures";
    public string filePrefix = "hololens_microphone";
    public bool timestampFiles = true;

    private AudioClip microphoneClip;
    private string activeDeviceName;
    private int readPosition;
    private float[] sampleBuffer;
    private byte[] pcmBuffer;
    private Pcm16WavWriter wavWriter;
    private int writerSampleRate;
    private int writerChannels;
    private float segmentStartTime;
    private float nextStatusTime;
    private long segmentFrameCount;
    private bool isRecording;
    private bool startRequested;

    private void Start()
    {
        if (recordOnStart)
        {
            StartCoroutine(StartRecordingWhenAuthorized());
        }
    }

    private void Update()
    {
        if (!isRecording || microphoneClip == null)
        {
            return;
        }

        int currentPosition = Microphone.GetPosition(activeDeviceName);
        if (currentPosition < 0)
        {
            return;
        }

        int availableFrames = GetAvailableFrames(currentPosition);
        if (availableFrames <= 0)
        {
            return;
        }

        WriteAvailableFrames(availableFrames);
        readPosition = (readPosition + availableFrames) % microphoneClip.samples;

        if (segmentSeconds > 0f && Time.unscaledTime - segmentStartTime >= segmentSeconds)
        {
            RotateSegment();
        }

        if (logStatus && Time.unscaledTime >= nextStatusTime)
        {
            nextStatusTime = Time.unscaledTime + 5f;
            Debug.Log("HololensMicrophoneWavRecorder writing " + segmentFrameCount +
                      " frames to " + GetCurrentPathForLog());
        }
    }

    private void OnDisable()
    {
        StopRecording();
    }

    private void OnDestroy()
    {
        StopRecording();
    }

    public void StartRecording()
    {
        if (!startRequested)
        {
            StartCoroutine(StartRecordingWhenAuthorized());
        }
    }

    public void StopRecording()
    {
        isRecording = false;
        startRequested = false;

        if (microphoneClip != null)
        {
            if (Microphone.IsRecording(activeDeviceName))
            {
                Microphone.End(activeDeviceName);
            }

            microphoneClip = null;
        }

        CloseWriter();
    }

    private IEnumerator StartRecordingWhenAuthorized()
    {
        if (isRecording || startRequested)
        {
            yield break;
        }

        startRequested = true;

        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        }

        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            Debug.LogWarning("HololensMicrophoneWavRecorder cannot record because microphone permission was not granted.");
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
            Debug.LogWarning("HololensMicrophoneWavRecorder could not start microphone capture.");
            startRequested = false;
            yield break;
        }

        float startWaitTime = Time.unscaledTime + 2f;
        while (Microphone.GetPosition(activeDeviceName) <= 0 && Time.unscaledTime < startWaitTime)
        {
            yield return null;
        }

        EnsureWriter();
        isRecording = true;
        startRequested = false;
        Debug.Log("HololensMicrophoneWavRecorder started. WAV files are written below: " + GetOutputDirectory());
    }

    private int GetAvailableFrames(int currentPosition)
    {
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

    private void WriteAvailableFrames(int frameCount)
    {
        int clipFrameCount = microphoneClip.samples;
        if (readPosition + frameCount <= clipFrameCount)
        {
            WriteFrames(readPosition, frameCount);
            return;
        }

        int firstFrameCount = clipFrameCount - readPosition;
        WriteFrames(readPosition, firstFrameCount);
        WriteFrames(0, frameCount - firstFrameCount);
    }

    private void WriteFrames(int startFrame, int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        EnsureWriter();

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
        int byteCount = EncodePcm16(sampleBuffer, sampleValueCount, pcmBuffer, wavGain, wavLimiterCeiling);
        wavWriter.WritePcm16(pcmBuffer, 0, byteCount);
        segmentFrameCount += frameCount;
    }

    private void EnsureWriter()
    {
        if (microphoneClip == null)
        {
            return;
        }

        int currentSampleRate = Mathf.Max(1, microphoneClip.frequency);
        int currentChannels = Mathf.Max(1, microphoneClip.channels);
        if (wavWriter != null && writerSampleRate == currentSampleRate && writerChannels == currentChannels)
        {
            return;
        }

        CloseWriter();

        string path = BuildWavPath();
        wavWriter = new Pcm16WavWriter(path, currentSampleRate, currentChannels);
        writerSampleRate = currentSampleRate;
        writerChannels = currentChannels;
        segmentStartTime = Time.unscaledTime;
        segmentFrameCount = 0;
        Debug.Log("HololensMicrophoneWavRecorder opened WAV: " + path);
    }

    private void RotateSegment()
    {
        CloseWriter();
        EnsureWriter();
    }

    private void CloseWriter()
    {
        if (wavWriter == null)
        {
            return;
        }

        wavWriter.Dispose();
        wavWriter = null;
        writerSampleRate = 0;
        writerChannels = 0;
        segmentFrameCount = 0;
    }

    private string GetOutputDirectory()
    {
        string root = Application.persistentDataPath;
        string safeFolder = string.IsNullOrWhiteSpace(folderName) ? "HololensMicrophoneCaptures" : folderName.Trim();
        string directory = Path.Combine(root, safeFolder);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string BuildWavPath()
    {
        string directory = GetOutputDirectory();
        string safePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "hololens_microphone" : filePrefix.Trim();
        string suffix = timestampFiles ? "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : "";
        return Path.Combine(directory, safePrefix + suffix + ".wav");
    }

    private string GetCurrentPathForLog()
    {
        return wavWriter != null ? wavWriter.FilePath : GetOutputDirectory();
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

    private sealed class Pcm16WavWriter : IDisposable
    {
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private long dataByteCount;
        private bool disposed;

        public string FilePath { get; private set; }

        public Pcm16WavWriter(string filePath, int sampleRate, int channels)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            FilePath = filePath;
            stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new BinaryWriter(stream);
            WriteHeader(sampleRate, channels);
        }

        public void WritePcm16(byte[] pcmBytes, int offset, int count)
        {
            if (disposed || pcmBytes == null || count <= 0)
            {
                return;
            }

            writer.Write(pcmBytes, offset, count);
            dataByteCount += count;
            writer.Flush();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            UpdateHeader();
            writer.Dispose();
            stream.Dispose();
        }

        private void WriteHeader(int sampleRate, int channels)
        {
            short bitsPerSample = 16;
            short channelCount = (short)Mathf.Clamp(channels, 1, 2);
            int byteRate = sampleRate * channelCount * bitsPerSample / 8;
            short blockAlign = (short)(channelCount * bitsPerSample / 8);

            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channelCount);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(0);
        }

        private void UpdateHeader()
        {
            stream.Seek(4, SeekOrigin.Begin);
            writer.Write((int)(36 + dataByteCount));
            stream.Seek(40, SeekOrigin.Begin);
            writer.Write((int)dataByteCount);
            writer.Flush();
        }
    }
}
