using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Windows.UI.Composition;
using WinRT;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// A node of a Direct2D effect graph for Windows.UI.Composition, without Win2D (whose builds target WinUI's
/// composition, not the system's). Sources are other nodes, or names for <see cref="CompositionEffectBrush.SetSourceParameter"/>.
/// </summary>
public sealed class D2DEffect
{
    internal Guid Clsid { get; }
    internal string Name { get; }
    internal object[] Properties { get; }
    internal object[] Sources { get; }
    /// <summary>Properties that may change once the brush is made ("Name.Property"), by name → index.</summary>
    internal Dictionary<string, uint> Named { get; } = new();

    D2DEffect(Guid clsid, string name, object[] properties, object[] sources)
    {
        Clsid = clsid;
        Name = name;
        Properties = properties;
        Sources = sources;
    }

    /// <summary>
    /// The colours more (or less) saturated: <paramref name="s"/> 1 unchanged, above 1 more vivid (a colour matrix with
    /// the luminance weights kept, so greys stay grey).
    /// </summary>
    public static D2DEffect Saturate(string name, float s, object source)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[] m =
        [
            lr * (1 - s) + s, lr * (1 - s), lr * (1 - s), 0,
            lg * (1 - s), lg * (1 - s) + s, lg * (1 - s), 0,
            lb * (1 - s), lb * (1 - s), lb * (1 - s) + s, 0,
            0, 0, 0, 1,
            0, 0, 0, 0,
        ];
        // D2D1 colour matrix: the 5×4 matrix, alpha premultiplied (1), output clamped.
        return new D2DEffect(new Guid("921F03D6-641C-47DF-852D-B4BB6153AE11"), name, [m, 1u, true], [source]);
    }

    /// <summary>The factory for this graph; <paramref name="animatable"/> ("Name.Property") can be set on its brushes later.</summary>
    public CompositionEffectFactory Factory(Compositor c, params string[] animatable)
    {
        IntPtr p = EffectNode.Create(this);
        try
        {
            var effect = MarshalInterface<Windows.Graphics.Effects.IGraphicsEffect>.FromAbi(p);
            return animatable.Length == 0 ? c.CreateEffectFactory(effect) : c.CreateEffectFactory(effect, animatable);
        }
        finally { Marshal.Release(p); }
    }
}

