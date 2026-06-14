using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Parsec.Audio.Midi;

/// <summary>
/// An independent CoreMIDI receiver, in a separate client from <see cref="MidiOutputSession"/>.
/// It connects an input port to every published source and decodes the channel-voice messages
/// it receives, so a test can prove that MIDI emitted by one process (e.g. the app, or the
/// <c>midi-smoke</c> CLI) is actually delivered through the macOS MIDIServer to an external
/// listener — the same path Ableton or a Web MIDI page uses.
///
/// Decoding does not trust a single packet-list layout: it tries both the 8-byte-aligned and
/// the packed MIDIPacket layouts and keeps whichever yields a valid status byte, so we get the
/// real CC/note values without relying on the famously fragile struct alignment.
/// </summary>
public static class MidiMonitorProbe
{
    private static int _msgCount, _ccCount, _noteOnCount, _noteOffCount, _undecoded;
    private static readonly int[] _lastCc = new int[128];
    private static readonly int[] _noteOnCounts = new int[128];

    public sealed record Result(
        int Messages, int CcMessages, int NoteOnMessages, int NoteOffMessages, int Undecoded,
        int SourcesConnected, int[] LastCc, int[] NoteOnCounts, string? Error = null);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReadProc(IntPtr pktlist, IntPtr readRefCon, IntPtr srcConnRefCon)
    {
        // CoreMIDI coalesces several rapid messages into one packet, so the packet's data
        // buffer can hold many 3-byte messages (length = 3·k). Find the layout, then parse
        // the whole buffer. (For our sender numPackets is 1; multi-packet lists would need
        // MIDIPacketNext, omitted here.)
        if (!TryDecodePacket(pktlist, lenOff: 16, dataOff: 18) &&
            !TryDecodePacket(pktlist, lenOff: 12, dataOff: 14))
            Interlocked.Increment(ref _undecoded);
    }

    private static bool TryDecodePacket(IntPtr pktlist, int lenOff, int dataOff)
    {
        ushort len = (ushort)Marshal.ReadInt16(pktlist, lenOff);
        if (len < 3 || len > 1024) return false;
        if ((Marshal.ReadByte(pktlist, dataOff) & 0x80) == 0) return false; // not a status byte → wrong layout

        bool any = false;
        for (int p = 0; p + 2 < len; p += 3)
        {
            byte status = Marshal.ReadByte(pktlist, dataOff + p);
            byte d1 = Marshal.ReadByte(pktlist, dataOff + p + 1);
            byte d2 = Marshal.ReadByte(pktlist, dataOff + p + 2);
            if ((status & 0x80) == 0 || d1 > 127 || d2 > 127) break; // stream desync — stop
            switch (status & 0xF0)
            {
                case 0xB0: _lastCc[d1] = d2; Interlocked.Increment(ref _ccCount); Interlocked.Increment(ref _msgCount); any = true; break;
                case 0x90:
                    if (d2 == 0) Interlocked.Increment(ref _noteOffCount);
                    else { _noteOnCounts[d1]++; Interlocked.Increment(ref _noteOnCount); }
                    Interlocked.Increment(ref _msgCount); any = true; break;
                case 0x80: Interlocked.Increment(ref _noteOffCount); Interlocked.Increment(ref _msgCount); any = true; break;
                default: return any; // unknown type — assume desync, keep what we have
            }
        }
        return any;
    }

    /// <summary>Listens for the given duration, connecting to sources as they appear, and
    /// returns a snapshot of what was received. macOS only.</summary>
    public static unsafe Result Run(int seconds)
    {
        if (!OperatingSystem.IsMacOS())
            return new Result(0, 0, 0, 0, 0, 0, _lastCc, _noteOnCounts, "requires macOS");

        // Reset shared state.
        _msgCount = _ccCount = _noteOnCount = _noteOffCount = _undecoded = 0;
        Array.Fill(_lastCc, -1); Array.Clear(_noteOnCounts);

        IntPtr clientName = CFStr("Parsec Monitor");
        int rc = CoreMidiNative.MIDIClientCreate(clientName, IntPtr.Zero, IntPtr.Zero, out uint client);
        CoreMidiNative.CFRelease(clientName);
        if (rc != 0) return new Result(0, 0, 0, 0, 0, 0, _lastCc, _noteOnCounts, $"MIDIClientCreate {rc}");

        IntPtr portName = CFStr("Parsec Monitor In");
        IntPtr proc = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ReadProc;
        rc = CoreMidiNative.MIDIInputPortCreate(client, portName, proc, IntPtr.Zero, out uint port);
        CoreMidiNative.CFRelease(portName);
        if (rc != 0) { CoreMidiNative.MIDIClientDispose(client); return new Result(0, 0, 0, 0, 0, 0, _lastCc, _noteOnCounts, $"MIDIInputPortCreate {rc}"); }

        // CoreMIDI compares run-loop modes by string value, so a CFString holding the literal
        // "kCFRunLoopDefaultMode" is equivalent to the exported constant.
        IntPtr defaultMode = CFStr("kCFRunLoopDefaultMode");

        var connected = new HashSet<uint>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            // Sources can appear after we start (the sender may launch later) — re-scan and
            // connect any we haven't seen yet.
            nuint n = CoreMidiNative.MIDIGetNumberOfSources();
            for (nuint i = 0; i < n; i++)
            {
                uint src = CoreMidiNative.MIDIGetSource(i);
                if (src != 0 && connected.Add(src))
                    CoreMidiNative.MIDIPortConnectSource(port, src, IntPtr.Zero);
            }
            // Pump the run loop (instead of sleeping) so the MIDIServer connection completes
            // and new-source notifications are processed.
            CoreMidiNative.CFRunLoopRunInMode(defaultMode, 0.2, 0);
        }

        CoreMidiNative.CFRelease(defaultMode);
        CoreMidiNative.MIDIPortDispose(port);
        CoreMidiNative.MIDIClientDispose(client);

        return new Result(
            Volatile.Read(ref _msgCount), Volatile.Read(ref _ccCount),
            Volatile.Read(ref _noteOnCount), Volatile.Read(ref _noteOffCount),
            Volatile.Read(ref _undecoded), connected.Count, _lastCc, _noteOnCounts);
    }

    private static IntPtr CFStr(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        return CoreMidiNative.CFStringCreateWithCString(IntPtr.Zero, bytes, CoreMidiNative.KCFStringEncodingUTF8);
    }
}
