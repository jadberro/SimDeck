using System.Runtime.InteropServices;

namespace SimDeck.Core.Sources;

/// <summary>
/// Direct P/Invoke into the native SimConnect.dll.
///
/// The managed wrapper that ships with the SDK
/// (Microsoft.FlightSimulator.SimConnect.dll) is a C++/CLI mixed-mode
/// assembly built against .NET Framework. .NET 8 cannot load those at all -
/// it throws BadImageFormatException the moment the type is touched, which
/// looks like a corrupt file but is nothing of the sort.
///
/// Calling the native DLL avoids the wrapper entirely, and leaves one
/// redistributable file instead of two.
/// </summary>
internal static class SimConnectNative
{
    private const string Dll = "SimConnect.dll";

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;

    // SIMCONNECT_RECV_ID
    public const uint RecvException = 1;
    public const uint RecvOpen = 2;
    public const uint RecvQuit = 3;
    public const uint RecvSimObjectData = 8;

    // SIMCONNECT_DATATYPE
    public const uint TypeFloat64 = 4;
    public const uint TypeString256 = 9;

    // SIMCONNECT_PERIOD
    public const uint PeriodSecond = 4;
    public const uint PeriodSimFrame = 3;

    // SIMCONNECT_DATA_REQUEST_FLAG
    public const uint FlagChanged = 1;

    /// <summary>
    /// SIMCONNECT_RECV is 3 DWORDs; SIMCONNECT_RECV_SIMOBJECT_DATA adds seven
    /// more before the payload. 12 + 28 = 40.
    /// </summary>
    public const int SimObjectDataOffset = 40;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_Open(out IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr hWnd,
        uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_Close(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_AddToDataDefinition(IntPtr handle,
        uint defineId, [MarshalAs(UnmanagedType.LPStr)] string datumName,
        [MarshalAs(UnmanagedType.LPStr)] string? unitsName, uint datumType,
        float epsilon, uint datumId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_ClearDataDefinition(IntPtr handle,
        uint defineId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_RequestDataOnSimObject(IntPtr handle,
        uint requestId, uint defineId, uint objectId, uint period, uint flags,
        uint origin, uint interval, uint limit);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_SetDataOnSimObject(IntPtr handle,
        uint defineId, uint objectId, uint flags, uint arrayCount,
        uint unitSize, IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_GetNextDispatch(IntPtr handle,
        out IntPtr data, out uint size);

    public static bool Ok(int hr) => hr >= 0;
}
