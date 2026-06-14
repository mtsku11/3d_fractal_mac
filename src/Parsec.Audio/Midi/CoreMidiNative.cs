using System.Runtime.InteropServices;

namespace Parsec.Audio.Midi;

/// <summary>
/// Minimal P/Invoke surface for CoreMIDI + CoreFoundation, enough to publish a
/// virtual MIDI source and push channel-voice messages out of it. MIDIObjectRef
/// (client/source/port) is a UInt32 typedef, not a pointer. Packet-list layout is
/// handled by Apple's MIDIPacketListInit/Add helpers rather than hand-marshalled,
/// because the struct's alignment is famously error-prone.
/// </summary>
internal static class CoreMidiNative
{
    private const string CoreMidi       = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal const uint KCFStringEncodingUTF8 = 0x08000100;

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte[] cStr, uint encoding);

    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr cf);

    // MIDIClientCreate(CFStringRef name, MIDINotifyProc, void* refCon, MIDIClientRef* outClient)
    [DllImport(CoreMidi)]
    internal static extern int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out uint outClient);

    // MIDISourceCreate(MIDIClientRef, CFStringRef name, MIDIEndpointRef* outSrc)
    [DllImport(CoreMidi)]
    internal static extern int MIDISourceCreate(uint client, IntPtr name, out uint outSrc);

    // MIDIReceived(MIDIEndpointRef src, const MIDIPacketList* pktlist)
    [DllImport(CoreMidi)]
    internal static extern int MIDIReceived(uint src, IntPtr pktlist);

    // MIDIPacket* MIDIPacketListInit(MIDIPacketList* pktlist)
    [DllImport(CoreMidi)]
    internal static extern IntPtr MIDIPacketListInit(IntPtr pktlist);

    // MIDIPacket* MIDIPacketListAdd(MIDIPacketList*, ByteCount listSize, MIDIPacket* cur,
    //                               MIDITimeStamp time, ByteCount nData, const Byte* data)
    [DllImport(CoreMidi)]
    internal static extern IntPtr MIDIPacketListAdd(IntPtr pktlist, ulong listSize, IntPtr curPacket,
                                                    ulong time, ulong nData, byte[] data);

    [DllImport(CoreMidi)]
    internal static extern int MIDIEndpointDispose(uint endpoint);

    [DllImport(CoreMidi)]
    internal static extern int MIDIClientDispose(uint client);

    // --- Self-test / loopback only ---

    // MIDIInputPortCreate(MIDIClientRef, CFStringRef, MIDIReadProc, void* refCon, MIDIPortRef* outPort)
    [DllImport(CoreMidi)]
    internal static extern int MIDIInputPortCreate(uint client, IntPtr portName, IntPtr readProc, IntPtr refCon, out uint outPort);

    [DllImport(CoreMidi)]
    internal static extern int MIDIPortConnectSource(uint port, uint source, IntPtr connRefCon);

    [DllImport(CoreMidi)]
    internal static extern int MIDIPortDispose(uint port);
}
