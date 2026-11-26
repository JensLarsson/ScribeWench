using System.Runtime.InteropServices;

namespace ScribeWench.CLI.Wizard.Environments;

// MemoryGb: the card's own memory (VRAM), which decides how large a model it holds.
public sealed record GraphicsCard(string Name, double MemoryGb);

// What setup needs to know about the machine to suggest models that will run on it.
public sealed record MachineInfo(double RamGb, IReadOnlyList<GraphicsCard> Cards)
{
    // Below this a card doesn't speed up a language model enough to count: it is a processor's built-in
    // graphics, or too small to hold a useful part of the model.
    public const double SmallestUsefulCardGb = 5.5;

    // The card a model would run on: the one with the most memory, if any has enough.
    public GraphicsCard? Card => Cards.Where(c => c.MemoryGb >= SmallestUsefulCardGb).MaxBy(c => c.MemoryGb);

    public double CardGb => Card?.MemoryGb ?? 0;

    // RAM as its owner knows it. Windows reports a little less than is fitted (31.1 for 32 GB), and
    // memory comes in even sizes.
    public double NominalRamGb => RamGb < 3 ? Math.Round(RamGb) : Math.Round(RamGb / 2, MidpointRounding.AwayFromZero) * 2;

    public string Describe() =>
        Card is { } card
            ? $"{NominalRamGb:0} GB of RAM, and a graphics card with {card.MemoryGb:0} GB ({card.Name})"
            : $"{NominalRamGb:0} GB of RAM, and no graphics card to speak of";

    public static MachineInfo Detect() => new(DetectRamGb(), DetectCards());

    private static double DetectRamGb() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

    // The graphics cards Windows has running, with their real memory. Asked of DXGI because the usual
    // places are wrong: WMI stops counting at 4 GB, and the registry keeps cards taken out long ago.
    // Empty anywhere else, or when asking fails; setup then asks the user.
    private static IReadOnlyList<GraphicsCard> DetectCards()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return DxgiCards();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or MarshalDirectiveException)
        {
            return [];
        }
    }

    // ─── DXGI, the part of DirectX that lists graphics adapters ──────────────

    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int EnumAdapters1Slot = 12;   // IDXGIFactory1::EnumAdapters1
    private const int GetDesc1Slot = 10;        // IDXGIAdapter1::GetDesc1
    private const int ReleaseSlot = 2;          // IUnknown::Release
    private const uint SoftwareAdapter = 2;     // DXGI_ADAPTER_FLAG_SOFTWARE: Windows' own stand-in renderer

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr self, out AdapterDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr self);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    // DXGI_ADAPTER_DESC1.
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static List<GraphicsCard> DxgiCards()
    {
        var cards = new List<GraphicsCard>();
        if (CreateDXGIFactory1(DxgiFactory1, out var factory) < 0 || factory == IntPtr.Zero) return cards;
        try
        {
            var enumAdapters = Method<EnumAdapters1>(factory, EnumAdapters1Slot);
            // Ends with DXGI_ERROR_NOT_FOUND; the limit is only there so a misbehaving driver can't loop forever.
            for (uint index = 0; index < 16 && enumAdapters(factory, index, out var adapter) >= 0 && adapter != IntPtr.Zero; index++)
            {
                try
                {
                    if (Method<GetDesc1>(adapter, GetDesc1Slot)(adapter, out var description) < 0) continue;
                    if ((description.Flags & SoftwareAdapter) != 0) continue;

                    var name = (description.Description ?? "").Trim();
                    var memoryGb = (double)description.DedicatedVideoMemory.ToUInt64() / (1024.0 * 1024 * 1024);
                    // One card can be listed once per output it drives.
                    if (name.Length > 0 && !cards.Any(c => c.Name == name && Math.Abs(c.MemoryGb - memoryGb) < 0.01))
                        cards.Add(new GraphicsCard(name, memoryGb));
                }
                finally
                {
                    Method<Release>(adapter, ReleaseSlot)(adapter);
                }
            }
        }
        finally
        {
            Method<Release>(factory, ReleaseSlot)(factory);
        }
        return cards;
    }
}
