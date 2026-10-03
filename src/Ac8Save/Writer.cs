using System.Text;

namespace Ac8Save;

public sealed class Writer
{
    readonly MemoryStream ms = new();
    readonly BinaryWriter w;
    public Writer() { w = new BinaryWriter(ms); }
    public byte[] ToArray() => ms.ToArray();
    public long Pos => ms.Position;

    public void I32(int v) => w.Write(v);
    public void U32(uint v) => w.Write(v);
    public void I64(long v) => w.Write(v);
    public void U8(byte v) => w.Write(v);
    public void Bytes(byte[] b) => w.Write(b);

    public void FString(string s, bool utf16 = false)
    {
        if (s.Length == 0) { I32(0); return; }
        if (!utf16 && s.All(c => c < 0x80))
        {
            var b = Encoding.UTF8.GetBytes(s);
            I32(b.Length + 1); Bytes(b); U8(0);
        }
        else
        {
            var b = Encoding.Unicode.GetBytes(s);
            I32(-(s.Length + 1)); Bytes(b); U8(0); U8(0);
        }
    }

    public void TypeName(TypeName t)
    {
        FString(t.Name);
        I32(t.Inner.Count);
        foreach (var i in t.Inner) TypeName(i);
    }

    public void Properties(IEnumerable<Property> props)
    {
        foreach (var p in props) Property(p);
        FString("None");
    }

    public void Property(Property p)
    {
        FString(p.Name);
        TypeName(p.Type);
        var payload = new Writer();
        var flags = p.Flags & ~TagFlags.BoolTrue;
        if (p.Value is BoolValue bv) { if (bv.V) flags |= TagFlags.BoolTrue; }
        else payload.Value(p.Type, p.Value);
        var bytes = payload.ToArray();
        I32(bytes.Length);
        U8((byte)flags);
        if ((flags & TagFlags.HasArrayIndex) != 0) I32(p.ArrayIndex);
        if ((flags & TagFlags.HasPropertyGuid) != 0) Bytes(p.Guid ?? new byte[16]);
        Bytes(bytes);
    }

    public void Value(TypeName t, Value v)
    {
        switch (v)
        {
            case IntValue iv:
                switch (iv.Kind)
                {
                    case "IntProperty": I32((int)iv.V); break;
                    case "UInt32Property": U32((uint)iv.V); break;
                    case "Int64Property": case "UInt64Property": I64(iv.V); break;
                    case "Int16Property": case "UInt16Property": w.Write((ushort)iv.V); break;
                    case "Int8Property": U8((byte)iv.V); break;
                    default: throw new InvalidDataException("int kind " + iv.Kind);
                }
                break;
            case FloatValue fv: if (fv.IsDouble) w.Write(fv.V); else w.Write((float)fv.V); break;
            case StrValue sv: FString(sv.V, sv.Utf16); break;
            case EnumValue ev: FString(ev.V); break;
            case ByteValue bv: U8(bv.V); break;
            case RawValue rv: Bytes(rv.Data); break;
            case StructValue st: Properties(st.Props); break;
            case ArrayValue av: Array(av); break;
            case SetValue se: Set(se); break;
            case MapValue mv: Map(mv); break;
            case BoolValue b: U8((byte)(b.V ? 1 : 0)); break; // only reached for container elements
            default: throw new InvalidDataException("value " + v.GetType().Name);
        }
    }

    void Elem(TypeName t, Value v)
    {
        switch (v)
        {
            case BoolValue b: U8((byte)(b.V ? 1 : 0)); break;
            case EnumValue e: FString(e.V); break;
            case ByteValue bv: U8(bv.V); break;
            case StructValue st: Properties(st.Props); break;
            default: Value(t, v); break;
        }
    }

    void Array(ArrayValue a)
    {
        if (a.ElemType.Name == "ByteProperty" && a.ElemType.Inner.Count == 0)
        {
            var raw = ((RawValue)a.Items[0].Value).Data;
            I32(raw.Length); Bytes(raw);
            return;
        }
        I32(a.Items.Count);
        if (a.ElemType.Name == "StructProperty" && a.LegacyInnerTag != null)
        {
            var body = new Writer();
            foreach (var e in a.Items) body.Elem(a.ElemType, e.Value);
            var bytes = body.ToArray();
            var tag = a.LegacyInnerTag;
            FString(tag.Name);
            TypeName(tag.Type);
            I32(bytes.Length);
            U8((byte)tag.Flags);
            if ((tag.Flags & TagFlags.HasArrayIndex) != 0) I32(tag.ArrayIndex);
            if ((tag.Flags & TagFlags.HasPropertyGuid) != 0) Bytes(tag.Guid ?? new byte[16]);
            Bytes(bytes);
            return;
        }
        foreach (var e in a.Items) Elem(a.ElemType, e.Value);
    }

    void Set(SetValue s)
    {
        I32(s.RemovedCount);
        I32(s.Items.Count);
        foreach (var e in s.Items) Elem(s.ElemType, e.Value);
    }

    void Map(MapValue m)
    {
        I32(m.RemovedCount);
        I32(m.Items.Count);
        foreach (var e in m.Items) { Elem(m.KeyType, e.Key); Elem(m.ValType, e.Val); }
    }

    // ---- outer file ----

    public static byte[] PackedData(SaveFile sf)
    {
        var w = new Writer();
        foreach (var sec in sf.Sections) w.Properties(sec.Props);
        return w.ToArray();
    }

    public static byte[] Save(SaveFile sf, Func<SaveFile, byte[], uint> checksum)
    {
        var packed = PackedData(sf);
        var w = new Writer();
        w.Bytes(sf.HeaderBytes);
        w.Property(new Property { Name = "SavedVersion", Type = new TypeName { Name = "IntProperty" }, Value = new IntValue { V = sf.SavedVersion } });
        var arr = new ArrayValue { ElemType = new TypeName { Name = "ByteProperty" } };
        arr.Items.Add(new Element { Value = new RawValue { Data = packed } });
        w.Property(new Property { Name = "PackedData", Type = new TypeName { Name = "ArrayProperty", Inner = { new TypeName { Name = "ByteProperty" } } }, Value = arr });
        w.Property(new Property { Name = "Checksum", Type = new TypeName { Name = "UInt32Property" }, Value = new IntValue { V = checksum(sf, packed), Kind = "UInt32Property" } });
        w.FString("None");
        w.I32(0);
        return w.ToArray();
    }
}
