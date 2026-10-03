using System.Text;

namespace Ac8Save;

public sealed class Reader
{
    readonly byte[] d;
    int o;
    public int Pos => o;
    public Reader(byte[] data, int offset = 0) { d = data; o = offset; }

    public int I32() { var v = BitConverter.ToInt32(d, o); o += 4; return v; }
    public uint U32() { var v = BitConverter.ToUInt32(d, o); o += 4; return v; }
    public long I64() { var v = BitConverter.ToInt64(d, o); o += 8; return v; }
    public ulong U64() { var v = BitConverter.ToUInt64(d, o); o += 8; return v; }
    public float F32() { var v = BitConverter.ToSingle(d, o); o += 4; return v; }
    public double F64() { var v = BitConverter.ToDouble(d, o); o += 8; return v; }
    public byte U8() => d[o++];
    public byte[] Bytes(int n) { var v = new byte[n]; Array.Copy(d, o, v, 0, n); o += n; return v; }

    public string FString(out bool utf16)
    {
        int len = I32();
        utf16 = false;
        if (len == 0) return "";
        if (len < 0)
        {
            utf16 = true;
            int n = -len;
            var s = Encoding.Unicode.GetString(d, o, (n - 1) * 2);
            o += n * 2;
            return s;
        }
        var a = Encoding.UTF8.GetString(d, o, len - 1);
        o += len;
        return a;
    }
    public string FString() => FString(out _);

    public TypeName TypeName()
    {
        var t = new TypeName { Name = FString() };
        int n = I32();
        for (int i = 0; i < n; i++) t.Inner.Add(TypeName());
        return t;
    }

    static readonly HashSet<string> BinaryStructs = new()
    {
        "Vector", "Rotator", "Quat", "Guid", "DateTime", "Timespan", "LinearColor", "Vector2D",
        "Transform", "IntPoint", "Color", "Vector4", "Box", "Box2D", "IntVector", "SoftObjectPath"
    };

    public List<Property> Properties(Node parent)
    {
        var list = new List<Property>();
        while (true)
        {
            var p = Property();
            if (p == null) return list;
            p.Parent = parent;
            list.Add(p);
        }
    }

    // Returns null on the "None" terminator.
    public Property? Property()
    {
        var name = FString();
        if (name == "None") return null;
        var p = new Property { Name = name, Type = TypeName() };
        int size = I32();
        p.Flags = (TagFlags)U8();
        if ((p.Flags & TagFlags.HasArrayIndex) != 0) p.ArrayIndex = I32();
        if ((p.Flags & TagFlags.HasPropertyGuid) != 0) p.Guid = Bytes(16);
        int start = o;
        try
        {
            p.Value = ReadValue(p.Type, size, p.Flags, p);
        }
        catch (Exception ex) when (ex is not InvalidDataException || !ex.Message.StartsWith("at "))
        {
            throw new InvalidDataException($"at {name} ({p.Type}) start=0x{start:X} size={size}: {ex.Message}", ex);
        }
        if (o != start + size)
            throw new InvalidDataException($"at {name} ({p.Type}): consumed {o - start} of {size} bytes at 0x{start:X}");
        return p;
    }

    Value ReadValue(TypeName t, int size, TagFlags flags, Node parent)
    {
        Value v;
        switch (t.Name)
        {
            case "BoolProperty": v = new BoolValue { V = (flags & TagFlags.BoolTrue) != 0 }; break;
            case "IntProperty": v = new IntValue { V = I32(), Kind = t.Name }; break;
            case "UInt32Property": v = new IntValue { V = U32(), Kind = t.Name }; break;
            case "Int64Property": v = new IntValue { V = I64(), Kind = t.Name }; break;
            case "UInt64Property": v = new IntValue { V = (long)U64(), Kind = t.Name }; break;
            case "Int16Property": v = new IntValue { V = BitConverter.ToInt16(d, o), Kind = t.Name }; o += 2; break;
            case "UInt16Property": v = new IntValue { V = BitConverter.ToUInt16(d, o), Kind = t.Name }; o += 2; break;
            case "Int8Property": v = new IntValue { V = (sbyte)U8(), Kind = t.Name }; break;
            case "FloatProperty": v = new FloatValue { V = F32() }; break;
            case "DoubleProperty": v = new FloatValue { V = F64(), IsDouble = true }; break;
            case "StrProperty":
            case "NameProperty":
            case "SoftObjectProperty":
                { var s = FString(out var u); v = new StrValue { V = s, Utf16 = u }; break; }
            case "EnumProperty": v = new EnumValue { V = FString() }; break;
            case "ByteProperty":
                if (size == 1) v = new ByteValue { V = U8() };
                else v = new EnumValue { V = FString() };
                break;
            case "StructProperty":
                {
                    var sn = t.Inner.Count > 0 ? t.Inner[0].Name : "";
                    if (BinaryStructs.Contains(sn)) { v = new RawValue { Data = Bytes(size) }; break; }
                    var sv = new StructValue();
                    sv.Props = Properties(sv);
                    v = sv;
                    break;
                }
            case "ArrayProperty": v = ReadArray(t, size); break;
            case "SetProperty": v = ReadSet(t, size); break;
            case "MapProperty": v = ReadMap(t, size); break;
            default: v = new RawValue { Data = Bytes(size) }; break;
        }
        v.Parent = parent;
        foreach (var c in v.Children) c.Parent = v;
        return v;
    }