/// <summary>
/// The native side of <see cref="D2DEffect"/>: a small COM object per node answering what CreateEffectFactory reads.
/// IGraphicsEffect (WinRT: the node's name), IGraphicsEffectSource (it can be another node's source) and
/// IGraphicsEffectD2D1Interop (the D2D effect's CLSID, its properties as IPropertyValue, its sources). The three
/// interface pointers are three vtable slots at the start of one block of native memory.
/// </summary>
internal static unsafe class EffectNode
{
    const int S_OK = 0, E_NOINTERFACE = unchecked((int)0x80004002), E_INVALIDARG = unchecked((int)0x80070057), E_BOUNDS = unchecked((int)0x8000000B);
    static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046"), IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"),
        IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), IGraphicsEffect = new("CB51C0CE-8FE6-4636-B202-861FAA07D8F3"),
        IGraphicsEffectSource = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2"), IGraphicsEffectD2D1Interop = new("2FC57384-A068-44D7-A331-30982FCF7177"),
        IPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62"), IPropertyValueStatics = new("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");

    [StructLayout(LayoutKind.Sequential)]
    struct Node
    {
        public void** Effect;  // the IGraphicsEffect pointer is this field's address
        public void** Source;  // IGraphicsEffectSource
        public void** Interop; // IGraphicsEffectD2D1Interop
        public int Refs;
        public IntPtr Data;    // GCHandle of NodeData
    }

    sealed class NodeData
    {
        public Guid Clsid;
        public string Name = "";
        public IntPtr[] Properties = [];
        public IntPtr[] Sources = [];
        public Dictionary<string, uint> Named = new();
    }

    static readonly void** EffectVtbl = Table(
        (delegate* unmanaged<void*, Guid*, void**, int>)&EffectQueryInterface,
        (delegate* unmanaged<void*, uint>)&EffectAddRef,
        (delegate* unmanaged<void*, uint>)&EffectRelease,
        (delegate* unmanaged<void*, uint*, Guid**, int>)&GetIids,
        (delegate* unmanaged<void*, IntPtr*, int>)&GetRuntimeClassName,
        (delegate* unmanaged<void*, int*, int>)&GetTrustLevel,
        (delegate* unmanaged<void*, IntPtr*, int>)&GetName,
        (delegate* unmanaged<void*, IntPtr, int>)&PutName);

    static readonly void** SourceVtbl = Table(
        (delegate* unmanaged<void*, Guid*, void**, int>)&SourceQueryInterface,
        (delegate* unmanaged<void*, uint>)&SourceAddRef,
        (delegate* unmanaged<void*, uint>)&SourceRelease,
        (delegate* unmanaged<void*, uint*, Guid**, int>)&GetIids,
        (delegate* unmanaged<void*, IntPtr*, int>)&GetRuntimeClassName,
        (delegate* unmanaged<void*, int*, int>)&GetTrustLevel);

    static readonly void** InteropVtbl = Table(
        (delegate* unmanaged<void*, Guid*, void**, int>)&InteropQueryInterface,
        (delegate* unmanaged<void*, uint>)&InteropAddRef,
        (delegate* unmanaged<void*, uint>)&InteropRelease,
        (delegate* unmanaged<void*, Guid*, int>)&GetEffectId,
        (delegate* unmanaged<void*, char*, uint*, int*, int>)&GetNamedPropertyMapping,
        (delegate* unmanaged<void*, uint*, int>)&GetPropertyCount,
        (delegate* unmanaged<void*, uint, void**, int>)&GetProperty,
        (delegate* unmanaged<void*, uint, void**, int>)&GetSource,
        (delegate* unmanaged<void*, uint*, int>)&GetSourceCount);

    static void** Table(params void*[] slots)
    {
        var t = (void**)NativeMemory.Alloc((nuint)slots.Length, (nuint)sizeof(void*)); // kept for the process's life
        for (int i = 0; i < slots.Length; i++) t[i] = slots[i];
        return t;
    }

    /// <summary>A new node for <paramref name="e"/> (and its source nodes): its IGraphicsEffect pointer, one reference owned by the caller.</summary>
    public static IntPtr Create(D2DEffect e)
    {
        var data = new NodeData { Clsid = e.Clsid, Name = e.Name, Named = e.Named };
        data.Properties = e.Properties.Select(PropertyValue).ToArray();
        data.Sources = e.Sources.Select(SourceOf).ToArray();
        var n = (Node*)NativeMemory.AllocZeroed((nuint)sizeof(Node));
        n->Effect = EffectVtbl;
        n->Source = SourceVtbl;
        n->Interop = InteropVtbl;
        n->Refs = 1;
        n->Data = GCHandle.ToIntPtr(GCHandle.Alloc(data));
        return (IntPtr)(&n->Effect);
    }

    static IntPtr SourceOf(object source)
    {
        IntPtr unknown;
        if (source is D2DEffect child) unknown = Create(child);
        else if (source is string name) unknown = MarshalInspectable<object>.FromManaged(new CompositionEffectSourceParameter(name));
        else throw new ArgumentException("an effect's source is another effect or a parameter's name");
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IGraphicsEffectSource, out var p));
            return p;
        }
        finally { Marshal.Release(unknown); }
    }

    // ---- IPropertyValue through Windows.Foundation.PropertyValue's factory -----------------------------------------

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);

    static IntPtr _statics;

    static IntPtr Statics
    {
        get
        {
            if (_statics != IntPtr.Zero) return _statics;
            Marshal.ThrowExceptionForHR(WindowsCreateString("Windows.Foundation.PropertyValue", "Windows.Foundation.PropertyValue".Length, out var cls));
            try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(cls, in IPropertyValueStatics, out _statics)); }
            finally { WindowsDeleteString(cls); }
            return _statics;
        }
    }

    static IntPtr PropertyValue(object value)
    {
        IntPtr s = Statics, inspectable;
        var vtbl = *(void***)s;
        int hr;
        switch (value)
        {
            case float f: hr = ((delegate* unmanaged<IntPtr, float, IntPtr*, int>)vtbl[14])(s, f, &inspectable); break;        // CreateSingle
            case uint u: hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)vtbl[11])(s, u, &inspectable); break;         // CreateUInt32
            case bool b: hr = ((delegate* unmanaged<IntPtr, byte, IntPtr*, int>)vtbl[17])(s, (byte)(b ? 1 : 0), &inspectable); break; // CreateBoolean
            case float[] a:
                fixed (float* p = a) hr = ((delegate* unmanaged<IntPtr, uint, float*, IntPtr*, int>)vtbl[33])(s, (uint)a.Length, p, &inspectable); // CreateSingleArray
                break;
            default: throw new ArgumentException("an effect property is a float, a uint, a bool or a float[]: " + value.GetType().Name);
        }
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in IPropertyValue, out var pv));
            return pv;
        }
        finally { Marshal.Release(inspectable); }
    }

    // ---- the node's methods ---------------------------------------------------------------------------------------

    static Node* OfEffect(void* p) => (Node*)p;
    static Node* OfSource(void* p) => (Node*)((byte*)p - sizeof(void*));
    static Node* OfInterop(void* p) => (Node*)((byte*)p - 2 * sizeof(void*));
    static NodeData DataOf(Node* n) => (NodeData)GCHandle.FromIntPtr(n->Data).Target!;

    static int QueryInterface(Node* n, Guid* riid, void** ppv)
    {
        var iid = *riid;
        if (iid == IUnknown || iid == IInspectable || iid == IGraphicsEffect || iid == IAgileObject) *ppv = &n->Effect;
        else if (iid == IGraphicsEffectSource) *ppv = &n->Source;
        else if (iid == IGraphicsEffectD2D1Interop) *ppv = &n->Interop;
        else { *ppv = null; return E_NOINTERFACE; }
        Interlocked.Increment(ref n->Refs);
        return S_OK;
    }

    static uint AddRef(Node* n) => (uint)Interlocked.Increment(ref n->Refs);

    static uint Release(Node* n)
    {
        int left = Interlocked.Decrement(ref n->Refs);
        if (left != 0) return (uint)left;
        var handle = GCHandle.FromIntPtr(n->Data);
        var d = (NodeData)handle.Target!;
        foreach (var p in d.Properties) Marshal.Release(p);
        foreach (var p in d.Sources) Marshal.Release(p);
        handle.Free();
        NativeMemory.Free(n);
        return 0;
    }

    [UnmanagedCallersOnly] static int EffectQueryInterface(void* t, Guid* riid, void** ppv) => QueryInterface(OfEffect(t), riid, ppv);
    [UnmanagedCallersOnly] static uint EffectAddRef(void* t) => AddRef(OfEffect(t));
    [UnmanagedCallersOnly] static uint EffectRelease(void* t) => Release(OfEffect(t));
    [UnmanagedCallersOnly] static int SourceQueryInterface(void* t, Guid* riid, void** ppv) => QueryInterface(OfSource(t), riid, ppv);
    [UnmanagedCallersOnly] static uint SourceAddRef(void* t) => AddRef(OfSource(t));
    [UnmanagedCallersOnly] static uint SourceRelease(void* t) => Release(OfSource(t));
    [UnmanagedCallersOnly] static int InteropQueryInterface(void* t, Guid* riid, void** ppv) => QueryInterface(OfInterop(t), riid, ppv);
    [UnmanagedCallersOnly] static uint InteropAddRef(void* t) => AddRef(OfInterop(t));
    [UnmanagedCallersOnly] static uint InteropRelease(void* t) => Release(OfInterop(t));

    [UnmanagedCallersOnly]
    static int GetIids(void* t, uint* count, Guid** iids)
    {
        var list = (Guid*)Marshal.AllocCoTaskMem(2 * sizeof(Guid));
        list[0] = IGraphicsEffect;
        list[1] = IGraphicsEffectSource;
        *count = 2;
        *iids = list;
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetRuntimeClassName(void* t, IntPtr* name) => WindowsCreateString("Frostbar.Platform.Dwm.D2DEffect", "Frostbar.Platform.Dwm.D2DEffect".Length, out *name);

    [UnmanagedCallersOnly]
    static int GetTrustLevel(void* t, int* level) { *level = 0; return S_OK; } // BaseTrust

    [UnmanagedCallersOnly]
    static int GetName(void* t, IntPtr* name)
    {
        var d = DataOf(OfEffect(t));
        return WindowsCreateString(d.Name, d.Name.Length, out *name);
    }

    [UnmanagedCallersOnly]
    static int PutName(void* t, IntPtr name)
    {
        uint length;
        char* raw = WindowsGetStringRawBuffer(name, &length);
        DataOf(OfEffect(t)).Name = new string(raw, 0, (int)length);
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetEffectId(void* t, Guid* id) { *id = DataOf(OfInterop(t)).Clsid; return S_OK; }

    [UnmanagedCallersOnly]
    static int GetNamedPropertyMapping(void* t, char* name, uint* index, int* mapping)
    {
        if (!DataOf(OfInterop(t)).Named.TryGetValue(new string(name), out var i)) return E_INVALIDARG;
        *index = i;
        *mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetPropertyCount(void* t, uint* count) { *count = (uint)DataOf(OfInterop(t)).Properties.Length; return S_OK; }

    [UnmanagedCallersOnly]
    static int GetProperty(void* t, uint index, void** value)
    {
        var d = DataOf(OfInterop(t));
        if (index >= d.Properties.Length) { *value = null; return E_BOUNDS; }
        Marshal.AddRef(d.Properties[index]);
        *value = (void*)d.Properties[index];
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetSource(void* t, uint index, void** source)
    {
        var d = DataOf(OfInterop(t));
        if (index >= d.Sources.Length) { *source = null; return E_BOUNDS; }
        Marshal.AddRef(d.Sources[index]);
        *source = (void*)d.Sources[index];
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetSourceCount(void* t, uint* count) { *count = (uint)DataOf(OfInterop(t)).Sources.Length; return S_OK; }
}
