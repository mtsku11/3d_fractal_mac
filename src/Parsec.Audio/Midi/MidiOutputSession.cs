using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Parsec.Audio.Midi;

/// <summary>
/// Publishes a virtual CoreMIDI source named after <c>sourceName</c>. Once created
/// it appears in every other MIDI app's input list with no configuration, and
/// channel-voice messages sent here are delivered to whoever is listening.
///
/// macOS only. On any failure (non-macOS, no MIDIServer, sandbox) it degrades to
/// <see cref="IsAvailable"/> == false with a reason, never throwing into the caller.
/// </summary>
public sealed class MidiOutputSession : IDisposable
{
    private uint _client;
    private uint _source;
    private IntPtr _pktBuf;       // reused 1 KB buffer for packet-list construction
    private const int PktBufBytes = 1024;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>The virtual source's MIDIEndpointRef (0 when unavailable). Exposed for self-test wiring.</summary>
    internal uint SourceRef => _source;

    public MidiOutputSession(string sourceName = "Parsec")
    {
        if (!OperatingSystem.IsMacOS())
        {
            UnavailableReason = "MIDI output requires macOS (CoreMIDI).";
            return;
        }

        try
        {
            IntPtr clientName = CFStr("Parsec MIDI");
            int s1 = CoreMidiNative.MIDIClientCreate(clientName, IntPtr.Zero, IntPtr.Zero, out _client);
            CoreMidiNative.CFRelease(clientName);
            if (s1 != 0) { UnavailableReason = $"MIDIClientCreate failed (OSStatus {s1})."; return; }

            IntPtr srcName = CFStr(sourceName);
            int s2 = CoreMidiNative.MIDISourceCreate(_client, srcName, out _source);
            CoreMidiNative.CFRelease(srcName);
            if (s2 != 0) { UnavailableReason = $"MIDISourceCreate failed (OSStatus {s2})."; return; }

            _pktBuf = Marshal.AllocHGlobal(PktBufBytes);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"CoreMIDI unavailable: {ex.Message}";
            IsAvailable = false;
        }
    }

    /// <summary>Control Change (0xB0). channel 0–15, cc/value 0–127.</summary>
    public bool SendControlChange(int channel, int cc, int value)
    {
        Span<byte> m = stackalloc byte[3];
        m[0] = (byte)(0xB0 | (channel & 0x0F));
        m[1] = (byte)(cc & 0x7F);
        m[2] = (byte)(value & 0x7F);
        return Send(m);
    }

    /// <summary>Note On (0x90). A velocity of 0 is, per spec, a Note Off.</summary>
    public bool SendNoteOn(int channel, int note, int velocity)
    {
        Span<byte> m = stackalloc byte[3];
        m[0] = (byte)(0x90 | (channel & 0x0F));
        m[1] = (byte)(note & 0x7F);
        m[2] = (byte)(velocity & 0x7F);
        return Send(m);
    }

    /// <summary>Note Off (0x80).</summary>
    public bool SendNoteOff(int channel, int note)
    {
        Span<byte> m = stackalloc byte[3];
        m[0] = (byte)(0x80 | (channel & 0x0F));
        m[1] = (byte)(note & 0x7F);
        m[2] = 0;
        return Send(m);
    }

    private bool Send(ReadOnlySpan<byte> msg)
    {
        if (!IsAvailable || _disposed) return false;
        byte[] data = msg.ToArray();
        IntPtr cur = CoreMidiNative.MIDIPacketListInit(_pktBuf);
        cur = CoreMidiNative.MIDIPacketListAdd(_pktBuf, PktBufBytes, cur, 0UL, (ulong)data.Length, data);
        if (cur == IntPtr.Zero) return false;   // packet list overflow
        return CoreMidiNative.MIDIReceived(_source, _pktBuf) == 0;
    }

    private static IntPtr CFStr(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        return CoreMidiNative.CFStringCreateWithCString(IntPtr.Zero, bytes, CoreMidiNative.KCFStringEncodingUTF8);
    }

    // ------------------------------------------------------------------------
    // In-process loopback self-test: connect an input port to our own virtual
    // source, send a few CCs, and confirm they come back through the MIDIServer.
    // Delivery is asynchronous, so we wait briefly. Returns the packet count
    // received (expected >= sent), or a negative code on setup failure.
    // ------------------------------------------------------------------------

    private static int _loopbackPackets;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void LoopbackReadProc(IntPtr pktlist, IntPtr readRefCon, IntPtr srcConnRefCon)
    {
        // MIDIPacketList begins with UInt32 numPackets at offset 0 (safe to read
        // without decoding the variable-length packet bodies).
        int n = Marshal.ReadInt32(pktlist);
        Interlocked.Add(ref _loopbackPackets, n);
    }

    public unsafe int RunLoopbackSelfTest(int sendCount = 4, int waitMs = 1000)
    {
        if (!IsAvailable) return -1;
        Interlocked.Exchange(ref _loopbackPackets, 0);

        IntPtr portName = CFStr("Parsec Loopback In");
        IntPtr proc = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&LoopbackReadProc;
        int r = CoreMidiNative.MIDIInputPortCreate(_client, portName, proc, IntPtr.Zero, out uint port);
        CoreMidiNative.CFRelease(portName);
        if (r != 0) return -2;

        if (CoreMidiNative.MIDIPortConnectSource(port, _source, IntPtr.Zero) != 0)
        {
            CoreMidiNative.MIDIPortDispose(port);
            return -3;
        }

        // The source→port connection is established asynchronously by the MIDIServer;
        // sends fired before it propagates are dropped. Let it settle, then space the
        // sends so each is delivered rather than coalesced.
        Thread.Sleep(150);
        for (int i = 0; i < sendCount; i++)
        {
            SendControlChange(0, MidiOutputController.CcSize, i * 30);
            Thread.Sleep(40);
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs && Volatile.Read(ref _loopbackPackets) < sendCount)
            Thread.Sleep(20);

        int got = Volatile.Read(ref _loopbackPackets);
        CoreMidiNative.MIDIPortDispose(port);
        return got;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pktBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_pktBuf); _pktBuf = IntPtr.Zero; }
        if (OperatingSystem.IsMacOS())
        {
            if (_source != 0) CoreMidiNative.MIDIEndpointDispose(_source);
            if (_client != 0) CoreMidiNative.MIDIClientDispose(_client);
        }
        _source = 0; _client = 0;
        IsAvailable = false;
    }
}