    // Element payloads inside containers have no tag; bools are 1 byte, enums are FStrings.
    Value ReadElem(TypeName t, Node parent)
    {
        Value v;
        switch (t.Name)
        {
            case "BoolProperty": v = new BoolValue { V = U8() != 0 }; break;
            case "ByteProperty":
                // ByteProperty<EnumName> elements are FStrings; plain ByteProperty elements are raw bytes.
                v = t.Inner.Count > 0 ? new EnumValue { V = FString() } : new ByteValue { V = U8() };
                break;
            case "StructProperty":
                {
                    var sn = t.Inner.Count > 0 ? t.Inner[0].Name : "";
                    if (BinaryStructs.Contains(sn)) throw new InvalidDataException("binary struct in container needs size: " + sn);
                    var sv = new StructValue();
                    sv.Props = Properties(sv);
                    v = sv;
                    break;
                }
            default: v = ReadValue(t, -1, TagFlags.None, parent); break;
        }
        v.Parent = parent;
        return v;
    }

    ArrayValue ReadArray(TypeName t, int size)
    {
        var a = new ArrayValue { ElemType = t.Inner[0] };
        int n = I32();
        int end = o - 4 + size;
        if (a.ElemType.Name == "ByteProperty" && a.ElemType.Inner.Count == 0)
        {
            // Byte arrays are stored as one raw blob to keep the tree small.
            var raw = new RawValue { Data = Bytes(n) };
            a.Items.Add(new Element { Index = 0, Value = raw });
            raw.Parent = a.Items[0];
            return a;
        }
        if (a.ElemType.Name == "StructProperty")
        {
            // UE 5.4 complete-type-name format: no legacy inner tag, elements follow the count directly.
            var sn = a.ElemType.Inner.Count > 0 ? a.ElemType.Inner[0].Name : "";
            if (BinaryStructs.Contains(sn))
            {
                int each = n == 0 ? 0 : (end - o) / n;
                for (int i = 0; i < n; i++)
                {
                    var e = new Element { Index = i, Value = new RawValue { Data = Bytes(each) } };
                    e.Value.Parent = e; a.Items.Add(e);
                }
                return a;
            }
        }
        for (int i = 0; i < n; i++)
        {
            var e = new Element { Index = i };
            e.Value = ReadElem(a.ElemType, e);
            a.Items.Add(e);
        }
        return a;
    }

    SetValue ReadSet(TypeName t, int size)
    {
        var s = new SetValue { ElemType = t.Inner[0] };
        s.RemovedCount = I32();
        if (s.RemovedCount != 0) throw new InvalidDataException("set with removed entries");
        int n = I32();
        for (int i = 0; i < n; i++)
        {
            var e = new Element { Index = i };
            e.Value = ReadElem(s.ElemType, e);
            s.Items.Add(e);
        }
        return s;
    }

    MapValue ReadMap(TypeName t, int size)
    {
        var m = new MapValue { KeyType = t.Inner[0], ValType = t.Inner[1] };
        m.RemovedCount = I32();
        if (m.RemovedCount != 0) throw new InvalidDataException("map with removed entries");
        int n = I32();
        for (int i = 0; i < n; i++)
        {
            var e = new MapEntry { Index = i };
            e.Key = ReadElem(m.KeyType, e);
            e.Val = ReadElem(m.ValType, e);
            m.Items.Add(e);
        }
        return m;
    }

    // ---- outer file ----

    public static SaveFile Load(byte[] file)
    {
        var r = new Reader(file);
        if (r.U32() != 0x53415647) throw new InvalidDataException("not a GVAS file");
        r.I32(); r.I32(); r.I32();               // SaveGameFileVersion, PackageFileUEVersion (UE4, UE5)
        r.Bytes(10);                             // engine version major/minor/patch/changelist
        r.FString();                             // branch
        r.I32();                                 // custom version format
        int nver = r.I32(); r.Bytes(nver * 20);  // custom versions
        var cls = r.FString();
        r.U8();                                  // trailing pad byte after class name in this title's saves
        var sf = new SaveFile { ClassName = cls };
        sf.HeaderBytes = file[..r.Pos];

        var sv = r.Property() ?? throw new InvalidDataException("missing SavedVersion");
        if (sv.Name != "SavedVersion") throw new InvalidDataException("unexpected " + sv.Name);
        sf.SavedVersion = (int)((IntValue)sv.Value).V;

        var pd = r.Property() ?? throw new InvalidDataException("missing PackedData");
        if (pd.Name != "PackedData") throw new InvalidDataException("unexpected " + pd.Name);
        var packed = ((RawValue)((ArrayValue)pd.Value).Items[0].Value).Data;

        var ck = r.Property() ?? throw new InvalidDataException("missing Checksum");
        if (ck.Name != "Checksum") throw new InvalidDataException("unexpected " + ck.Name);
        sf.StoredChecksum = (uint)((IntValue)ck.Value).V;

        var inner = new Reader(packed);
        while (inner.Pos < packed.Length)
        {
            var sec = new StructValue();
            sec.Props = inner.Properties(sec);
            sf.Sections.Add(sec);
        }
        return sf;
    }
}
